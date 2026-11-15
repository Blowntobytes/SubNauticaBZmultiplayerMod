using System;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// test30: a player sometimes ends up in the open sea with no water effect and falls to the sea floor, walking as if
    /// on dry land. The game decides "underwater" from where it thinks the player is: while Player.currentInterior is
    /// set (a Seatruck segment, a drop pod...) the player counts as dry wherever they really are. A Seatruck that is
    /// detached, docked or moved by the other player can leave that set after the player is no longer inside it.
    ///
    /// Every quarter second: if the local player is below the sea surface, not in a vehicle, seat or cutscene, the game
    /// says "dry", and the interior it thinks they are in is a Seatruck segment they are NOT inside (no segment of any
    /// truck contains them, 0.5 m slack), for a full second, they are taken out of that interior (the game's own
    /// ExitCurrentInterior), which puts them back in the water. Other "dry below the surface" states (bases, the drop
    /// pod, dry story areas) are only logged, once a minute, with what the game thinks, so the next log shows the cause.
    /// </summary>
    public static class WaterGuard
    {
        private static float nextCheck, wrongSince = -1f, nextOtherLog, confirmAt = -1f;
        private static string fixedWhat;

        public static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;
            var p = Player.main;
            if (p == null || !LocalPlayerSync.InWorld) { wrongSince = -1f; confirmAt = -1f; return; }

            if (confirmAt > 0f && Time.unscaledTime >= confirmAt)
            {
                confirmAt = -1f;
                string motor = "?";
                try { var pc = p.playerController; motor = pc != null && pc.activeController != null ? pc.activeController.GetType().Name : "none"; } catch { }
                Plugin.Log.LogInfo("Water check: 1 s after taking you out of " + fixedWhat + ": " + (p.IsUnderwater() ? "in the water" : "STILL DRY")
                    + ", motor " + motor + ", interior " + Describe(p) + ".");
            }

            try
            {
                bool free = !p.cinematicModeActive && p.GetMode() == Player.Mode.Normal && !p.IsTeleporting()
                    && p.GetVehicle() == null && p.currentWaterPark == null;
                // test32: the other way round - back inside a Seatruck module (swam in through an open joint) but the
                // game still has them in the sea: swimming and losing oxygen in a dry room.
                if (free && p.IsUnderwater() && p.currentInterior == null && p.currentSub == null) { CheckBackInside(p); wrongSince = -1f; return; }
                insideSince = -1f;
                bool candidate = free && p.transform.position.y < Ocean.GetOceanLevel() - 1f && !p.IsUnderwater();
                if (!candidate) { wrongSince = -1f; return; }

                var seg = p.currentInterior as SeaTruckSegment;
                // test36: fell out of a docked Seatruck into open water and walked on the bottom: the game still had the
                // player in the base (interior and sub Base). If no room of that base is at the player's spot (and no
                // Seatruck is around them), take them out of it.
                if (seg == null && OutsideOwnBase(p))
                {
                    if (wrongSince < 0f) { wrongSince = Time.unscaledTime; return; }
                    if (Time.unscaledTime - wrongSince < 1f) return;
                    Plugin.Log.LogWarning("Water check: you were in the sea at " + p.transform.position.ToString("F1") + " (" + (Ocean.GetOceanLevel() - p.transform.position.y).ToString("0.0")
                        + " m deep), outside every room of the base, but the game still had you inside " + Describe(p) + ", so no water. Taking you out of it.");
                    fixedWhat = "the base";
                    p.ExitCurrentInterior();
                    wrongSince = -1f;
                    confirmAt = Time.unscaledTime + 1f;
                    return;
                }
                if (seg == null)
                {
                    if (Time.unscaledTime >= nextOtherLog && wrongSince > 0f && Time.unscaledTime - wrongSince > 3f)
                    {
                        nextOtherLog = Time.unscaledTime + 60f;
                        Plugin.Log.LogInfo("Water check: dry " + (Ocean.GetOceanLevel() - p.transform.position.y).ToString("0.0") + " m below the sea surface at "
                            + p.transform.position.ToString("F1") + ", interior " + Describe(p) + " (not a Seatruck; left alone).");
                    }
                    if (wrongSince < 0f) wrongSince = Time.unscaledTime;
                    return;
                }

                // Really inside some Seatruck (this one or any other)? Then the game is right.
                if (VehicleSync.SegmentAt(p.transform.position) != null) { wrongSince = -1f; return; }

                if (wrongSince < 0f) { wrongSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - wrongSince < 1f) return;

                float dist = Vector3.Distance(seg.transform.position, p.transform.position);
                fixedWhat = CraftData.GetTechType(seg.gameObject) + " (" + dist.ToString("0.0") + " m away)";
                Plugin.Log.LogWarning("Water check: you were in the sea at " + p.transform.position.ToString("F1") + " ("
                    + (Ocean.GetOceanLevel() - p.transform.position.y).ToString("0.0") + " m deep) but the game still had you inside "
                    + fixedWhat + ", so no water. Taking you out of it.");
                try { if (p.transform.parent != null && p.transform.parent.GetComponentInParent<SeaTruckSegment>() != null) p.transform.SetParent(null, true); } catch { }
                p.ExitCurrentInterior();
                wrongSince = -1f;
                confirmAt = Time.unscaledTime + 1f;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Water check failed: " + e.Message); nextCheck = Time.unscaledTime + 5f; }
        }

        private static float insideSince = -1f;

        private static void CheckBackInside(Player p)
        {
            SeaTruckSegment seg = null; float best = 0.3f;
            foreach (var sg in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
            {
                float depth;
                if (sg != null && VehicleSync.InsideSegment(sg, p.transform.position, out depth) && depth >= best) { best = depth; seg = sg; }
            }
            if (seg == null) { insideSince = -1f; return; }
            bool canBreathe = true;
            try { canBreathe = ((IInteriorSpace)seg).CanBreathe(); } catch { }
            if (!canBreathe) { insideSince = -1f; return; }
            if (insideSince < 0f) { insideSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - insideSince < 1f) return;
            insideSince = -1f;
            string what = CraftData.GetTechType(seg.gameObject).ToString();
            Plugin.Log.LogWarning("Water check: you are " + best.ToString("0.0") + " m inside " + what + " but the game had you in the sea (swimming, using oxygen). Putting you back inside it.");
            try
            {
                HarmonyLib.Traverse.Create(seg).Field("player").SetValue(p);
                seg.PropagatePlayer();
                p.EnterInterior(seg);
                seg.UpdateKinematicState();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Water check: putting you back inside failed: " + e.Message); return; }
            fixedWhat = what + " (back inside)";
            confirmAt = Time.unscaledTime + 1f;
        }

        private static bool OutsideOwnBase(Player p)
        {
            try
            {
                var sub = p.currentSub;
                if (sub == null || !sub.isBase) return false;
                var b = sub.GetComponent<Base>();
                if (b == null) return false;
                Vector3 pos = p.transform.position;
                if (VehicleSync.SegmentAt(pos) != null) return false;          // in a (docked) Seatruck
                // The cell at the player's spot, and the one just below their feet: a room counts for either.
                if (!b.IsCellEmpty(b.WorldToGrid(pos))) return false;
                if (!b.IsCellEmpty(b.WorldToGrid(pos + Vector3.down * 1.5f))) return false;
                return true;
            }
            catch { return false; }
        }

        private static string Describe(Player p)
        {
            string s = "none";
            try
            {
                var i = p.currentInterior;
                if (i != null) { var c = i as Component; s = c != null ? c.gameObject.name : i.GetType().Name; }
                if (p.currentSub != null) s += ", sub " + p.currentSub.gameObject.name;
                if (p.forceWalkMotorMode) s += ", forceWalkMotorMode";
            }
            catch { }
            return s;
        }
    }
}
