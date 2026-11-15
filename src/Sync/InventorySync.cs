using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Per-player, per-world inventory for joiners. The HOST owns the records and keeps them on its own disk.
    ///
    ///  - A joiner loads the host's save, so the game first gives him a copy of the HOST's inventory and equipment.
    ///    Once his world settles he asks the host for his record; the host always answers (an empty record if he
    ///    has never been in this world) and the joiner's inventory and equipment are replaced with it.
    ///  - While he plays, the joiner sends his inventory to the host whenever it changes (checked every 2 s), so the
    ///    host already has it when he leaves. A send at the moment of leaving is not enough: the connection closes
    ///    right after and the packet was being dropped, and quitting to the menu has no inventory left to send.
    ///  - World identity is the host save's session id (a GUID the game gives every new game and keeps in its save),
    ///    decided by the host only. Before 0.12.2 each side built its own key from its slot name ("slot0000_Freedom"
    ///    on the host, "slot9990_Freedom" on the joiner), so records were saved under a key that was never looked up,
    ///    and a new game started in the same slot would have inherited the old records.
    ///
    /// Storage (host): {SavedGames}/BZMultiplayer/inventories/{steamId}_{worldKey}.inv
    /// </summary>
    public static class InventorySync
    {
        private const int FileVersion = 3;   // 3: + powered items (battery charge); 2 still readable
        private const float SyncInterval = 2f;
        private const float HostReplyTimeout = 20f;

        private static SteamNet net;

        // client state
        private static bool requested;        // asked the host for our record
        private static float requestedAt;
        private static bool restoring;        // replacing our inventory right now
        private static bool restored;         // our inventory is ours now; safe to report it to the host
        private static string lastSentSignature;
        private static float nextSyncCheck;

        // host state
        private static string lastHostWorldKey;

        public static void Init(SteamNet steamNet) { net = steamNet; }

        // ------------------------------------------------------------------ world key (host decides)

        private static FieldInfo fSessionId;

        /// <summary>Host: identity of the world being hosted. The save's session GUID, falling back to slot + mode.</summary>
        public static string GetWorldKey()
        {
            string key = null;
            try
            {
                var slm = SaveLoadManager.main;
                if (slm != null)
                {
                    if (fSessionId == null) fSessionId = typeof(SaveLoadManager).GetField("sessionId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    string sid = fSessionId != null ? fSessionId.GetValue(slm) as string : null;
                    string slot = slm.GetCurrentSlot();
                    if (string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(slot))
                    {
                        var info = slm.GetGameInfo(slot);
                        if (info != null) sid = info.session;
                    }
                    if (!string.IsNullOrEmpty(sid)) key = "world-" + sid;
                    else if (!string.IsNullOrEmpty(slot)) key = slot;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("World key lookup failed: " + e.Message); }
            if (key != null) lastHostWorldKey = key;
            return key ?? lastHostWorldKey;
        }

        // ------------------------------------------------------------------ records

        private class Record
        {
            public List<InventoryEntry> Items = new List<InventoryEntry>();
            public List<EquipmentEntry> Equipment = new List<EquipmentEntry>();
            // Items whose energy matters, one entry each: batteries/power cells (their own charge) and tools with a
            // battery slot (which battery is in it and its charge). Kept apart from Items so a restored battery or
            // flashlight comes back with its charge instead of as a fresh prefab.
            public List<PoweredEntry> Powered = new List<PoweredEntry>();
            public int Count { get { return Items.Count + Equipment.Count + Powered.Count; } }
        }

        private static void WriteRecord(PacketWriter pw, Record rec)
        {
            pw.Write(rec.Items.Count);
            foreach (var e in rec.Items) { pw.Write(e.TechType); pw.Write(e.Quantity); }
            pw.Write(rec.Equipment.Count);
            foreach (var e in rec.Equipment) { pw.Write(e.Slot); pw.Write(e.TechType); }
            pw.Write(rec.Powered.Count);
            foreach (var e in rec.Powered) { pw.Write(e.TechType); pw.Write(e.BatteryType); pw.Write(e.Charge); }
        }

        private static Record ReadRecord(PacketReader r)
        {
            var rec = new Record();
            int n = r.ReadInt();
            for (int i = 0; i < n; i++) rec.Items.Add(new InventoryEntry { TechType = r.ReadInt(), Quantity = r.ReadInt() });
            if (!r.AtEnd)
            {
                int m = r.ReadInt();
                for (int i = 0; i < m; i++) rec.Equipment.Add(new EquipmentEntry { Slot = r.ReadString(), TechType = r.ReadInt() });
            }
            if (!r.AtEnd)
            {
                int k = r.ReadInt();
                for (int i = 0; i < k; i++) rec.Powered.Add(new PoweredEntry { TechType = r.ReadInt(), BatteryType = r.ReadInt(), Charge = r.ReadFloat() });
            }
            return rec;
        }

        private static string Describe(Record rec)
        {
            int total = 0;
            foreach (var e in rec.Items) total += e.Quantity;
            total += rec.Powered.Count;
            return total + " items (" + (rec.Items.Count + rec.Powered.Count) + " kinds), " + rec.Powered.Count + " with charge, " + rec.Equipment.Count + " equipped";
        }

        // ------------------------------------------------------------------ host storage

        private static string InventoryDir()
        {
            return Path.Combine(Path.Combine(SaveSync.GetSavedGamesPath(), "BZMultiplayer"), "inventories");
        }

        private static string InventoryPath(ulong steamId, string worldKey)
        {
            var sb = new StringBuilder(worldKey.Length);
            foreach (char c in worldKey) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return Path.Combine(InventoryDir(), steamId + "_" + sb + ".inv");
        }

        private static void HostSave(ulong steamId, string worldKey, Record rec)
        {
            try
            {
                Directory.CreateDirectory(InventoryDir());
                string path = InventoryPath(steamId, worldKey);
                string tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(FileVersion);
                    bw.Write(worldKey);
                    bw.Write(rec.Items.Count);
                    foreach (var e in rec.Items) { bw.Write(e.TechType); bw.Write(e.Quantity); }
                    bw.Write(rec.Equipment.Count);
                    foreach (var e in rec.Equipment) { bw.Write(e.Slot ?? ""); bw.Write(e.TechType); }
                    bw.Write(rec.Powered.Count);
                    foreach (var e in rec.Powered) { bw.Write(e.TechType); bw.Write(e.BatteryType); bw.Write(e.Charge); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Plugin.Log.LogInfo("Saved inventory for " + steamId + " in " + worldKey + ": " + Describe(rec) + ".");
            }
            catch (Exception e) { Plugin.Log.LogError("Failed to save inventory for " + steamId + ": " + e.Message); }
        }

        private static Record HostLoad(ulong steamId, string worldKey)
        {
            string path = InventoryPath(steamId, worldKey);
            if (!File.Exists(path)) return null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    int version = br.ReadInt32();
                    if (version != FileVersion && version != 2) { Plugin.Log.LogWarning("Ignoring inventory file with version " + version + ": " + path); return null; }
                    br.ReadString();
                    var rec = new Record();
                    int n = br.ReadInt32();
                    for (int i = 0; i < n; i++) rec.Items.Add(new InventoryEntry { TechType = br.ReadInt32(), Quantity = br.ReadInt32() });
                    int m = br.ReadInt32();
                    for (int i = 0; i < m; i++) rec.Equipment.Add(new EquipmentEntry { Slot = br.ReadString(), TechType = br.ReadInt32() });
                    if (version >= 3)
                    {
                        int k = br.ReadInt32();
                        for (int i = 0; i < k; i++) rec.Powered.Add(new PoweredEntry { TechType = br.ReadInt32(), BatteryType = br.ReadInt32(), Charge = br.ReadSingle() });
                    }
                    return rec;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Failed to load inventory for " + steamId + ": " + e.Message); return null; }
        }

        // ------------------------------------------------------------------ host side

        /// <summary>Host: a joiner reports his current inventory. Stored under the host's own world key.</summary>
        public static void OnInventoryDataFromClient(ulong fromSteamId, PacketReader r)
        {
            r.ReadString(); // the joiner's idea of the world key: ignored, the host decides
            var rec = ReadRecord(r);
            string key = GetWorldKey();
            if (string.IsNullOrEmpty(key)) { Plugin.Log.LogWarning("Inventory from " + fromSteamId + " arrived but the hosted world is unknown; not saved."); return; }
            HostSave(fromSteamId, key, rec);
        }

        /// <summary>Host: a joiner's world is ready; send his record for this world (empty if he has none).</summary>
        public static void HostCheckAndSendInventory(ulong clientSteamId)
        {
            string key = GetWorldKey();
            if (key == null) { Plugin.Log.LogWarning("Inventory request from " + clientSteamId + " but the hosted world is unknown."); return; }
            var rec = HostLoad(clientSteamId, key);
            bool fresh = rec == null;
            if (fresh) rec = new Record();
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryData);
            pw.Write(key);
            WriteRecord(pw, rec);
            net.SendInventoryPacket(clientSteamId, pw);
            Plugin.Log.LogInfo(fresh
                ? "No saved inventory for " + clientSteamId + " in " + key + ": they start empty."
                : "Sent saved inventory to " + clientSteamId + " for " + key + ": " + Describe(rec) + ".");
        }

        /// <summary>Host: ask every joiner for a final report (used before the host closes the session).</summary>
        public static void HostSaveAllOnLeave()
        {
            if (net == null || !net.IsHost) return;
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryRequest);
            net.BroadcastInventoryRequest(pw);
        }

        // ------------------------------------------------------------------ client side

        /// <summary>Client: the host asked for our inventory (it is closing) — report it if it is really ours.</summary>
        public static void ClientSendInventoryToHost() { ClientSendInventoryToHost(false); }

        public static void ClientSendInventoryToHost(bool immediate)
        {
            if (net == null || !net.IsInSession || net.IsHost || !restored) return;
            if (!LocalPlayerSync.InWorld || Inventory.main == null) return;
            var rec = Snapshot();
            var pw = new PacketWriter();
            pw.Begin(PacketType.InventoryData);
            pw.Write("");
            WriteRecord(pw, rec);
            if (immediate) net.SendInventoryPacketNow(net.HostSteamId, pw);
            else net.SendInventoryPacket(net.HostSteamId, pw);
            lastSentSignature = Signature(rec);
            if (Plugin.VerboseLog.Value || immediate) Plugin.Log.LogInfo("Sent inventory to host: " + Describe(rec) + ".");
        }

        /// <summary>Client: the host sent our record for this world. Always replaces what the game gave us.</summary>
        public static void OnInventoryDataFromHost(PacketReader r)
        {
            if (restored || restoring) return;
            string key = r.ReadString();
            var rec = ReadRecord(r);
            Plugin.Log.LogInfo(rec.Count == 0
                ? "Host has no inventory for us in " + key + ": starting empty."
                : "Host sent our inventory for " + key + ": " + Describe(rec) + ".");
            Plugin.Instance.StartCoroutine(Restore(rec));
        }

        private static IEnumerator Restore(Record rec)
        {
            restoring = true;
            float deadline = Time.unscaledTime + 30f;
            while (!LocalPlayerSync.InWorld && Time.unscaledTime < deadline) yield return null;
            if (Inventory.main == null || !net.IsInSession) { restoring = false; yield break; }

            int cleared = 0, added = 0, equipped = 0;
            WorldSync.applyingRemote = true;
            try
            {
                cleared = ClearLocal();
            }
            catch (Exception e) { Plugin.Log.LogError("Clearing the host's copy of the inventory failed: " + e); }
            finally { WorldSync.applyingRemote = false; }

            foreach (var entry in rec.Items)
            {
                for (int q = 0; q < entry.Quantity; q++)
                {
                    var result = new TaskResult<GameObject>();
                    yield return CraftData.InstantiateFromPrefabAsync((TechType)entry.TechType, result, false);
                    WorldSync.applyingRemote = true;
                    try { if (AddToInventory(result.Get(), (TechType)entry.TechType)) added++; }
                    finally { WorldSync.applyingRemote = false; }
                }
            }
            int powered = 0;
            foreach (var entry in rec.Powered)
            {
                var result = new TaskResult<GameObject>();
                yield return CraftData.InstantiateFromPrefabAsync((TechType)entry.TechType, result, false);
                GameObject go = result.Get();
                bool ok;
                WorldSync.applyingRemote = true;
                try { ok = AddToInventory(go, (TechType)entry.TechType); }
                finally { WorldSync.applyingRemote = false; }
                if (!ok) continue;
                added++;
                yield return ApplyEnergy(go, entry);
                powered++;
            }
            foreach (var entry in rec.Equipment)
            {
                var result = new TaskResult<GameObject>();
                yield return CraftData.InstantiateFromPrefabAsync((TechType)entry.TechType, result, false);
                WorldSync.applyingRemote = true;
                try { if (Equip(result.Get(), entry.Slot, (TechType)entry.TechType)) equipped++; }
                finally { WorldSync.applyingRemote = false; }
            }

            Plugin.Log.LogInfo("Inventory set up: removed " + cleared + " items copied from the host, restored " + added + " items (" + powered + " with their charge) and " + equipped + " equipped.");
            restoring = false;
            restored = true;
            lastSentSignature = Signature(Snapshot());
        }

        /// <summary>Gives a restored battery its charge, or puts the right battery (and charge) back into a restored tool.</summary>
        internal static IEnumerator ApplyEnergy(GameObject go, PoweredEntry entry)
        {
            if (go == null) yield break;
            var mixin = go.GetComponent<EnergyMixin>();
            if (mixin != null)
            {
                if (entry.BatteryType == (int)TechType.None)
                {
                    if (mixin.HasItem()) { WorldSync.applyingRemote = true; try { mixin.RemoveBattery(); } finally { WorldSync.applyingRemote = false; } }
                    yield break;
                }
                var res = new TaskResult<InventoryItem>();
                yield return mixin.SetBatteryAsync((TechType)entry.BatteryType, 1f, res);
                var b = mixin.GetBattery();
                if (b != null) b.charge = Mathf.Clamp(entry.Charge, 0f, b.capacity);
                yield break;
            }
            var battery = go.GetComponent<Battery>();
            if (battery != null) battery.charge = Mathf.Clamp(entry.Charge, 0f, battery.capacity);
        }

        /// <summary>Energy state of an item, or null if it has none worth keeping.</summary>
        internal static PoweredEntry? EnergyOf(Pickupable p)
        {
            if (p == null) return null;
            var mixin = p.GetComponent<EnergyMixin>();
            if (mixin != null)
            {
                var bgo = mixin.GetBatteryGameObject();
                var b = mixin.GetBattery();
                return new PoweredEntry
                {
                    TechType = (int)p.GetTechType(),
                    BatteryType = bgo != null ? (int)CraftData.GetTechType(bgo) : (int)TechType.None,
                    Charge = b != null ? b.charge : 0f
                };
            }
            var battery = p.GetComponent<Battery>();
            if (battery != null) return new PoweredEntry { TechType = (int)p.GetTechType(), BatteryType = (int)TechType.None, Charge = battery.charge };
            return null;
        }

        /// <summary>Removes everything from our inventory and equipment (the copy of the host's we got with his save).</summary>
        private static int ClearLocal()
        {
            int n = 0;
            var container = Inventory.main.container;
            if (container != null)
            {
                var items = new List<InventoryItem>();
                foreach (InventoryItem it in container) items.Add(it);
                foreach (var it in items)
                {
                    if (it == null || it.item == null) continue;
                    container.RemoveItem(it.item, true);
                    UnityEngine.Object.Destroy(it.item.gameObject);
                    n++;
                }
            }
            var eq = Inventory.main.equipment;
            if (eq != null)
            {
                foreach (var slot in new List<string>(Equipment.slotMapping.Keys))
                {
                    InventoryItem it = null;
                    try { if (eq.GetItemInSlot(slot) != null) it = eq.RemoveItem(slot, true, false); } catch { }
                    if (it != null && it.item != null) { UnityEngine.Object.Destroy(it.item.gameObject); n++; }
                }
            }
            return n;
        }

        private static bool AddToInventory(GameObject go, TechType tt)
        {
            if (go == null) return false;
            var p = go.GetComponent<Pickupable>();
            if (p == null) { UnityEngine.Object.Destroy(go); return false; }
            go.SetActive(true);
            p.Pickup(false);
            if (Inventory.main.container.AddItem(p) == null)
            {
                Plugin.Log.LogWarning("Inventory full, could not restore " + tt);
                UnityEngine.Object.Destroy(go);
                return false;
            }
            return true;
        }

        private static bool Equip(GameObject go, string slot, TechType tt)
        {
            if (go == null) return false;
            var p = go.GetComponent<Pickupable>();
            if (p == null) { UnityEngine.Object.Destroy(go); return false; }
            go.SetActive(true);
            p.Pickup(false);
            var eq = Inventory.main.equipment;
            bool ok = false;
            try { ok = eq != null && eq.GetItemInSlot(slot) == null && eq.AddItem(slot, new InventoryItem(p), true); }
            catch (Exception e) { Plugin.Log.LogWarning("Could not equip " + tt + " in " + slot + ": " + e.Message); }
            if (ok) return true;
            // Slot gone or taken: keep the item in the inventory rather than losing it.
            if (Inventory.main.container.AddItem(p) != null) return true;
            UnityEngine.Object.Destroy(go);
            return false;
        }

        // ------------------------------------------------------------------ snapshot

        private static Record Snapshot()
        {
            var rec = new Record();
            var counts = new Dictionary<int, int>();
            if (Inventory.main != null && Inventory.main.container != null)
            {
                foreach (InventoryItem it in Inventory.main.container)
                {
                    if (it == null || it.item == null) continue;
                    var energy = EnergyOf(it.item);
                    if (energy.HasValue) { rec.Powered.Add(energy.Value); continue; }
                    int tt = (int)it.item.GetTechType();
                    int c; counts.TryGetValue(tt, out c); counts[tt] = c + 1;
                }
            }
            foreach (var kv in counts) rec.Items.Add(new InventoryEntry { TechType = kv.Key, Quantity = kv.Value });
            rec.Items.Sort((a, b) => a.TechType.CompareTo(b.TechType));
            rec.Powered.Sort((a, b) => a.TechType != b.TechType ? a.TechType.CompareTo(b.TechType) : a.Charge.CompareTo(b.Charge));

            var eq = Inventory.main != null ? Inventory.main.equipment : null;
            if (eq != null)
            {
                foreach (var slot in Equipment.slotMapping.Keys)
                {
                    TechType tt = TechType.None;
                    try { tt = eq.GetTechTypeInSlot(slot); } catch { }
                    if (tt != TechType.None) rec.Equipment.Add(new EquipmentEntry { Slot = slot, TechType = (int)tt });
                }
                rec.Equipment.Sort((a, b) => string.CompareOrdinal(a.Slot, b.Slot));
            }
            return rec;
        }

        private static string Signature(Record rec)
        {
            var sb = new StringBuilder();
            foreach (var e in rec.Items) sb.Append(e.TechType).Append('x').Append(e.Quantity).Append(';');
            sb.Append('|');
            foreach (var e in rec.Equipment) sb.Append(e.Slot).Append('=').Append(e.TechType).Append(';');
            sb.Append('|');
            // Charge to the nearest whole unit: a battery draining while a tool is used re-sends at most every 2 s.
            foreach (var e in rec.Powered) sb.Append(e.TechType).Append('/').Append(e.BatteryType).Append('@').Append(Mathf.RoundToInt(e.Charge)).Append(';');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ update

        public static void Update()
        {
            if (net == null || !net.IsInSession || net.IsHost) return;
            if (!LocalPlayerSync.InWorld)
            {
                // Out of the world (e.g. re-downloading it with the resend key): the next load hands us the host's
                // inventory again, so ask for our record again once we are back in.
                if (!restoring && (requested || restored)) { requested = false; restored = false; lastSentSignature = null; }
                return;
            }

            // Ask the host for our record once our world has settled.
            if (!requested && WorldSync.WorldSettled)
            {
                requested = true;
                requestedAt = Time.unscaledTime;
                var pw = new PacketWriter();
                pw.Begin(PacketType.InventoryRequest);
                net.SendInventoryPacket(net.HostSteamId, pw);
                Plugin.Log.LogInfo("Asked the host for our inventory in this world.");
            }

            // A host on an older version never answers when it has nothing saved: treat silence as "nothing saved".
            if (requested && !restored && !restoring && Time.unscaledTime - requestedAt > HostReplyTimeout)
            {
                Plugin.Log.LogWarning("Host did not send an inventory record; starting empty.");
                Plugin.Instance.StartCoroutine(Restore(new Record()));
            }

            // Keep the host's record current so it is already saved when we leave.
            if (restored && Time.unscaledTime >= nextSyncCheck)
            {
                nextSyncCheck = Time.unscaledTime + SyncInterval;
                string sig = Signature(Snapshot());
                if (sig != lastSentSignature) ClientSendInventoryToHost(false);
            }
        }

        /// <summary>Reset state when leaving a session.</summary>
        public static void Reset()
        {
            requested = false;
            restoring = false;
            restored = false;
            lastSentSignature = null;
        }
    }

    /// <summary>Compact representation of an inventory item type and its count.</summary>
    public struct InventoryEntry
    {
        public int TechType;
        public int Quantity;
    }

    /// <summary>An item with energy: a battery (its charge) or a tool with a battery slot (which battery, its charge).</summary>
    public struct PoweredEntry
    {
        public int TechType;
        public int BatteryType;   // for tools: the battery inside (None = empty); for batteries: None
        public float Charge;      // absolute charge of the battery (the item itself, or the one inside the tool)
    }

    /// <summary>An equipped item: which slot and what is in it.</summary>
    public struct EquipmentEntry
    {
        public string Slot;
        public int TechType;
    }
}
