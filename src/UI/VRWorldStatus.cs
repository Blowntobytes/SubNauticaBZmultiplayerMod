using UnityEngine;
using BZMultiplayer.Sync;
using BZMultiplayer.VR;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// VR only (test21): the world-transfer status ("Downloading world 42%", "loading host world"...) as floating
    /// text in front of the headset, because the flat-screen overlay (OnGUI) never reaches the headset.
    /// Shown only while joining, before we are in the world.
    /// </summary>
    public static class VRWorldStatus
    {
        private static GameObject panel;
        private static TextMesh text;
        private static string lastLogged;

        public static void Update()
        {
            string status = null;
            try
            {
                if (VRBridge.IsVRActive && !LocalPlayerSync.InWorld && Plugin.Instance != null && Plugin.Instance.Net != null && Plugin.Instance.Net.Saves != null)
                    status = Plugin.Instance.Net.Saves.StatusText;
            }
            catch { }

            if (string.IsNullOrEmpty(status)) { if (panel != null && panel.activeSelf) panel.SetActive(false); RestoreMenu(); return; }

            // test22: draw it with SubmersedVR's HUD camera (UI layer, depth test off), the same way the player
            // markers are drawn; the plain world camera did not show it in the headset at the menu.
            Transform rig = null, uiRig = null; Camera uiCam = null;
            bool ui = VRBridge.TryGetUiSpace(out rig, out uiRig, out uiCam);
            Camera cam = ui ? uiCam : Camera.main;
            if (cam == null) return;
            if (panel == null) Build();
            if (panel == null) return;
            if (!panel.activeSelf) panel.SetActive(true);
            int layer = ui ? 5 : 0;
            if (panel.layer != layer) panel.layer = layer;
            string space = (ui ? "HUD camera '" : "world camera '") + cam.name + "'";
            if (space != lastSpace) { lastSpace = space; Plugin.Log.LogInfo("VR status panel drawn with the " + space + "."); }

            string shown = char.ToUpper(status[0]) + status.Substring(1);
            if (text.text != shown) text.text = shown;
            if (shown != lastLogged && (lastLogged == null || !shown.StartsWith("Downloading") || !lastLogged.StartsWith("Downloading")))
            {
                lastLogged = shown;
                Plugin.Log.LogInfo("VR status panel: " + shown);
            }

            // test24: the Multiplayer menu is where the player is looking while joining, so the text sits on the
            // menu itself (just above it, a little toward the viewer). Head-following is only the fallback once the
            // menu is gone (the loading screen).
            Transform menu = MenuCanvas();
            if (menu != null)
            {
                if (panel.layer != menu.gameObject.layer) panel.layer = menu.gameObject.layer;
                var rt = menu as RectTransform;
                float h = rt != null ? rt.rect.height * rt.lossyScale.y : 1f;
                // test26: another 50% of the menu's height lower (h*0.3 -> h*-0.2): about the middle of where the menu was.
                panel.transform.position = menu.position + menu.up * (h * -0.2f + 0.12f) - menu.forward * 0.05f;
                HideMenu(menu, status);
                panel.transform.rotation = menu.rotation;
                panel.transform.localScale = Vector3.one * Mathf.Clamp(h / 1.2f, 0.3f, 3f);
                if (lastAnchor != "menu") { lastAnchor = "menu"; Plugin.Log.LogInfo("VR status panel anchored above the menu '" + menu.name + "' (" + h.ToString("F2") + " m tall)."); }
                return;
            }
            if (lastAnchor != "head") { lastAnchor = "head"; Plugin.Log.LogInfo("VR status panel following the head (no menu open)."); }
            panel.transform.localScale = Vector3.one;
            Transform hd = cam.transform;
            Vector3 fwd = Vector3.ProjectOnPlane(hd.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = hd.forward;
            fwd.Normalize();
            panel.transform.position = hd.position + fwd * 1.5f;
            panel.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        private static string lastAnchor;
        private static Canvas hiddenCanvas;

        /// <summary>
        /// test25: once joining has started, the Multiplayer menu is hidden in VR (its canvas stops drawing) so the
        /// status text is the only thing in front of the player. It comes back if the transfer fails.
        /// </summary>
        private static void HideMenu(Transform menu, string status)
        {
            string s = status.ToLowerInvariant();
            bool failed = s.Contains("fail") || s.Contains("stall") || s.Contains("bad") || s.Contains("manually");
            var c = menu.GetComponent<Canvas>();
            if (c == null) return;
            if (failed) { RestoreMenu(); return; }
            if (c.enabled) { c.enabled = false; hiddenCanvas = c; Plugin.Log.LogInfo("VR: Multiplayer menu hidden while joining."); }
        }

        private static void RestoreMenu()
        {
            if (hiddenCanvas == null) return;
            try { hiddenCanvas.enabled = true; } catch { }
            hiddenCanvas = null;
            Plugin.Log.LogInfo("VR: menu shown again.");
        }

        /// <summary>The root canvas of the main menu (in VR a world-space canvas in front of the player), or null.</summary>
        private static Transform MenuCanvas()
        {
            try
            {
                var menu = uGUI_MainMenu.main;
                if (menu == null || !menu.isActiveAndEnabled) return null;
                var c = menu.GetComponentInParent<Canvas>();
                if (c != null) c = c.rootCanvas;
                if (c == null || c.renderMode != RenderMode.WorldSpace) return null;
                return c.transform;
            }
            catch { return null; }
        }

        private static string lastSpace;

        private static void Build()
        {
            try
            {
                panel = new GameObject("BZMP_VRWorldStatus");
                Object.DontDestroyOnLoad(panel);
                text = panel.AddComponent<TextMesh>();
                text.characterSize = 0.02f;
                text.fontSize = 64;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.color = Color.white;
                var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                var mr = panel.GetComponent<MeshRenderer>();
                if (font != null)
                {
                    text.font = font;
                    var m = new Material(font.material);
                    m.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                    m.renderQueue = 4001;
                    mr.material = m;
                }
                panel.transform.position = Vector3.zero;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("VR status panel could not be built: " + e.Message); panel = null; }
        }
    }
}
