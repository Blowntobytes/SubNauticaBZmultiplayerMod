using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Per-player per-world inventory persistence. The host stores each joined player's personal inventory
    /// on disk, keyed by SteamID and world identity (slot + game mode). When a player rejoins the same world,
    /// they get their saved inventory back instead of a copy of the host's.
    ///
    /// World key = "{saveSlot}_{gameModePresetId}" so that:
    ///   - Creative world A != Freedom world B
    ///   - Two different Freedom saves are independent
    ///
    /// Storage: {SavedGames}/BZMultiplayer/inventories/{steamId}_{worldKey}.inv  (simple binary)
    ///
    /// Flow:
    ///   1. Client joins, host's world loads via SaveSync (client starts with host's inventory)
    ///   2. WorldSync.ReidLocalInventory gives items fresh IDs (existing)
    ///   3. Once the world settles, client tells host "I'm ready for my inventory"
    ///   4. Host checks for a saved .inv file for this player+world
    ///   5. If found, host sends it; client replaces inventory contents
    ///   6. On leave / periodic save, client sends its current inventory to host; host writes it to disk
    /// </summary>
    public static class InventorySync
    {
        private static SteamNet net;
        private static bool inventorySent;       // client: have we sent our initial inventory to host
        private static bool inventoryRestored;   // client: have we received saved inventory from host
        private static float readyCheckAt;

        public static void Init(SteamNet steamNet)
        {
            net = steamNet;
        }

        // ------------------------------------------------------------------ world key

        /// <summary>Builds a unique key for the current world: "{slot}_{gameMode}".</summary>
        public static string GetWorldKey()
        {
            if (SaveLoadManager.main == null) return null;
            string slot = SaveLoadManager.main.GetCurrentSlot();
            if (string.IsNullOrEmpty(slot)) return null;

            string mode = "Unknown";
            try
            {
                var info = SaveLoadManager.main.GetGameInfo(slot);
                if (info != null && info.gameModePresetId != null)
                    mode = info.gameModePresetId.ToString();
            }
            catch { }

            return slot + "_" + mode;
        }

        // ------------------------------------------------------------------ storage paths

        private static string InventoryDir()
        {
            string savedGames = SaveSync.GetSavedGamesPath();
            return Path.Combine(savedGames, "BZMultiplayer", "inventories");
        }

        private static string InventoryPath(ulong steamId, string worldKey)
        {
            // Sanitize worldKey for filesystem safety
            string safe = worldKey.Replace(Path.DirectorySeparatorChar, '_')
                                  .Replace(Path.AltDirectorySeparatorChar, '_')
                                  .Replace(':', '_').Replace('?', '_').Replace('*', '_');
            return Path.Combine(InventoryDir(), steamId + "_" + safe + ".inv");
        }

        // ------------------------------------------------------------------ host side

        /// <summary>Host: save a player's inventory to disk.</summary>
        public static void HostSavePlayerInventory(ulong steamId, string worldKey, List<InventoryEntry> items)
        {
            if (string.IsNullOrEmpty(worldKey) || items == null) return;
            try
            {
                string dir = InventoryDir();
                Directory.CreateDirectory(dir);
                string path = InventoryPath(steamId, worldKey);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((int)1); // version
                    bw.Write(worldKey);
                    bw.Write(items.Count);
                    foreach (var e in items)
                    {
                        bw.Write(e.TechType);
                        bw.Write(e.Quantity);
                    }
                }
                Plugin.Log.LogInfo("Saved inventory for " + steamId + " in " + worldKey + ": " + items.Count + " item types.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Failed to save inventory for " + steamId + ": " + e.Message);
            }
        }

        /// <summary>Host: load a previously saved inventory for a player+world.</summary>
        public static List<InventoryEntry> HostLoadPlayerInventory(ulong steamId, string worldKey)
        {
            if (string.IsNullOrEmpty(worldKey)) return null;
            string path = InventoryPath(steamId, worldKey);
            if (!File.Exists(path)) return null;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    int version = br.ReadInt32();
                    if (version != 1) { Plugin.Log.LogWarning("Unknown inventory version " + version); return null; }
                    string key = br.ReadString(); // worldKey, for verification
                    int count = br.ReadInt32();
                    var items = new List<InventoryEntry>(count);
                    for (int i = 0; i < count; i++)
                        items.Add(new InventoryEntry { TechType = br.ReadInt32(), Quantity = br.ReadInt32() });
                    Plugin.Log.LogInfo("Loaded inventory for " + steamId + " in " + worldKey + ": " + count + " item types.");
                    return items;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Failed to load inventory for " + steamId + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Host: called when we receive a player's inventory data.</summary>
        public static void OnInventoryDataFromClient(ulong fromSteamId, PacketReader r)
        {
            string worldKey = r.ReadString();
            int count = r.ReadInt();
            var items = new List<InventoryEntry>(count);
            for (int i = 0; i < count; i++)
                items.Add(new InventoryEntry { TechType = r.ReadInt(), Quantity = r.ReadInt() });
            HostSavePlayerInventory(fromSteamId, worldKey, items);
        }

        /// <summary>Host: check if we have a saved inventory for a newly joined player and send it.</summary>
        public static void HostCheckAndSendInventory(ulong clientSteamId)
        {
            string worldKey = GetWorldKey();
            if (worldKey == null) return;

            var saved = HostLoadPlayerInventory(clientSteamId, worldKey);
            if (saved == null || saved.Count == 0)
            {
                Plugin.Log.LogInfo("No saved inventory for " + clientSteamId + " in " + worldKey);
                return;
            }

            // Send the saved inventory to the client
            SendInventoryData(clientSteamId, worldKey, saved);
            Plugin.Log.LogInfo("Sent saved inventory to " + clientSteamId + ": " + saved.Count + " item types.");
        }

        private static void SendInventoryData(ulong to, string worldKey, List<InventoryEntry> items)
        {
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryData);
            pw.Write(worldKey);
            pw.Write(items.Count);
            foreach (var e in items)
            {
                pw.Write(e.TechType);
                pw.Write(e.Quantity);
            }
            net.SendInventoryPacket(to, pw);
        }

        // ------------------------------------------------------------------ client side

        /// <summary>Client: snapshot our current inventory and send it to the host for saving.</summary>
        public static void ClientSendInventoryToHost()
        {
            if (net == null || !net.IsInSession || net.IsHost) return;
            if (!LocalPlayerSync.InWorld || Inventory.main == null) return;

            string worldKey = GetWorldKey();
            if (worldKey == null) return;

            var items = SnapshotInventory();
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryData);
            pw.Write(worldKey);
            pw.Write(items.Count);
            foreach (var e in items)
            {
                pw.Write(e.TechType);
                pw.Write(e.Quantity);
            }
            net.SendInventoryPacket(net.HostSteamId, pw);
            Plugin.Log.LogInfo("Sent inventory to host: " + items.Count + " item types in " + worldKey);
        }

        /// <summary>Client: received our saved inventory from the host — replace our current items.</summary>
        public static void OnInventoryDataFromHost(PacketReader r)
        {
            if (inventoryRestored) return; // only restore once per session
            string worldKey = r.ReadString();
            int count = r.ReadInt();
            var items = new List<InventoryEntry>(count);
            for (int i = 0; i < count; i++)
                items.Add(new InventoryEntry { TechType = r.ReadInt(), Quantity = r.ReadInt() });

            if (items.Count == 0) return;

            Plugin.Log.LogInfo("Received saved inventory from host: " + count + " item types for " + worldKey);
            inventoryRestored = true;
            Plugin.Instance.StartCoroutine(RestoreInventory(items));
        }

        private static IEnumerator RestoreInventory(List<InventoryEntry> items)
        {
            // Wait for the world to settle
            float deadline = Time.unscaledTime + 30f;
            while (!LocalPlayerSync.InWorld && Time.unscaledTime < deadline) yield return null;
            yield return new WaitForSecondsRealtime(3f); // let world fully load

            if (Inventory.main == null) yield break;

            WorldSync.applyingRemote = true;
            try
            {
                // Clear current inventory
                var container = Inventory.main.container;
                if (container != null)
                {
                    var toRemove = new List<InventoryItem>();
                    foreach (InventoryItem item in container) toRemove.Add(item);
                    foreach (var item in toRemove)
                    {
                        container.RemoveItem(item.item, true);
                        UnityEngine.Object.Destroy(item.item.gameObject);
                    }
                }

                // Spawn saved items
                foreach (var entry in items)
                {
                    TechType tt = (TechType)entry.TechType;
                    for (int q = 0; q < entry.Quantity; q++)
                    {
                        var result = new TaskResult<GameObject>();
                        yield return CraftData.InstantiateFromPrefabAsync(tt, result, false);
                        var go = result.Get();
                        if (go == null) continue;
                        var p = go.GetComponent<Pickupable>();
                        if (p == null) { UnityEngine.Object.Destroy(go); continue; }
                        go.SetActive(true);
                        p.Pickup(false);
                        if (Inventory.main.container.AddItem(p) == null)
                        {
                            Plugin.Log.LogWarning("Inventory full, could not restore " + tt);
                            UnityEngine.Object.Destroy(go);
                        }
                    }
                }
                Plugin.Log.LogInfo("Inventory restored: " + items.Count + " item types.");
            }
            catch (Exception e) { Plugin.Log.LogError("Inventory restore failed: " + e); }
            finally { WorldSync.applyingRemote = false; }
        }

        // ------------------------------------------------------------------ inventory snapshot

        private static List<InventoryEntry> SnapshotInventory()
        {
            var dict = new Dictionary<int, int>();
            if (Inventory.main != null && Inventory.main.container != null)
            {
                foreach (InventoryItem item in Inventory.main.container)
                {
                    if (item == null || item.item == null) continue;
                    int tt = (int)item.item.GetTechType();
                    if (dict.ContainsKey(tt)) dict[tt]++;
                    else dict[tt] = 1;
                }
            }

            var list = new List<InventoryEntry>(dict.Count);
            foreach (var kv in dict)
                list.Add(new InventoryEntry { TechType = kv.Key, Quantity = kv.Value });
            return list;
        }

        // ------------------------------------------------------------------ update

        /// <summary>Called every frame by the plugin. Handles periodic inventory saves and ready checks.</summary>
        public static void Update()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld) return;

            // Client: once the world settles, notify the host we're ready for inventory
            if (!net.IsHost && !inventorySent && WorldSync.WorldSettled)
            {
                inventorySent = true;
                // Tell the host to check for our saved inventory
                var pw = new PacketWriter();
                pw.Begin(PacketType.InventoryRequest);
                net.SendInventoryPacket(net.HostSteamId, pw);
            }
        }

        /// <summary>Reset state when leaving a session.</summary>
        public static void Reset()
        {
            inventorySent = false;
            inventoryRestored = false;
        }

        /// <summary>Host: save all connected players' inventories. Called before the host leaves.</summary>
        public static void HostSaveAllOnLeave()
        {
            // The host can't directly read remote players' inventories.
            // Instead, we request each client to send their inventory.
            // This is done by sending InventoryRequest to all clients.
            if (net == null || !net.IsHost) return;
            // Broadcast inventory request to all clients
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryRequest);
            net.BroadcastInventoryRequest(pw);
        }
    }

    /// <summary>Compact representation of an inventory item type and its count.</summary>
    public struct InventoryEntry
    {
        public int TechType;
        public int Quantity;
    }
}
