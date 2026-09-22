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

        /// <summary>The label showing the live session status, refreshed while the panel is on screen.</summary>
        private static TMPro.TMP_Text statusLabel;
        private static float nextStatusRefresh;

        /// <summary>The live network object, looked up on every click so nothing here goes stale.</summary>
        private static SteamNet Net { get { return Plugin.Instance != null ? Plugin.Instance.Net : null; } }


        public static void Install(Harmony harmony)
        {
            var addTabs = AccessTools.Method(typeof(uGUI_OptionsPanel), "AddTabs");
            if (addTabs == null) { Plugin.Log.LogWarning("OptionsTab: uGUI_OptionsPanel.AddTabs not found; no Multiplayer tab."); return; }
            // Run after other mods' patches on the same method so our tab is added last (and so ends up at the
            // bottom). The tab MUST be added inside this call: adding one after the panel has been built and
            // highlighted leaves the panel's own tab bookkeeping inconsistent and it throws on the next click.
            var post = new HarmonyMethod(typeof(OptionsTab), "AddTabsPostfix");
            post.priority = Priority.Last;
            post.after = new[] { "SubmersedVR", "com.submersedvr", "submersedvr" };
            harmony.Patch(addTabs, postfix: post);

            // Optional: keep Steam achievements from unlocking while playing multiplayer.
            var unlock = AccessTools.Method(typeof(GameAchievements), "Unlock");
            if (unlock != null) harmony.Patch(unlock, prefix: new HarmonyMethod(typeof(OptionsTab), "AchievementPrefix"));
        }

        private static string StatusText()
        {
            var n = Net;
            return "Status:  " + (n == null ? "Steam not ready" : n.StatusLine());
        }

        /// <summary>Keeps the status line current while the options panel is open.</summary>
        public static void Update()
        {
            if (statusLabel == null) { nextStatusRefresh = 0f; return; }
            if (Time.unscaledTime < nextStatusRefresh) return;
            nextStatusRefresh = Time.unscaledTime + 0.5f;
            try { statusLabel.text = StatusText(); }
            catch { statusLabel = null; }
        }

        /// <summary>The text of the control most recently added to a tab, so we can keep it up to date.</summary>
        private static TMPro.TMP_Text LastLabel(uGUI_OptionsPanel panel, int tabIndex)
        {
            try
            {
                var pane = Pane(panel, tabIndex);
                if (pane == null || pane.transform.childCount == 0) return null;
                var last = pane.transform.GetChild(pane.transform.childCount - 1);
                foreach (var t in last.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    // Translation components overwrite whatever we set, so remove them from this one label.
                    var live = t.GetComponent<TranslationLiveUpdate>();
                    if (live != null) UnityEngine.Object.Destroy(live);
                    return t;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("OptionsTab: no status label (" + e.Message + ")"); }
            return null;
        }

        private static GameObject Pane(uGUI_OptionsPanel panel, int tabIndex) { return TabField(panel, tabIndex, "pane") as GameObject; }

        /// <summary>The toggle that selects a tab: turning it on both shows the pane and highlights the tab.</summary>
        public static UnityEngine.UI.Toggle TabToggle(uGUI_OptionsPanel panel, int tabIndex)
        {
            var go = TabField(panel, tabIndex, "tab") as GameObject;
            return go != null ? go.GetComponentInChildren<UnityEngine.UI.Toggle>(true) : null;
        }

        /// <summary>
        /// Show a tab and make sure it is the ONLY highlighted one. The tab toggles are not in a toggle group, so
        /// switching ours on left the previously highlighted tab (General) lit as well. Turning the others off is
        /// safe: the panel's own handler ignores a toggle going false, so this cannot change which pane is shown.
        /// </summary>
        public static bool SelectTabExclusive(uGUI_OptionsPanel panel, int tabIndex)
        {
            var wanted = TabToggle(panel, tabIndex);
            if (wanted == null) return false;
            for (int i = 0; i < TabCount(panel); i++)
            {
                if (i == tabIndex) continue;
                var other = TabToggle(panel, i);
                if (other != null && other.isOn) other.isOn = false;
            }
            wanted.isOn = true;

            // The panel highlights a tab by SELECTING its button through the EventSystem (its own highlight routine
            // calls GamepadInputModule.SelectItem on tabs[currentTab].tabButton, and ToggleButton.OnSelect is what
            // turns a tab on). So the lit tab follows the selected object: without moving the selection here, the
            // previously selected tab keeps its highlight no matter what the toggles say.
            var button = TabButton(panel, tabIndex);
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (button != null && events != null && events.currentSelectedGameObject != button.gameObject)
                events.SetSelectedGameObject(button.gameObject);
            return true;
        }

        public static UnityEngine.UI.Selectable TabButton(uGUI_OptionsPanel panel, int tabIndex)
        {
            return TabField(panel, tabIndex, "tabButton") as UnityEngine.UI.Selectable;
        }

        /// <summary>Diagnostic: which tabs report themselves on, and which object the EventSystem has selected.</summary>
        public static void LogTabState(uGUI_OptionsPanel panel, string when)
        {
            try
            {
                var sb = new System.Text.StringBuilder("Tab state (" + when + "): ");
                for (int i = 0; i < TabCount(panel); i++)
                {
                    var go = TabField(panel, i, "tab") as GameObject;
                    var t = TabToggle(panel, i);
                    sb.Append(i).Append(':').Append(go != null ? go.name : "?")
                      .Append(t != null && t.isOn ? "=ON " : "=off ");
                }
                var events = UnityEngine.EventSystems.EventSystem.current;
                var sel = events != null ? events.currentSelectedGameObject : null;
                sb.Append(" | selected: ").Append(sel != null ? sel.name : "(none)");
                sb.Append(" | ours: ").Append(TabIndex);
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("OptionsTab: could not read tab state: " + e.Message); }
        }

        private static int TabCount(uGUI_OptionsPanel panel)
        {
            try
            {
                var tabs = Traverse.Create(panel).Field("tabs").GetValue() as System.Collections.IList;
                return tabs == null ? 0 : tabs.Count;
            }
            catch { return 0; }
        }

        private static object TabField(uGUI_OptionsPanel panel, int tabIndex, string field)
        {
            try
            {
                if (panel == null || tabIndex < 0) return null;
                var tabs = Traverse.Create(panel).Field("tabs").GetValue() as System.Collections.IList;
                if (tabs == null || tabIndex >= tabs.Count) return null;
                return Traverse.Create(tabs[tabIndex]).Field(field).GetValue();
            }
            catch { return null; }
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
            int tab = panel.AddTab("Multiplayer");
            TabIndex = tab;

            // Every control is always present and decides what to do when it is CLICKED. The panel is built once, so
            // anything that branched on the session state here would still show the state from when Options was first
            // opened - which made Leave look dead and kept showing "hosting" after the session had ended.
            panel.AddHeading(tab, "Session");
            panel.AddButton(tab, StatusText(), () =>
            {
                var n = Net;
                ErrorMessage.AddMessage(n == null ? "Steam is not ready yet." : "BZMultiplayer: " + n.StatusLine());
            });
            statusLabel = LastLabel(panel, tab);   // kept updated by Update(), so it reads like the old status line

            panel.AddButton(tab, "Host the world you are in  (" + Plugin.HostKey.Value + ")", () =>
            {
                var n = Net;
                if (n == null) { ErrorMessage.AddMessage("Steam is not ready yet."); return; }
                if (n.IsInSession) { ErrorMessage.AddMessage("You are already in a session. Leave it first."); return; }
                if (!Sync.LocalPlayerSync.InWorld) { ErrorMessage.AddMessage("Load a world first, or pick one below."); return; }
                n.Host();
                ErrorMessage.AddMessage("Hosting. Friends can join from Steam, Discord, or with " + Plugin.JoinFriendKey.Value + ".");
            });

            panel.AddButton(tab, "Join a friend's game  (" + Plugin.JoinFriendKey.Value + ")", () =>
            {
                var n = Net;
                if (n == null) { ErrorMessage.AddMessage("Steam is not ready yet."); return; }
                if (n.IsInSession) { ErrorMessage.AddMessage("You are already in a session. Leave it first."); return; }
                if (!n.JoinFriend() && !n.JoinById(GUIUtility.systemCopyBuffer))
                    ErrorMessage.AddMessage("No friend is hosting right now, and the clipboard has no lobby id.");
            });

            panel.AddButton(tab, "Invite / copy lobby id", () =>
            {
                var n = Net;
                if (n == null || !n.IsInSession || !n.IsHost) { ErrorMessage.AddMessage("You are not hosting a session."); return; }
                n.OpenInvite();
            });

            panel.AddButton(tab, "Leave / end session  (" + Plugin.LeaveKey.Value + ")", () =>
            {
                var n = Net;
                if (n == null || !n.IsInSession) { ErrorMessage.AddMessage("You are not in a session."); return; }
                bool wasHost = n.IsHost;
                n.Leave();
                // Say so out loud: a host stays in their own world when the session ends, so without this the button
                // looks like it did nothing at all.
                ErrorMessage.AddMessage(wasHost
                    ? "Session closed. Nobody can join now; you are still in your world."
                    : "Left the session.");
            });

            // Load one of your own saved worlds and host it, in one click.
            var saves = HostLauncher.ListSaves();
            if (saves.Count > 0)
            {
                panel.AddHeading(tab, "Load a saved world and host it");
                foreach (var save in saves)
                {
                    var chosen = save;   // capture per iteration
                    panel.AddButton(tab, chosen.Label, () =>
                    {
                        var n = Net;
                        if (n != null && n.IsInSession) { ErrorMessage.AddMessage("Leave the current session first."); return; }
                        HostLauncher.LoadAndHost(chosen);
                    });
                }
            }

            panel.AddHeading(tab, "Settings");
            panel.AddSliderOption(tab, "Max players (host)", Plugin.MaxPlayers.Value, 2f, 8f, 8f, 1f,
                v => { Plugin.MaxPlayers.Value = Mathf.RoundToInt(v); if (Net != null) Net.ApplyMaxPlayers(); },
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
