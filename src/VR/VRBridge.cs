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
        private static FieldInfo fInstance, fCamera, fLeft, fRight, fLeftHandTarget, fRightHandTarget, fUiCamera;

        public static bool IsAvailable
        {
            get { Probe(); return fInstance != null; }
        }

        /// <summary>True when the SubmersedVR rig exists and is currently tracking.</summary>
        public static bool IsVRActive
        {
            get
            {
                if (!IsAvailable || !XREnabled) return false;
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
                    fUiCamera = t.GetField("uiCamera", any);
                    if (fInstance == null || fCamera == null || fLeft == null || fRight == null)
                    {
                        Plugin.Log.LogWarning("SubmersedVR found but VRCameraRig layout changed; VR hands disabled.");
                        fInstance = null;
                    }
                    else Plugin.Log.LogInfo("SubmersedVR detected; VR head/hand tracking will be used when the game runs in VR.");
                    break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR probe failed: " + e.Message);
                fInstance = null;
            }
        }

        /// <summary>
        /// SubmersedVR draws the game's HUD with the game's UI camera parked on a separate "UI rig" at the world
        /// origin. Each LateUpdate the UI rig takes the VR rig's rotation, and both cameras get the same head
        /// tracking, so a point P in the world shows up for the UI camera at uiRig.TransformPoint(rig.InverseTransformPoint(P)).
        /// That camera draws after the main one without the game's HDR exposure / tonemapping.
        /// </summary>
        public static bool TryGetUiSpace(out Transform rig, out Transform uiRig, out Camera uiCamera)
        {
            rig = uiRig = null; uiCamera = null;
            if (!IsAvailable || fUiCamera == null) return false;
            var inst = fInstance.GetValue(null) as Component;
            if (inst == null) return false;
            uiCamera = fUiCamera.GetValue(inst) as Camera;
            if (uiCamera == null || uiCamera.transform.parent == null) return false;
            rig = inst.transform;
            uiRig = uiCamera.transform.parent;
            return true;
        }

        /// <summary>Fills head/hand transforms. Returns false if the rig is not live.</summary>
        private static bool xrChecked, xrOn;
        private static float xrNextCheck;

        /// <summary>
        /// Unity's VR is really running (headset device loaded). SubmersedVR installed but the game launched flat still
        /// creates its camera rig, so the rig alone made a flat-screen player send VR head/hand data (0.14.11-test1 and
        /// earlier): the other side then lifted and offset their avatar as for a headset.
        /// </summary>
        public static bool XREnabled
        {
            get
            {
                if (xrChecked && Time.unscaledTime < xrNextCheck) return xrOn;
                xrChecked = true; xrNextCheck = Time.unscaledTime + 2f;
                try
                {
                    var t = Type.GetType("UnityEngine.XR.XRSettings, UnityEngine.XRModule") ?? Type.GetType("UnityEngine.XR.XRSettings, UnityEngine.VRModule");
                    if (t == null) { xrOn = false; return xrOn; }
                    bool enabled = (bool)(t.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static).GetValue(null, null));
                    var devProp = t.GetProperty("loadedDeviceName", BindingFlags.Public | BindingFlags.Static);
                    string dev = devProp != null ? devProp.GetValue(null, null) as string : "x";
                    xrOn = enabled && !string.IsNullOrEmpty(dev) && dev != "None";
                }
                catch { xrOn = false; }
                return xrOn;
            }
        }

        public static bool TryGetRig(out Transform head, out Transform left, out Transform right)
        {
            head = left = right = null;
            if (!IsAvailable || !XREnabled) return false;
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
