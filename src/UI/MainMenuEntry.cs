using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// A "Multiplayer" entry in the main menu, directly below Play. BZ's main menu is a
    /// <see cref="uGUI_NavigableControlGrid"/> (primaryOptions) whose entries are plain rows carrying a Button -
    /// there is no marker component to look for - so the real Options row is located through the persistent onClick
    /// method the menu wired in the editor, and that row is cloned. Because the grid collects its Selectables at
    /// runtime, the clone is automatically navigable with a controller and in VR.
    /// </summary>
    public static class MainMenuEntry
    {
        private const string EntryName = "BZMP_MultiplayerOption";

        public static void Install(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(uGUI_MainMenu), "Start");
            if (start == null) { Plugin.Log.LogWarning("MainMenuEntry: uGUI_MainMenu.Start not found; no main-menu entry."); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(MainMenuEntry), "StartPostfix"));
        }

        private static void StartPostfix(uGUI_MainMenu __instance)
        {
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(BuildWhenReady(__instance));
        }

        private static IEnumerator BuildWhenReady(uGUI_MainMenu menu)
        {
            // The menu fills primaryOptions over its first frames; wait until buttons actually exist.
            float deadline = Time.unscaledTime + 10f;
            while (Time.unscaledTime < deadline)
            {
                yield return null;
                var root = RootOf(menu);
                if (root != null && root.GetComponentsInChildren<Button>(true).Length >= 2) break;
            }
            yield return null;
            try { Build(menu); }
            catch (Exception e) { Plugin.Log.LogWarning("MainMenuEntry: " + e); }
        }

        private static Transform RootOf(uGUI_MainMenu menu)
        {
            if (menu == null) return null;
            if (menu.primaryOptions != null) return menu.primaryOptions.transform;
            return menu.transform;
        }

        private static void Build(uGUI_MainMenu menu)
        {
            var root = RootOf(menu);
            if (root == null) { Plugin.Log.LogWarning("MainMenuEntry: main menu has no primary options."); return; }

            // The menu buttons are direct children of one column (MenuButtons, a VerticalLayoutGroup). Find them by the
            // persistent onClick method the menu wired in the editor, never by position or by walking up the tree: a
            // wrong guess here clones a singleton panel and breaks the menus that depend on it.
            Button optionsButton = null, playButton = null;
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                string m = PersistentMethod(b);
                if (m == "OnButtonOptions") optionsButton = optionsButton ?? b;
                else if (m == "OnButtonLoad" || m == "OnButtonNew") playButton = playButton ?? b;
            }
            if (optionsButton == null)
            {
                Plugin.Log.LogWarning("MainMenuEntry: the Options button was not found; leaving the menu alone.");
                Dump(root);
                return;
            }

            // The row is the button itself; its parent is the column. Both are taken from the real button, so we can
            // never end up cloning something bigger than one menu entry.
            Transform template = optionsButton.transform;
            Transform container = template.parent;
            if (container == null) { Plugin.Log.LogWarning("MainMenuEntry: the Options button has no parent column."); return; }

            foreach (Transform child in container) if (child.name == EntryName) return;   // already added

            Transform play = playButton != null && playButton.transform.parent == container ? playButton.transform : null;

            var go = UnityEngine.Object.Instantiate(template.gameObject, container, false);
            go.name = EntryName;
            go.SetActive(true);

            int wanted = play != null ? play.GetSiblingIndex() + 1 : Mathf.Max(0, container.childCount - 1);
            go.transform.SetSiblingIndex(wanted);

            // Some menus place their rows by hand instead of with a layout group; keep the column evenly spaced.
            if (container.GetComponent<LayoutGroup>() == null) Reflow(container);

            SetLabel(go, "Multiplayer");

            // Our own click, and none of the editor-wired ones. EventTrigger drives the hover animation, so it stays.
            foreach (var b in go.GetComponentsInChildren<Button>(true))
            {
                b.onClick = new Button.ButtonClickedEvent();
                b.onClick.AddListener(OpenMultiplayer);
                b.interactable = true;
            }

            Plugin.Log.LogInfo("Main menu: Multiplayer entry added at index " + wanted + " under '" + container.name
                             + "' (below '" + (play != null ? play.name : "(Play not found)")
                             + "', copied from '" + template.name + "').");
        }

        /// <summary>The name of the method the menu itself wired to this button in the editor ("OnButtonOptions"...).</summary>
        private static string PersistentMethod(Button b)
        {
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
            {
                string m = b.onClick.GetPersistentMethodName(i);
                if (!string.IsNullOrEmpty(m)) return m;
            }
            return null;
        }

        /// <summary>Re-space a hand-placed column so the inserted row does not sit on top of its neighbour.</summary>
        private static void Reflow(Transform container)
        {
            var rows = new List<RectTransform>();
            foreach (Transform child in container)
            {
                var rt = child as RectTransform;
                if (rt != null && child.gameObject.activeSelf && child.GetComponentInChildren<Button>(true) != null) rows.Add(rt);
            }
            if (rows.Count < 3) return;

            // Spacing from two rows that were already there, ignoring the one we just inserted.
            RectTransform a = null, b = null;
            foreach (var rt in rows) { if (rt.name == EntryName) continue; if (a == null) a = rt; else { b = rt; break; } }
            if (a == null || b == null) return;
            float step = b.anchoredPosition.y - a.anchoredPosition.y;
            if (Mathf.Abs(step) < 0.01f) return;

            float y = a.anchoredPosition.y;
            foreach (var rt in rows)
            {
                var p = rt.anchoredPosition;
                rt.anchoredPosition = new Vector2(p.x, y);
                y += step;
            }
        }

        /// <summary>Set the label, whichever text component the game uses, and stop translations overwriting it.</summary>
        private static void SetLabel(GameObject go, string label)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.IndexOf("Translat", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.Destroy(c);
            }
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var prop = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop == null || prop.PropertyType != typeof(string) || !prop.CanWrite) continue;
                try { prop.SetValue(c, label, null); } catch { }
            }
        }

        /// <summary>Diagnostic: the live menu hierarchy, so a log tells us the real structure instead of a guess.</summary>
        private static void Dump(Transform root)
        {
            try
            {
                var sb = new System.Text.StringBuilder("MainMenuEntry: hierarchy under '" + root.name + "':\n");
                DumpInto(sb, root, 0);
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static void DumpInto(System.Text.StringBuilder sb, Transform t, int depth)
        {
            if (depth > 6) return;
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (inactive)");
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(" [").Append(c.GetType().Name).Append(']');
            var btn = t.GetComponent<Button>();
            if (btn != null && btn.onClick.GetPersistentEventCount() > 0) sb.Append(" -> ").Append(btn.onClick.GetPersistentMethodName(0));
            sb.Append('\n');
            foreach (Transform child in t) DumpInto(sb, child, depth + 1);
        }

        private static void OpenMultiplayer()
        {
            var menu = uGUI_MainMenu.main;
            if (menu == null) return;
            menu.OnButtonOptions();
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(SelectTab());
        }

        private static IEnumerator SelectTab()
        {
            float deadline = Time.unscaledTime + 5f;
            while (Time.unscaledTime < deadline)
            {
                yield return null;
                var panel = UnityEngine.Object.FindObjectOfType<uGUI_OptionsPanel>();
                if (panel == null || OptionsTab.TabIndex < 0) continue;

                // Switch tabs by turning the tab's own toggle on, NOT by calling SetVisibleTab: the toggle drives both
                // the visible pane and the highlight, so calling the method alone showed our pane with the first tab
                // still highlighted.
                bool opened;
                try { opened = OptionsTab.SelectTabExclusive(panel, OptionsTab.TabIndex); }
                catch (Exception e) { Plugin.Log.LogWarning("MainMenuEntry: could not open the Multiplayer tab: " + e.Message); yield break; }
                if (!opened) continue;

                // The panel highlights its own current tab from a coroutine that finishes AFTER this runs the first
                // time a panel is opened, which re-lit General next to ours. Hold the selection for a moment so the
                // late highlight cannot leave two tabs lit.
                float hold = Time.unscaledTime + 1f;
                while (Time.unscaledTime < hold)
                {
                    yield return null;
                    if (panel == null) yield break;
                    try { OptionsTab.SelectTabExclusive(panel, OptionsTab.TabIndex); }
                    catch { yield break; }
                }
                yield break;
            }
            Plugin.Log.LogWarning("MainMenuEntry: the options panel never offered the Multiplayer tab.");
        }
    }
}
