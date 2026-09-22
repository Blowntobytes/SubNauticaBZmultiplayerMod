using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Phase 2/3 first slice: live world events. Every machine reports what its own player does (Harmony
    /// postfixes on the game's own methods); the host relays to everyone else and also applies them, so its save
    /// stays the truth. Objects are addressed by the game's UniqueIdentifier GUIDs, which are shared because the
    /// joiner loaded the host's save.
    ///
    ///   Clock         host -> clients, every few seconds (DayNightCycle.timePassed)
    ///   ItemPickup    id                                      (Pickupable.Pickup)
    ///   ItemDrop      techType, id, position, rotation        (Pickupable.Drop)
    ///   Placed        techType, id, position, rotation, parent id   (Builder.TryPlace, non-base pieces)
    ///   Progress      id, amount                              (Constructable.Construct, throttled)
    /// </summary>
    public static class WorldSync
    {
        private static SteamNet net;
        internal static bool applyingRemote;         // suppress our own hooks while applying a remote event
        private static float nextClockSend;
        private static readonly Dictionary<string, float> lastProgressSent = new Dictionary<string, float>();
        private static readonly HashSet<string> pendingSpawns = new HashSet<string>();

        public static bool BaseHullWarned;

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;
            Patch(harmony, typeof(Pickupable), "Pickup", new[] { typeof(bool) }, "PickupPostfix");
            Patch(harmony, typeof(Pickupable), "Drop", new[] { typeof(Vector3), typeof(Vector3), typeof(bool) }, "DropPostfix");
            Patch(harmony, typeof(Builder), "TryPlace", Type.EmptyTypes, "TryPlacePostfix");
            Patch(harmony, typeof(Constructable), "Construct", Type.EmptyTypes, "ConstructPostfix");
            // Deconstruction of placed modules runs as a coroutine; hook its MoveNext to stream progress and the final removal.
            try
            {
                var decon = AccessTools.Method(typeof(Constructable), "DeconstructAsync");
                var mn = decon != null ? AccessTools.EnumeratorMoveNext(decon) : null;
                if (mn == null) Plugin.Log.LogWarning("WorldSync: Constructable.DeconstructAsync not found; module deconstruction will not sync.");
                else harmony.Patch(mn, postfix: new HarmonyMethod(typeof(WorldSync), "DeconstructMoveNextPostfix"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSync: deconstruct hook failed: " + e.Message); }
            // StorageContainer.Awake -> CreateContainer assigns the private 'container' setter directly (SetContainer is only
            // used by a couple of vehicles), so hook the setter itself to catch every locker, pod and wall storage.
            // Loading a save (and streaming a cell back in) re-adds every stored item through onAddItem; that is the
            // game restoring state, not a player action, so mute our hooks for the whole of each restore path.
            var deser = AccessTools.Method(typeof(StorageContainer), "OnProtoDeserializeObjectTree");
            if (deser != null) harmony.Patch(deser, prefix: new HarmonyMethod(typeof(WorldSync), "SuppressBegin"), postfix: new HarmonyMethod(typeof(WorldSync), "SuppressEnd"));
            var transfer = AccessTools.Method(typeof(StorageHelper), "TransferItems", new[] { typeof(GameObject), typeof(ItemsContainer) });
            if (transfer != null) harmony.Patch(transfer, prefix: new HarmonyMethod(typeof(WorldSync), "SuppressBegin"), postfix: new HarmonyMethod(typeof(WorldSync), "SuppressEnd"));
            try
            {
                var restore = AccessTools.Method(typeof(StorageHelper), "RestoreItemsAsync");
                var restoreStep = restore != null ? AccessTools.EnumeratorMoveNext(restore) : null;
                if (restoreStep != null) harmony.Patch(restoreStep, prefix: new HarmonyMethod(typeof(WorldSync), "SuppressBegin"), postfix: new HarmonyMethod(typeof(WorldSync), "SuppressEnd"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSync: item-restore hook failed: " + e.Message); }

            // Breakable outcrops: the smash itself, then the (random) drops it spawns.
            Patch(harmony, typeof(BreakableResource), "BreakIntoResources", Type.EmptyTypes, "BreakPostfix");
            try
            {
                var spawn = AccessTools.Method(typeof(BreakableResource), "SpawnResourceFromPrefab", new[] { typeof(UnityEngine.AddressableAssets.AssetReferenceGameObject), typeof(Vector3), typeof(Vector3) });
                var moveNext = spawn != null ? AccessTools.EnumeratorMoveNext(spawn) : null;
                if (moveNext == null) Plugin.Log.LogWarning("WorldSync: BreakableResource spawn coroutine not found; outcrop drops will not sync.");
                else harmony.Patch(moveNext, postfix: new HarmonyMethod(typeof(WorldSync), "SpawnResourceMoveNextPostfix"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSync: outcrop drop hook failed: " + e.Message); }

            var setter = AccessTools.PropertySetter(typeof(StorageContainer), "container");
            if (setter == null) Plugin.Log.LogWarning("WorldSync: StorageContainer.container setter not found; storage will not sync.");
            else harmony.Patch(setter, postfix: new HarmonyMethod(typeof(WorldSync), "ContainerSetPostfix"));
        }

        private static void Patch(Harmony h, Type type, string method, Type[] args, string postfix)
        {
            var m = AccessTools.Method(type, method, args);
            if (m == null) { Plugin.Log.LogWarning("WorldSync: " + type.Name + "." + method + " not found; that event will not sync."); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(WorldSync), postfix));
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Id of the object or its nearest ancestor. Walks transforms by hand because GetComponentInParent skips
        /// inactive objects, and items inside containers (or mid-pickup) are inactive.
        /// </summary>
        internal static string IdOf(Component c)
        {
            if (c == null) return null;
            for (var t = c.transform; t != null; t = t.parent)
            {
                var uid = t.GetComponent<UniqueIdentifier>();
                if (uid != null) return uid.Id;
            }
            return null;
        }

        internal static GameObject Find(string id)
        {
            UniqueIdentifier uid;
            if (string.IsNullOrEmpty(id) || !UniqueIdentifier.TryGetIdentifier(id, out uid) || uid == null) return null;
            return uid.gameObject;
        }

        private static int suppressDepth; // >0 while the game itself is (de)serializing containers
        internal static bool CanSend { get { return net != null && net.IsInSession && !applyingRemote && suppressDepth == 0 && WorldSettled; } }

        private static float worldReadyAt = -1f;

        /// <summary>
        /// True once the local world has been in play for a few seconds. A world that has just finished loading is still
        /// restoring saved containers, and those restores must never be broadcast as if the player had just done them.
        /// </summary>
        internal static bool WorldSettled
        {
            get
            {
                if (!LocalPlayerSync.InWorld) { worldReadyAt = -1f; return false; }
                if (worldReadyAt < 0f) worldReadyAt = Time.unscaledTime + 10f;
                return Time.unscaledTime >= worldReadyAt;
            }
        }

        // Items we have just touched while applying a remote event (or destroyed ourselves). Unity defers Destroy to the
        // end of the frame, so a container's onRemoveItem for them fires outside the applyingRemote guard; without this
        // the echo goes back out and the two games bounce the same items back and forth forever.
        private static readonly Dictionary<string, float> mutedItems = new Dictionary<string, float>();

        internal static void MuteItem(string id)
        {
            if (!string.IsNullOrEmpty(id)) mutedItems[id] = Time.unscaledTime + 5f;
        }

        private static bool IsMuted(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            float until;
            if (!mutedItems.TryGetValue(id, out until)) return false;
            if (Time.unscaledTime < until) return true;
            mutedItems.Remove(id);
            return false;
        }

        // ------------------------------------------------------------------ clock (host only)

        private static bool inventoryReided;

        public static void Update()
        {
            if (net == null || !net.IsInSession) { inventoryReided = false; return; }
            if (!LocalPlayerSync.InWorld) { inventoryReided = false; return; }
            if (!net.IsHost && !inventoryReided && Inventory.main != null && Player.main != null) ReidLocalInventory();
            if (!net.IsHost) return;
            if (Time.unscaledTime < nextClockSend) return;
            nextClockSend = Time.unscaledTime + 5f;
            if (DayNightCycle.main == null || !LocalPlayerSync.InWorld) return;
            net.SendClock(DayNightCycle.main.timePassed);
        }

        /// <summary>
        /// A joiner loads the host's save, so his starting inventory is a copy of the host's with the same item ids.
        /// Give those copies fresh ids so a host drop/pickup/chest event is never mistaken for one of the joiner's own items.
        /// </summary>
        private static void ReidLocalInventory()
        {
            inventoryReided = true;
            int n = 0;
            try
            {
                var uids = Inventory.main.GetComponentsInChildren<UniqueIdentifier>(true);
                foreach (var u in uids)
                {
                    if (u == null || u.gameObject == Inventory.main.gameObject) continue;
                    u.Id = Guid.NewGuid().ToString();
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Inventory re-id failed: " + e.Message); }
            Plugin.Log.LogInfo("Gave " + n + " inventory items fresh ids (joiner copy of the host's inventory).");
        }

        /// <summary>True if this object lives in the local player's own inventory or equipment (never touched by remote events).</summary>
        private static bool IsLocalInventory(GameObject go)
        {
            return go != null && Inventory.main != null && go.transform.IsChildOf(Inventory.main.transform);
        }

        public static void OnClock(double hostTime)
        {
            if (DayNightCycle.main == null || !LocalPlayerSync.InWorld) return;
            double drift = hostTime - DayNightCycle.main.timePassed;
            if (Math.Abs(drift) > 1.5)
            {
                DayNightCycle.main.SetTimePassed((float)hostTime);
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Clock resynced by " + drift.ToString("F1") + " s");
            }
        }

        // ------------------------------------------------------------------ pickup / drop

        private static void PickupPostfix(Pickupable __instance)
        {
            if (!CanSend) return;
            string id = IdOf(__instance);
            if (id == null) return;
            net.SendItemPickup(id);
            Plugin.Log.LogInfo("Pickup sent: " + __instance.GetTechType() + " [" + id + "]");
        }

        public static void OnItemPickup(string id)
        {
            var go = Find(id);
            if (go == null || IsLocalInventory(go)) { Plugin.Log.LogInfo("Pickup received [" + id + "]: " + (go == null ? "not in this world" : "ignored, in local inventory")); return; }
            Plugin.Log.LogInfo("Pickup received: " + go.name + " [" + id + "]");
            MuteItem(id);
            applyingRemote = true;
            try { UnityEngine.Object.Destroy(go); }
            finally { applyingRemote = false; }
        }

        private static void DropPostfix(Pickupable __instance, Vector3 dropPosition)
        {
            if (!CanSend || __instance == null) return;
            string id = IdOf(__instance);
            if (id == null) return;
            TechType tt = __instance.GetTechType();
            net.SendItemDrop((int)tt, id, __instance.transform.position, __instance.transform.rotation);
            Plugin.Log.LogInfo("Drop sent: " + tt + " [" + id + "]");
        }

        public static void OnItemDrop(int techType, string id, Vector3 pos, Quaternion rot)
        {
            if (pendingSpawns.Contains(id)) return;
            var existing = Find(id);
            if (existing != null)
            {
                if (!IsLocalInventory(existing)) return; // already in the world
                var u = existing.GetComponent<UniqueIdentifier>(); if (u != null) u.Id = Guid.NewGuid().ToString();
            }
            pendingSpawns.Add(id);
            Plugin.Log.LogInfo("Drop received: " + (TechType)techType + " [" + id + "]");
            Plugin.Instance.StartCoroutine(SpawnDropped((TechType)techType, id, pos, rot));
        }

        private static IEnumerator SpawnDropped(TechType tt, string id, Vector3 pos, Quaternion rot)
        {
            var result = new TaskResult<GameObject>();
            yield return CraftData.InstantiateFromPrefabAsync(tt, result, false);
            pendingSpawns.Remove(id);
            var go = result.Get();
            if (go == null) { Plugin.Log.LogWarning("Could not spawn dropped " + tt); yield break; }
            applyingRemote = true;
            try
            {
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = id;
                go.transform.position = pos;
                go.transform.rotation = rot;
                go.SetActive(true);
                var p = go.GetComponent<Pickupable>();
                if (p != null) p.Drop(pos, Vector3.zero, false);
            }
            finally { applyingRemote = false; }
        }

        private static void SuppressBegin() { suppressDepth++; }
        private static void SuppressEnd() { if (suppressDepth > 0) suppressDepth--; }

        // ------------------------------------------------------------------ breakable outcrops

        private static void BreakPostfix(BreakableResource __instance)
        {
            if (!CanSend || __instance == null) return;
            string id = IdOf(__instance);
            if (id == null) return;
            net.SendResourceBroken(id);
            Plugin.Log.LogInfo("Outcrop broken sent: " + __instance.name + " [" + id + "]");
        }

        public static void OnResourceBroken(string id)
        {
            var go = Find(id);
            if (go == null) { Plugin.Log.LogInfo("Outcrop broken received [" + id + "]: not in this world"); return; }
            Plugin.Log.LogInfo("Outcrop broken received: " + go.name + " [" + id + "]");
            applyingRemote = true;
            try
            {
                var br = go.GetComponent<BreakableResource>();
                // Play the effects but do not let it roll its own random drops: the breaker's drops arrive as ItemDrop.
                if (br != null) { go.SendMessage("OnBreakResource", null, SendMessageOptions.DontRequireReceiver); go.BroadcastMessage("OnKill", SendMessageOptions.DontRequireReceiver); }
                UnityEngine.Object.Destroy(go);
            }
            finally { applyingRemote = false; }
        }

        private static readonly Dictionary<object, bool> spawnSeen = new Dictionary<object, bool>();

        private static void SpawnResourceMoveNextPostfix(object __instance, bool __result)
        {
            if (__result || __instance == null || !CanSend) return; // only when the coroutine finishes
            try
            {
                var f = __instance.GetType().GetField("<result>5__2", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var task = f != null ? f.GetValue(__instance) as CoroutineTask<GameObject> : null;
                var go = task != null ? task.GetResult() : null;
                if (go == null) return;
                var p = go.GetComponent<Pickupable>();
                string id = IdOf(go.transform);
                if (p == null || id == null) return;
                net.SendItemDrop((int)p.GetTechType(), id, go.transform.position, go.transform.rotation);
                Plugin.Log.LogInfo("Outcrop drop sent: " + p.GetTechType() + " [" + id + "]");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Outcrop drop hook: " + e.Message); }
        }

        // ------------------------------------------------------------------ building (placeables)

        private static void TryPlacePostfix(bool __result)
        {
            if (!__result || !CanSend) return;
            // Base hull pieces merge into the Base object and need their own sync path (next slice).
            var ghost = Traverse.Create(typeof(Builder)).Field("ghostModel").GetValue<GameObject>();
            Vector3 placePos = Traverse.Create(typeof(Builder)).Field("placePosition").GetValue<Vector3>();
            Quaternion placeRot = Traverse.Create(typeof(Builder)).Field("placeRotation").GetValue<Quaternion>();
            TechType tt = Traverse.Create(typeof(Builder)).Property("lastTechType").GetValue<TechType>();

            if (ghost != null && ghost.GetComponent<BaseGhost>() != null)
            {
                
                return;
            }

            // The freshly placed constructable sits exactly at placePosition with 0 progress.
            Constructable best = null; float bestDist = 0.05f;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Constructable>())
            {
                if (c.constructedAmount > 0.001f || c._constructed) continue;
                float d = Vector3.Distance(c.transform.position, placePos);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            if (best == null) { Plugin.Log.LogWarning("Placed " + tt + " but could not find the new constructable to sync."); return; }
            string id = IdOf(best);
            if (id == null) return;
            var parentUid = best.transform.parent != null ? best.transform.parent.GetComponentInParent<UniqueIdentifier>() : null;
            net.SendPlaced((int)tt, id, placePos, placeRot, parentUid != null ? parentUid.Id : "");
        }

        public static void OnPlaced(int techType, string id, Vector3 pos, Quaternion rot, string parentId)
        {
            if (Find(id) != null || pendingSpawns.Contains(id)) return;
            pendingSpawns.Add(id);
            Plugin.Instance.StartCoroutine(SpawnPlaced((TechType)techType, id, pos, rot, parentId));
        }

        private static IEnumerator SpawnPlaced(TechType tt, string id, Vector3 pos, Quaternion rot, string parentId)
        {
            var task = CraftData.GetPrefabForTechTypeAsync(tt, false);
            yield return task;
            pendingSpawns.Remove(id);
            var prefab = task.GetResult();
            if (prefab == null) { Plugin.Log.LogWarning("No prefab for placed " + tt); yield break; }
            applyingRemote = true;
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab);
                var parent = Find(parentId);
                if (parent != null)
                {
                    var sub = parent.GetComponentInParent<SubRoot>();
                    go.transform.parent = sub != null ? sub.GetModulesRoot() : parent.transform;
                }
                go.transform.position = pos;
                go.transform.rotation = rot;
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = id;
                var c = go.GetComponentInParent<Constructable>();
                if (c != null)
                {
                    c.SetState(false, false);
                    c.SetIsInside(parent != null);
                }
                go.SetActive(true);
            }
            finally { applyingRemote = false; }
        }

        // ------------------------------------------------------------------ storage containers (lockers, chests, vehicle storage)

        private static readonly HashSet<ItemsContainer> hooked = new HashSet<ItemsContainer>();

        private static void ContainerSetPostfix(StorageContainer __instance, ItemsContainer value)
        {
            if (value == null || hooked.Contains(value)) return;
            hooked.Add(value);
            var sc = __instance;
            value.onAddItem += item => OnLocalContainerAdd(sc, item);
            value.onRemoveItem += item => OnLocalContainerRemove(sc, item);
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Hooked storage " + sc.name + " (" + IdOf(sc) + ")");
        }

        private static void OnLocalContainerAdd(StorageContainer sc, InventoryItem item)
        {
            if (!CanSend || sc == null || item == null || item.item == null) return;
            string cid = ContainerKey(sc); string iid = IdOf(item.item);
            if (cid == null || iid == null || IsMuted(iid)) return;
            net.SendContainerAdd(cid, iid, (int)item.item.GetTechType());
            Plugin.Log.LogInfo("Storage add sent: " + item.item.GetTechType() + " [" + iid + "] -> " + sc.name + " [" + cid + "]");
        }

        private static void OnLocalContainerRemove(StorageContainer sc, InventoryItem item)
        {
            if (!CanSend || sc == null || item == null || item.item == null) return;
            string cid = ContainerKey(sc); string iid = IdOf(item.item);
            if (cid == null || iid == null || IsMuted(iid)) return;
            net.SendContainerRemove(cid, iid);
            Plugin.Log.LogInfo("Storage remove sent: " + item.item.GetTechType() + " [" + iid + "] <- " + sc.name + " [" + cid + "]");
        }

        /// <summary>
        /// The entity a container belongs to: its NEAREST ancestor identifier (the locker itself, the pod, the vehicle),
        /// never the base it happens to stand in - a base's child order differs per machine, so indexing inside a base
        /// addressed the wrong locker on the other side.
        /// </summary>
        private static UniqueIdentifier OwnerOf(Component c)
        {
            if (c == null) return null;
            for (var t = c.transform; t != null; t = t.parent)
            {
                var u = t.GetComponent<UniqueIdentifier>();
                // Skip ChildObjectIdentifier: StorageContainer.storageRoot is one, and its id is generated per
                // machine, so addressing a container by it named something the other side had never heard of.
                if (u != null && !(u is ChildObjectIdentifier)) return u;
            }
            return null;
        }

        /// <summary>Containers that belong to this entity itself (not to entities nested inside it), in prefab order.</summary>
        private static List<StorageContainer> ContainersOf(UniqueIdentifier owner)
        {
            var list = new List<StorageContainer>();
            if (owner == null) return list;
            foreach (var c in owner.GetComponentsInChildren<StorageContainer>(true))
                if (OwnerOf(c) == owner) list.Add(c);
            return list;
        }

        /// <summary>
        /// Container address: the owning entity's id, plus an index when that one entity has several containers
        /// ("id#1"). The index is stable because it is an index inside a single prefab, identical on every machine.
        /// </summary>
        private static string ContainerKey(StorageContainer sc)
        {
            var owner = OwnerOf(sc);
            if (owner == null) return null;
            var all = ContainersOf(owner);
            int idx = all.IndexOf(sc);
            if (idx < 0)
            {
                // Should not happen: the container is not among its own owner's containers. Addressing it as index 0
                // would quietly fill the wrong chest, so refuse to address it at all.
                Plugin.Log.LogWarning("Container " + sc.name + " is not listed under its owner " + owner.Id + "; not syncing it.");
                return null;
            }
            return idx > 0 ? owner.Id + "#" + idx : owner.Id;
        }

        private static StorageContainer FindContainer(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int idx = 0; string id = key;
            int hash = key.IndexOf('#');
            if (hash >= 0) { id = key.Substring(0, hash); int.TryParse(key.Substring(hash + 1), out idx); }
            var go = Find(id);
            if (go == null) { Plugin.Log.LogWarning("Container lookup [" + key + "]: no object with id " + id + " in this world."); return null; }
            var uid = go.GetComponent<UniqueIdentifier>();
            var all = ContainersOf(uid);
            if (idx < all.Count) return all[idx];
            // Falling back to "any container under this object" used to pick a container belonging to a nested entity,
            // which silently put items in the wrong place. Report the mismatch instead.
            Plugin.Log.LogWarning("Container lookup [" + key + "]: '" + go.name + "' has " + all.Count
                                + " container(s), index " + idx + " requested. The two worlds disagree about this object.");
            return null;
        }

        public static void OnContainerAdd(string containerId, string itemId, int techType)
        {
            var sc = FindContainer(containerId);
            if (sc == null || sc.container == null) { Plugin.Log.LogWarning("Container " + containerId + " not found for add"); return; }
            foreach (InventoryItem it in sc.container) if (it.item != null && IdOf(it.item) == itemId) return; // already there
            if (pendingSpawns.Contains(itemId)) return;
            pendingSpawns.Add(itemId);
            MuteItem(itemId);
            Plugin.Log.LogInfo("Storage add received: " + (TechType)techType + " [" + itemId + "] -> " + sc.name + " [" + containerId + "]");
            Plugin.Instance.StartCoroutine(SpawnIntoContainer(sc, (TechType)techType, itemId));
        }

        private static IEnumerator SpawnIntoContainer(StorageContainer sc, TechType tt, string itemId)
        {
            var result = new TaskResult<GameObject>();
            yield return CraftData.InstantiateFromPrefabAsync(tt, result, false);
            pendingSpawns.Remove(itemId);
            var go = result.Get();
            if (go == null || sc == null || sc.container == null) { if (go != null) UnityEngine.Object.Destroy(go); yield break; }
            applyingRemote = true;
            try
            {
                // If the same item already exists in the world (e.g. lying on the ground), remove that copy first.
                var existing = Find(itemId);
                if (existing != null && existing != go)
                {
                    if (IsLocalInventory(existing)) { var eu = existing.GetComponent<UniqueIdentifier>(); if (eu != null) eu.Id = Guid.NewGuid().ToString(); }
                    else UnityEngine.Object.Destroy(existing); // its container's onRemoveItem fires next frame - muted above
                }
                MuteItem(itemId);
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = itemId;
                var p = go.GetComponent<Pickupable>();
                if (p == null) { UnityEngine.Object.Destroy(go); yield break; }
                go.SetActive(true);
                p.Pickup(false); // the game's own "now in an inventory" state: deactivated, unregistered from the world
                if (sc.container.AddItem(p) == null) { Plugin.Log.LogWarning("Container full, could not add " + tt); UnityEngine.Object.Destroy(go); }
            }
            finally { applyingRemote = false; }
        }

        public static void OnContainerRemove(string containerId, string itemId)
        {
            var sc = FindContainer(containerId);
            if (sc == null || sc.container == null) return;
            Pickupable found = null;
            foreach (InventoryItem it in sc.container) if (it.item != null && IdOf(it.item) == itemId) { found = it.item; break; }
            if (found == null) return;
            Plugin.Log.LogInfo("Storage remove received: " + found.GetTechType() + " [" + itemId + "] <- " + sc.name + " [" + containerId + "]");
            MuteItem(itemId);
            applyingRemote = true;
            try
            {
                sc.container.RemoveItem(found, true);
                UnityEngine.Object.Destroy(found.gameObject); // it is now in the remote player's inventory
            }
            finally { applyingRemote = false; }
        }

        private static void ConstructPostfix(Constructable __instance, bool __result)
        {
            if (!CanSend || __instance == null) return;
            string id = IdOf(__instance);
            if (id == null) return;
            float amount = __instance._constructed ? 1f : __instance.constructedAmount;
            float last;
            bool done = amount >= 0.999f;
            if (!done && lastProgressSent.TryGetValue(id, out last) && amount - last < 0.1f) return;
            lastProgressSent[id] = amount;
            if (done) lastProgressSent.Remove(id);
            net.SendProgress(id, amount);
        }

        private static readonly Dictionary<int, float> deconLastSent = new Dictionary<int, float>();

        private static void DeconstructMoveNextPostfix(object __instance)
        {
            if (!CanSend || __instance == null) return;
            try
            {
                var f = __instance.GetType().GetField("<>4__this", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var c = f != null ? f.GetValue(__instance) as Constructable : null;
                if (c == null) return;
                string id = IdOf(c);
                if (id == null) return;
                float amount = c.constructedAmount;
                int key = c.GetInstanceID();
                float last;
                bool final = amount <= 0.001f;
                if (!final && deconLastSent.TryGetValue(key, out last) && Time.unscaledTime - last < 0.1f) return;
                deconLastSent[key] = Time.unscaledTime;
                if (final)
                {
                    deconLastSent.Remove(key);
                    net.SendItemPickup(id); // receivers destroy the object
                    Plugin.Log.LogInfo("Deconstructed sent: " + c.techType + " [" + id + "]");
                }
                else net.SendProgress(id, amount);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Deconstruct hook: " + e.Message); }
        }

        public static void OnProgress(string id, float amount)
        {
            var go = Find(id);
            if (go == null) return;
            var c = go.GetComponentInParent<Constructable>();
            if (c == null) return;
            applyingRemote = true;
            try
            {
                if (amount >= 0.999f) { if (!c._constructed) c.SetState(true, true); }
                else
                {
                    if (c._constructed) c.SetState(false, false); // being deconstructed: back to the ghost look
                    c.constructedAmount = amount;
                    Traverse.Create(c).Method("UpdateMaterial").GetValue();
                }
            }
            finally { applyingRemote = false; }
        }
    }
}
