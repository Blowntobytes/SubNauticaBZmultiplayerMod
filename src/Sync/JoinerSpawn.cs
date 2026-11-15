using System;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// A joiner loads the host's save, and that save puts the player wherever the HOST was - out in open water, or even
    /// in the seat of the host's Prawn or Seatruck. So once the joiner's world has finished loading he is moved to a
    /// safe spot, first taken out of any vehicle without an exit animation. Until that is done VehicleSync ignores his
    /// "driving" so he never takes the host's vehicle over.
    ///
    /// test41: the safe spot is now wherever the HOST is, when that is safe (from the host's live position):
    ///  - host standing in a Seatruck module: next to the host in that module;
    ///  - host driving a Seatruck: in the first module behind the cab (or the cab when there is none);
    ///  - host on dry ground (land, a base, the drop pod, an alien dry area...): next to the host, inside the base
    ///    or pod if the host is in one;
    ///  - otherwise (host swimming, in a Prawn suit, no position from the host yet): the nearest base or drop pod,
    ///    measured from the host's position (before: from where the joiner arrived).
    /// A spot next to a host on dry ground is checked 2 s later: still in the water there, the joiner is moved to the
    /// nearest base or drop pod instead.
    /// </summary>
    public static class JoinerSpawn
    {
        /// <summary>True from the joiner's arrival until the spawn has been checked (and fixed if needed).</summary>
        public static bool Pending { get; private set; }

        private const float HostPoseWait = 8f;   // longest wait for the host's position after the world has settled

        private static bool wasInWorld;
        private static float readySince = -1f;
        private static float enteredAt;
        private static float verifyAt = -1f;
        private static string verifyWhat;
        private static Vector3 verifyHostPos;
        private static bool loggedWaiting;

        public static void Update()
        {
            var net = Plugin.Instance != null ? Plugin.Instance.Net : null;
            bool inWorld = LocalPlayerSync.InWorld;
            if (!inWorld || net == null || !net.IsInSession)
            {
                wasInWorld = inWorld && net != null && net.IsInSession;
                if (!inWorld) { Pending = false; wasInWorld = false; verifyAt = -1f; }
                return;
            }
            if (!wasInWorld)
            {
                wasInWorld = true;
                Pending = !net.IsHost;
                readySince = -1f;
                enteredAt = Time.unscaledTime;
                verifyAt = -1f;
                loggedWaiting = false;
            }
            if (verifyAt > 0f && Time.unscaledTime >= verifyAt) { verifyAt = -1f; try { Verify(Player.main); } catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn check failed: " + e.Message); } }
            if (!Pending) return;

            var p = Player.main;
            bool loading = true;
            try { loading = UWE.FreezeTime.HasFreezers(); } catch { }
            if (p == null || loading) { readySince = -1f; return; }
            if (readySince < 0f) { readySince = Time.unscaledTime; return; }
            if (Time.unscaledTime - readySince < 1f) return; // let the world settle for a moment

            PlayerPose hp;
            bool hasHost = HostPose(net, out hp);
            if (!hasHost && Time.unscaledTime - readySince < 1f + HostPoseWait)
            {
                if (!loggedWaiting) { loggedWaiting = true; Plugin.Log.LogInfo("Joiner spawn: world loaded; waiting up to " + HostPoseWait.ToString("0") + " s for the host's position."); }
                return;
            }

            Pending = false;
            try { Fix(p, hasHost, hp); }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn check failed: " + e); }
        }

        private static bool HostPose(SteamNet net, out PlayerPose pose)
        {
            pose = default(PlayerPose);
            try
            {
                var reg = Plugin.Instance != null ? Plugin.Instance.Players : null;
                var rp = reg != null ? reg.Get(net.HostSteamId) : null;
                return rp != null && rp.TryGetLastPose(out pose, 2f);
            }
            catch { return false; }
        }

        private static string VehicleOf(Player p)
        {
            if (p.currentMountedVehicle != null) return p.currentMountedVehicle.GetType().Name;
            if (p.IsPilotingSeatruck()) return "SeaTruck (driver's seat)";
            if (p.GetComponentInParent<SeaTruckSegment>() != null || p.currentInterior is SeaTruckSegment) return "SeaTruck";
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Hoverbike>()) if (b != null && b.isPiloting) return "Snowfox";
            return null;
        }

        private static string HostState(PlayerPose hp)
        {
            var f = hp.Flags;
            string s = (f & PoseFlags.Underwater) != 0 ? "underwater" : (f & PoseFlags.Swimming) != 0 ? "swimming" : "dry";
            if ((f & PoseFlags.InVehicle) != 0) s += ", in a vehicle";
            if ((f & PoseFlags.Anchored) != 0) s += ", in Seatruck module [" + hp.AnchorId + "]";
            return s;
        }

        /// <summary>Where to put the joiner. kind: "seatruck", "dry" or "base/pod".</summary>
        private struct Choice
        {
            public string Kind, Where;
            public Vector3 Pos;
            public IInteriorSpace Interior;   // null = none (open ground)
            public SubRoot Sub;
            public SeaTruckSegment Segment;   // entered with the game's own Seatruck enter
            public bool Valid;
        }

        private static void Fix(Player p, bool hasHost, PlayerPose hp)
        {
            Vector3 from = p.transform.position;
            string vehicle = VehicleOf(p);
            string arrived = "arrived " + (vehicle != null ? "in the host's " + vehicle : "where the host was") + " at " + Fmt(from);

            Choice c = default(Choice);
            if (!hasHost) Plugin.Log.LogWarning("Joiner spawn: " + arrived + "; no position from the host yet, so going to the nearest base or drop pod from here.");
            else
            {
                Plugin.Log.LogInfo("Joiner spawn: " + arrived + "; the host is at " + Fmt(hp.BodyPos) + " (" + HostState(hp) + ").");
                c = InHostSeatruck(hp);
                if (!c.Valid) c = NextToDryHost(p, hp);
            }
            if (!c.Valid) c = NearestBaseOrPod(hasHost ? hp.BodyPos : from, hasHost ? "the host" : "here");

            if (!c.Valid) Plugin.Log.LogWarning("Joiner spawn: found no safe spot, no base and no drop pod; using the game's own respawn point.");
            else Plugin.Log.LogInfo("Joiner spawn: going to " + c.Where + " at " + Fmt(c.Pos) + " (" + Vector3.Distance(c.Pos, from).ToString("F0") + " m from where you arrived"
                + (hasHost ? ", " + Vector3.Distance(c.Pos, hp.BodyPos).ToString("0.0") + " m from the host" : "") + ").");

            LeaveVehicles(p, c.Valid ? c.Pos : from, c.Interior);
            Place(p, c);

            string after = VehicleOf(p);
            Plugin.Log.LogInfo("Joiner spawn: now at " + Fmt(p.transform.position) + ", mode " + p.GetMode() + ", interior " +
                (p.currentInterior != null ? p.currentInterior.GetGameObject().name : "none") + (after != null && c.Kind != "seatruck" ? ", STILL IN " + after : "") + ".");

            if (c.Valid && c.Kind == "dry") { verifyAt = Time.unscaledTime + 2f; verifyWhat = c.Where; verifyHostPos = hasHost ? hp.BodyPos : from; }
        }

        // ---------------------------------------------------------------- host in a Seatruck
        private static Choice InHostSeatruck(PlayerPose hp)
        {
            var c = new Choice();
            if ((hp.Flags & PoseFlags.InVehicle) != 0) return c;   // Prawn suit (even one docked in a Seatruck module)
            try
            {
                // Standing in a module: put the joiner next to him, in our copy of that module.
                if ((hp.Flags & PoseFlags.Anchored) != 0 && !string.IsNullOrEmpty(hp.AnchorId))
                {
                    var go = WorldSync.Find(hp.AnchorId);
                    var seg = go != null ? go.GetComponent<SeaTruckSegment>() : null;
                    if (seg == null) { Plugin.Log.LogInfo("Joiner spawn: the host stands in Seatruck module [" + hp.AnchorId + "], which is not loaded here."); return c; }
                    Vector3 local = Quaternion.Inverse(hp.AnchorRot) * (hp.BodyPos - hp.AnchorPos);
                    Vector3 hostHere = seg.transform.position + seg.transform.rotation * local;
                    Vector3 spot = BesideInSegment(seg, hostHere);
                    c.Valid = true; c.Kind = "seatruck"; c.Segment = seg; c.Pos = spot;
                    c.Where = "the host's Seatruck module " + Name(seg.gameObject) + " [" + hp.AnchorId + "], next to the host";
                    return c;
                }
                // Driving: the host sits in the cab. Join him in the first module behind it (or the cab).
                var at = VehicleSync.SegmentAt(hp.BodyPos);
                if (at == null) return c;
                var cab = VehicleSync.FirstOf(at) ?? at;
                SeaTruckSegment behind = null;
                try { var rc = cab.rearConnection != null ? cab.rearConnection.GetConnection() : null; behind = rc != null ? rc.truckSegment : null; } catch { }
                var target = behind ?? cab;
                c.Valid = true; c.Kind = "seatruck"; c.Segment = target; c.Pos = InsidePoint(target);
                c.Where = behind != null ? "the host's Seatruck, module " + Name(behind.gameObject) + " behind the cab" : "the host's Seatruck cab (no modules behind it)";
                return c;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: Seatruck spot failed: " + e.Message); return new Choice(); }
        }

        private static Vector3 InsidePoint(SeaTruckSegment seg)
        {
            float d;
            if (seg.enterPosition != null && VehicleSync.InsideSegment(seg, seg.enterPosition.position, out d) && d >= 0.5f) return seg.enterPosition.position;
            try { var b = OrientedBounds.ToWorldBounds(seg.transform, seg.interiorBounds); if (b.extents.sqrMagnitude > 0.01f) return b.position - seg.transform.up * Mathf.Max(0f, b.extents.y - 1f); } catch { }
            return seg.enterPosition != null ? seg.enterPosition.position : seg.transform.position;
        }

        /// <summary>A spot about 0.8 m beside the host that is still well inside the module (else the host's spot).</summary>
        private static Vector3 BesideInSegment(SeaTruckSegment seg, Vector3 hostHere)
        {
            var t = seg.transform;
            Vector3[] dirs = { t.right, -t.right, -t.forward, t.forward };
            Vector3 best = hostHere; float bestDepth = 0.5f;
            foreach (var dir in dirs)
                foreach (float dist in new[] { 0.8f, 0.5f })
                {
                    Vector3 cand = hostHere + dir * dist; float depth;
                    if (VehicleSync.InsideSegment(seg, cand, out depth) && depth > bestDepth + 0.05f) { bestDepth = depth; best = cand; }
                }
            return best;
        }

        // ---------------------------------------------------------------- host on dry ground
        private static Choice NextToDryHost(Player p, PlayerPose hp)
        {
            var c = new Choice();
            var f = hp.Flags;
            if ((f & (PoseFlags.Underwater | PoseFlags.Swimming | PoseFlags.InVehicle)) != 0) return c;
            Vector3 host = hp.BodyPos;
            if (host == Vector3.zero) return c;
            try
            {
                float hostGround;
                bool hostOnGround = GroundBelow(p, host, out hostGround);
                Vector3 spot = host;
                if (hostOnGround)
                {
                    float above = host.y - hostGround;
                    Vector3 fwd = hp.BodyRot * Vector3.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
                    Vector3 right = Vector3.Cross(Vector3.up, fwd);
                    // Beside, then behind, then in front of the host: on the same floor (within 0.8 m) and with room.
                    Vector3[] offs = { right * 1.2f, -right * 1.2f, -fwd * 1.2f, fwd * 1.2f, right * 0.7f, -right * 0.7f };
                    foreach (var o in offs)
                    {
                        Vector3 cand = host + o; float g;
                        if (!GroundBelow(p, cand, out g) || Mathf.Abs(g - hostGround) > 0.8f) continue;
                        cand.y = g + above;
                        if (Blocked(p, host, cand)) continue;
                        spot = cand; break;
                    }
                }
                c.Valid = true; c.Kind = "dry"; c.Pos = spot;
                string beside = spot == host ? "at the host's own spot" : Vector3.Distance(spot, host).ToString("0.0") + " m beside the host";

                // Inside a base? Then be in it (oxygen, no swimming).
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Base>())
                {
                    if (b == null) continue;
                    bool inside = false;
                    try { inside = !b.IsCellEmpty(b.WorldToGrid(host)) || !b.IsCellEmpty(b.WorldToGrid(host + Vector3.down * 1.5f)); } catch { }
                    if (!inside) continue;
                    var sub = b.GetComponent<SubRoot>();
                    if (sub == null) continue;
                    c.Sub = sub; c.Interior = sub;
                    c.Where = "the host's base '" + sub.name + "', " + beside;
                    return c;
                }
                // In the drop pod?
                var pod = UnityEngine.Object.FindObjectOfType<LifepodDrop>();
                if (pod != null && Vector3.Distance(pod.transform.position, host) < 3f)
                {
                    c.Interior = pod;
                    c.Where = "the drop pod with the host, " + beside;
                    return c;
                }
                c.Where = "dry ground next to the host (" + beside + (hostOnGround ? "" : "; no ground found under the host") + ")";
                return c;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: dry spot failed: " + e.Message); return new Choice(); }
        }

        private static bool GroundBelow(Player p, Vector3 pos, out float y)
        {
            y = 0f;
            var hits = Physics.RaycastAll(pos + Vector3.up * 1.0f, Vector3.down, 4f, -1, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(p.transform)) continue;
                if (h.distance < bestDist) { bestDist = h.distance; y = h.point.y; }
            }
            return bestDist < float.MaxValue;
        }

        /// <summary>Something solid in the way from the host to the spot, or around the spot at chest height.</summary>
        private static bool Blocked(Player p, Vector3 host, Vector3 cand)
        {
            Vector3 a = host + Vector3.up * 0.3f, b = cand + Vector3.up * 0.3f;
            foreach (var h in Physics.RaycastAll(a, (b - a).normalized, Vector3.Distance(a, b), -1, QueryTriggerInteraction.Ignore))
                if (h.collider != null && !h.collider.transform.IsChildOf(p.transform)) return true;
            foreach (var col in Physics.OverlapSphere(cand + Vector3.up * 0.3f, 0.35f, -1, QueryTriggerInteraction.Ignore))
                if (col != null && !col.transform.IsChildOf(p.transform)) return true;
            return false;
        }

        // ---------------------------------------------------------------- nearest base / drop pod
        private static Choice NearestBaseOrPod(Vector3 from, string fromWhat)
        {
            var c = new Choice();
            float best = float.MaxValue;
            foreach (var sub in UnityEngine.Object.FindObjectsOfType<SubRoot>())
            {
                if (sub == null || !sub.isBase) continue;
                RespawnPoint rp = null;
                try { if (!sub.IsValidForRespawn()) continue; rp = sub.GetRespawnPoint(); } catch { }
                if (rp == null) continue;
                Vector3 pos = rp.GetSpawnPosition();
                float d = (pos - from).sqrMagnitude;
                if (d < best) { best = d; c.Sub = sub; c.Interior = sub; c.Pos = pos; c.Where = "base '" + sub.name + "'"; c.Valid = true; }
            }
            var pod = UnityEngine.Object.FindObjectOfType<LifepodDrop>();
            RespawnPoint podRp = null;
            if (pod != null) try { podRp = ((IInteriorSpace)pod).GetRespawnPoint(); } catch { }
            if (podRp != null)
            {
                Vector3 pos = podRp.GetSpawnPosition();
                float d = (pos - from).sqrMagnitude;
                if (d < best) { best = d; c.Interior = pod; c.Sub = null; c.Pos = pos; c.Where = "the drop pod"; c.Valid = true; }
            }
            c.Kind = "base/pod";
            if (c.Valid) c.Where = "the nearest base or drop pod to " + fromWhat + ": " + c.Where + " (" + Mathf.Sqrt(best).ToString("F0") + " m from " + fromWhat + ")";
            return c;
        }

        // ---------------------------------------------------------------- moving the player
        private static void LeaveVehicles(Player p, Vector3 spawn, IInteriorSpace target)
        {
            // Get out of the vehicle without any exit animation or exit teleport.
            try
            {
                if (p.IsPilotingSeatruck())
                {
                    SeaTruckMotor motor = null;
                    var seg0 = p.GetComponentInParent<SeaTruckSegment>();
                    if (seg0 != null) motor = seg0.GetComponent<SeaTruckMotor>();
                    if (motor == null)
                        foreach (var m in UnityEngine.Object.FindObjectsOfType<SeaTruckMotor>())
                            if (m != null && m._piloting) { motor = m; break; }
                    if (motor != null) motor.StopPiloting(false, true, false, true, false, target);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: leaving the Seatruck seat failed: " + e.Message); }
            try
            {
                var seg = p.GetComponentInParent<SeaTruckSegment>() ?? (p.currentInterior as SeaTruckSegment);
                if (seg != null) seg.Exit(spawn, true, true, target, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: leaving the Seatruck failed: " + e.Message); }
            try
            {
                if (p.currentMountedVehicle != null || p.GetMode() != Player.Mode.Normal) p.ExitLockedMode(false, false, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: leaving the vehicle failed: " + e.Message); }
            try { if (p.transform.parent != null && p.transform.parent.GetComponentInParent<SeaTruckSegment>() != null) p.transform.SetParent(null, true); } catch { }
            // Out of whatever interior the save left us in (a base or pod far away); the new spot sets its own.
            try { if (p.currentInterior != null && p.currentInterior != target) p.ExitCurrentInterior(); } catch { }
        }

        private static void Place(Player p, Choice c)
        {
            if (!c.Valid)
            {
                try { p.MovePlayerToRespawnPoint(false); } catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: respawn move failed: " + e.Message); }
                return;
            }
            if (c.Segment != null)
            {
                // The game's own Seatruck enter (sets the truck's player, the interior, oxygen...), then our spot.
                try { c.Segment.Enter(p); } catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: entering the Seatruck failed: " + e.Message); }
                p.SetPosition(c.Pos);
                try { c.Segment.UpdateKinematicState(); } catch { }
                return;
            }
            p.SetPosition(c.Pos);
            try { p.SetCurrentSub(c.Sub); } catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: SetCurrentSub failed: " + e.Message); }
            try { if (c.Interior != null && p.currentInterior != c.Interior) p.EnterInterior(c.Interior); } catch (Exception e) { Plugin.Log.LogWarning("Joiner spawn: EnterInterior failed: " + e.Message); }
        }

        /// <summary>2 s after a spot next to the host on dry ground: still dry? If the game has us in the water there, go to the nearest base / pod.</summary>
        private static void Verify(Player p)
        {
            if (p == null) return;
            bool wet = p.IsUnderwater() || p.IsSwimming();
            if (!wet) { Plugin.Log.LogInfo("Joiner spawn check: 2 s after arriving at " + verifyWhat + ": dry, at " + Fmt(p.transform.position) + ". Good."); return; }
            Plugin.Log.LogWarning("Joiner spawn check: 2 s after arriving at " + verifyWhat + " you are in the water (at " + Fmt(p.transform.position) + "); moving you to the nearest base or drop pod instead.");
            var c = NearestBaseOrPod(verifyHostPos, "the host");
            if (!c.Valid) { Plugin.Log.LogWarning("Joiner spawn check: no base or drop pod to go to; staying here."); return; }
            Place(p, c);
            Plugin.Log.LogInfo("Joiner spawn check: moved to " + c.Where + " at " + Fmt(p.transform.position) + ".");
        }

        private static string Name(GameObject go) { try { return CraftData.GetTechType(go).ToString(); } catch { return go != null ? go.name : "?"; } }
        private static string Fmt(Vector3 v) { return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")"; }
    }
}
