using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BZMultiplayer.Sync;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// Pick one of your own saved worlds from the main menu and go straight into hosting it: the slot is loaded through
    /// the game's own load path, and hosting starts once the world is up.
    /// </summary>
    public static class HostLauncher
    {
        public class SaveEntry
        {
            public string Slot;
            public string Label;
            public SaveLoadManager.GameInfo Info;
        }

        /// <summary>
        /// The player's own saved worlds, most recently played first. Read from the game's own save registry rather
        /// than by scanning folders, so every save the Load menu shows appears here with the game's own play time and
        /// last-played date. The multiplayer slot a joiner receives is left out.
        /// </summary>
        public static List<SaveEntry> ListSaves()
        {
            var list = new List<SaveEntry>();
            try
            {
                var slm = SaveLoadManager.main;
                if (slm == null) return list;
                string mpSlot = Plugin.MultiplayerSlot.Value;

                var slots = slm.GetActiveSlotNames();
                if (slots == null) return list;
                foreach (var slot in slots)
                {
                    if (string.IsNullOrEmpty(slot) || slot == mpSlot) continue;   // the copy of someone else's world
                    SaveLoadManager.GameInfo info;
                    try { info = slm.GetGameInfo(slot); }
                    catch (Exception e) { Plugin.Log.LogWarning("Save " + slot + ": could not read its info (" + e.Message + ")."); continue; }
                    if (info == null) { Plugin.Log.LogWarning("Save " + slot + ": the game reports no info for it."); continue; }

                    // Everything the game lists is offered, including saves it flags as odd - a save that loads fine
                    // from the Load menu must not silently vanish from this list.
                    string label = Describe(slot, info);
                    if (info.corrupted) label += "  (flagged corrupt)";
                    list.Add(new SaveEntry { Slot = slot, Info = info, Label = label });
                }
                list.Sort((a, b) => b.Info.dateTicks.CompareTo(a.Info.dateTicks));
                var names = new List<string>();
                foreach (var e in list) names.Add(e.Slot);
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Saved worlds offered for hosting: " + (names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray()))
                                 + "  [game listed " + slots.Length + "]");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not list saved worlds: " + e.Message); }
            return list;
        }

        /// <summary>"slot0002 - Survival - 3h 12m - 19 Sep 18:16", from the save's own data.</summary>
        private static string Describe(string slot, SaveLoadManager.GameInfo info)
        {
            string played;
            int mins = Mathf.Max(0, info.gameTime) / 60;
            played = mins >= 60 ? (mins / 60) + "h " + (mins % 60) + "m" : mins + "m";

            string when;
            try { when = new DateTime(info.dateTicks).ToString("d MMM HH:mm"); }
            catch { when = "?"; }

            return slot + "  -  " + info.gameModePresetId + "  -  " + played + "  -  " + when;
        }

        public static void LoadAndHost(SaveEntry entry)
        {
            if (entry == null || Plugin.Instance == null) return;
            Plugin.Instance.StartCoroutine(Routine(entry));
        }

        private static IEnumerator Routine(SaveEntry entry)
        {
            var net = Plugin.Instance.Net;
            if (net == null) { ErrorMessage.AddMessage("Steam is not ready yet."); yield break; }

            if (LocalPlayerSync.InWorld) yield return MenuFlow.QuitToMainMenu();
            yield return MenuFlow.WaitForMenuReady();
            if (uGUI_MainMenu.main == null)
            {
                Plugin.Log.LogWarning("Host launcher: no main menu; cannot load " + entry.Slot + ".");
                yield break;
            }

            Plugin.Log.LogInfo("Host launcher: loading " + entry.Slot + " to host it.");
            var info = entry.Info;
            yield return uGUI_MainMenu.main.LoadGameAsync(entry.Slot, info.session, info.changeSet,
                                                          info.gameModePresetId, info.gameOptions, info.storyVersion);

            // The load coroutine returns before the world is actually playable; wait for the player to exist.
            float deadline = Time.unscaledTime + 180f;
            while (!LocalPlayerSync.InWorld && Time.unscaledTime < deadline) yield return null;
            if (!LocalPlayerSync.InWorld)
            {
                Plugin.Log.LogWarning("Host launcher: " + entry.Slot + " did not finish loading; not hosting.");
                yield break;
            }
            yield return new WaitForSecondsRealtime(2f);

            if (net.IsInSession) yield break;   // something else already started a session while we loaded
            net.Host();
            ErrorMessage.AddMessage("Hosting " + entry.Slot + ". Friends can join from Steam, Discord, or with "
                                  + Plugin.JoinFriendKey.Value + ".");
        }
    }
}
