using System;
using System.Reflection;
using UnityEngine;

namespace BZMultiplayer.VR
{
    /// <summary>
    /// Soft link to SubmersedVR_BZ. Everything is resolved by reflection so this plugin loads and works
    /// for flat-screen players who do not have SubmersedVR installed.
    /// Reads SubmersedVR.VRCameraRig.instance { vrCamera, leftController, rightController }.
    /// </summary>
    public static class VRBridge
    {
        private static bool probed;
        private static FieldInfo fInstance, fCamera, fLeft, fRight, fLeftHandTarget, fRightHandTarget;

        public static bool IsAvailable
        {
            get { Probe(); return fInstance != null; }
        }

        /// <summary>True when the SubmersedVR rig exists and is currently tracking.</summary>
        public static bool IsVRActive
        {
            get
            {
                if (!IsAvailable) return false;
                var rig = fInstance.GetValue(null) as Component;
                return rig != null && rig.gameObject.activeInHierarchy;
            }
        }

        private static void Probe()
        {
            if (probed) return;
            probed = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "SubmersedVR") continue;
                    var t = asm.GetType("SubmersedVR.VRCameraRig");
                    if (t == null) break;
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                    fInstance = t.GetField("instance", any);
                    fCamera = t.GetField("vrCamera", any);
                    fLeft = t.GetField("leftController", any);
                    fRight = t.GetField("rightController", any);
                    fLeftHandTarget = t.GetField("leftHandTarget", any);
                    fRightHandTarget = t.GetField("rightHandTarget", any);
                    if (fInstance == null || fCamera == null || fLeft == null || fRight == null)
                    {
                        Plugin.Log.LogWarning("SubmersedVR found but VRCameraRig layout changed; VR hands disabled.");
                        fInstance = null;
                    }
                    else Plugin.Log.LogInfo("SubmersedVR detected; VR head/hand tracking enabled.");
                    break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR probe failed: " + e.Message);
                fInstance = null;
            }
        }

        /// <summary>Fills head/hand transforms. Returns false if the rig is not live.</summary>
        public static bool TryGetRig(out Transform head, out Transform left, out Transform right)
        {
            head = left = right = null;
            if (!IsAvailable) return false;
            var rig = fInstance.GetValue(null);
            if (rig == null) return false;
            var cam = fCamera.GetValue(rig) as Camera;
            // Prefer the hand-target objects (already offset to sit where the hand bone goes); fall back to raw controllers.
            var l = (fLeftHandTarget != null ? fLeftHandTarget.GetValue(rig) as GameObject : null) ?? fLeft.GetValue(rig) as GameObject;
            var r = (fRightHandTarget != null ? fRightHandTarget.GetValue(rig) as GameObject : null) ?? fRight.GetValue(rig) as GameObject;
            if (cam == null) return false;
            head = cam.transform;
            left = l != null ? l.transform : null;
            right = r != null ? r.transform : null;
            return true;
        }
    }
}
