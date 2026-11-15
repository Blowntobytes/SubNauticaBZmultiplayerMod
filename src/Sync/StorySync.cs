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
        public const byte KindGoal = 1, KindKnownTech = 2, KindScan = 3, KindEncyclopedia = 4, KindLog = 5, KindAnalyze = 6;

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
            Post(harmony, AccessTools.Method(typeof(KnownTech), "Analyze", new[] { typeof(TechType), typeof(bool), typeof(bool) }), "AnalyzePostfix");

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
            // Mute audio for all non-Story goal types unless the user opts in with RemoteDatabankAudio.
            // Story goals (radio calls, scripted events) always play for everyone.
            // Mute audio for all non-Story goal types unless the user opts in with RemoteDatabankAudio.
            // Story goals (radio calls, scripted events) always play for everyone. (0.13.5 unmuted PDA goals by mistake:
            // the cutscene audio already played once for the other player in 0.13.4.)
            if (goalType != Story.GoalType.Story && !Plugin.RemoteDatabankAudio.Value) { __state.Muted = true; muteDepth++; }
            Plugin.Log.LogInfo("Story goal from the other player: " + key + " (" + goalType + (__state.Muted ? ", muted" : "") + ")");
        }

        private static void ExecuteFinished(ExecState __state)
        {
            if (__state.Muted && muteDepth > 0) muteDepth--;
            if (__state.Remote && remoteDepth > 0) remoteDepth--;
        }

        // Voice lines recently played, split by where they came from. A story event the other player triggers can
        // also run on this machine (e.g. the first Marg encounter: FirstEncounterStart starts the scene here too),
        // so the same line arrived twice - once from this machine's copy of the scene, once forwarded - and the
        // player who did not trigger it heard everything doubled. A line already played from the other source in
        // the last DuplicateWindow seconds is skipped, whichever copy comes first.
        private const float DuplicateWindow = 90f;
        private static readonly System.Collections.Generic.Dictionary<string, float> playedLocal = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> playedRemote = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>Silence a line that is not meant for this player or already played, and queue the ones that are.</summary>
        private static bool Filter(string key, ref bool allowQueueing)
        {
            if (muteDepth > 0) return false;
            bool remote = remoteDepth > 0;
            if (!string.IsNullOrEmpty(key))
            {
                float now = UnityEngine.Time.unscaledTime, t;
                var other = remote ? playedLocal : playedRemote;
                if (other.TryGetValue(key, out t) && now - t < DuplicateWindow)
                {
                    Plugin.Log.LogInfo("Skipped duplicate voice line " + key + " (already played " + (remote ? "here" : "from the other player") + " " + (now - t).ToString("F0") + " s ago).");
                    return false;
                }
                (remote ? playedRemote : playedLocal)[key] = now;
            }
            // Never drop a line. The game's SoundQueue.Play throws a new line away when another one is still
            // playing and queueing is not allowed, and PDA logs are added that way: in single player two lines
            // rarely collide, but with two players' events arriving on top of each other they do all the time
            // (Al-An's post-download lines landed while a hint from the other player was still talking and were
            // lost). Queue everything instead, so every line is heard, in order.
            allowQueueing = true;
            if (remote) Plugin.Log.LogInfo("Playing voice line " + (key ?? "?") + " for the other player's event.");
            else Plugin.Log.LogInfo("Playing voice line " + (key ?? "?") + ".");
            return true;
        }

        private static bool SoundQueuePlayString(string sound, ref bool allowQueueing) { return Filter(sound, ref allowQueueing); }
        private static bool SoundQueuePlayAsset(FMODAsset asset, ref bool allowQueueing) { return Filter(asset != null ? (string.IsNullOrEmpty(asset.path) ? asset.name : asset.path) : null, ref allowQueueing); }

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

        private static void AnalyzePostfix(TechType techType)
        {
            if (!WorldSync.CanSend) return;
            net.SendStory(KindAnalyze, "", (int)techType);
            Plugin.Log.LogInfo("Analyze sent: " + techType);
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
                        if (KnownTech.Add((TechType)techType, true, false)) Plugin.Log.LogInfo("Blueprint applied: " + (TechType)techType);
                        break;
                    case KindScan:
                    {
                        var entry = PDAScanner.GetEntryData((TechType)techType);
                        if (entry != null)
                        {
                            bool mute = !Plugin.RemoteDatabankAudio.Value;
                            if (mute) muteDepth++;
                            try {
                            // Track partial fragment progress rather than fully unlocking on the first scan.
                            // PDAScanner.Unlock fires on every fragment, not just the last, so we must count.
                            if (entry.totalFragments <= 1)
                            {
                                // Single-scan item: unlock immediately
                                AccessTools.Method(typeof(PDAScanner), "Unlock", new[] { typeof(PDAScanner.EntryData), typeof(bool), typeof(bool), typeof(bool) }).Invoke(null, new object[] { entry, true, true, true });
                                Plugin.Log.LogInfo("Scan unlock applied (single): " + (TechType)techType);
                            }
                            else
                            {
                                // Multi-fragment item: increment partial progress
                                var partialField = AccessTools.Field(typeof(PDAScanner), "partial");
                                var partialList = partialField != null ? partialField.GetValue(null) as System.Collections.Generic.List<PDAScanner.Entry> : null;
                                PDAScanner.Entry scanEntry = null;
                                if (partialList != null)
                                {
                                    for (int pi = 0; pi < partialList.Count; pi++)
                                    {
                                        if (partialList[pi].techType == (TechType)techType) { scanEntry = partialList[pi]; break; }
                                    }
                                    if (scanEntry == null)
                                    {
                                        scanEntry = new PDAScanner.Entry();
                                        scanEntry.techType = (TechType)techType;
                                        scanEntry.unlocked = 0;
                                        partialList.Add(scanEntry);
                                    }
                                    scanEntry.unlocked++;
                                    Plugin.Log.LogInfo("Scan fragment " + scanEntry.unlocked + "/" + entry.totalFragments + " applied: " + (TechType)techType);
                                    if (scanEntry.unlocked >= entry.totalFragments)
                                    {
                                        AccessTools.Method(typeof(PDAScanner), "Unlock", new[] { typeof(PDAScanner.EntryData), typeof(bool), typeof(bool), typeof(bool) }).Invoke(null, new object[] { entry, true, true, true });
                                        Plugin.Log.LogInfo("Scan fully unlocked: " + (TechType)techType);
                                    }
                                }
                                else
                                {
                                    // Fallback if reflection fails
                                    AccessTools.Method(typeof(PDAScanner), "Unlock", new[] { typeof(PDAScanner.EntryData), typeof(bool), typeof(bool), typeof(bool) }).Invoke(null, new object[] { entry, true, true, true });
                                    Plugin.Log.LogInfo("Scan unlock applied (fallback): " + (TechType)techType);
                                }
                            }
                            }
                            finally { if (mute && muteDepth > 0) muteDepth--; }
                        }
                        break;
                    }
                    case KindEncyclopedia:
                    {
                        bool mute = !Plugin.RemoteDatabankAudio.Value;
                        if (mute) muteDepth++;
                        remoteDepth++;
                        try { PDAEncyclopedia.Add(key, true, true); }
                        finally { if (mute && muteDepth > 0) muteDepth--; if (remoteDepth > 0) remoteDepth--; }
                        break;
                    }
                    case KindLog:
                    {
                        // A cutscene's own dialogue is part of the cinematic, which plays here too (CutsceneSync), so its
                        // PDA log must arrive silently or the line is heard twice: once from the log, once from the scene.
                        // The game itself adds logs silently while a cinematic is active; the mod also covers the moments
                        // before the scene starts here (the pull-in travel). Only then: the "Log_Cine_" logs Al-An speaks
                        // on the walk through the sanctuary are ordinary in-world lines and must be heard (test6 muted
                        // every Log_Cine_ log and the build-up to the cube scene went silent for the other player).
                        bool mute = (Player.main != null && Player.main.cinematicModeActive) || CutsceneSync.Busy;
                        if (mute) { muteDepth++; Plugin.Log.LogInfo("PDA log from the other player: " + key + " (silent: cutscene)"); }
                        remoteDepth++;
                        try { PDALog.Add(key, true); }
                        finally { if (remoteDepth > 0) remoteDepth--; if (mute && muteDepth > 0) muteDepth--; }
                        break;
                    }
                    case KindAnalyze:
                    {
                        bool mute = !Plugin.RemoteDatabankAudio.Value;
                        if (mute) muteDepth++;
                        try { KnownTech.Analyze((TechType)techType, true, true); Plugin.Log.LogInfo("Analyze applied: " + (TechType)techType); }
                        finally { if (mute && muteDepth > 0) muteDepth--; }
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Story apply failed (" + kind + " " + key + "): " + e.Message); }
            finally { WorldSync.applyingRemote = false; }
        }
    }
}
