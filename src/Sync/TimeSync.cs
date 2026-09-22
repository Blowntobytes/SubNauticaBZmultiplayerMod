using HarmonyLib;
using UnityEngine;
using UWE;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Keeps the world running while someone opens their PDA or a menu. Below Zero is single player, so it freezes
    /// time whenever the PDA, the pause menu or a text box opens - which in a session would stop the world for
    /// everyone else too (and makes the other player look frozen to you). Loading and saving still freeze, because
    /// those genuinely must not be interrupted.
    ///
    /// Everything funnels through FreezeTime.Set(id, strength): Begin is Set(id, 1), End is Set(id, 0), and the PDA
    /// bypasses both and calls Set(id, t) directly every frame as it slides open. Patching Begin/End alone (the first
    /// two versions of this file) never stopped the PDA at all, and swallowing only the bookkeeping calls around it
    /// left the game's freezer list and its cached time scale disagreeing - a joiner arrived with time at zero and
    /// nothing holding it. So: one prefix on Set, and no counters.
    /// </summary>
    public static class TimeSync
    {
        private static Net.SteamNet net;
        private static float zeroSince = -1f;

        // The accessibility option "PDA pauses the game" (MiscSettings.pdaPause) is the switch the PDA itself reads
        // every frame to decide whether to touch time at all. In a session it is forced off and put back afterwards.
        private static bool wasInSession;
        private static bool savedPdaPause;
        private static bool pdaPauseOverridden;

        private static readonly System.Reflection.FieldInfo cachedTimeScale = AccessTools.Field(typeof(FreezeTime), "cachedTimeScale");

        public static void Install(Harmony harmony, Net.SteamNet steamNet)
        {
            net = steamNet;
            var set = AccessTools.Method(typeof(FreezeTime), "Set", new[] { typeof(FreezeTime.Id), typeof(float) });
            if (set == null)
            {
                Plugin.Log.LogWarning("TimeSync: UWE.FreezeTime.Set not found; the PDA will still pause the world.");
                return;
            }
            harmony.Patch(set, prefix: new HarmonyMethod(typeof(TimeSync), "SetPrefix"));
        }

        /// <summary>The freezes that are one player's business and must not stop everyone else's world.</summary>
        private static bool IsPersonal(FreezeTime.Id id)
        {
            return id == FreezeTime.Id.PDA
                || id == FreezeTime.Id.IngameMenu
                || id == FreezeTime.Id.TextInput
                || id == FreezeTime.Id.FeedbackPanel
                || id == FreezeTime.Id.ApplicationFocus;
        }

        // A strength of zero is a release: always allowed, so a freeze that began before the session (PDA already
        // open when hosting starts) can still be let go. Anything above zero would add or raise a freezer, and for a
        // personal id in a session that is refused outright - nothing is ever added, so nothing can be left behind.
        // Harmony binds prefix arguments by NAME, and the game calls this one "value".
        private static bool SetPrefix(FreezeTime.Id id, float value)
        {
            if (value <= 0f) return true;
            if (net == null || !net.IsInSession || !IsPersonal(id)) return true;
            return false;
        }

        /// <summary>
        /// Safety net for the state the game can never recover from on its own: time at zero with no freezer holding
        /// it (the cached scale was captured while already frozen). In a session that is never wanted; unstick it.
        /// </summary>
        public static void Update()
        {
            bool inSession = net != null && net.IsInSession;
            if (inSession != wasInSession)
            {
                wasInSession = inSession;
                try
                {
                    if (inSession)
                    {
                        savedPdaPause = MiscSettings.pdaPause;
                        pdaPauseOverridden = true;
                        if (savedPdaPause) { MiscSettings.pdaPause = false; Plugin.Log.LogInfo("Session started: 'PDA pauses the game' turned off for the session."); }
                    }
                    else if (pdaPauseOverridden)
                    {
                        pdaPauseOverridden = false;
                        if (savedPdaPause) { MiscSettings.pdaPause = true; Plugin.Log.LogInfo("Session ended: 'PDA pauses the game' restored."); }
                    }
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("Could not toggle the PDA pause setting: " + e.Message); }
            }
            // The setting can be flipped from the options screen mid-session; keep it off while the session runs.
            if (inSession && pdaPauseOverridden && MiscSettings.pdaPause) MiscSettings.pdaPause = false;

            if (!inSession || !LocalPlayerSync.InWorld) { zeroSince = -1f; return; }
            bool stuck = Time.timeScale <= 0.0001f && !FreezeTime.HasFreezers();
            if (!stuck) { zeroSince = -1f; return; }
            if (zeroSince < 0f) { zeroSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - zeroSince < 1f) return;
            Time.timeScale = 1f;
            // The game "restores" its cached scale whenever the last freezer leaves; if that cache is 0 (captured
            // during a load) every later freeze would land back on 0. Repair the cache too, not just the scale.
            try { if (cachedTimeScale != null) cachedTimeScale.SetValue(null, 1f); } catch { }
            zeroSince = -1f;
            Plugin.Log.LogWarning("Time scale was 0 with no freezer holding it; restored to 1 (and the game's cached scale).");
        }
    }
}
