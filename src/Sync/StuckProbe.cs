using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// When a player reports "I was frozen on arrival", the log needs to say WHICH lock the game was holding, not
    /// leave it to guesswork: time scale, a FreezeTime freezer, cinematic mode, the PDA, disabled input, a frozen
    /// mixin, or the player mode. This writes that state a few seconds after every spawn, and once more on leaving.
    /// </summary>
    public static class StuckProbe
    {
        private static readonly float[] At = { 3f, 10f, 25f };
        private static float enteredAt = -1f;
        private static int next;
        private static bool wasInWorld;

        public static void Update()
        {
            bool inWorld = LocalPlayerSync.InWorld;
            if (inWorld && !wasInWorld) { enteredAt = Time.unscaledTime; next = 0; }
            if (!inWorld && wasInWorld && enteredAt >= 0f) Report("leaving the world");
            wasInWorld = inWorld;
            if (!inWorld || enteredAt < 0f || next >= At.Length) return;
            if (Time.unscaledTime - enteredAt >= At[next])
            {
                Report(At[next].ToString("F0") + "s after spawn");
                next++;
            }
        }

        private static void Report(string when)
        {
            try
            {
                var p = Player.main;
                var sb = new StringBuilder("Player state (" + when + "): ");
                sb.Append("timeScale ").Append(Time.timeScale.ToString("F2"));

                try
                {
                    sb.Append(", freezers ").Append(UWE.FreezeTime.HasFreezers() ? UWE.FreezeTime.GetTopmostId().ToString() : "none");
                }
                catch (Exception e) { sb.Append(", freezers ?(").Append(e.Message).Append(')'); }

                if (p == null) { sb.Append(", no Player"); Plugin.Log.LogInfo(sb.ToString()); return; }

                sb.Append(", cinematic ").Append(p.cinematicModeActive);
                sb.Append(", mode ").Append(p.GetMode());
                try { sb.Append(", pda ").Append(p.pda != null && p.pda.isInUse ? "open" : "closed"); } catch { sb.Append(", pda ?"); }

                var pc = p.playerController;
                if (pc != null)
                {
                    sb.Append(", input ").Append(pc.inputEnabled ? "on" : "OFF");
                    sb.Append(", motor ").Append(pc.activeController != null ? pc.activeController.GetType().Name : "none");
                    if (pc.activeController != null) sb.Append(pc.activeController.enabled ? "" : "(disabled)");
                }
                else sb.Append(", no controller");

                try
                {
                    var fm = p.frozenMixin;
                    if (fm != null)
                    {
                        var isFrozen = AccessTools.Method(fm.GetType(), "IsFrozen");
                        if (isFrozen != null) sb.Append(", frozenMixin ").Append(isFrozen.Invoke(fm, null));
                    }
                }
                catch { }

                sb.Append(", at ").Append(p.transform.position.ToString("F1"));
                sb.Append(", ").Append(p.IsUnderwater() ? "underwater" : "dry");
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Player state probe failed: " + e.Message); }
        }
    }
}
