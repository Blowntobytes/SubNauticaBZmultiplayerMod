using UnityEngine;
using BZMultiplayer.Sync;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// Draws on-screen HUD icons for remote players who are more than 50 metres away.
    /// A small diamond marker with the player's name and distance is projected onto the screen edge
    /// when the player is off-screen, or shown at the projected position when on-screen.
    /// Drawn in OnGUI from Plugin so it sits on top of everything.
    /// </summary>
    public static class PlayerHUD
    {
        private const float MinDistance = 50f;
        private const float EdgeMargin = 40f; // pixels from screen edge for off-screen clamping

        private static GUIStyle labelStyle;
        private static GUIStyle distStyle;
        private static Texture2D markerTex;

        public static void OnGUI()
        {
            if (!Plugin.ShowOverlay.Value) return;
            if (!LocalPlayerSync.InWorld || Plugin.Instance == null) return;
            var players = Plugin.Instance.Players;
            if (players == null || players.Count == 0) return;

            Camera cam = SNCameraRoot.main != null ? SNCameraRoot.main.GetComponent<Camera>() : Camera.main;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            Vector3 localPos = Player.main != null ? Player.main.transform.position : cam.transform.position;

            EnsureStyles();

            foreach (var p in players.All)
            {
                if (!p.HasPose) continue;
                float dist = Vector3.Distance(localPos, p.Position);
                if (dist < MinDistance) continue;

                DrawMarker(cam, p.Position, p.Name, dist);
            }
        }

        private static void EnsureStyles()
        {
            if (labelStyle != null) return;

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 13;
            labelStyle.fontStyle = FontStyle.Bold;
            labelStyle.normal.textColor = new Color(0.85f, 0.95f, 1f, 0.9f);
            labelStyle.alignment = TextAnchor.MiddleCenter;

            distStyle = new GUIStyle(GUI.skin.label);
            distStyle.fontSize = 11;
            distStyle.normal.textColor = new Color(0.7f, 0.85f, 1f, 0.75f);
            distStyle.alignment = TextAnchor.MiddleCenter;

            // Create a small diamond marker texture
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

        private static void DrawMarker(Camera cam, Vector3 worldPos, string name, float dist)
        {
            // Head height offset so the marker sits above the player
            Vector3 markerWorld = worldPos + Vector3.up * 1.8f;
            Vector3 screen = cam.WorldToScreenPoint(markerWorld);

            float sw = Screen.width;
            float sh = Screen.height;

            // Behind the camera: flip to the opposite edge
            bool behind = screen.z < 0f;
            if (behind) { screen.x = sw - screen.x; screen.y = sh - screen.y; }

            // Convert to GUI coords (Y is flipped)
            float gx = screen.x;
            float gy = sh - screen.y;

            // Check if on-screen
            bool onScreen = !behind && gx > EdgeMargin && gx < sw - EdgeMargin && gy > EdgeMargin && gy < sh - EdgeMargin;

            if (!onScreen)
            {
                // Clamp to screen edge
                // Direction from screen center to the projected point
                float cx = sw * 0.5f;
                float cy = sh * 0.5f;
                float dx = gx - cx;
                float dy = gy - cy;
                if (Mathf.Abs(dx) < 1f && Mathf.Abs(dy) < 1f) { dx = 0f; dy = -1f; }

                float scaleX = (sw * 0.5f - EdgeMargin) / Mathf.Max(Mathf.Abs(dx), 0.001f);
                float scaleY = (sh * 0.5f - EdgeMargin) / Mathf.Max(Mathf.Abs(dy), 0.001f);
                float scale = Mathf.Min(scaleX, scaleY);
                if (scale < 1f) { gx = cx + dx * scale; gy = cy + dy * scale; }
            }

            // Draw diamond marker
            float ms = 12f;
            GUI.DrawTexture(new Rect(gx - ms * 0.5f, gy - ms * 0.5f, ms, ms), markerTex);

            // Name above marker
            string label = name;
            Vector2 nameSize = labelStyle.CalcSize(new GUIContent(label));
            GUI.Label(new Rect(gx - nameSize.x * 0.5f, gy - ms * 0.5f - nameSize.y - 2f, nameSize.x, nameSize.y), label, labelStyle);

            // Distance below marker
            string distText = Mathf.RoundToInt(dist) + "m";
            Vector2 distSize = distStyle.CalcSize(new GUIContent(distText));
            GUI.Label(new Rect(gx - distSize.x * 0.5f, gy + ms * 0.5f + 2f, distSize.x, distSize.y), distText, distStyle);
        }
    }
}
