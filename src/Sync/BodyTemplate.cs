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
            Plugin.Log.LogInfo("Body template captured (" + template.GetComponentsInChildren<Transform>(true).Length + " transforms).");
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
