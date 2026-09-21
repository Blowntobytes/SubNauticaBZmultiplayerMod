using System;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// "Multiplayer" tab in the game's Options screen (main menu and in-game): host / join / leave buttons and the
    /// mod's settings, so nothing needs the keyboard shortcuts or the config file.
    /// </summary>
    public static class OptionsTab
    {
        /// <summary>Index of the Multiplayer tab in the options panel, so the main-menu button can jump straight to it.</summary>
        public static int TabIndex { get; private set; }

        public static void Install(Harmony harmony)
        {
            var addTabs = AccessTools.Method(typeof(uGUI_OptionsPanel), "AddTabs");
            if (addTabs == null) { Plugin.Log.LogWarning("OptionsTab: uGUI_OptionsPanel.AddTabs not found; no Multiplayer tab."); return; }
            harmony.Patch(addTabs, postfix: new HarmonyMethod(typeof(OptionsTab), "AddTabsPostfix"));

            // Optional: keep Steam achievements from unlocking while playing multiplayer.
            var unlock = AccessTools.Method(typeof(GameAchievements), "Unlock");
            if (unlock != null) harmony.Patch(unlock, prefix: new HarmonyMethod(typeof(OptionsTab), "AchievementPrefix"));
        }

        private static bool AchievementPrefix(GameAchievements.Id id)
        {
            if (!Plugin.DisableAchievements.Value) return true;
            Plugin.Log.LogInfo("Achievement suppressed: " + id);
            return false;
        }

        private static void AddTabsPostfix(uGUI_OptionsPanel __instance)
        {
            try { Build(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("OptionsTab: failed to build tab: " + e); }
        }

        private static void Build(uGUI_OptionsPanel panel)
        {
            var net = Plugin.Instance != null ? Plugin.Instance.Net : null;
            int tab = panel.AddTab("Multiplayer");
            TabIndex = tab;

            panel.AddHeading(tab, "Session");
            string status = net == null ? "Steam not ready" : net.StatusLine();
            panel.AddButton(tab, "Status: " + status, () => { });
            if (net == null || !net.IsInSession)
            {
                panel.AddButton(tab, "Host this world  (" + Plugin.HostKey.Value + ")", () =>
                {
                    if (net == null) return;
                    if (!Sync.LocalPlayerSync.InWorld) { ErrorMessage.AddMessage("Load a save first, then host it."); return; }
                    net.Host();
                    ErrorMessage.AddMessage("Hosting. Friends can join from Steam, Discord, or by pressing " + Plugin.JoinFriendKey.Value + ".");
                });
                panel.AddButton(tab, "Join a friend's game  (" + Plugin.JoinFriendKey.Value + ")", () =>
                {
                    if (net == null) return;
                    if (!net.JoinFriend()) { if (!net.JoinById(GUIUtility.systemCopyBuffer)) ErrorMessage.AddMessage("No friend is hosting right now, and the clipboard has no lobby id."); }
                });
            }
            else
            {
                if (net.IsHost) panel.AddButton(tab, "Copy invite id / open Steam invite", () => net.OpenInvite());
                else panel.AddButton(tab, "Re-download the host's world", () => net.SendSaveRequest());
                panel.AddButton(tab, "Leave session  (" + Plugin.LeaveKey.Value + ")", () => net.Leave());
            }

            panel.AddHeading(tab, "Settings");
            panel.AddSliderOption(tab, "Max players (host)", Plugin.MaxPlayers.Value, 2f, 8f, 8f, 1f,
                v => { Plugin.MaxPlayers.Value = Mathf.RoundToInt(v); if (net != null) net.ApplyMaxPlayers(); },
                SliderLabelMode.Int, "0", "How many players the lobby accepts, including you. Applies to the current session too.");
            panel.AddToggleOption(tab, "Disable Steam achievements", Plugin.DisableAchievements.Value,
                v => Plugin.DisableAchievements.Value = v, "Achievements will not unlock while this is on.");
            panel.AddToggleOption(tab, "Discord presence and Join button", Plugin.DiscordEnabled.Value,
                v => { Plugin.DiscordEnabled.Value = v; Plugin.Instance.ApplyDiscordSetting(); }, "Shows your session in Discord so friends can click Join.");
            panel.AddToggleOption(tab, "Load the host's world automatically on join", Plugin.AutoSyncSave.Value,
                v => Plugin.AutoSyncSave.Value = v, null);
            panel.AddToggleOption(tab, "Show status box on the desktop window", Plugin.ShowOverlay.Value,
                v => Plugin.ShowOverlay.Value = v, null);
            panel.AddSliderOption(tab, "Player update rate", Plugin.SendRate.Value, 5f, 60f, 20f, 1f,
                v => Plugin.SendRate.Value = Mathf.RoundToInt(v), SliderLabelMode.Int, "0", "Position updates per second sent to other players.");
        }
    }
}
