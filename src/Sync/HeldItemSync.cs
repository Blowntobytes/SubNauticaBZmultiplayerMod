using System.Collections;
using UnityEngine;
using HarmonyLib;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// What each player is holding (scanner, knife, builder, flashlight, seaglide, a fish...). The local player's held
    /// tech type is broadcast whenever it changes, and every player's avatar carries a display-only copy of that
    /// prefab in its right hand.
    /// </summary>
    public static class HeldItemSync
    {
        private static SteamNet net;
        private static TechType lastSent = TechType.None;
        private static int lastFlags;
        private static float nextRefresh;

        /// <summary>Held-item flag: the item's light is switched on.</summary>
        public const int LitFlag = 1;

        public static void Install(SteamNet steamNet) { net = steamNet; }

        public static void Update()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld) { lastSent = TechType.None; return; }

            TechType held = TechType.None;
            int flags = 0;
            try
            {
                var inv = Inventory.main;
                var tool = inv != null ? inv.GetHeldTool() : null;
                if (tool != null && tool.pickupable != null)
                {
                    held = tool.pickupable.GetTechType();
                    // The flashlight (and anything else with a switch) reports whether it is lit, so the copy in
                    // the other player's hand can light up too.
                    var lights = tool.GetComponentInChildren<ToggleLights>(true);
                    if (lights != null && lights.lightsActive) flags |= LitFlag;
                }
            }
            catch { }

            // Re-announce now and then so someone who joined after we picked it up still sees it.
            bool refresh = Time.unscaledTime >= nextRefresh;
            if (held == lastSent && flags == lastFlags && !refresh) return;
            nextRefresh = Time.unscaledTime + 5f;
            lastSent = held;
            lastFlags = flags;
            net.SendHeldItem((int)held, flags);
        }

        // ------------------------------------------------------------------ where the hand actually holds a tool

        // The game does not hold a tool at the "attach1" bone: it plugs it into QuickSlots.toolSocket, a transform on
        // the player rig with its own orientation. Plugging into attach1 instead leaves every item rotated, however
        // correctly its own grip point is read. Rather than guess that difference, we measure it once on this machine:
        // toolSocket expressed in attach1's space is a property of the rig, identical for every player, so the same
        // numbers place a tool correctly in any avatar's hand.
        private static bool calibrated;
        private static Vector3 socketPos = Vector3.zero;
        private static Quaternion socketRot = Quaternion.identity;
        private static Vector3 socketScale = Vector3.one;

        /// <summary>
        /// Called from the body-template capture, which runs before SubmersedVR re-parents the hand bones - on a VR
        /// machine the live rig is twisted moments later, and measuring then would bake that twist into every avatar.
        /// </summary>
        public static void Calibrate(GameObject armsRig)
        {
            if (calibrated || armsRig == null) return;
            try
            {
                Transform attach = FindDescendant(armsRig.transform, "attach1");
                if (attach == null) { Plugin.Log.LogWarning("Held items: no 'attach1' bone on the player rig; using the bone origin."); return; }

                Transform socket = null;
                var inv = Inventory.main;
                var qs = inv != null ? inv.quickSlots : null;
                if (qs != null) socket = Traverse.Create(qs).Field("toolSocket").GetValue<Transform>();
                if (socket == null)
                {
                    Plugin.Log.LogWarning("Held items: the game's tool socket is not ready yet; items will sit at the hand bone until the next world load.");
                    return;
                }

                socketPos = attach.InverseTransformPoint(socket.position);
                socketRot = Quaternion.Inverse(attach.rotation) * socket.rotation;
                var a = attach.lossyScale; var s = socket.lossyScale;
                socketScale = new Vector3(Ratio(s.x, a.x), Ratio(s.y, a.y), Ratio(s.z, a.z));
                calibrated = true;

                Plugin.Log.LogInfo("Held items calibrated: tool socket sits at " + socketPos.ToString("F4")
                                 + " rot " + socketRot.eulerAngles.ToString("F1")
                                 + " scale " + socketScale.ToString("F3") + " relative to the hand bone.");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Held items: could not measure the tool socket (" + e.Message + ")."); }
        }

        private static float Ratio(float a, float b) { return Mathf.Abs(b) < 0.0001f ? 1f : a / b; }

        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        /// <summary>The hand bone with the game's own tool socket recreated under it, so a plug lands where it should.</summary>
        private static Transform SocketUnder(Transform attach)
        {
            if (!calibrated) return attach;
            const string name = "BZMP_ToolSocket";
            var existing = attach.Find(name);
            if (existing != null) return existing;

            var go = new GameObject(name);
            var t = go.transform;
            t.SetParent(attach, false);
            t.localPosition = socketPos;
            t.localRotation = socketRot;
            t.localScale = socketScale;
            return t;
        }

        // ------------------------------------------------------------------ the model in a remote player's hand

        /// <summary>Builds a display-only copy of an item and parents it to the avatar's tool attachment point.</summary>
        public static IEnumerator SpawnHeldModel(TechType tt, Transform attach, TaskResult<GameObject> result)
        {
            var spawned = new TaskResult<GameObject>();
            yield return CraftData.InstantiateFromPrefabAsync(tt, spawned, false);
            var go = spawned.Get();
            if (go == null) yield break;
            if (attach == null) { Object.Destroy(go); yield break; }

            go.SetActive(false);

            // The grip point the game itself uses, read BEFORE stripping: every tool prefab carries a ModelPlug whose
            // plugOrigin marks where the hand takes hold. Zeroing the transform instead (what we used to do) throws
            // that away, which is why items looked right to the holder - they see the game's own first-person copy -
            // and wrong to everyone else.
            Transform plug = null;
            string plugNote;
            try
            {
                var mp = go.GetComponentInChildren<ModelPlug>(true);
                if (mp == null) plugNote = "no ModelPlug component";
                else if (mp.plugOrigin == null) plugNote = "ModelPlug on '" + mp.GetType().Name + "' but plugOrigin is not set";
                else { plug = mp.plugOrigin; plugNote = "plug '" + plug.name + "'"; }
            }
            catch (System.Exception e) { plugNote = "could not read the ModelPlug (" + e.Message + ")"; }

            // The switchable part of a torch: ToggleLights.lightsParent holds the Light and the beam. Read before
            // Strip removes the component, and protect it from the generic "no lights, no effects" pass so it can be
            // switched on later exactly as the holder switches theirs.
            GameObject lightRig = null;
            try
            {
                var toggle = go.GetComponentInChildren<ToggleLights>(true);
                if (toggle != null && toggle.lightsParent != null) lightRig = toggle.lightsParent;
            }
            catch { }

            Strip(go, lightRig);
            Align(go.transform, plug, SocketUnder(attach), tt, plugNote);
            if (lightRig != null)
            {
                lightRig.SetActive(false);   // off until the holder says otherwise
                var handle = go.AddComponent<HeldLight>();
                handle.rig = lightRig;
            }
            go.SetActive(true);
            result.Set(go);
        }

        /// <summary>Lets the avatar switch a held tool's light on or off without knowing how the prefab is built.</summary>
        public class HeldLight : MonoBehaviour
        {
            public GameObject rig;
            private bool lit;
            private bool logged;

            public void Set(bool on)
            {
                if (rig == null || lit == on) return;
                lit = on;
                rig.SetActive(on);
                if (on && !logged && Plugin.VerboseLog.Value)
                {
                    logged = true;
                    var sb = new System.Text.StringBuilder("Held light on: '" + rig.name + "' contains");
                    foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
                        sb.Append(" [").Append(r.name).Append(": ").Append(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "no shader").Append(", layer ").Append(r.gameObject.layer).Append(']');
                    foreach (var l in rig.GetComponentsInChildren<Light>(true))
                        sb.Append(" [Light ").Append(l.type).Append(" range ").Append(l.range.ToString("F1")).Append(']');
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }
        }

        /// <summary>
        /// Seat the model in the hand the way the game does, then undo the socket's own scale so the item keeps its
        /// real-world size whatever the avatar's bone scale happens to be, then apply any per-item correction.
        /// </summary>
        private static void Align(Transform model, Transform plug, Transform attach, TechType tt, string plugNote)
        {
            try { ModelPlug.PlugIntoSocket(model, plug, attach); }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Held item " + tt + ": could not plug into the hand socket (" + e.Message + "); using the bare pivot.");
                model.SetParent(attach, false);
                model.localPosition = Vector3.zero;
                model.localRotation = Quaternion.identity;
            }

            // The game leaves the tool at local scale 1 under its socket, and our socket carries the same scale the
            // real one does, so 1 is already correct. HeldItemScale is only here for a residual nudge.
            model.localScale = Vector3.one * Plugin.HeldItemScale.Value;

            Offset o;
            if (TryGetOffset(tt, out o))
            {
                model.localPosition += model.localRotation * o.Position;
                model.localRotation = model.localRotation * Quaternion.Euler(o.Euler);
                model.localScale = model.localScale * o.Scale;
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Held item " + tt + ": applied the configured correction.");
            }

            // Always logged, one line per item: two builds in a row changed nothing visible, and without this we
            // cannot tell a correct placement from a silent fallback to the bare pivot.
            Plugin.Log.LogInfo("Held item " + tt + ": " + plugNote
                             + "; seated at " + model.localPosition.ToString("F4")
                             + " rot " + model.localRotation.eulerAngles.ToString("F1")
                             + " scale " + model.localScale.ToString("F3")
                             + " under '" + (model.parent != null ? model.parent.name : "(none)") + "'.");
        }

        // ------------------------------------------------------------------ per-item corrections from the config

        private struct Offset { public Vector3 Position; public Vector3 Euler; public float Scale; }

        private static System.Collections.Generic.Dictionary<TechType, Offset> offsets;
        private static string offsetsParsedFrom;

        private static bool TryGetOffset(TechType tt, out Offset o)
        {
            o = default(Offset);
            string raw = Plugin.HeldItemOffsets.Value ?? "";
            if (offsets == null || offsetsParsedFrom != raw) ParseOffsets(raw);
            return offsets.TryGetValue(tt, out o);
        }

        private static void ParseOffsets(string raw)
        {
            offsets = new System.Collections.Generic.Dictionary<TechType, Offset>();
            offsetsParsedFrom = raw;
            foreach (var chunk in raw.Split(';'))
            {
                var entry = chunk.Trim();
                if (entry.Length == 0) continue;

                int colon = entry.IndexOf(':');
                if (colon <= 0) { Plugin.Log.LogWarning("HeldItemOffsets: '" + entry + "' has no 'TechType:' prefix; ignored."); continue; }

                string name = entry.Substring(0, colon).Trim();
                TechType tt;
                if (!TechTypeExtensions.FromString(name, out tt, true) || tt == TechType.None)
                { Plugin.Log.LogWarning("HeldItemOffsets: '" + name + "' is not a TechType this game knows; ignored."); continue; }

                var parts = entry.Substring(colon + 1).Split(',');
                if (parts.Length != 7) { Plugin.Log.LogWarning("HeldItemOffsets: '" + name + "' needs 7 numbers (px,py,pz,rx,ry,rz,scale); ignored."); continue; }

                var n = new float[7];
                bool ok = true;
                for (int i = 0; i < 7; i++)
                    if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out n[i])) { ok = false; break; }
                if (!ok) { Plugin.Log.LogWarning("HeldItemOffsets: '" + name + "' has a value that is not a number; ignored."); continue; }
                if (Mathf.Abs(n[6]) < 0.0001f) { Plugin.Log.LogWarning("HeldItemOffsets: '" + name + "' has a scale of zero; ignored."); continue; }

                offsets[tt] = new Offset { Position = new Vector3(n[0], n[1], n[2]), Euler = new Vector3(n[3], n[4], n[5]), Scale = n[6] };
            }
            if (offsets.Count > 0) Plugin.Log.LogInfo("Held item corrections loaded for " + offsets.Count + " item(s).");
        }

        /// <summary>False for a renderer that would draw magenta: no material, no shader, or an unsupported one.</summary>
        private static bool ShaderUsable(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) return false;
            foreach (var m in mats)
            {
                if (m == null || m.shader == null) return false;
                if (!m.shader.isSupported) return false;
                if (m.shader.name.IndexOf("InternalErrorShader", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return true;
        }

        /// <summary>True when another component on the same object still needs this one to exist.</summary>
        private static bool RequiredByAnother(MonoBehaviour target, System.Collections.Generic.List<MonoBehaviour> others)
        {
            var tt = target.GetType();
            foreach (var o in others)
            {
                if (o == null || ReferenceEquals(o, target) || o.gameObject != target.gameObject) continue;
                foreach (var a in o.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    var r = a as RequireComponent;
                    if (r == null) continue;
                    if (Needs(r.m_Type0, tt) || Needs(r.m_Type1, tt) || Needs(r.m_Type2, tt)) return true;
                }
            }
            return false;
        }

        private static bool Needs(System.Type required, System.Type candidate)
        {
            return required != null && required.IsAssignableFrom(candidate);
        }

        /// <summary>Leave only what draws: no physics, no pickup logic, no identity of its own.</summary>
        private static void Strip(GameObject go, GameObject keepLit)
        {
            System.Func<Component, bool> protectedPart = c => keepLit != null && c != null && c.transform.IsChildOf(keepLit.transform);

            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }

            // Unity refuses to remove a component another one [RequireComponent]s (the flashlight needs its
            // EnergyMixin), and logs an error rather than throwing. So a dependant goes before what it depends on:
            // keep passing over the list, each time removing only what nothing remaining still requires.
            var pending = new System.Collections.Generic.List<MonoBehaviour>();
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (b == null) continue;
                if (b is SkyApplier) { b.enabled = true; continue; }   // keep it lit like the rest of the avatar
                pending.Add(b);
            }
            for (int pass = 0; pending.Count > 0 && pass < 8; pass++)
            {
                bool progress = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var b = pending[i];
                    if (RequiredByAnother(b, pending)) continue;
                    Object.Destroy(b);
                    pending.RemoveAt(i);
                    progress = true;
                }
                if (!progress) break;   // a cycle; leave the rest disabled rather than loop forever
            }
            foreach (var b in pending) b.enabled = false;

            // A tool's light, beam and particles are driven by the script we just removed. Left running they show
            // as a raw magenta cone (the flashlight's beam mesh with its unconfigured shader). Keep each renderer
            // exactly as the prefab had it - never force one on - and drop anything whose shader is not usable.
            foreach (var l in go.GetComponentsInChildren<Light>(true)) if (!protectedPart(l)) l.enabled = false;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) if (!protectedPart(ps)) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (protectedPart(r)) continue;   // the beam keeps its own layer, shader and state; the game set them up
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) { r.enabled = false; continue; }
                if (!ShaderUsable(r)) { r.enabled = false; continue; }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.gameObject.layer = 0;
            }
        }
    }
}
