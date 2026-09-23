using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using BZMultiplayer.Sync;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// Draws HUD icons for remote players who are more than 50 metres away.
    /// Uses world-space Canvas billboards so the markers are visible in both VR and flat-screen.
    /// Each remote player gets a small Canvas hovering above their head with a diamond marker,
    /// name label, and distance readout. The Canvas always faces the camera (billboard).
    /// </summary>
    public static class PlayerHUD
    {
        private const float MinDistance = 50f;
        private const float HeadOffset = 2.5f; // metres above the player position
        private const float CanvasScale = 0.02f; // world-space canvas scale
        private const float MaxLabelDist = 800f; // beyond this, hide label to reduce clutter

        // Pool of marker objects keyed by player steam id (ulong as string)
        private static readonly Dictionary<string, MarkerUI> markers = new Dictionary<string, MarkerUI>();
        private static readonly List<string> removeList = new List<string>();
        private static Material spriteMat;

        private class MarkerUI
        {
            public GameObject root;
            public Canvas canvas;
            public TextMeshProUGUI nameLabel;
            public TextMeshProUGUI distLabel;
            public Image diamond;
            public bool usedThisFrame;
        }

        /// <summary>Called from Plugin.Update instead of OnGUI.</summary>
        public static void UpdateHUD()
        {
            if (!Plugin.ShowOverlay.Value || !LocalPlayerSync.InWorld || Plugin.Instance == null)
            {
                HideAll();
                return;
            }

            var players = Plugin.Instance.Players;
            if (players == null || players.Count == 0) { HideAll(); return; }

            Camera cam = SNCameraRoot.main != null ? SNCameraRoot.main.GetComponent<Camera>() : Camera.main;
            if (cam == null) cam = Camera.main;
            if (cam == null) { HideAll(); return; }

            Vector3 localPos = Player.main != null ? Player.main.transform.position : cam.transform.position;

            // Mark all unused
            foreach (var kv in markers) kv.Value.usedThisFrame = false;

            foreach (var p in players.All)
            {
                if (!p.HasPose) continue;
                float dist = Vector3.Distance(localPos, p.Position);
                if (dist < MinDistance) continue;

                string key = p.SteamId.ToString();
                MarkerUI m;
                if (!markers.TryGetValue(key, out m) || m.root == null)
                {
                    m = CreateMarker();
                    markers[key] = m;
                }

                // Position above the player's head
                m.root.transform.position = p.Position + Vector3.up * HeadOffset;

                // Billboard: face the camera
                m.root.transform.rotation = cam.transform.rotation;

                // Scale up with distance so it stays readable
                float scaleMul = Mathf.Clamp(dist / 100f, 0.8f, 3f);
                m.root.transform.localScale = Vector3.one * CanvasScale * scaleMul;

                m.nameLabel.text = p.Name;
                m.distLabel.text = Mathf.RoundToInt(dist) + "m";

                bool showLabels = dist < MaxLabelDist;
                m.nameLabel.enabled = showLabels;
                m.distLabel.enabled = showLabels;

                m.root.SetActive(true);
                m.usedThisFrame = true;
            }

            // Hide unused markers
            removeList.Clear();
            foreach (var kv in markers)
            {
                if (!kv.Value.usedThisFrame)
                {
                    if (kv.Value.root != null) kv.Value.root.SetActive(false);
                }
                if (kv.Value.root == null) removeList.Add(kv.Key);
            }
            for (int i = 0; i < removeList.Count; i++) markers.Remove(removeList[i]);
        }

        private static void HideAll()
        {
            foreach (var kv in markers)
                if (kv.Value.root != null) kv.Value.root.SetActive(false);
        }

        public static void Cleanup()
        {
            foreach (var kv in markers)
                if (kv.Value.root != null) Object.Destroy(kv.Value.root);
            markers.Clear();
        }

        // ------------------------------------------------------------------ OnGUI fallback
        // Keep the old OnGUI path as a flat-screen overlay so the debug box still works.
        // The world-space markers handle VR; this adds the screen-edge clamped overlay for flat only.

        private static GUIStyle labelStyleGUI;
        private static GUIStyle distStyleGUI;
        private static Texture2D markerTex;

        public static void OnGUI()
        {
            // Skip the IMGUI overlay when in VR — the world-space markers handle it
            if (BZMultiplayer.VR.VRBridge.IsVRActive) return;
            if (!Plugin.ShowOverlay.Value) return;
            if (!LocalPlayerSync.InWorld || Plugin.Instance == null) return;
            var players = Plugin.Instance.Players;
            if (players == null || players.Count == 0) return;

            Camera cam = SNCameraRoot.main != null ? SNCameraRoot.main.GetComponent<Camera>() : Camera.main;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            Vector3 localPos = Player.main != null ? Player.main.transform.position : cam.transform.position;

            EnsureStylesGUI();

            foreach (var p in players.All)
            {
                if (!p.HasPose) continue;
                float dist = Vector3.Distance(localPos, p.Position);
                if (dist < MinDistance) continue;

                DrawMarkerGUI(cam, p.Position, p.Name, dist);
            }
        }

        // ------------------------------------------------------------------ GUI helpers (flat-screen only)

        private static void EnsureStylesGUI()
        {
            if (labelStyleGUI != null) return;

            labelStyleGUI = new GUIStyle(GUI.skin.label);
            labelStyleGUI.fontSize = 13;
            labelStyleGUI.fontStyle = FontStyle.Bold;
            labelStyleGUI.normal.textColor = new Color(0.85f, 0.95f, 1f, 0.9f);
            labelStyleGUI.alignment = TextAnchor.MiddleCenter;

            distStyleGUI = new GUIStyle(GUI.skin.label);
            distStyleGUI.fontSize = 11;
            distStyleGUI.normal.textColor = new Color(0.7f, 0.85f, 1f, 0.75f);
            distStyleGUI.alignment = TextAnchor.MiddleCenter;

            markerTex = new Texture2D(12, 12, TextureFormat.ARGB32, false);
            markerTex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[12 * 12];
            Color fill = new Color(0.3f, 0.7f, 1f, 0.9f);
            Color edge = new Color(1f, 1f, 1f, 0.6f);
            Vector2 center = new Vector2(5.5f, 5.5f);
            for (int y = 0; y < 12; y++)
            {
                for (int x = 0; x < 12; x++)
                {
                    float d = Mathf.Abs(x - center.x) + Mathf.Abs(y - center.y);
                    if (d <= 4.5f) pixels[y * 12 + x] = fill;
                    else if (d <= 5.5f) pixels[y * 12 + x] = edge;
                    else pixels[y * 12 + x] = Color.clear;
                }
            }
            markerTex.SetPixels(pixels);
            markerTex.Apply();
        }

        private static void DrawMarkerGUI(Camera cam, Vector3 worldPos, string name, float dist)
        {
            Vector3 markerWorld = worldPos + Vector3.up * 1.8f;
            Vector3 screen = cam.WorldToScreenPoint(markerWorld);

            float sw = Screen.width;
            float sh = Screen.height;

            bool behind = screen.z < 0f;
            if (behind) { screen.x = sw - screen.x; screen.y = sh - screen.y; }

            float gx = screen.x;
            float gy = sh - screen.y;
            float edgeMargin = 40f;

            bool onScreen = !behind && gx > edgeMargin && gx < sw - edgeMargin && gy > edgeMargin && gy < sh - edgeMargin;

            if (!onScreen)
            {
                float cx = sw * 0.5f;
                float cy = sh * 0.5f;
                float dx = gx - cx;
                float dy = gy - cy;
                if (Mathf.Abs(dx) < 1f && Mathf.Abs(dy) < 1f) { dx = 0f; dy = -1f; }

                float scaleX = (sw * 0.5f - edgeMargin) / Mathf.Max(Mathf.Abs(dx), 0.001f);
                float scaleY = (sh * 0.5f - edgeMargin) / Mathf.Max(Mathf.Abs(dy), 0.001f);
                float scale = Mathf.Min(scaleX, scaleY);
                if (scale < 1f) { gx = cx + dx * scale; gy = cy + dy * scale; }
            }

            float ms = 12f;
            GUI.DrawTexture(new Rect(gx - ms * 0.5f, gy - ms * 0.5f, ms, ms), markerTex);

            string label = name;
            Vector2 nameSize = labelStyleGUI.CalcSize(new GUIContent(label));
            GUI.Label(new Rect(gx - nameSize.x * 0.5f, gy - ms * 0.5f - nameSize.y - 2f, nameSize.x, nameSize.y), label, labelStyleGUI);

            string distText = Mathf.RoundToInt(dist) + "m";
            Vector2 distSize = distStyleGUI.CalcSize(new GUIContent(distText));
            GUI.Label(new Rect(gx - distSize.x * 0.5f, gy + ms * 0.5f + 2f, distSize.x, distSize.y), distText, distStyleGUI);
        }

        // ------------------------------------------------------------------ world-space marker creation

        private static MarkerUI CreateMarker()
        {
            var m = new MarkerUI();

            m.root = new GameObject("PlayerHUDMarker");
            Object.DontDestroyOnLoad(m.root);

            m.canvas = m.root.AddComponent<Canvas>();
            m.canvas.renderMode = RenderMode.WorldSpace;
            m.canvas.sortingOrder = 30000;

            var scaler = m.root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            // Diamond image
            var diamondGO = new GameObject("Diamond");
            diamondGO.transform.SetParent(m.root.transform, false);
            m.diamond = diamondGO.AddComponent<Image>();
            m.diamond.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            m.diamond.sprite = CreateDiamondSprite();
            var drt = m.diamond.rectTransform;
            drt.sizeDelta = new Vector2(20, 20);
            drt.anchoredPosition = Vector2.zero;
            drt.localRotation = Quaternion.Euler(0, 0, 45); // rotate square into diamond

            // Name label above
            var nameGO = new GameObject("Name");
            nameGO.transform.SetParent(m.root.transform, false);
            m.nameLabel = nameGO.AddComponent<TextMeshProUGUI>();
            m.nameLabel.fontSize = 28;
            m.nameLabel.fontStyle = FontStyles.Bold;
            m.nameLabel.color = new Color(0.85f, 0.95f, 1f, 0.9f);
            m.nameLabel.alignment = TextAlignmentOptions.Center;
            m.nameLabel.enableWordWrapping = false;
            m.nameLabel.overflowMode = TextOverflowModes.Overflow;
            var nrt = m.nameLabel.rectTransform;
            nrt.sizeDelta = new Vector2(300, 40);
            nrt.anchoredPosition = new Vector2(0, 28);

            // Distance label below
            var distGO = new GameObject("Dist");
            distGO.transform.SetParent(m.root.transform, false);
            m.distLabel = distGO.AddComponent<TextMeshProUGUI>();
            m.distLabel.fontSize = 22;
            m.distLabel.color = new Color(0.7f, 0.85f, 1f, 0.75f);
            m.distLabel.alignment = TextAlignmentOptions.Center;
            m.distLabel.enableWordWrapping = false;
            m.distLabel.overflowMode = TextOverflowModes.Overflow;
            var drt2 = m.distLabel.rectTransform;
            drt2.sizeDelta = new Vector2(200, 32);
            drt2.anchoredPosition = new Vector2(0, -24);

            // Canvas rect
            var crt = m.canvas.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(300, 100);

            return m;
        }

        private static Sprite diamondSprite;
        private static Sprite CreateDiamondSprite()
        {
            if (diamondSprite != null) return diamondSprite;
            var tex = new Texture2D(4, 4, TextureFormat.ARGB32, false);
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            diamondSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            return diamondSprite;
        }
    }
}
