using System.Collections.Generic;
using UnityEngine;
using BZMultiplayer.Sync;
using BZMultiplayer.VR;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// Markers for remote players who are more than 50 metres away. Exactly one path draws per mode:
    ///
    ///  - Flat screen: an IMGUI overlay projected to the screen and clamped to the edges (OnGUI).
    ///  - VR (SubmersedVR rig live): a waypoint badge (diamond, name, distance; no background) drawn at the other
    ///    player's position and scaled with distance so it always looks as big as it would 6 m away (UpdateHUD).
    ///    Config VRMarkerAtPlayer = false floats it 6 m in front of the headset instead. IMGUI is not visible in VR.
    ///
    /// Before 0.11.13 the world-space marker ran in both modes and sat at the other player's real position.
    /// In flat mode that drew a second icon (the stray white one underwater); in VR, at 50 m+, it was a few
    /// pixels across and lost in the water fog, so nothing showed. 0.11.13 floated a badge on a dark plate 6 m away;
    /// 0.11.14 drops the plate and puts the badge back at the player, scaled to the same apparent size.
    /// </summary>
    public static class PlayerHUD
    {
        private const float MinDistance = 50f;

        // ---- flat-screen overlay sizes (1.5x the 0.11.12 sizes)
        private const float FlatMarkerPx = 18f;
        private const int FlatNameFont = 20;
        private const int FlatDistFont = 17;
        private const float FlatHeadOffset = 1.8f;

        // ---- VR badge
        // The badge is sized as if it hung 6 m in front of you. By default it is drawn at the other player's real
        // position (scaled up so it keeps that same apparent size); with VRMarkerAtPlayer off it floats 6 m away.
        private const float VRBadgeDistance = 6f;
        private const float VRMaxPlaceDistance = 150f; // cap so a far-off player never lands past the camera's far plane
        private const float VRAbovePlayer = 2.3f;      // metres above the player's position (clears the head)
        private const float VRDiamondSize = 0.11f;  // ~1 degree at 6 m
        private const float VRNameCharSize = 0.022f; // TextMesh characterSize (fontSize 48) -> ~10 cm letters
        private const float VRDistCharSize = 0.018f;

        // ================================================================== per-player colours

        // Fallback if the game's ping palette can't be read (cyan, as before).
        private static readonly Color DefaultColor = new Color(0f, 0.87f, 0.87f, 1f);
        private static Color[] palette;
        private static readonly Dictionary<ulong, int> colorIndex = new Dictionary<ulong, int>();
        private static readonly HashSet<ulong> present = new HashSet<ulong>();
        private static readonly List<ulong> staleColors = new List<ulong>();

        /// <summary>
        /// The game's own beacon/ping colours (PingManager.colorOptions), one per remote player. The starting pick
        /// comes from the Steam id, so a player usually gets the same colour every session; if another player in the
        /// session already has it, the next free colour is used.
        /// </summary>
        private static Color PlayerColor(ulong id)
        {
            if (palette == null)
            {
                try { palette = PingManager.colorOptions; } catch { palette = null; }
                if (palette == null || palette.Length == 0) palette = new[] { DefaultColor };
                Plugin.Log.LogInfo("Player HUD: " + palette.Length + " ping colours available for player markers.");
            }
            int idx;
            if (!colorIndex.TryGetValue(id, out idx))
            {
                int n = palette.Length;
                int start = (int)(((id * 2654435761UL) >> 16) % (ulong)n);
                idx = start;
                for (int i = 0; i < n; i++)
                {
                    int c = (start + i) % n;
                    if (!colorIndex.ContainsValue(c)) { idx = c; break; }
                }
                colorIndex[id] = idx;
            }
            Color col = palette[idx % palette.Length];
            col.a = 1f;
            return col;
        }

        /// <summary>Frees the colours of players who have left, so a newcomer can take them.</summary>
        private static void PruneColors(PlayerRegistry players)
        {
            if (colorIndex.Count == 0) return;
            present.Clear();
            if (players != null) foreach (var p in players.All) present.Add(p.SteamId);
            staleColors.Clear();
            foreach (var kv in colorIndex) if (!present.Contains(kv.Key)) staleColors.Add(kv.Key);
            for (int i = 0; i < staleColors.Count; i++) colorIndex.Remove(staleColors[i]);
        }

        private static Color NameTint(Color c) { return new Color(Mathf.Lerp(c.r, 1f, 0.25f), Mathf.Lerp(c.g, 1f, 0.25f), Mathf.Lerp(c.b, 1f, 0.25f), 0.95f); }
        private static Color DistTint(Color c) { return new Color(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, 0.85f); }

        // ================================================================== VR world-space badges

        private class Badge
        {
            public GameObject root;
            public TextMesh name;
            public TextMesh dist;
            public MeshRenderer diamond;
            public ulong colorFor;
            public bool used;
        }

        private static readonly Dictionary<ulong, Badge> badges = new Dictionary<ulong, Badge>();
        private static readonly List<ulong> removeList = new List<ulong>();
        private static Material diamondMat, textMat;
        private static Texture fontTex;
        private static bool hookedBeforeRender;
        private static string loggedSpace;
        private const int UILayer = 5; // Unity's built-in "UI" layer, which SubmersedVR's HUD camera renders
        private static Font arial;
        private static bool loggedShader;

        /// <summary>Called from Plugin.Update. Draws the VR badges; hides them in flat mode.</summary>
        public static void UpdateHUD()
        {
            // Place again right before rendering: SubmersedVR moves its rigs in LateUpdate, after our Update.
            if (!hookedBeforeRender) { hookedBeforeRender = true; Application.onBeforeRender += OnBeforeRender; }

            if (Plugin.Instance != null) PruneColors(Plugin.Instance.Players);
            if (!VRBridge.IsVRActive || !Plugin.ShowOverlay.Value || !LocalPlayerSync.InWorld || Plugin.Instance == null)
            {
                HideAll();
                return;
            }

            var players = Plugin.Instance.Players;
            if (players == null || players.Count == 0) { HideAll(); return; }

            // The headset camera, not SNCameraRoot's: under SubmersedVR they are not the same transform.
            Transform head, l, r;
            if (!VRBridge.TryGetRig(out head, out l, out r) || head == null)
            {
                Camera c = Camera.main;
                if (c == null) { HideAll(); return; }
                head = c.transform;
            }

            Vector3 eye = head.position;
            Vector3 localPos = Player.main != null ? Player.main.transform.position : eye;

            // Our copy of the font material has to follow the font atlas when Unity rebuilds it.
            if (textMat != null && arial != null && arial.material != null && arial.material.mainTexture != fontTex)
            {
                fontTex = arial.material.mainTexture;
                textMat.mainTexture = fontTex;
            }

            foreach (var kv in badges) kv.Value.used = false;

            foreach (var p in players.All)
            {
                if (!p.HasPose) continue;
                float dist = Vector3.Distance(localPos, p.Position);
                if (dist < MinDistance) continue;

                Vector3 toTarget = p.Position + Vector3.up * VRAbovePlayer - eye;
                float targetDist = toTarget.magnitude;
                if (targetDist < 0.1f) continue;
                Vector3 dir = toTarget / targetDist;

                Badge b;
                if (!badges.TryGetValue(p.SteamId, out b) || b.root == null)
                {
                    b = CreateBadge();
                    badges[p.SteamId] = b;
                }

                float place = Mathf.Min(targetDist, VRMaxPlaceDistance);
                b.root.transform.position = eye + dir * place;
                b.root.transform.localScale = Vector3.one * (place / VRBadgeDistance);
                // +Z away from the viewer: TextMesh reads correctly and the quads' front faces point at the viewer.
                Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.98f ? head.up : Vector3.up;
                b.root.transform.rotation = Quaternion.LookRotation(dir, up);

                PlaceInUiSpace(b);

                if (b.colorFor != p.SteamId || b.name.text != p.Name) ApplyColor(b, p.SteamId);
                if (b.name.text != p.Name) b.name.text = p.Name;
                string d = Mathf.RoundToInt(dist) + "m";
                if (b.dist.text != d) b.dist.text = d;

                if (!b.root.activeSelf) b.root.SetActive(true);
                b.used = true;
            }

            removeList.Clear();
            foreach (var kv in badges)
            {
                if (kv.Value.root == null) { removeList.Add(kv.Key); continue; }
                if (!kv.Value.used && kv.Value.root.activeSelf) kv.Value.root.SetActive(false);
            }
            for (int i = 0; i < removeList.Count; i++) badges.Remove(removeList[i]);
        }

        private static void OnBeforeRender()
        {
            try { if (VRBridge.IsVRActive) UpdateHUD(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Player HUD update failed: " + e.Message); }
        }

        /// <summary>
        /// Moves a badge from its world placement into SubmersedVR's HUD-camera space (UI layer), where the game's
        /// underwater exposure can't wash its colour out. Falls back to drawing in the world (default layer).
        /// </summary>
        private static void PlaceInUiSpace(Badge b)
        {
            Transform rig = null, uiRig = null; Camera uiCam = null;
            bool ui = VRBridge.TryGetUiSpace(out rig, out uiRig, out uiCam);
            string space = ui ? "HUD camera '" + uiCam.name + "' (far clip " + uiCam.farClipPlane + " m)" : "world camera";
            if (loggedSpace != space) { loggedSpace = space; Plugin.Log.LogInfo("Player HUD (VR): drawing markers with the " + space + "."); }

            int layer = ui ? UILayer : 0;
            if (b.root.layer != layer)
                foreach (var t in b.root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            if (!ui) return;

            var tr = b.root.transform;
            tr.position = uiRig.TransformPoint(rig.InverseTransformPoint(tr.position));
            tr.rotation = uiRig.rotation * Quaternion.Inverse(rig.rotation) * tr.rotation;
            float rs = rig.lossyScale.x, us = uiRig.lossyScale.x;
            if (rs > 0.0001f && Mathf.Abs(us / rs - 1f) > 0.001f) tr.localScale *= us / rs;
        }

        private static void ApplyColor(Badge b, ulong id)
        {
            Color c = PlayerColor(id);
            b.diamond.material.color = c;   // per-badge instance
            b.name.color = NameTint(c);
            b.dist.color = DistTint(c);
            b.colorFor = id;
        }

        private static void HideAll()
        {
            foreach (var kv in badges)
                if (kv.Value.root != null && kv.Value.root.activeSelf) kv.Value.root.SetActive(false);
        }

        public static void Cleanup()
        {
            foreach (var kv in badges)
                if (kv.Value.root != null) Object.Destroy(kv.Value.root);
            badges.Clear();
            colorIndex.Clear();
        }

        private static void EnsureMaterials()
        {
            if (diamondMat != null) return;

            // Drawn like the game's own HUD pings: after everything else (render queue 4000+) with the depth test
            // off, so the water surface, terrain or a base wall never cuts through it. In 0.11.14 the text used
            // the normal transparent queue and the water surface drew over it when your eyes were at the waterline.
            // UI/Default and GUI/Text both read their depth test from unity_GUIZTestMode.
            Shader s = Shader.Find("UI/Default");
            string used = "UI/Default";
            if (s == null) { s = Shader.Find("Sprites/Default"); used = "Sprites/Default"; }
            if (!loggedShader) { Plugin.Log.LogInfo("Player HUD (VR): badge shader " + (s != null ? used : "NONE FOUND")); loggedShader = true; }

            diamondMat = new Material(s);
            diamondMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            diamondMat.renderQueue = 4000;

            arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            textMat = new Material(arial.material);
            textMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            textMat.renderQueue = 4001;
            fontTex = arial.material.mainTexture;
        }

        private static GameObject Quad(string name, Transform parent, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.SetParent(parent, false);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        private static TextMesh Text(string name, Transform parent, float charSize, Color color, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = arial;
            tm.fontSize = 48;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = textMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return tm;
        }

        private static Badge CreateBadge()
        {
            EnsureMaterials();
            var b = new Badge();
            b.root = new GameObject("BZMP_PlayerBadge");
            Object.DontDestroyOnLoad(b.root);

            var diamond = Quad("diamond", b.root.transform, diamondMat);
            b.diamond = diamond.GetComponent<MeshRenderer>();
            diamond.transform.localPosition = Vector3.zero;
            diamond.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            diamond.transform.localScale = Vector3.one * VRDiamondSize;

            b.name = Text("name", b.root.transform, VRNameCharSize, Color.white, 0.15f);
            b.dist = Text("dist", b.root.transform, VRDistCharSize, Color.white, -0.14f);
            return b;
        }

        // ================================================================== flat-screen overlay

        private static GUIStyle labelStyleGUI;
        private static GUIStyle distStyleGUI;
        private static Texture2D markerTex;

        /// <summary>Called from Plugin.OnGUI. Flat screen only.</summary>
        public static void OnGUI()
        {
            if (VRBridge.IsVRActive) return;
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
                DrawMarkerGUI(cam, p.Position, p.Name, dist, PlayerColor(p.SteamId));
            }
        }

        private static void EnsureStylesGUI()
        {
            if (labelStyleGUI != null) return;

            labelStyleGUI = new GUIStyle(GUI.skin.label);
            labelStyleGUI.fontSize = FlatNameFont;
            labelStyleGUI.fontStyle = FontStyle.Bold;
            labelStyleGUI.alignment = TextAnchor.MiddleCenter;

            distStyleGUI = new GUIStyle(GUI.skin.label);
            distStyleGUI.fontSize = FlatDistFont;
            distStyleGUI.alignment = TextAnchor.MiddleCenter;

            const int n = 18;
            markerTex = new Texture2D(n, n, TextureFormat.ARGB32, false);
            markerTex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[n * n];
            // White, tinted per player with GUI.color when drawn; the softer rim keeps the old outlined look.
            Color fill = Color.white;
            Color edge = new Color(1f, 1f, 1f, 0.55f);
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Abs(x - c) + Mathf.Abs(y - c);
                    pixels[y * n + x] = d <= c - 1.5f ? fill : d <= c ? edge : Color.clear;
                }
            markerTex.SetPixels(pixels);
            markerTex.Apply();
        }

        private static void DrawMarkerGUI(Camera cam, Vector3 worldPos, string name, float dist, Color color)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPos + Vector3.up * FlatHeadOffset);

            float sw = Screen.width;
            float sh = Screen.height;

            bool behind = screen.z < 0f;
            if (behind) { screen.x = sw - screen.x; screen.y = sh - screen.y; }

            float gx = screen.x;
            float gy = sh - screen.y;
            float edgeMargin = 60f;

            bool onScreen = !behind && gx > edgeMargin && gx < sw - edgeMargin && gy > edgeMargin && gy < sh - edgeMargin;
            if (!onScreen)
            {
                float cx = sw * 0.5f, cy = sh * 0.5f;
                float dx = gx - cx, dy = gy - cy;
                if (Mathf.Abs(dx) < 1f && Mathf.Abs(dy) < 1f) { dx = 0f; dy = -1f; }
                float scaleX = (sw * 0.5f - edgeMargin) / Mathf.Max(Mathf.Abs(dx), 0.001f);
                float scaleY = (sh * 0.5f - edgeMargin) / Mathf.Max(Mathf.Abs(dy), 0.001f);
                float scale = Mathf.Min(scaleX, scaleY);
                if (scale < 1f) { gx = cx + dx * scale; gy = cy + dy * scale; }
            }

            float ms = FlatMarkerPx;
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(gx - ms * 0.5f, gy - ms * 0.5f, ms, ms), markerTex);
            GUI.color = prev;

            labelStyleGUI.normal.textColor = NameTint(color);
            distStyleGUI.normal.textColor = DistTint(color);

            Vector2 nameSize = labelStyleGUI.CalcSize(new GUIContent(name));
            GUI.Label(new Rect(gx - nameSize.x * 0.5f, gy - ms * 0.5f - nameSize.y - 3f, nameSize.x, nameSize.y), name, labelStyleGUI);

            string distText = Mathf.RoundToInt(dist) + "m";
            Vector2 distSize = distStyleGUI.CalcSize(new GUIContent(distText));
            GUI.Label(new Rect(gx - distSize.x * 0.5f, gy + ms * 0.5f + 3f, distSize.x, distSize.y), distText, distStyleGUI);
        }
    }
}
