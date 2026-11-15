using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Ends the session when the player leaves the world. Quitting to the main menu (or to the desktop) from the
    /// pause menu used to leave the lobby open: the host was gone but the world stayed hosted, so a joiner kept
    /// playing in a world nobody was serving until the host's process finally exited.
    /// </summary>
    public static class SessionExit
    {
        private static SteamNet net;
        private static bool wasInWorld;

        public static void Install(SteamNet steamNet) { net = steamNet; }

        public static void Update()
        {
            bool inWorld = LocalPlayerSync.InWorld;
            if (wasInWorld && !inWorld) OnLeftWorld();
            wasInWorld = inWorld;
        }

        private static void OnLeftWorld()
        {
            if (net == null || !net.IsInSession) return;

            // Our own leave path and the joiner's world transfer both pass through the menu on purpose.
            if (MenuFlow.Quitting) return;
            if (net.Saves != null && net.Saves.Busy) return;

            Plugin.Log.LogInfo(net.IsHost
                ? "Left the world; closing the session so nobody is left in an unhosted world."
                : "Left the world; leaving the session.");
            net.Leave(false);
        }
    }
}
