using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using BZMultiplayer.Sync;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// Dial in where an item sits in another player's hand, by eye, while looking at them.
    ///
    /// The automatic route is a dead end: every tool prefab carries a ModelPlug whose plugOrigin is null, so the
    /// game's own grip data simply is not there to read. What is left is to nudge the model until it looks right
    /// and write the numbers down. This does exactly that, live, and saves them into HeldItemOffsets so the result
    /// ships with the build and applies on every machine.
    ///
    /// Everything here is keyboard-driven and off by default, so it costs a VR session nothing.
    /// </summary>
    public static class HeldItemTuner
    {
        private enum Mode { Position, Rotation, Scale }

        private static bool active;
        private static Mode mode = Mode.Position;
        private static RemotePlayer target;
        private static TechType tuning = TechType.None;

        // The live edit, in the same units the config uses.
        private static Vector3 pos;
        // Rotation is kept as a quaternion and nudged about the item's own axes. Editing three Euler angles instead
        // hit gimbal lock the moment an item sat pitched near 90 degrees (most tools do), which folded the Y and Z
        // keys onto one axis and lost a degree of freedom.
        private static Quaternion rot = Quaternion.identity;
        private static float scale = 1f;

        // Where the model sat before we started, so each nudge is applied to a clean baseline.
        private static Vector3 basePos;
        private static Quaternion baseRot;
        private static Vector3 baseScale;
        private static Transform bound;

        public static bool Active { get { return active; } }

        public static void Update()
        {
            if (Plugin.TuneHeldItemsKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.TuneHeldItemsKey.Value)) Toggle();
            if (!active) return;

            // Leaving the world or the session ends the sitting rather than editing thin air.
            if (!LocalPlayerSync.InWorld) { Stop("left the world"); return; }

            var t = FindTarget();
            if (t == null) { Hint(WhyNoTarget()); return; }

            if (!ReferenceEquals(t, target) || t.ShownHeld != tuning || bound == null || bound != t.HeldModel)
                Bind(t);

            if (bound == null) return;

            if (Down(Plugin.TuneModeKey)) mode = (Mode)(((int)mode + 1) % 3);
            if (Down(Plugin.TuneResetKey)) { pos = Vector3.zero; rot = Quaternion.identity; scale = 1f; }
            if (Down(Plugin.TuneSaveKey)) { Save(); return; }

            // Numpad only. The game already owns Tab (the PDA), the arrows and most letters, so nothing here can
            // fire a game action by accident while you are lining an item up.
            //
            // Movement is a RATE, per second of holding the key, not a step per frame: the first version stepped
            // per frame, which at 90 fps was a metre a second and made Shift look dead. Hold Shift for slow.
            bool slow = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float dt = Time.unscaledDeltaTime;
            float mps = slow ? 0.01f : 0.06f;         // metres per second
            float dps = slow ? 4f : 30f;              // degrees per second
            float sps = slow ? 0.05f : 0.3f;          // scale per second

            Vector3 d = Vector3.zero;
            if (Input.GetKey(KeyCode.Keypad4)) d.x -= 1f;
            if (Input.GetKey(KeyCode.Keypad6)) d.x += 1f;
            if (Input.GetKey(KeyCode.Keypad8)) d.y += 1f;
            if (Input.GetKey(KeyCode.Keypad2)) d.y -= 1f;
            if (Input.GetKey(KeyCode.Keypad9)) d.z += 1f;
            if (Input.GetKey(KeyCode.Keypad3)) d.z -= 1f;

            // A single tap always moves at least one small notch, so a quick press is never lost to a short frame.
            Vector3 tap = Vector3.zero;
            if (Input.GetKeyDown(KeyCode.Keypad4)) tap.x -= 1f;
            if (Input.GetKeyDown(KeyCode.Keypad6)) tap.x += 1f;
            if (Input.GetKeyDown(KeyCode.Keypad8)) tap.y += 1f;
            if (Input.GetKeyDown(KeyCode.Keypad2)) tap.y -= 1f;
            if (Input.GetKeyDown(KeyCode.Keypad9)) tap.z += 1f;
            if (Input.GetKeyDown(KeyCode.Keypad3)) tap.z -= 1f;

            if (mode == Mode.Position) pos += d * mps * dt + tap * (slow ? 0.0005f : 0.002f);
            else if (mode == Mode.Rotation)
            {
                // Each axis turns about the item's CURRENT local axis (the coloured rod), never about a fixed world
                // angle, so the three keys always stay independent.
                Vector3 deg = d * dps * dt + tap * (slow ? 0.25f : 1f);
                if (deg.x != 0f) rot = rot * Quaternion.AngleAxis(deg.x, Vector3.right);
                if (deg.y != 0f) rot = rot * Quaternion.AngleAxis(deg.y, Vector3.up);
                if (deg.z != 0f) rot = rot * Quaternion.AngleAxis(deg.z, Vector3.forward);
            }
            else
            {
                float ds = 0f;
                if (Input.GetKey(KeyCode.KeypadPlus) || Input.GetKey(KeyCode.Keypad8)) ds += sps * dt;
                if (Input.GetKey(KeyCode.KeypadMinus) || Input.GetKey(KeyCode.Keypad2)) ds -= sps * dt;
                if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Keypad8)) ds += slow ? 0.002f : 0.01f;
                if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Keypad2)) ds -= slow ? 0.002f : 0.01f;
                scale = Mathf.Max(0.05f, scale + ds);
            }

            Apply();
            Hint(Describe());
        }

        private static void Toggle()
        {
            if (active) { Stop("done"); return; }
            if (!LocalPlayerSync.InWorld) { ErrorMessage.AddMessage("Held-item tuning needs you to be in the world."); return; }
            active = true;
            target = null; bound = null; tuning = TechType.None;
            pos = Vector3.zero; rot = Quaternion.identity; scale = 1f;
            Plugin.Log.LogInfo("Held-item tuning on.");
            ErrorMessage.AddMessage("Held-item tuning ON. Numpad 4/6 left-right, 8/2 up-down, 9/3 forward-back, +/- size. "
                                  + Plugin.TuneModeKey.Value + " switches move/rotate/scale, " + Plugin.TuneSaveKey.Value
                                  + " saves, " + Plugin.TuneResetKey.Value + " resets, hold Shift to go slow. Red = X, green = Y, blue = Z.");
        }

        private static void Stop(string why)
        {
            if (!active) return;
            active = false;
            bound = null; target = null;
            HideGizmo();
            Plugin.Log.LogInfo("Held-item tuning off (" + why + ").");
            ErrorMessage.AddMessage("Held-item tuning off.");
        }

        /// <summary>The nearest other player who is actually showing something in their hand.</summary>
        private static RemotePlayer FindTarget()
        {
            var players = Plugin.Instance != null ? Plugin.Instance.Players : null;
            if (players == null) return null;

            RemotePlayer best = null;
            float bestDist = float.MaxValue;
            var me = Player.main != null ? Player.main.transform.position : Vector3.zero;
            foreach (var p in players.All)
            {
                var m = p.HeldModel;
                if (m == null || p.ShownHeld == TechType.None) continue;
                float d = (m.position - me).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        /// <summary>Which stage is missing, so "waiting" is never the whole answer.</summary>
        private static string WhyNoTarget()
        {
            var players = Plugin.Instance != null ? Plugin.Instance.Players : null;
            if (players == null || players.Count == 0) return "Tuning: no other player in the session yet.";
            var sb = new StringBuilder("Tuning: waiting - ");
            foreach (var p in players.All)
            {
                sb.Append(p.Name).Append(' ');
                if (p.ShownHeld == TechType.None) sb.Append("has sent no held item");
                else if (p.HeldModel == null) sb.Append("is holding ").Append(p.ShownHeld).Append(" but its model is still loading");
                else sb.Append("ok");
                sb.Append("; ");
            }
            return sb.ToString();
        }

        private static void Bind(RemotePlayer t)
        {
            target = t;
            tuning = t.ShownHeld;
            bound = t.HeldModel;
            if (bound == null) return;

            basePos = bound.localPosition;
            baseRot = bound.localRotation;
            baseScale = bound.localScale;

            // Start from whatever this item already has configured, so a second pass refines rather than restarts.
            Vector3 p0, e0; float s0;
            if (TryExisting(tuning, out p0, out e0, out s0)) { pos = p0; rot = Quaternion.Euler(e0); scale = s0; }
            else { pos = Vector3.zero; rot = Quaternion.identity; scale = 1f; }

            // The baseline must exclude a correction already applied when the model was built, or it double-counts.
            if (TryExisting(tuning, out p0, out e0, out s0))
            {
                baseRot = baseRot * Quaternion.Inverse(Quaternion.Euler(e0));
                basePos = basePos - baseRot * p0;
                if (Mathf.Abs(s0) > 0.0001f) baseScale = baseScale / s0;
            }

            Apply();
            ShowGizmo(bound);
            Plugin.Log.LogInfo("Tuning " + tuning + " on " + t.Name + ".");
        }

        private static void Apply()
        {
            if (bound == null) return;
            bound.localPosition = basePos + baseRot * pos;
            bound.localRotation = baseRot * rot;
            bound.localScale = baseScale * scale;
        }

        private static string Describe()
        {
            return "Tuning " + tuning + "  [" + mode + "]"
                 + "  pos " + pos.ToString("F3")
                 + "  rot " + Signed(rot.eulerAngles).ToString("F0")
                 + "  scale " + scale.ToString("F2", CultureInfo.InvariantCulture)
                 + "   (" + Plugin.TuneModeKey.Value + " switches, " + Plugin.TuneSaveKey.Value + " saves)";
        }

        // ------------------------------------------------------------------ the axis marker

        // Three coloured rods from the item's pivot - X red, Y green, Z blue, the Unity convention - plus a white
        // dot at the pivot itself. Parented to the model, so it turns with every nudge and shows which way each
        // numpad key will push. Only exists while tuning.
        private static GameObject gizmo;

        private static void ShowGizmo(Transform parent)
        {
            HideGizmo();
            if (parent == null) return;
            try
            {
                gizmo = new GameObject("BZMP_TuneGizmo");
                gizmo.transform.SetParent(parent, false);

                // Undo the item's own scale so the rods stay the same size whatever the model is.
                var ls = parent.lossyScale;
                gizmo.transform.localScale = new Vector3(Inv(ls.x), Inv(ls.y), Inv(ls.z));

                const float len = 0.12f, thick = 0.004f;
                Rod("X", Color.red,   new Vector3(len * 0.5f, 0, 0), new Vector3(len, thick, thick));
                Rod("Y", Color.green, new Vector3(0, len * 0.5f, 0), new Vector3(thick, len, thick));
                Rod("Z", Color.blue,  new Vector3(0, 0, len * 0.5f), new Vector3(thick, thick, len));
                var dot = Prim(PrimitiveType.Sphere, "pivot", Color.white);
                dot.transform.localScale = Vector3.one * 0.012f;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Tuning gizmo could not be drawn: " + e.Message); }
        }

        private static float Inv(float v) { return Mathf.Abs(v) < 0.0001f ? 1f : 1f / v; }

        private static void Rod(string name, Color c, Vector3 centre, Vector3 size)
        {
            var g = Prim(PrimitiveType.Cube, name, c);
            g.transform.localPosition = centre;
            g.transform.localScale = size;
        }

        private static GameObject Prim(PrimitiveType type, string name, Color c)
        {
            var g = GameObject.CreatePrimitive(type);
            g.name = name;
            g.transform.SetParent(gizmo.transform, false);
            var col = g.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            var r = g.GetComponent<Renderer>();
            if (r != null)
            {
                r.material = new Material(GizmoShader()) { color = c };
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return g;
        }

        private static Shader GizmoShader()
        {
            // Whatever unlit shader this build still ships; the UI ones are always present.
            foreach (var n in new[] { "Unlit/Color", "Sprites/Default", "UI/Default", "Legacy Shaders/Diffuse", "Standard" })
            {
                var sh = Shader.Find(n);
                if (sh != null) return sh;
            }
            return Shader.Find("Diffuse");
        }

        private static void HideGizmo()
        {
            if (gizmo != null) Object.Destroy(gizmo);
            gizmo = null;
        }

        private static bool Down(BepInEx.Configuration.ConfigEntry<KeyCode> key)
        {
            return key != null && key.Value != KeyCode.None && Input.GetKeyDown(key.Value);
        }

        private static float lastHint;
        private static void Hint(string s)
        {
            if (Time.unscaledTime - lastHint < 0.25f) return;
            lastHint = Time.unscaledTime;
            ErrorMessage.AddMessage(s);
        }

        // ------------------------------------------------------------------ reading and writing the config string

        private static bool TryExisting(TechType tt, out Vector3 p, out Vector3 e, out float s)
        {
            p = Vector3.zero; e = Vector3.zero; s = 1f;
            foreach (var chunk in (Plugin.HeldItemOffsets.Value ?? "").Split(';'))
            {
                var entry = chunk.Trim();
                int colon = entry.IndexOf(':');
                if (colon <= 0) continue;
                TechType found;
                if (!TechTypeExtensions.FromString(entry.Substring(0, colon).Trim(), out found, true) || found != tt) continue;

                var parts = entry.Substring(colon + 1).Split(',');
                if (parts.Length != 7) continue;
                var n = new float[7];
                bool ok = true;
                for (int i = 0; i < 7; i++)
                    if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) { ok = false; break; }
                if (!ok) continue;
                p = new Vector3(n[0], n[1], n[2]); e = new Vector3(n[3], n[4], n[5]); s = n[6];
                return true;
            }
            return false;
        }

        /// <summary>Write this item's numbers into HeldItemOffsets, replacing any entry it already had.</summary>
        private static void Save()
        {
            if (tuning == TechType.None) return;

            var kept = new List<string>();
            foreach (var chunk in (Plugin.HeldItemOffsets.Value ?? "").Split(';'))
            {
                var entry = chunk.Trim();
                if (entry.Length == 0) continue;
                int colon = entry.IndexOf(':');
                TechType found;
                if (colon > 0 && TechTypeExtensions.FromString(entry.Substring(0, colon).Trim(), out found, true) && found == tuning) continue;
                kept.Add(entry);
            }

            // Saved as Euler angles for the config, which is fine: Quaternion.Euler(eulerAngles) rebuilds the same
            // rotation. Only the live editing needed to avoid angles.
            var e = Signed(rot.eulerAngles);
            var sb = new StringBuilder();
            sb.Append(tuning.AsString(false)).Append(':')
              .Append(F(pos.x)).Append(',').Append(F(pos.y)).Append(',').Append(F(pos.z)).Append(',')
              .Append(F(e.x)).Append(',').Append(F(e.y)).Append(',').Append(F(e.z)).Append(',')
              .Append(F(scale));
            kept.Add(sb.ToString());

            Plugin.HeldItemOffsets.Value = string.Join(";", kept.ToArray());
            Plugin.Log.LogInfo("Held item correction saved: " + sb + "   (full setting: " + Plugin.HeldItemOffsets.Value + ")");
            ErrorMessage.AddMessage("Saved " + tuning + ". Send me the line from the log and I'll bake it into the build.");
        }

        private static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        /// <summary>Euler angles in -180..180 rather than 0..360, so the config reads like a human wrote it.</summary>
        private static Vector3 Signed(Vector3 e)
        {
            return new Vector3(e.x > 180f ? e.x - 360f : e.x, e.y > 180f ? e.y - 360f : e.y, e.z > 180f ? e.z - 360f : e.z);
        }
    }
}
