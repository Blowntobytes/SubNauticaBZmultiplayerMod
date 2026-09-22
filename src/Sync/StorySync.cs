using System;
using HarmonyLib;
using BZMultiplayer.Net;
using Story;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Story / progression sync: story goals (which drive radio calls, scripted events, PDA voice lines), PDA log entries,
    /// encyclopedia entries, known blueprints and scanner unlocks. Each is broadcast when it happens and applied on the
    /// other side through the same game call, so the same triggers fire for everyone. A joiner receives the state that
    /// existed before they joined through the save transfer.
    /// </summary>
    public static class StorySync
    {
        public const byte KindGoal = 1, KindKnownTech = 2, KindScan = 3, KindEncyclopedia = 4, KindLog = 5;

        private static SteamNet net;

        // Goals we are applying because someone else triggered them. Kept until the goal actually executes, because a
        // goal with a delay runs later through the scheduler.
        private static readonly System.Collections.Generic.HashSet<string> remoteGoals = new System.Collections.Generic.HashSet<string>();
        private static int muteDepth;      // >0: the dialogue this goal wants to play is not for us
        private static int remoteDepth;    // >0: we are executing a goal someone else triggered

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;
            Post(harmony, AccessTools.Method(typeof(StoryGoalManager), "OnGoalComplete", new[] { typeof(string) }), "GoalPostfix");
            Post(harmony, AccessTools.Method(typeof(KnownTech), "Add", new[] { typeof(TechType), typeof(bool), typeof(bool) }), "KnownTechPostfix");
            Post(harmony, AccessTools.Method(typeof(PDAScanner), "Unlock", new[] { typeof(PDAScanner.EntryData), typeof(bool), typeof(bool), typeof(bool) }), "ScanPostfix");
            Post(harmony, AccessTools.Method(typeof(PDAEncyclopedia), "Add", new[] { typeof(string), typeof(bool), typeof(bool) }), "EncyPostfix");
            Post(harmony, AccessTools.Method(typeof(PDALog), "Add", new[] { typeof(string), typeof(bool) }), "LogPostfix");

            // Story audio: a goal someone else triggered executes here too. Databank/creature-discovery entries are
            // silent for everyone but the finder; the main story lines play for all, queued behind whatever is talking.
            var exec = AccessTools.Method(typeof(StoryGoal), "Execute", new[] { typeof(string), typeof(Story.GoalType), typeof(bool), typeof(bool) });
            if (exec != null) harmony.Patch(exec, prefix: new HarmonyMethod(typeof(StorySync), "ExecutePrefix"), postfix: new HarmonyMethod(typeof(StorySync), "ExecuteFinished"));
            else Plugin.Log.LogWarning("StorySync: StoryGoal.Execute not found; story audio will not be filtered.");

            foreach (var m in typeof(SoundQueue).GetMethods())
            {
                if (m.Name != "Play") continue;
                var ps = m.GetParameters();
                if (ps.Length < 3 || ps[2].ParameterType != typeof(bool)) continue;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(StorySync), ps[0].ParameterType == typeof(string) ? "SoundQueuePlayString" : "SoundQueuePlayAsset"));
            }
        }

        // ------------------------------------------------------------------ story audio

        private struct ExecState { public bool Muted; public bool Remote; }

        private static void ExecutePrefix(string key, Story.GoalType goalType, out ExecState __state)
        {
            __state = new ExecState();
            if (string.IsNullOrEmpty(key) || !remoteGoals.Remove(key)) return;
            __state.Remote = true;
            remoteDepth++;
            // Mute audio for all non-story-critical remote discoveries.
            // Story goals (GoalType.Story) are critical narrative — audio plays for everyone.
            // Encyclopedia, PDA, and other goal types are discovery material — notification only, no audio.
            bool isStoryCritical = goalType == Story.GoalType.Story;
            if (!isStoryCritical && !Plugin.RemoteDatabankAudio.Value) { __state.Muted = true; muteDepth++; }
        }

        private static void ExecuteFinished(ExecState __state)
        {
            if (__state.Muted && muteDepth > 0) muteDepth--;
            if (__state.Remote && remoteDepth > 0) remoteDepth--;
        }

        /// <summary>Silence a line that is not meant for this player, and queue the ones that are.</summary>
        private static bool Filter(ref bool allowQueueing)
        {
            if (muteDepth > 0) return false;
            if (remoteDepth > 0) allowQueueing = true; // never cut off whatever this player is already listening to
            return true;
        }

        private static bool SoundQueuePlayString(string sound, ref bool allowQueueing) { return Filter(ref allowQueueing); }
        private static bool SoundQueuePlayAsset(ref bool allowQueueing) { return Filter(ref allowQueueing); }

        private static void Post(Harmony h, System.Reflection.MethodInfo m, string postfix)
        {
            if (m == null) { Plugin.Log.LogWarning("StorySync: hook target for " + postfix + " not found."); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(StorySync), postfix));
        }

        // ------------------------------------------------------------------ local events

        private static void GoalPostfix(string key, bool __result)
        {
            if (!__result || !WorldSync.CanSend || string.IsNullOrEmpty(key)) return;
            net.SendStory(KindGoal, key, 0);
            Plugin.Log.LogInfo("Story goal sent: " + key);
        }

        private static void KnownTechPostfix(TechType techType, bool __result)
        {
            if (!__result || !WorldSync.CanSend) return;
            net.SendStory(KindKnownTech, "", (int)techType);
            Plugin.Log.LogInfo("Blueprint sent: " + techType);
        }

        private static void ScanPostfix(PDAScanner.EntryData entryData)
        {
            if (!WorldSync.CanSend || entryData == null) return;
            net.SendStory(KindScan, "", (int)entryData.key);
            Plugin.Log.LogInfo("Scan unlock sent: " + entryData.key);
        }

        private static void EncyPostfix(string key)
        {
            if (!WorldSync.CanSend || string.IsNullOrEmpty(key)) return;
            net.SendStory(KindEncyclopedia, key, 0);
        }

        private static void LogPostfix(string key)
        {
            if (!WorldSync.CanSend || string.IsNullOrEmpty(key)) return;
            net.SendStory(KindLog, key, 0);
        }

        // ------------------------------------------------------------------ remote events

        public static void OnStory(byte kind, string key, int techType)
        {
            if (!LocalPlayerSync.InWorld) return;
            WorldSync.applyingRemote = true;
            try
            {
                switch (kind)
                {
                    case KindGoal:
                        remoteGoals.Add(key);
                        if (StoryGoalManager.main != null && StoryGoalManager.main.OnGoalComplete(key)) Plugin.Log.LogInfo("Story goal applied: " + key);
                        else remoteGoals.Remove(key);
                        break;
                    case KindKnownTech:
                    {
                        // Show the notification (second arg = true) but mute the audio.
                        muteDepth++;
                        try { if (KnownTech.Add((TechType)techType, true, false)) Plugin.Log.LogInfo("Blueprint applied: " + (TechType)techType); }
                        finally { if (muteDepth > 0) muteDepth--; }
                        break;
                    }
                    case KindScan:
                    {
                        var entry = PDAScanner.GetEntryData((TechType)techType);
                        if (entry != null)
                        {
                            // Someone else scanned it: we get the blueprint and the notification, not the narration.
                            muteDepth++;
                            try {
                            AccessTools.Method(typeof(PDAScanner), "Unlock", new[] { typeof(PDAScanner.EntryData), typeof(bool), typeof(bool), typeof(bool) }).Invoke(null, new object[] { entry, true, true, false });
                            Plugin.Log.LogInfo("Scan unlock applied: " + (TechType)techType);
                            }
                            finally { if (muteDepth > 0) muteDepth--; }
                        }
                        break;
                    }
                    case KindEncyclopedia:
                    {
                        // Discovery notification shows; audio is muted (not story-critical).
                        muteDepth++;
                        try { PDAEncyclopedia.Add(key, false, false); }
                        finally { if (muteDepth > 0) muteDepth--; }
                        break;
                    }
                    case KindLog:
                        PDALog.Add(key, false);
                        break;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Story apply failed (" + kind + " " + key + "): " + e.Message); }
            finally { WorldSync.applyingRemote = false; }
        }
    }
}
