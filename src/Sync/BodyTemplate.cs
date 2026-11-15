using System;
using HarmonyLib;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Keeps an untouched copy of the player body prefab instance. SubmersedVR re-parents the hand bones under the
    /// VR controllers and rotates the body toward the headset, so cloning the live body on a VR machine gives a
    /// twisted model with no hands. We snapshot it in a prefix on ArmsController.Start, which runs before
    /// SubmersedVR's postfix on the same method.
    /// </summary>
    public static class BodyTemplate
    {
        private static GameObject template;
        private static Vector3 localPos;
        private static Quaternion localRot;
        private static Vector3 localScale;
        private static bool capturing;

        public static bool IsReady { get { return template != null; } }

        /// <summary>Path (under the body) of the PDA model in the template, or null when none was found (test12).</summary>
        public static string PdaPath { get; private set; }

        public static void Install(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ArmsController), "Start");
            if (start == null) { Plugin.Log.LogWarning("ArmsController.Start not found; body template disabled."); return; }
            harmony.Patch(start, prefix: new HarmonyMethod(typeof(BodyTemplate), "StartPrefix"));
        }

        private static void StartPrefix(ArmsController __instance)
        {
            if (capturing) return;
            try
            {
                capturing = true;
                Capture(__instance.gameObject);
                // Same moment, same reason: the rig is still untouched, so the tool socket can be measured honestly.
                HeldItemSync.Calibrate(__instance.gameObject);
            }
            catch (Exception e) { Plugin.Log.LogError("Body template capture failed: " + e); }
            finally { capturing = false; }
        }

        private static void Capture(GameObject src)
        {
            var player = src.GetComponentInParent<Player>();
            Transform pt = player != null ? player.transform : src.transform.parent;
            if (template != null) UnityEngine.Object.Destroy(template);

            // Clone while the source is active (so the copy keeps its bindings), then park it inactive.
            template = UnityEngine.Object.Instantiate(src);
            template.name = "BZMP_BodyTemplate";
            template.SetActive(false);
            template.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(template);
            template.hideFlags = HideFlags.HideAndDontSave;

            if (pt != null)
            {
                localPos = pt.InverseTransformPoint(src.transform.position);
                localRot = Quaternion.Inverse(pt.rotation) * src.transform.rotation;
            }
            else { localPos = Vector3.zero; localRot = Quaternion.identity; }
            localScale = src.transform.localScale;
            CapturePda(src, player);
            Plugin.Log.LogInfo("Body template captured (" + template.GetComponentsInChildren<Transform>(true).Length + " transforms).");
        }

        private static string RelPath(Transform root, Transform t)
        {
            if (t == null) return null;
            string path = null;
            for (var c = t; c != null; c = c.parent)
            {
                if (c == root) return path ?? "";
                path = path == null ? c.name : c.name + "/" + path;
            }
            return null; // not under root
        }

        /// <summary>
        /// Remember where the PDA model sits in the untouched rig so avatars can show it while its owner has the PDA
        /// open. When the game keeps it inside the body we just note its path; when it lives elsewhere on the player
        /// we copy it into the template under the same parent bone with the same local pose (measured, not guessed).
        /// </summary>
        private static void CapturePda(GameObject src, Player player)
        {
            PdaPath = null;
            try
            {
                PDA pda = player != null ? player.GetComponentInChildren<PDA>(true) : null;
                if (pda == null) { Plugin.Log.LogWarning("PDA model: no PDA component found on the player; avatars will not show it."); return; }
                Transform pt = pda.transform;
                int rcount = pt.GetComponentsInChildren<Renderer>(true).Length;
                string inBody = RelPath(src.transform, pt);
                if (inBody != null)
                {
                    PdaPath = inBody;
                    Plugin.Log.LogInfo("PDA model: inside the body at '" + inBody + "' (" + rcount + " renderers, active " + pt.gameObject.activeSelf
                        + ", local pos " + pt.localPosition.ToString("F3") + " rot " + pt.localEulerAngles.ToString("F1") + ").");
                    return;
                }
                // Outside the body: find the nearest ancestor that is inside it, else fall back to the left hand.
                Transform anchor = pt.parent;
                string anchorPath = null;
                while (anchor != null && (anchorPath = RelPath(src.transform, anchor)) == null) anchor = anchor.parent;
                Transform tAnchor;
                string note;
                if (anchorPath != null)
                {
                    tAnchor = anchorPath.Length == 0 ? template.transform : template.transform.Find(anchorPath);
                    note = "under body bone '" + anchorPath + "'";
                }
                else
                {
                    tAnchor = null;
                    foreach (var t in template.GetComponentsInChildren<Transform>(true))
                        if (t.name == "attach1_L" || t.name == "hand_L") { tAnchor = t; if (t.name == "attach1_L") break; }
                    anchor = null;
                    foreach (var t in src.GetComponentsInChildren<Transform>(true))
                        if (tAnchor != null && t.name == tAnchor.name) { anchor = t; break; }
                    note = "not attached to the body; parked on '" + (tAnchor != null ? tAnchor.name : "?") + "'";
                }
                if (tAnchor == null) { Plugin.Log.LogWarning("PDA model: no bone to attach it to (" + note + ")."); return; }
                // Instantiated straight under the inactive template so none of the PDA's own scripts wake up.
                var copy = UnityEngine.Object.Instantiate(pt.gameObject, tAnchor, false);
                copy.name = "BZMP_PDA";
                if (anchor != null)
                {
                    copy.transform.localPosition = anchor.InverseTransformPoint(pt.position);
                    copy.transform.localRotation = Quaternion.Inverse(anchor.rotation) * pt.rotation;
                }
                copy.transform.localScale = pt.lossyScale.x > 0f && tAnchor.lossyScale.x > 0f ? pt.lossyScale / tAnchor.lossyScale.x : Vector3.one;
                PdaPath = RelPath(template.transform, copy.transform);
                Plugin.Log.LogInfo("PDA model: copied from '" + pt.name + "' (" + note + ", " + rcount + " renderers) to '" + PdaPath
                    + "', local pos " + copy.transform.localPosition.ToString("F3") + " rot " + copy.transform.localEulerAngles.ToString("F1") + ".");
            }
            catch (Exception e) { Plugin.Log.LogWarning("PDA model capture failed: " + e.Message); PdaPath = null; }
        }

        /// <summary>Instantiates a copy of the pristine body under the given parent, inactive.</summary>
        public static GameObject Spawn(Transform parent)
        {
            if (template == null) return null;
            var go = UnityEngine.Object.Instantiate(template);
            go.hideFlags = HideFlags.None;
            go.name = "body";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;
            return go;
        }
    }
}
