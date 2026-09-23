using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BZMultiplayer.Net;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Cutscene / cinematic sync: when one player triggers a world-level cinematic (alien arch, artifact
    /// interaction, frozen leviathan, etc.), all other players see the same cinematic play on the world object.
    /// Personal cinematics (bulkheads, base doors, drop pod, vehicle entry/exit) are NOT synced.
    /// Remote players are never teleported — only the world-side animation plays.
    /// </summary>
    public static class CutsceneSync
    {
        private static SteamNet net;
        private static bool applying;

        /// <summary>
        /// Animation names that should NOT be synced — these are personal (entering/exiting things).
        /// Matched by prefix so e.g. "bulkhead" catches "bulkhead_open", "bulkhead_close" etc.
        /// </summary>
        private static readonly string[] BlockedPrefixes = new[]
        {
            "drop_pod",         // drop pod hatch
            "droppod",          // drop pod (no underscore variant: droppod_enter_cin etc.)
            "bulkhead",         // base bulkhead doors
            "door",             // generic doors
            "hatch",            // hatches
            "enter",            // entering vehicles/bases
            "exit",             // exiting vehicles/bases
            "climb",            // ladders
            "use_chair",        // sitting
            "bench",            // bench sitting
            "moonpool",         // moonpool docking
            "cyclops",          // cyclops interactions
            "seatruck",         // seatruck docking/entering
            "dock",             // docking
            "snowfox",          // snowfox mount/dismount
            "prawn",            // prawn suit
            "exosuit",          // exosuit
            "jukebox",          // jukebox use
            "shower",           // shower use
            "bed",              // bed use
            "toilet",           // toilet use
            "vending",          // vending machine
            "coffee",           // coffee machine
            "fabricat",         // fabricator
            "terminal",         // terminal use
            "constructor",      // mobile vehicle bay
            "maproom",          // map room / scanner room
            "charging",         // charging station
        };

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;

            var start = AccessTools.Method(typeof(PlayerCinematicController), "StartCinematicMode",
                new[] { typeof(Player) });
            if (start != null)
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(CutsceneSync), "CinematicStartPostfix"));
            else
                Plugin.Log.LogWarning("CutsceneSync: PlayerCinematicController.StartCinematicMode(Player) not found.");

            Plugin.Log.LogInfo("CutsceneSync installed.");
        }

        // ------------------------------------------------------------------ helpers

        private static string GetAnimName(PlayerCinematicController controller)
        {
            try
            {
                var field = AccessTools.Field(typeof(PlayerCinematicController), "playerViewAnimationName");
                if (field != null) return field.GetValue(controller) as string;
            }
            catch { }
            return null;
        }

        private static bool IsBlocked(string animName)
        {
            if (string.IsNullOrEmpty(animName)) return true; // unknown animation — safer to block
            string lower = animName.ToLowerInvariant();
            for (int i = 0; i < BlockedPrefixes.Length; i++)
            {
                if (lower.StartsWith(BlockedPrefixes[i])) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers: identification

        /// <summary>
        /// Build a scene-hierarchy path like "marge_intro(Clone)/PlayerCinematicController" for objects
        /// that lack a UniqueIdentifier.  Prefixed with "path:" so the receive side knows to look up
        /// by path rather than by UniqueIdentifier.
        /// </summary>
        private static string ScenePath(Component c)
        {
            if (c == null) return null;
            var parts = new System.Collections.Generic.List<string>();
            for (var t = c.transform; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return "path:" + string.Join("/", parts.ToArray());
        }

        // ------------------------------------------------------------------ local event

        private static void CinematicStartPostfix(PlayerCinematicController __instance)
        {
            if (applying || !WorldSync.CanSend) return;
            if (__instance == null) return;

            string animName = GetAnimName(__instance);
            if (IsBlocked(animName))
            {
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Cutscene blocked (personal): " + (animName ?? "null"));
                return;
            }

            string id = WorldSync.IdOf(__instance);
            if (string.IsNullOrEmpty(id))
                id = ScenePath(__instance);
            if (string.IsNullOrEmpty(id)) return;

            net.SendCutscene(id, animName ?? "");
            Plugin.Log.LogInfo("Cutscene sent: " + id + " anim=" + animName);
        }

        // ------------------------------------------------------------------ remote event

        public static void OnCutscene(string objectId, string animName)
        {
            if (!LocalPlayerSync.InWorld || string.IsNullOrEmpty(objectId)) return;

            // Double-check the blocklist on the receiving side too
            if (IsBlocked(animName)) return;

            applying = true;
            WorldSync.applyingRemote = true;
            try
            {
                GameObject go;
                if (objectId.StartsWith("path:"))
                {
                    go = GameObject.Find(objectId.Substring(5));
                }
                else
                {
                    go = WorldSync.Find(objectId);
                }
                if (go == null)
                {
                    Plugin.Log.LogWarning("CutsceneSync: object not found: " + objectId);
                    return;
                }

                // Play only the world-side animation — do NOT call StartCinematicMode on the local player.
                // This means the remote player sees the object animate but is not teleported or camera-locked.
                bool played = false;

                // Try PlayableDirector (timeline-based cutscenes like alien arches, frozen leviathan)
                // Use reflection to avoid compile-time dependency on UnityEngine.DirectorModule
                var directorType = Type.GetType("UnityEngine.Playables.PlayableDirector, UnityEngine.DirectorModule");
                if (directorType != null)
                {
                    var director = go.GetComponent(directorType);
                    if (director == null) director = go.GetComponentInChildren(directorType);
                    if (director != null)
                    {
                        var playMethod = directorType.GetMethod("Play", Type.EmptyTypes);
                        if (playMethod != null) { playMethod.Invoke(director, null); played = true; }
                    }
                }

                // Try Animator trigger
                var animator = go.GetComponent<Animator>();
                if (animator == null) animator = go.GetComponentInChildren<Animator>();
                if (animator != null && !string.IsNullOrEmpty(animName))
                {
                    try { animator.SetTrigger(animName); played = true; }
                    catch { }
                }

                if (played) Plugin.Log.LogInfo("Cutscene world anim played: " + objectId + " anim=" + animName);
                else Plugin.Log.LogInfo("Cutscene object found but no director/animator: " + objectId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CutsceneSync apply failed: " + e.Message);
            }
            finally
            {
                applying = false;
                WorldSync.applyingRemote = false;
            }
        }
    }
}
