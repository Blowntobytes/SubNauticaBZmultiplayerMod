using System;
using HarmonyLib;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Prevents the host from freezing time when a menu is opened, so the world stays live for other players.
    /// The game calls UWE.FreezeTime.Set(id, value) to pause; this prefix skips it when KeepWorldRunning is on
    /// and the host is in a multiplayer session.
    /// </summary>
    public static class TimeSync
    {
        public static void Install(Harmony harmony)
        {
            var setMethod = AccessTools.Method(typeof(UWE.FreezeTime), "Set",
                new[] { typeof(UWE.FreezeTime.Id), typeof(float) });
            if (setMethod == null)
            {
                Plugin.Log.LogWarning("TimeSync: UWE.FreezeTime.Set not found; time-freeze prevention disabled.");
                return;
            }
            harmony.Patch(setMethod, prefix: new HarmonyMethod(typeof(TimeSync), "SetPrefix"));
        }

        /// <summary>
        /// Skip freeze requests while hosting with KeepWorldRunning enabled.
        /// Parameter name 'value' must match the game's IL exactly.
        /// </summary>
        private static bool SetPrefix(float value)
        {
            if (!Plugin.KeepWorldRunning.Value) return true;
            if (Plugin.Instance == null || Plugin.Instance.Net == null || !Plugin.Instance.Net.IsHost) return true;
            // Allow unfreezes (value == 0) so the game can resume normally.
            if (value == 0f) return true;
            return false; // skip the freeze
        }
    }
}
