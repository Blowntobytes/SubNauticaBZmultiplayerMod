using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BZMultiplayer.UI
{
    /// <summary>
    /// A "Multiplayer" entry in the main menu, directly below Play. It is a copy of one of the game's own primary
    /// options (the same component Play / Options / Credits / Quit use), so it looks and behaves like the others and
    /// works in VR. Clicking it opens the options screen on the Multiplayer tab.
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
            yield return null;   // let the menu finish building itself
            yield return null;
            try { Build(menu); }
            catch (Exception e) { Plugin.Log.LogWarning("MainMenuEntry: " + e); }
        }

        private static void Build(uGUI_MainMenu menu)
        {
            if (menu == null) return;

            var options = menu.GetComponentsInChildren<MainMenuPrimaryOption>(true);
            if (options == null || options.Length == 0) { Plugin.Log.LogWarning("MainMenuEntry: no main-menu options found."); return; }
            foreach (var o in options) if (o != null && o.name == EntryName) return;   // already added

            MainMenuPrimaryOption play = null, template = null;
            foreach (var o in options)
            {
                if (o == null || !o.gameObject.activeInHierarchy) continue;
                string method = PersistentMethod(o);
                if (method != null && method.IndexOf("Play", StringComparison.OrdinalIgnoreCase) >= 0) play = play ?? o;
                if (method != null && method.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0) template = template ?? o;
            }
            // Fall back on order: the first entry is Play, and any other entry serves as the template.
            if (play == null) play = FirstActive(options);
            if (template == null) foreach (var o in options) { if (o != null && o != play && o.gameObject.activeInHierarchy) { template = o; break; } }
            if (template == null) template = play;
            if (play == null || template == null) { Plugin.Log.LogWarning("MainMenuEntry: could not identify the Play entry."); return; }

            var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            go.name = EntryName;
            go.SetActive(true);

            int wanted = play.transform.GetSiblingIndex() + 1;
            go.transform.SetSiblingIndex(wanted);

            // Some menus place their entries by hand instead of with a layout group; keep the column evenly spaced.
            if (template.transform.parent.GetComponent<LayoutGroup>() == null) Reflow(play.transform.parent, wanted);

            var opt = go.GetComponent<MainMenuPrimaryOption>();
            SetLabel(opt != null && opt.optionText != null ? opt.optionText : go, "Multiplayer");

            foreach (var b in go.GetComponentsInChildren<Button>(true))
            {
                b.onClick = new Button.ButtonClickedEvent();
                b.onClick.AddListener(OpenMultiplayer);
                b.interactable = true;
            }
            Plugin.Log.LogInfo("Main menu: Multiplayer entry added below " + play.name + " (copied from " + template.name + ").");
        }

        private static MainMenuPrimaryOption FirstActive(MainMenuPrimaryOption[] options)
        {
            MainMenuPrimaryOption first = null;
            int bestIndex = int.MaxValue;
            foreach (var o in options)
            {
                if (o == null || !o.gameObject.activeInHierarchy) continue;
                int i = o.transform.GetSiblingIndex();
                if (i < bestIndex) { bestIndex = i; first = o; }
            }
            return first;
        }

        /// <summary>The name of the method the menu itself wired to this entry in the editor ("OnButtonOptions"...).</summary>
        private static string PersistentMethod(MainMenuPrimaryOption opt)
        {
            foreach (var b in opt.GetComponentsInChildren<Button>(true))
            {
                int n = b.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    string m = b.onClick.GetPersistentMethodName(i);
                    if (!string.IsNullOrEmpty(m)) return m;
                }
            }
            return null;
        }

        /// <summary>Re-space a hand-placed column so the inserted entry does not sit on top of its neighbour.</summary>
        private static void Reflow(Transform parent, int insertedAt)
        {
            var rows = new System.Collections.Generic.List<RectTransform>();
            foreach (Transform child in parent)
            {
                var rt = child as RectTransform;
                if (rt != null && child.gameObject.activeSelf && child.GetComponent<MainMenuPrimaryOption>() != null) rows.Add(rt);
            }
            if (rows.Count < 3) return;

            // Spacing from the two entries that were already there, measured before our insert shifted anything.
            RectTransform a = null, b = null;
            foreach (var rt in rows) { if (rt.name == EntryName) continue; if (a == null) a = rt; else if (b == null) { b = rt; break; } }
            if (a == null || b == null) return;
            float step = b.anchoredPosition.y - a.anchoredPosition.y;
            if (Mathf.Abs(step) < 0.01f) return;

            float y = a.anchoredPosition.y;
            for (int i = 0; i < rows.Count; i++)
            {
                var rt = rows[i];
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

        private static void OpenMultiplayer()
        {
            var menu = uGUI_MainMenu.main;
            if (menu == null) return;
            menu.OnButtonOptions();
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(SelectTab());
        }

        private static IEnumerator SelectTab()
        {
            float deadline = Time.unscaledTime + 3f;
            while (Time.unscaledTime < deadline)
            {
                var panel = UnityEngine.Object.FindObjectOfType<uGUI_OptionsPanel>();
                if (panel != null && OptionsTab.TabIndex >= 0)
                {
                    yield return null;   // let the panel finish opening before we switch tabs
                    try { Traverse.Create(panel).Method("SetVisibleTab", new object[] { OptionsTab.TabIndex }).GetValue(); }
                    catch (Exception e) { Plugin.Log.LogWarning("MainMenuEntry: could not open the Multiplayer tab: " + e.Message); }
                    yield break;
                }
                yield return null;
            }
        }
    }
}
