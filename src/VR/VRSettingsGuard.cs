using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace BZMultiplayer.VR
{
    /// <summary>
    /// Keeps two SubmersedVR settings the user reported turning off on every join: "Enable desktop cinematics"
    /// (the game's VROptions.enableCinematics) and "Put survival meter on left wrist" (SubmersedVR.Settings.PutBarsOnWrist).
    /// Both live in the game's options file, which this machine's log shows failing to load at startup
    /// ("RegisterSaveGame: load operation for options was not successful").
    ///
    /// The mod remembers each value in its own config when the player sets it in the options menu (or when it is
    /// seen switched on), and if the value changes anywhere else - not in the options menu - it is put back and the
    /// log says so. Nothing here ever turns a setting on that the player has not turned on.
    /// </summary>
    public static class VRSettingsGuard
    {
        private class Guarded
        {
            public string Name;
            public ConfigEntry<int> Remembered;   // -1 unknown, 0 off, 1 on
            public Func<bool?> Get;
            public Action<bool> Set;
        }

        private static Guarded[] guarded;
        private static float nextCheck;
        private static bool resolved;

        public static void Init(ConfigFile config)
        {
            var cinematics = new Guarded
            {
                Name = "Enable desktop cinematics",
                Remembered = config.Bind("VR", "RememberDesktopCinematics", -1, "Set by the mod: SubmersedVR's 'Enable desktop cinematics' as you last set it (-1 not known yet, 0 off, 1 on). Put back if something turns it off outside the options menu."),
                Get = () => VROptions.enableCinematics,
                Set = v => VROptions.enableCinematics = v
            };
            var wrist = new Guarded
            {
                Name = "Put survival meter on left wrist",
                Remembered = config.Bind("VR", "RememberSurvivalMeterOnWrist", -1, "Set by the mod: SubmersedVR's 'Put survival meter on left wrist' as you last set it (-1 not known yet, 0 off, 1 on). Put back if something turns it off outside the options menu."),
                Get = GetWrist,
                Set = SetWrist
            };
            guarded = new[] { cinematics, wrist };
        }

        // ------------------------------------------------------------------ SubmersedVR.Settings.PutBarsOnWrist via reflection

        private static FieldInfo fWrist, fWristChanged;

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "SubmersedVR") continue;
                var t = asm.GetType("SubmersedVR.Settings");
                if (t == null) break;
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                fWrist = t.GetField("PutBarsOnWrist", st);
                fWristChanged = t.GetField("PutBarsOnWristChanged", st);
                break;
            }
        }

        private static bool? GetWrist()
        {
            Resolve();
            if (fWrist == null) return null;
            return (bool)fWrist.GetValue(null);
        }

        private static void SetWrist(bool v)
        {
            Resolve();
            if (fWrist == null) return;
            fWrist.SetValue(null, v);
            // Tell SubmersedVR so the meter actually moves (the same event its options toggle raises).
            var d = fWristChanged != null ? fWristChanged.GetValue(null) as Delegate : null;
            if (d != null)
            {
                try { d.DynamicInvoke(v); }
                catch (Exception e) { Plugin.Log.LogWarning("Survival meter move failed: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ tick

        private static bool OptionsOpen()
        {
            var panels = UnityEngine.Object.FindObjectsOfType<uGUI_OptionsPanel>();
            for (int i = 0; i < panels.Length; i++)
                if (panels[i] != null && panels[i].isActiveAndEnabled) return true;
            return false;
        }

        /// <summary>Called from Plugin.Update. Checks once a second.</summary>
        public static void Tick()
        {
            if (guarded == null || !VRBridge.IsAvailable) return;
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;

            bool? optionsOpen = null;
            foreach (var g in guarded)
            {
                bool? cur;
                try { cur = g.Get(); } catch { cur = null; }
                if (!cur.HasValue) continue;
                int now = cur.Value ? 1 : 0;
                int want = g.Remembered.Value;
                if (want == now) continue;

                if (!optionsOpen.HasValue) optionsOpen = OptionsOpen();
                if (optionsOpen.Value || (want < 0 && now == 1))
                {
                    // Changed by the player in the options menu (or first seen switched on): remember it.
                    g.Remembered.Value = now;
                    Plugin.Log.LogInfo("SubmersedVR setting '" + g.Name + "' is " + (cur.Value ? "on" : "off") + "; the mod will keep it that way.");
                }
                else if (want >= 0)
                {
                    try
                    {
                        g.Set(want == 1);
                        Plugin.Log.LogInfo("SubmersedVR setting '" + g.Name + "' was switched " + (cur.Value ? "on" : "off") + " outside the options menu; put back " + (want == 1 ? "on" : "off") + ".");
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Could not restore '" + g.Name + "': " + e.Message); }
                }
            }
        }
    }
}
