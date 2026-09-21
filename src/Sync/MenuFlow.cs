using System.Collections;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>Single place that returns the local player to the main menu, so two callers never race the game's own quit.</summary>
    public static class MenuFlow
    {
        public static bool Quitting { get; private set; }

        private static float menuFirstSeen = -1f;
        private static bool sawWorld;

        /// <summary>Call every frame: tracks when the main menu appeared so joins/loads can wait for it to settle.</summary>
        public static void Tick()
        {
            if (LocalPlayerSync.InWorld) { sawWorld = true; menuFirstSeen = -1f; return; }
            if (uGUI_MainMenu.main != null && (menuFirstSeen < 0f || sawWorld)) { menuFirstSeen = Time.unscaledTime; sawWorld = false; }
        }

        /// <summary>Waits until the main menu exists and has been up for a few seconds (save slots scanned, VR rig up).</summary>
        public static IEnumerator WaitForMenuReady()
        {
            float deadline = Time.unscaledTime + 90f;
            while (Time.unscaledTime < deadline)
            {
                Tick();
                if (uGUI_MainMenu.main != null && !LocalPlayerSync.InWorld && menuFirstSeen >= 0f && Time.unscaledTime - menuFirstSeen >= 4f && SaveLoadManager.main != null) yield break;
                yield return null;
            }
        }

        public static IEnumerator QuitToMainMenu()
        {
            if (Quitting)
            {
                while (Quitting) yield return null;
                yield break;
            }
            if (!LocalPlayerSync.InWorld && uGUI_MainMenu.main != null) yield break;
            Quitting = true;
            try
            {
                Plugin.Instance.Players.Clear();
                if (LocalPlayerSync.InWorld) yield return IngameMenu.QuitToMainMenuAsync();
                float deadline = Time.unscaledTime + 60f;
                while (uGUI_MainMenu.main == null && Time.unscaledTime < deadline) yield return null;
                yield return new WaitForSecondsRealtime(1f);
            }
            finally { Quitting = false; }
        }
    }
}
