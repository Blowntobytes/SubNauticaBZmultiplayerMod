using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Base hull sync (corridors, rooms, hatches, windows, ladders, moonpools...).
    /// Instead of replicating the ghost/placement logic, every time a hull piece is finished (or deconstructed) the
    /// whole shape of that base - the Base component's own save data - is sent, and the receivers deserialize it into
    /// their copy of the base and rebuild the geometry, exactly as the game does when loading a save.
    /// Only finished pieces are shown to others; the ghost stage stays local to the builder.
    /// </summary>
    public static class BaseSync
    {
        private static SteamNet net;

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;
            var finish = AccessTools.Method(typeof(BaseGhost), "Finish");
            if (finish == null) Plugin.Log.LogWarning("BaseSync: BaseGhost.Finish not found; base pieces will not sync.");
            else harmony.Patch(finish, postfix: new HarmonyMethod(typeof(BaseSync), "FinishPostfix"));

            // Guard: a Base whose arrays were never allocated (mid-deserialization) must not run Update, it only throws.
            var upd = AccessTools.Method(typeof(Base), "Update");
            if (upd != null) harmony.Patch(upd, prefix: new HarmonyMethod(typeof(BaseSync), "BaseUpdatePrefix"));

            var decon = AccessTools.Method(typeof(BaseDeconstructable), "Deconstruct");
            if (decon == null) Plugin.Log.LogWarning("BaseSync: BaseDeconstructable.Deconstruct not found; base deconstruction will not sync.");
            else harmony.Patch(decon, prefix: new HarmonyMethod(typeof(BaseSync), "DeconstructPrefix"), postfix: new HarmonyMethod(typeof(BaseSync), "DeconstructPostfix"));
        }

        private static readonly AccessTools.FieldRef<Base, BaseCellLighting[]> lightingRef = AccessTools.FieldRefAccess<Base, BaseCellLighting[]>("cellLighting");

        private static bool BaseUpdatePrefix(Base __instance)
        {
            return lightingRef(__instance) != null;
        }

        private static float nextAudit;
        private static readonly System.Collections.Generic.HashSet<int> auditReported = new System.Collections.Generic.HashSet<int>();

        /// <summary>Diagnostic: report any live Base whose arrays were never allocated (they spam NREs from Base.Update).</summary>
        public static void Update()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld || Time.unscaledTime < nextAudit) return;
            nextAudit = Time.unscaledTime + 5f;
            try
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Base>())
                {
                    if (b == null || auditReported.Contains(b.GetInstanceID())) continue;
                    var lighting = Traverse.Create(b).Field("cellLighting").GetValue<BaseCellLighting[]>();
                    if (lighting != null) continue;
                    auditReported.Add(b.GetInstanceID());
                    var parent = b.transform.parent;
                    Plugin.Log.LogWarning("Base '" + b.name + "' (ghost " + b.isGhost + ", id " + WorldSync.IdOf(b) + ", parent " + (parent != null ? parent.name : "none") + ", active " + b.gameObject.activeInHierarchy + ") has no allocated arrays");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Base audit: " + e.Message); }
        }

        // ------------------------------------------------------------------ local events

        private static void FinishPostfix(BaseGhost __instance)
        {
            if (!WorldSync.CanSend || __instance == null) return;
            var b = Traverse.Create(__instance).Field("targetBase").GetValue<Base>();
            if (b == null) return;
            SendState(b, "piece finished");
        }

        private struct DeconState { public Base Base; public string Id; }

        private static void DeconstructPrefix(BaseDeconstructable __instance, out DeconState __state)
        {
            __state = new DeconState();
            if (__instance == null) return;
            __state.Base = __instance.GetComponentInParent<Base>();
            __state.Id = WorldSync.IdOf(__state.Base);
        }

        private static void DeconstructPostfix(DeconState __state)
        {
            if (!WorldSync.CanSend || __state.Id == null) return;
            if (__state.Base == null)
            {
                net.SendBaseRemoved(__state.Id);
                Plugin.Log.LogInfo("Base removed sent [" + __state.Id + "]");
                return;
            }
            // The deconstructed piece has been cut out of the base already; send the new shape after this frame so the
            // base has finished its own rebuild.
            Plugin.Instance.StartCoroutine(SendNextFrame(__state.Base, "piece deconstructed"));
        }

        private static System.Collections.IEnumerator SendNextFrame(Base b, string why)
        {
            yield return null;
            if (b == null) { yield break; }
            if (WorldSync.CanSend) SendState(b, why);
        }

        private static void SendState(Base b, string why)
        {
            string id = WorldSync.IdOf(b);
            if (id == null) { Plugin.Log.LogWarning("Base has no id; cannot sync " + why); return; }
            byte[] data;
            try { data = Serialize(b); }
            catch (Exception e) { Plugin.Log.LogWarning("Base serialize failed: " + e.Message); return; }
            net.SendBaseState(id, true, b.transform.position, b.transform.rotation, data);
            Plugin.Log.LogInfo("Base state sent (" + why + ") [" + id + "] " + data.Length + " bytes");
        }

        // Hand-rolled shape serialization: no game serializer involved, so nothing can have side effects on the live base.
        private static byte[] Serialize(Base b)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(b.baseShape.x); w.Write(b.baseShape.y); w.Write(b.baseShape.z);
                w.Write(b.cellOffset.x); w.Write(b.cellOffset.y); w.Write(b.cellOffset.z);
                w.Write(b.anchor.x); w.Write(b.anchor.y); w.Write(b.anchor.z);
                WriteBytes(w, b.cells == null ? null : Array.ConvertAll(b.cells, c => (byte)c));
                WriteBytes(w, b.faces == null ? null : Array.ConvertAll(b.faces, f => (byte)f));
                WriteBytes(w, b.links);
                WriteBytes(w, b.masks);
                WriteBools(w, b.isGlass);
                WriteBools(w, b.unpowered);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteBytes(BinaryWriter w, byte[] a) { if (a == null) { w.Write(-1); return; } w.Write(a.Length); w.Write(a); }
        private static void WriteBools(BinaryWriter w, bool[] a) { if (a == null) { w.Write(-1); return; } w.Write(a.Length); foreach (var v in a) w.Write(v); }
        private static byte[] ReadBytes(BinaryReader r) { int n = r.ReadInt32(); return n < 0 ? null : r.ReadBytes(n); }
        private static bool[] ReadBools(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) return null; var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = r.ReadBoolean(); return a; }

        // ------------------------------------------------------------------ remote events

        public static void OnBaseState(string baseId, bool mayCreate, Vector3 pos, Quaternion rot, byte[] data)
        {
            if (!LocalPlayerSync.InWorld) return;
            var go = WorldSync.Find(baseId);
            Base b = go != null ? go.GetComponent<Base>() : null;
            WorldSync.applyingRemote = true;
            try
            {
                if (b == null)
                {
                    if (!mayCreate) { Plugin.Log.LogWarning("Base " + baseId + " not found"); return; }
                    var prefab = BaseGhost.GetBasePrefab();
                    if (prefab == null) { Plugin.Log.LogWarning("Base prefab not loaded yet; cannot create base " + baseId); return; }
                    if (go != null) UnityEngine.Object.Destroy(go);
                    go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    var uid = go.GetComponent<UniqueIdentifier>();
                    if (uid != null) uid.Id = baseId;
                    if (LargeWorldStreamer.main != null && LargeWorldStreamer.main.cellManager != null) LargeWorldStreamer.main.cellManager.RegisterEntity(go);
                    b = go.GetComponent<Base>();
                    if (b == null) { Plugin.Log.LogWarning("Base prefab has no Base component?"); return; }
                    Plugin.Log.LogInfo("New base created [" + baseId + "]");
                }
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    var shape = new Grid3Shape(new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                    var offset = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    var anchor = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    var cells = ReadBytes(r); var faces = ReadBytes(r);
                    var links = ReadBytes(r); var masks = ReadBytes(r);
                    var glass = ReadBools(r); var unpowered = ReadBools(r);
                    b.baseShape = shape;
                    b.cellOffset = offset;
                    b.anchor = anchor;
                    b.cells = cells == null ? null : Array.ConvertAll(cells, c => (Base.CellType)c);
                    b.faces = faces == null ? null : Array.ConvertAll(faces, f => (Base.FaceType)f);
                    b.links = links; b.masks = masks; b.isGlass = glass; b.unpowered = unpowered;
                }
                // Same steps the game runs after loading a base from a save.
                var t = Traverse.Create(b);
                t.Method("StorePreviousFaces").GetValue();
                t.Method("FixCorridorLinks").GetValue();
                t.Field("deserializationFinished").SetValue(false);
                t.Method("FinishDeserialization").GetValue(); // Initialize, AllocateArrays, BindCellObjects, flow data, RebuildGeometry
                Plugin.Log.LogInfo("Base state applied [" + baseId + "] " + data.Length + " bytes");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Base state apply failed: " + e); }
            finally { WorldSync.applyingRemote = false; }
        }

        public static void OnBaseRemoved(string baseId)
        {
            var go = WorldSync.Find(baseId);
            if (go == null) return;
            WorldSync.applyingRemote = true;
            try { UnityEngine.Object.Destroy(go); Plugin.Log.LogInfo("Base removed [" + baseId + "]"); }
            finally { WorldSync.applyingRemote = false; }
        }
    }
}
