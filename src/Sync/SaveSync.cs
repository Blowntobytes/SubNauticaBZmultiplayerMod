using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Streams the host's save slot to a joining client over the reliable channel, then loads it on the client
    /// into a dedicated multiplayer slot. Removes the need to hand save folders around.
    ///
    /// Host side:   SaveRequest -> (force a save, wait) -> SaveBegin, SaveFile+SaveChunk..., SaveEnd
    /// Client side: writes files under <SavedGames>/<MultiplayerSlot>/, then loads that slot
    ///              (quits to the main menu first if a game is running).
    /// </summary>
    public sealed class SaveSync
    {
        private const int ChunkSize = 192 * 1024; // under Steam's 512 KB reliable message cap

        private readonly SteamNet net;

        // --- host state
        private Coroutine hostJob;
        private Coroutine loadJob;

        // --- client state
        private string clientSlotDir;
        private string currentFile;
        private FileStream currentStream;
        private int fileCount, filesDone;
        private long totalBytes, bytesDone;
        private bool receiving;
        private float lastProgress;

        public string StatusText { get; private set; }

        public SaveSync(SteamNet net) { this.net = net; StatusText = ""; }

        // ------------------------------------------------------------------ paths

        /// <summary>The folder the game keeps its slotNNNN directories in (SNAppData/SavedGames on Steam PC).</summary>
        public static string GetSavedGamesPath()
        {
            try
            {
                var services = PlatformUtils.main != null ? PlatformUtils.main.GetServices() : null;
                var storage = services != null ? services.GetUserStorage() : null;
                if (storage != null)
                {
                    var f = storage.GetType().GetField("savePath", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var p = f != null ? f.GetValue(storage) as string : null;
                    if (!string.IsNullOrEmpty(p)) return Path.GetFullPath(p);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("savePath lookup failed: " + e.Message); }
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../SNAppData/SavedGames"));
        }

        // ------------------------------------------------------------------ host

        /// <summary>Client asked for the world. Save first so they get the live state, then stream.</summary>
        public void OnSaveRequested(ulong client)
        {
            if (hostJob != null) { net.SendSaveStatus(client, "host is already sending a save, try again shortly"); return; }
            hostJob = Plugin.Instance.StartCoroutine(HostSendRoutine(client));
        }

        private IEnumerator HostSendRoutine(ulong client)
        {
            try
            {
                string slot = SaveLoadManager.main != null ? SaveLoadManager.main.GetCurrentSlot() : null;
                if (string.IsNullOrEmpty(slot) || !LocalPlayerSync.InWorld)
                {
                    net.SendSaveStatus(client, "host is not in a game yet");
                    yield break;
                }

                // Force a save so the client gets the current world, not the last manual save.
                if (IngameMenu.main != null && !SaveLoadManager.main.isSaving)
                {
                    net.SendSaveStatus(client, "host is saving...");
                    Plugin.Log.LogInfo("Saving before sending world to " + client);
                    IngameMenu.main.SaveGame();
                    yield return null;
                    float deadline = Time.unscaledTime + 120f;
                    while (SaveLoadManager.main.isSaving && Time.unscaledTime < deadline) yield return null;
                    yield return new WaitForSecondsRealtime(1.5f);
                }

                string dir = Path.Combine(GetSavedGamesPath(), slot);
                if (!Directory.Exists(dir))
                {
                    net.SendSaveStatus(client, "host save folder missing: " + dir);
                    Plugin.Log.LogError("Save folder not found: " + dir);
                    yield break;
                }

                var files = new List<string>();
                CollectFiles(dir, dir, files);
                long total = 0;
                foreach (var f in files) total += new FileInfo(Path.Combine(dir, f)).Length;
                Plugin.Log.LogInfo("Sending save " + slot + " to " + client + ": " + files.Count + " files, " + (total / 1024) + " KB");
                net.SendSaveBegin(client, slot, files.Count, total);

                for (int i = 0; i < files.Count; i++)
                {
                    string rel = files[i];
                    byte[] data = null;
                    string err = null;
                    // The game keeps writing slot files (screenshot, gameinfo) for a moment after isSaving clears; retry.
                    for (int attempt = 0; attempt < 40 && data == null; attempt++)
                    {
                        if (TryRead(Path.Combine(dir, rel), out data, out err)) break;
                        yield return new WaitForSecondsRealtime(0.25f);
                    }
                    if (data == null)
                    {
                        Plugin.Log.LogError("Could not read " + rel + " after retries: " + err);
                        net.SendSaveAbort(client, "host could not read " + rel);
                        yield break;
                    }
                    net.SendSaveFile(client, i, rel, data.Length);
                    for (int off = 0; off < data.Length; off += ChunkSize)
                    {
                        int len = Math.Min(ChunkSize, data.Length - off);
                        net.SendSaveChunk(client, i, off, data, len);
                        // Let the reliable queue drain a bit so we don't buffer the whole save in memory twice.
                        while (net.ReliableQueueLength > 8) yield return null;
                    }
                    if (data.Length == 0) net.SendSaveChunk(client, i, 0, data, 0);
                }
                net.SendSaveEnd(client);
                Plugin.Log.LogInfo("Save queued for " + client);
            }
            finally { hostJob = null; }
        }

        private static bool TryRead(string path, out byte[] data, out string error)
        {
            data = null; error = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        private static void CollectFiles(string root, string dir, List<string> outRel)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                string rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                outRel.Add(rel);
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(d).Equals("screenshots", StringComparison.OrdinalIgnoreCase)) continue; // player photos, not world state
                CollectFiles(root, d, outRel);
            }
        }

        // ------------------------------------------------------------------ client

        public void OnSaveStatus(string text)
        {
            StatusText = text;
            Plugin.Log.LogInfo("Host: " + text);
        }

        public void OnSaveBegin(string hostSlot, int count, long total)
        {
            string slot = Plugin.MultiplayerSlot.Value;
            clientSlotDir = Path.Combine(GetSavedGamesPath(), slot);
            try
            {
                if (Directory.Exists(clientSlotDir)) Directory.Delete(clientSlotDir, true);
                Directory.CreateDirectory(clientSlotDir);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Cannot prepare slot folder " + clientSlotDir + ": " + e.Message);
                receiving = false;
                return;
            }
            fileCount = count; filesDone = 0; totalBytes = total; bytesDone = 0; receiving = true; lastProgress = Time.unscaledTime;
            StatusText = "receiving world (0%)";
            Plugin.Log.LogInfo("Receiving host save '" + hostSlot + "' into " + slot + ": " + count + " files, " + (total / 1024) + " KB");
        }

        public void OnSaveFile(int index, string rel, int size)
        {
            if (!receiving) return;
            CloseCurrent();
            if (rel.Contains("..")) { Plugin.Log.LogError("Rejected path " + rel); return; }
            string full = Path.Combine(clientSlotDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            currentFile = rel;
            lastProgress = Time.unscaledTime;
            currentStream = new FileStream(full, FileMode.Create, FileAccess.Write);
        }

        public void OnSaveChunk(int index, int offset, byte[] buf, int start, int len)
        {
            if (!receiving || currentStream == null) return;
            currentStream.Position = offset;
            currentStream.Write(buf, start, len);
            bytesDone += len;
            lastProgress = Time.unscaledTime;
            if (totalBytes > 0) StatusText = "receiving world (" + (int)(bytesDone * 100 / totalBytes) + "%)";
        }

        public void OnSaveAbort(string reason)
        {
            CloseCurrent();
            receiving = false;
            StatusText = "transfer failed: " + reason + "  (" + Plugin.RequestWorldKey.Value + " to retry)";
            Plugin.Log.LogError("World transfer aborted by host: " + reason);
        }

        /// <summary>Called every frame by the plugin; times out a transfer that stops making progress.</summary>
        public void Update()
        {
            if (!receiving) return;
            if (Time.unscaledTime - lastProgress > 30f)
            {
                CloseCurrent();
                receiving = false;
                StatusText = "transfer stalled  (" + Plugin.RequestWorldKey.Value + " to retry)";
                Plugin.Log.LogError("World transfer stalled: no data for 30 s (" + bytesDone / 1024 + " of " + totalBytes / 1024 + " KB).");
            }
        }

        public void OnSaveEnd()
        {
            if (!receiving) return;
            CloseCurrent();
            receiving = false;
            StatusText = "world received, loading...";
            Plugin.Log.LogInfo("Save received (" + (bytesDone / 1024) + " KB). Loading slot " + Plugin.MultiplayerSlot.Value);
            loadJob = Plugin.Instance.StartCoroutine(ClientLoadRoutine());
        }

        private void CloseCurrent()
        {
            if (currentStream != null) { currentStream.Flush(); currentStream.Dispose(); currentStream = null; filesDone++; }
            currentFile = null;
        }

        private IEnumerator ClientLoadRoutine()
        {
            string slot = Plugin.MultiplayerSlot.Value;
            string infoPath = Path.Combine(clientSlotDir, "gameinfo.json");
            if (!File.Exists(infoPath))
            {
                Plugin.Log.LogError("Received save has no gameinfo.json; cannot load.");
                StatusText = "bad save received";
                yield break;
            }

            // If we're in a game, go back to the menu first.
            if (LocalPlayerSync.InWorld || uGUI_MainMenu.main == null)
            {
                Plugin.Log.LogInfo("Quitting to main menu to load the host's world.");
                yield return MenuFlow.QuitToMainMenu();
            }
            yield return MenuFlow.WaitForMenuReady();
            if (uGUI_MainMenu.main == null)
            {
                Plugin.Log.LogError("Main menu not available; load " + slot + " manually from the Load menu.");
                StatusText = "load slot " + slot + " manually";
                yield break;
            }

            SaveLoadManager.GameInfo info;
            try
            {
                string shot = Path.Combine(clientSlotDir, "screenshot.jpg");
                info = SaveLoadManager.GameInfo.LoadFromBytes(File.ReadAllBytes(infoPath), File.Exists(shot) ? File.ReadAllBytes(shot) : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("gameinfo.json parse failed: " + e);
                StatusText = "bad gameinfo";
                yield break;
            }

            Plugin.Log.LogInfo("Loading multiplayer slot " + slot + " (session " + info.session + ", mode " + info.gameModePresetId + ")");
            StatusText = "loading host world";
            yield return uGUI_MainMenu.main.LoadGameAsync(slot, info.session, info.changeSet, info.gameModePresetId, info.gameOptions, info.storyVersion);
            StatusText = "";
        }

        public void Reset()
        {
            if (loadJob != null) { Plugin.Instance.StopCoroutine(loadJob); loadJob = null; }
            CloseCurrent();
            receiving = false;
            StatusText = "";
        }
    }
}
