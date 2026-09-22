using System;
using HarmonyLib;
using BZMultiplayer.Net;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Cutscene / cinematic sync: when one player triggers a cinematic (alien arch, artifact interaction,
    /// frozen leviathan, etc.), all other players see the same cinematic play on the world object.
    /// The cinematic plays for the remote player too — their camera is taken over by the cinematic controller.
    /// </summary>
    public static class CutsceneSync
    {
        private static SteamNet net;
        private static bool applying;

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;

            // Hook PlayerCinematicController.StartCinematicMode to broadcast when a cinematic starts
            var start = AccessTools.Method(typeof(PlayerCinematicController), "StartCinematicMode",
                new[] { typeof(Player) });
            if (start != null)
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(CutsceneSync), "CinematicStartPostfix"));
            else
                Plugin.Log.LogWarning("CutsceneSync: PlayerCinematicController.StartCinematicMode(Player) not found.");

            Plugin.Log.LogInfo("CutsceneSync installed.");
        }

        // ------------------------------------------------------------------ local event

        private static void CinematicStartPostfix(PlayerCinematicController __instance)
        {
            if (applying || !WorldSync.CanSend) return;
            if (__instance == null) return;

            string id = WorldSync.IdOf(__instance);
            if (string.IsNullOrEmpty(id)) return;

            net.SendCutscene(id);
            Plugin.Log.LogInfo("Cutscene sent: " + id);
        }

        // ------------------------------------------------------------------ remote event

        public static void OnCutscene(string objectId)
        {
            if (!LocalPlayerSync.InWorld || string.IsNullOrEmpty(objectId)) return;

            applying = true;
            WorldSync.applyingRemote = true;
            try
            {
                var go = WorldSync.Find(objectId);
                if (go == null)
                {
                    Plugin.Log.LogWarning("CutsceneSync: object not found: " + objectId);
                    return;
                }

                var controller = go.GetComponent<PlayerCinematicController>();
                if (controller == null)
                {
                    // Some objects have the controller on a child
                    controller = go.GetComponentInChildren<PlayerCinematicController>();
                }

                if (controller == null)
                {
                    Plugin.Log.LogWarning("CutsceneSync: no PlayerCinematicController on " + objectId);
                    return;
                }

                if (Player.main != null)
                {
                    controller.StartCinematicMode(Player.main);
                    Plugin.Log.LogInfo("Cutscene applied: " + objectId);
                }
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
