using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Vehicles built at the Mobile Vehicle Bay (Snowfox, Seatruck and its modules, Prawn suit...) - since 0.14.3.
    /// The bay spawns the vehicle locally only (ConstructorInput.OnCraftingBeginAsync), so the other players never
    /// saw it. The game calls CrafterLogic.NotifyCraftEnd(vehicle, techType) once the new vehicle is in place: that is
    /// hooked here and the vehicle is sent with its id, position and rotation. The other side spawns the same prefab
    /// with the same id (so later events about it line up), plays the build effect, and registers it with the world so
    /// the host's save keeps it.
    /// </summary>
    public static class VehicleSync
    {
        private static SteamNet net;

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;
            var m = AccessTools.Method(typeof(CrafterLogic), "NotifyCraftEnd", new[] { typeof(GameObject), typeof(TechType) });
            if (m == null) { Plugin.Log.LogWarning("VehicleSync: CrafterLogic.NotifyCraftEnd not found; built vehicles will not sync."); return; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(VehicleSync), "CraftEndPostfix"));
            InstallMovement(harmony);
            InstallDocking(harmony);
            InstallOccupancy(harmony);
            Plugin.Log.LogInfo("VehicleSync installed.");
        }

        private static bool IsVehicle(GameObject go)
        {
            if (go == null) return false;
            if (go.GetComponent<Pickupable>() != null) return false;          // fabricator items go to the inventory
            return go.GetComponent<Vehicle>() != null || go.GetComponent<SeaTruckSegment>() != null
                || go.GetComponent<Hoverbike>() != null || go.GetComponent<SubRoot>() != null;
        }

        private static void CraftEndPostfix(GameObject target, TechType techType)
        {
            try
            {
                if (!WorldSync.CanSend || !IsVehicle(target)) return;
                string id = WorldSync.IdOf(target.transform);
                if (string.IsNullOrEmpty(id)) { Plugin.Log.LogWarning("Built " + techType + " but it has no id; not shared."); return; }
                net.SendVehicleBuilt((int)techType, id, target.transform.position, target.transform.rotation);
                Plugin.Log.LogInfo("Vehicle built, sent: " + techType + " [" + id + "] at " + target.transform.position);
            }
            catch (Exception e) { Plugin.Log.LogWarning("VehicleSync send failed: " + e.Message); }
        }

        public static void OnVehicleBuilt(int techType, string id, Vector3 pos, Quaternion rot)
        {
            if (!LocalPlayerSync.InWorld) return;
            if (WorldSync.Find(id) != null) { Plugin.Log.LogInfo("Vehicle from the other player already here: " + (TechType)techType + " [" + id + "]"); return; }
            Plugin.Instance.StartCoroutine(Spawn((TechType)techType, id, pos, rot));
        }

        private static IEnumerator Spawn(TechType tt, string id, Vector3 pos, Quaternion rot)
        {
            var result = new TaskResult<GameObject>();
            yield return CraftData.InstantiateFromPrefabAsync(tt, result, false);
            var go = result.Get();
            if (go == null) { Plugin.Log.LogWarning("Vehicle from the other player: no prefab for " + tt); yield break; }
            if (WorldSync.Find(id) != null) { UnityEngine.Object.Destroy(go); yield break; }   // arrived twice meanwhile
            WorldSync.applyingRemote = true;
            try
            {
                go.transform.SetPositionAndRotation(pos, rot);
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = id;
                go.SetActive(true);
                var vfx = go.GetComponentInChildren<VFXConstructing>();
                if (vfx != null) { vfx.timeToConstruct = 10f; vfx.StartConstruction(); }
                LargeWorldEntity.Register(go);
                Plugin.Log.LogInfo("Vehicle from the other player: " + tt + " [" + id + "] built at " + pos);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Vehicle from the other player could not be placed: " + e.Message); }
            finally { WorldSync.applyingRemote = false; }
        }
    
        // ================================================================== driving (since 0.14.4)
        //
        // The player driving a vehicle (Snowfox, Seatruck with all its modules, Prawn suit) streams its position about
        // 12 times a second. The other side holds its copy of that vehicle still (kinematic) and glides it to each new
        // position. When the driver lets go (gets out, or the stream stops), a final position is sent reliably and the
        // copy gets its physics back. A Mobile Vehicle Bay that is deploying is streamed the same way for a while, and
        // the other side then deploys its copy too, instead of letting it sink to the bottom.

        private const float SendInterval = 1f / 12f;
        private const float ConstructorTrackSeconds = 15f;
        private const byte FlagDeployed = 1;

        private static float nextSend, nextScan, nextWhyLog, nextCabFallbackLog, lastPilotedAt = -100f;
        private static SeaTruckSegment lastPilotedCab;
        private static Hoverbike[] bikes = new Hoverbike[0];
        private static readonly System.Collections.Generic.Dictionary<string, GameObject> sentLast = new System.Collections.Generic.Dictionary<string, GameObject>();
        private static readonly System.Collections.Generic.Dictionary<string, float> trackedConstructors = new System.Collections.Generic.Dictionary<string, float>();
        private static bool applyingDeploy;

        internal static void InstallMovement(Harmony harmony)
        {
            var deploy = AccessTools.Method(typeof(Constructor), "Deploy", new[] { typeof(bool) });
            if (deploy != null) harmony.Patch(deploy, postfix: new HarmonyMethod(typeof(VehicleSync), "DeployPostfix"));
            else Plugin.Log.LogWarning("VehicleSync: Constructor.Deploy not found; the vehicle bay deploy will not sync.");
        }

        private static void DeployPostfix(Constructor __instance, bool value)
        {
            if (applyingDeploy || !value || !WorldSync.CanSend || __instance == null) return;
            string id = WorldSync.IdOf(__instance);
            if (string.IsNullOrEmpty(id) || remote.ContainsKey(id)) return;
            trackedConstructors[id] = Time.unscaledTime + ConstructorTrackSeconds;
            // The deploy itself goes as its own reliable message, at the moment the game does it. Before 0.14.11-test6 it
            // was only a flag on the (unreliable) position stream, applied when that stream was released - and the
            // release came while the bay was still sinking and not yet deployed, so the other side never unfolded it.
            net.SendVehicleDeploy(id, __instance.transform.position, __instance.transform.rotation);
            Plugin.Log.LogInfo("Vehicle bay deployed; sent [" + id + "].");
        }

        private class PendingDeploy { public Vector3 Pos; public Quaternion Rot; public float Until; }
        private static readonly System.Collections.Generic.Dictionary<string, PendingDeploy> pendingDeploys = new System.Collections.Generic.Dictionary<string, PendingDeploy>();

        /// <summary>Each tick: a deploy that arrived before its bay is applied once the bay is here.</summary>
        private static void TickPendingDeploys()
        {
            if (pendingDeploys.Count == 0) return;
            System.Collections.Generic.List<string> done = null;
            foreach (var kv in pendingDeploys)
            {
                var go = WorldSync.Find(kv.Key);
                if (go == null) { if (Time.unscaledTime > kv.Value.Until) (done = done ?? new System.Collections.Generic.List<string>()).Add(kv.Key); continue; }
                (done = done ?? new System.Collections.Generic.List<string>()).Add(kv.Key);
                var c = go.GetComponent<Constructor>();
                if (c == null) continue;
                Plugin.Log.LogInfo("Vehicle bay appeared; applying the deploy that arrived earlier [" + kv.Key + "].");
                OnVehicleDeploy(kv.Key, kv.Value.Pos, kv.Value.Rot);
            }
            if (done != null) foreach (var k in done) pendingDeploys.Remove(k);
        }

        public static void OnVehicleDeploy(string id, Vector3 pos, Quaternion rot)
        {
            if (!LocalPlayerSync.InWorld) return;
            remoteTouched[id] = Time.unscaledTime;
            var go = WorldSync.Find(id);
            if (go == null)
            {
                // The dropped bay is spawned here asynchronously and the deploy message can arrive first (it did in
                // 0.14.11-test6: "deploy for a bay not loaded here"). Keep it and apply it when the bay appears.
                pendingDeploys[id] = new PendingDeploy { Pos = pos, Rot = rot, Until = Time.unscaledTime + 20f };
                Plugin.Log.LogInfo("Vehicle bay deploy arrived before the bay [" + id + "]; will unfold it when it appears.");
                return;
            }
            var c = go.GetComponent<Constructor>();
            if (c == null) return;
            // Stop gliding it (the bay's own physics settles it from here) and unfold it where the other player did.
            RemoteState st;
            if (remote.TryGetValue(id, out st)) { remote.Remove(id); var rb = BodyOf(go); if (rb != null) rb.isKinematic = st.WasKinematic; }
            go.transform.SetPositionAndRotation(pos, rot);
            Plugin.Instance.StartCoroutine(DeployWhenReady(c));
        }

        private static readonly System.Reflection.MethodInfo mFirstSegment = AccessTools.Method(typeof(SeaTruckSegment), "GetFirstSegment");
        private static readonly System.Reflection.MethodInfo mDeploy = AccessTools.Method(typeof(Constructor), "Deploy", new[] { typeof(bool) });
        internal static SeaTruckSegment FirstOf(SeaTruckSegment s)
        {
            try { return mFirstSegment != null ? (SeaTruckSegment)mFirstSegment.Invoke(s, null) : s; } catch { return s; }
        }

        private static readonly System.Collections.Generic.Dictionary<string, bool> held = new System.Collections.Generic.Dictionary<string, bool>();
        private static readonly System.Collections.Generic.List<GameObject> controlled = new System.Collections.Generic.List<GameObject>();

        /// <summary>What this player is moving right now.</summary>
        private static void CollectControlled()
        {
            controlled.Clear();
            var p = Player.main;
            if (p == null) return;
            if (JoinerSpawn.Pending) return; // arrived in the host's vehicle: never take it over
            // test40: back as before test39 - a docked vehicle you sit in is counted as occupied (so the other PC seats you
            // in it) but not streamed: the docked check further down ("held") already does that.
            if (p.currentMountedVehicle != null) controlled.Add(p.currentMountedVehicle.gameObject);

            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; bikes = UnityEngine.Object.FindObjectsOfType<Hoverbike>(); }
            foreach (var b in bikes) if (b != null && b.isPiloting) controlled.Add(b.gameObject);

            // test38: the game's IsPilotingSeatruck only looks for a Seatruck part above the player - inside a Prawn
            // suit docked in a Seatruck docking module that is the module, so getting into the Prawn counted as driving
            // the truck: the mod freed the docked tail from the dock tube and streamed it (it floated off). Now only a
            // Seatruck whose controls this player is really at (motor piloting) counts, and never while in a vehicle.
            if (p.IsPilotingSeatruck() && p.currentMountedVehicle == null)
            {
                SeaTruckSegment seg = null;
                foreach (var m in UnityEngine.Object.FindObjectsOfType<SeaTruckMotor>())
                    if (m != null && m._piloting) { seg = m.GetComponent<SeaTruckSegment>(); break; }
                // test41: during a dock the controls stop counting as "piloting", which stopped the dock from being
                // shared (the other player's cab ended up in the wrong spot). Fall back to the cab the player is in,
                // but only a real cab (never a docking module holding a Prawn).
                if (seg != null) { var f0 = FirstOf(seg); if (f0 != null) { lastPilotedCab = f0; lastPilotedAt = Time.unscaledTime; } }
                if (seg == null)
                {
                    var ps = p.GetComponentInParent<SeaTruckSegment>();
                    // Only the cab this player was driving a moment ago (a passenger in the cab must never take it over).
                    string psId = ps != null ? WorldSync.IdOf(ps.transform) : null;
                    if (ps != null && ps.isMainCab && ps == lastPilotedCab && Time.unscaledTime - lastPilotedAt < 60f
                        && (psId == null || (!remote.ContainsKey(psId) && !occupiedByOthers.ContainsKey(psId))))
                    {
                        seg = ps;
                        if (Time.unscaledTime >= nextCabFallbackLog) { nextCabFallbackLog = Time.unscaledTime + 10f; Plugin.Log.LogInfo("Seatruck controls not active (docking?); sharing the cab you are in: " + ps.gameObject.name + "."); }
                    }
                }
                if (seg == null && Time.unscaledTime >= nextWhyLog) { nextWhyLog = Time.unscaledTime + 10f; Plugin.Log.LogWarning("Piloting a Seatruck but could not find which one; its movement is not shared."); }
                if (seg != null)
                {
                    var first = FirstOf(seg);
                    var fdk = first != null ? first.GetComponent<Dockable>() : null;
                    if (first != null && first.isMainCab && first.rb != null && first.rb.isKinematic && (fdk == null || !fdk.isDocked))
                    {
                        string fid = WorldSync.IdOf(first.transform);
                        if (fid == null || !remote.ContainsKey(fid))
                        {
                            Plugin.Log.LogInfo("Your Seatruck was still frozen from its dock; freeing it.");
                            UnfreezeTruck(first);
                        }
                    }
                    // test32: a module connected to the one in front has no physics body of its own - the game parents it
                    // to that module and keeps it lined up. Only the front piece of each connected group is streamed;
                    // the other PC lets its game carry the rest (streaming each module separately bent the chain).
                    foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
                        if (s != null && FirstOf(s) == first && !s.isFrontConnected) controlled.Add(s.gameObject);
                    ResyncChain(first);
                }
            }

            // test33: after this player's own dock, the dock takes the modules off and pulls them into the tube - on
            // this PC only. Keep sending their movement for 20 s (and a final position), so the other PC's copy goes into
            // the tube too instead of floating where the chain was when the dock started.
            if (trackedTail != null)
            {
                if (Time.unscaledTime < trackedTailUntil && !trackedTail.isFrontConnected) controlled.Add(trackedTail.gameObject);
                else { Plugin.Log.LogInfo("Dock tail tracking ended: " + TechName(trackedTail.gameObject) + " at " + trackedTail.transform.position.ToString("F1") + "."); trackedTail = null; }
            }

            if (trackedConstructors.Count > 0)
            {
                var done = new System.Collections.Generic.List<string>();
                foreach (var kv in trackedConstructors)
                {
                    var go = kv.Value < Time.unscaledTime ? null : WorldSync.Find(kv.Key);
                    if (go == null) done.Add(kv.Key); else controlled.Add(go);
                }
                foreach (var k in done) trackedConstructors.Remove(k);
            }
        }

        private static byte FlagsOf(GameObject go)
        {
            var c = go.GetComponent<Constructor>();
            return (byte)(c != null && c.deployed ? FlagDeployed : 0);
        }

        // ---------------------------------------------------------------- test29: Seatruck module diagnostics
        // A Fabricator module on the host's truck was not visible to the joiner. Before changing anything, measure:
        // (1) every time the other player moves a vehicle id that does not exist here, say so (once per id per 20 s);
        // (2) every 30 s while in a session, list every Seatruck chain in this world: each segment's type, id,
        //     position, whether it is active and how many of its renderers are on. Both PCs log the same list, so
        //     comparing the two logs shows which module is missing, misplaced or hidden on which PC.
        private static readonly System.Collections.Generic.Dictionary<string, float> missingLogged = new System.Collections.Generic.Dictionary<string, float>();
        private static float nextTruckCensus;

        private static void LogMissingMoved(string id, Vector3 pos)
        {
            float t;
            if (missingLogged.TryGetValue(id, out t) && Time.unscaledTime < t) return;
            missingLogged[id] = Time.unscaledTime + 20f;
            string dist = "?";
            try { if (Player.main != null) dist = Vector3.Distance(Player.main.transform.position, pos).ToString("0") + " m"; } catch { }
            Plugin.Log.LogWarning("The other player is moving [" + id + "] at " + pos.ToString("F1") + " (" + dist + " from you), but no object with that id exists in this world here.");
        }

        // test30: "a player in a Seatruck floats in the air". Measure it: every 4 s while someone stands inside a
        // Seatruck (not in the driver's seat), log where they are in the truck's own coordinates and how high above the
        // floor below them. The passenger's PC logs "You ..." and the other PC "Avatar ..." for the same segment id:
        // the difference between the two lines is how far off the avatar is drawn.
        internal static SeaTruckSegment SegmentAt(Vector3 p)
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
            {
                float depth;
                if (s != null && InsideSegment(s, p, out depth)) return s;
            }
            return null;
        }

        private static float FloorBelow(SeaTruckSegment seg, Vector3 p)
        {
            try
            {
                var hits = Physics.RaycastAll(p + Vector3.up * 1.2f, Vector3.down, 4f, -1, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                foreach (var h in hits)
                    if (h.collider != null && h.collider.transform.IsChildOf(seg.transform) && h.distance < best) best = h.distance;
                return best == float.MaxValue ? float.NaN : (best - 1.2f) * -1f;
            }
            catch { return float.NaN; }
        }

        internal static void PassengerDiag(string who, Vector3 body, Vector3 drawn, ref float next)
        {
            if (Time.unscaledTime < next || !(net != null && net.IsInSession)) return;
            next = Time.unscaledTime + 4f;
            try
            {
                var seg = SegmentAt(body) ?? SegmentAt(drawn);
                if (seg == null) return;
                string id = WorldSync.IdOf(seg.transform); if (id != null && id.Length > 8) id = id.Substring(0, 8);
                Vector3 lb = seg.transform.InverseTransformPoint(body), ld = seg.transform.InverseTransformPoint(drawn);
                RemoteState st; string sid = WorldSync.IdOf(seg.transform);
                bool glided = sid != null && remote.TryGetValue(sid, out st) && st.Go != null;
                Plugin.Log.LogInfo("Passenger check [" + who + "] in " + TechName(seg.gameObject) + " [" + id + "]: body local " + lb.ToString("F2")
                    + (who.StartsWith("You") ? "" : ", drawn local " + ld.ToString("F2") + " (" + Vector3.Distance(body, drawn).ToString("0.00") + " m from the sent position)")
                    + ", " + FloorBelow(seg, drawn).ToString("0.00") + " m above the floor, truck " + (glided ? "moved by the other player" : "local") + ".");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Passenger check failed: " + e.Message); }
        }

        private static float nextLocalPassengerDiag;
        private static void LocalPassengerDiag()
        {
            var p = Player.main;
            if (p == null || !LocalPlayerSync.InWorld || p.GetMode() != Player.Mode.Normal) return;
            if (!(p.currentInterior is SeaTruckSegment)) return;
            PassengerDiag("You", p.transform.position, p.transform.position, ref nextLocalPassengerDiag);
        }

        private static string SegInfo(SeaTruckSegment s)
        {
            int on = 0, all = 0;
            try { foreach (var r in s.GetComponentsInChildren<Renderer>(true)) { all++; if (r.enabled && r.gameObject.activeInHierarchy) on++; } } catch { }
            string id = WorldSync.IdOf(s.transform);
            string sid = string.IsNullOrEmpty(id) ? "no id" : (id.Length > 8 ? id.Substring(0, 8) : id);
            return TechName(s.gameObject) + " [" + sid + "] " + s.transform.position.ToString("F1")
                + (s.gameObject.activeInHierarchy ? "" : " INACTIVE") + " renderers " + on + "/" + all
                + (s.isDocked ? " docked" : "");
        }

        private static void TruckCensus()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld || Time.unscaledTime < nextTruckCensus) return;
            nextTruckCensus = Time.unscaledTime + 30f;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>();
                var seen = new System.Collections.Generic.HashSet<SeaTruckSegment>();
                var sb = new System.Text.StringBuilder();
                int chains = 0;
                foreach (var s in all)
                {
                    if (s == null || seen.Contains(s) || FirstOf(s) != s) continue;
                    var chain = new System.Collections.Generic.List<SeaTruckSegment>();
                    try { s.GetTruckChain(chain); } catch { }
                    if (chain.Count == 0) chain.Add(s);
                    chains++;
                    sb.Append("\n   chain ").Append(chains).Append(": ");
                    for (int i = 0; i < chain.Count; i++) { if (chain[i] == null) continue; seen.Add(chain[i]); if (i > 0) sb.Append("  >  "); sb.Append(SegInfo(chain[i])); }
                }
                foreach (var s in all)
                    if (s != null && !seen.Contains(s)) { sb.Append("\n   not in a chain: ").Append(SegInfo(s)); }
                // test39: what the Seatruck docking modules hold (also records it, so its undock is sent the right way).
                if (fTruckBayDocked != null)
                    foreach (var tb in UnityEngine.Object.FindObjectsOfType<SeaTruckDockingBay>())
                    {
                        var dd = tb != null ? fTruckBayDocked.GetValue(tb) as Dockable : null;
                        if (dd == null || tb.truckSegment == null) continue;
                        string did = WorldSync.IdOf(dd.transform), mid = WorldSync.IdOf(tb.truckSegment.transform);
                        if (did != null && mid != null) truckDockedIn[did] = mid;
                        sb.Append("\n   docked in ").Append(TechName(tb.truckSegment.gameObject)).Append(": ").Append(TechName(dd.gameObject)).Append(" [").Append(did != null && did.Length > 8 ? did.Substring(0, 8) : did).Append("]");
                    }
                Plugin.Log.LogInfo("Seatrucks here: " + all.Length + " segments in " + chains + " chains." + sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck census failed: " + e.Message); }
        }

        public static void Tick()
        {
            gameDetachDepth = 0;   // test31: game detaches are synchronous; never let a missed finalizer leave it raised
            TickRemote();
            TruckCensus();
            LocalPassengerDiag();
            TickPendingDeploys();
            if (net == null || !net.IsInSession || !WorldSync.CanSend) { sentLast.Clear(); occupiedSent.Clear(); if (net == null || !net.IsInSession) occupiedByOthers.Clear(); return; }
            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + SendInterval;

            CollectControlled();
            LocalSeatDiag();
            var now = new System.Collections.Generic.Dictionary<string, GameObject>();
            held.Clear();
            foreach (var go in controlled)
            {
                string id = WorldSync.IdOf(go.transform);
                if (!string.IsNullOrEmpty(id) && remote.ContainsKey(id))
                {
                    // We are at its controls, so the other player is not: drop their old hold on it.
                    Plugin.Log.LogInfo("You are driving " + TechName(go) + " that was still held for the other player; taking it over.");
                    var old = remote[id]; remote.Remove(id); pendingTruckDock.Remove(id);
                    var orb = BodyOf(go); if (orb != null) orb.isKinematic = false;
                    var osg = go.GetComponent<SeaTruckSegment>(); if (osg != null) { try { osg.SetKinematicsLocked(false); osg.UpdateKinematicState(); } catch { } }
                }
                if (string.IsNullOrEmpty(id)) continue;
                localTouched[id] = Time.unscaledTime;
                var dk = go.GetComponent<Dockable>();
                // The bay moves it; dock state is sent on its own. Except a Seatruck: its dock's animation moves it while the
                // driver is still in the seat, and those positions are what make the docking look smooth on the other side.
                bool truck = go.GetComponent<SeaTruckSegment>() != null;
                if (dk != null && !truck && (dk.isDocked || (dk.isInTransition && CurrentBay(dk) != null))) { held[id] = true; continue; }
                if (dk != null && dk.isInTransition && !dk.isDocked && CurrentBay(dk) == null) ClearTransition(dk);   // stale flag from an earlier dock
                now[id] = go;
            }

            var occ = new System.Collections.Generic.HashSet<string>(now.Keys);
            foreach (var k in held.Keys) occ.Add(k);
            UpdateOccupancy(occ);

            if (now.Count > 0)
            {
                var ids = new System.Collections.Generic.List<string>(now.Keys);
                var gos = new System.Collections.Generic.List<GameObject>(now.Values);
                net.SendVehicleMove(ids, gos, false);
                foreach (var id in ids) if (!sentLast.ContainsKey(id)) Plugin.Log.LogInfo("Driving " + TechName(now[id]) + " [" + id + "]: sharing its movement.");
            }

            // Whatever we stopped moving: send where it ended up, reliably, so the copy is released there.
            var released = new System.Collections.Generic.List<string>(); var releasedGos = new System.Collections.Generic.List<GameObject>();
            foreach (var kv in sentLast)
                if (!now.ContainsKey(kv.Key) && !held.ContainsKey(kv.Key) && kv.Value != null && (!IsDocked(kv.Value) || kv.Value.GetComponent<SeaTruckSegment>() != null)) { released.Add(kv.Key); releasedGos.Add(kv.Value); }
            if (released.Count > 0)
            {
                net.SendVehicleMove(released, releasedGos, true);
                for (int i = 0; i < released.Count; i++) Plugin.Log.LogInfo("Stopped driving " + TechName(releasedGos[i]) + " [" + released[i] + "] at " + releasedGos[i].transform.position);
            }
            sentLast.Clear();
            foreach (var kv in now) sentLast[kv.Key] = kv.Value;
        }

        private static string TechName(GameObject go)
        {
            try { return CraftData.GetTechType(go).ToString(); } catch { return go != null ? go.name : "?"; }
        }

        // ------------------------------------------------------------------ receiving

        private class RemoteState
        {
            public GameObject Go;
            public Vector3 Pos; public Quaternion Rot;
            public float LastHeard;
            public bool WasKinematic;
            public bool Snapped;
        }

        private static readonly System.Collections.Generic.Dictionary<string, RemoteState> remote = new System.Collections.Generic.Dictionary<string, RemoteState>();

        private static Rigidbody BodyOf(GameObject go)
        {
            var v = go.GetComponent<Vehicle>(); if (v != null && v.useRigidbody != null) return v.useRigidbody;
            var s = go.GetComponent<SeaTruckSegment>(); if (s != null && s.rb != null) return s.rb;
            var h = go.GetComponent<Hoverbike>(); if (h != null && h.rb != null) return h.rb;
            return go.GetComponent<Rigidbody>();
        }

        public static void OnVehicleMove(string id, Vector3 pos, Quaternion rot, byte flags, bool final)
        {
            if (!LocalPlayerSync.InWorld || sentLast.ContainsKey(id)) return;   // we are driving it ourselves
            remoteTouched[id] = Time.unscaledTime;
            float dockedAt;
            if (remoteDockedAt.TryGetValue(id, out dockedAt))
            {
                if (Time.unscaledTime - dockedAt < 3f) return;                 // late position from before it docked
                remoteDockedAt.Remove(id);
            }
            RemoteState st;
            if (!remote.TryGetValue(id, out st) || st.Go == null)
            {
                if (final)
                {
                    var g = WorldSync.Find(id);
                    if (g != null)
                    {
                        var gs = g.GetComponent<SeaTruckSegment>();
                        if (gs != null && gs.isFrontConnected) return;         // test32: carried by the module in front
                        g.transform.SetPositionAndRotation(pos, rot); ApplyFlags(g, flags);
                    }
                    return;
                }
                var go = WorldSync.Find(id);
                if (go == null) { LogMissingMoved(id, pos); return; }          // not loaded here (test29: now logged)
                // test32: the driver streams only modules that are NOT connected in front on his PC. If ours still is,
                // the two PCs disagree: his PC is right (he is moving it), so take it off here too.
                var mseg = go.GetComponent<SeaTruckSegment>();
                if (mseg != null && mseg.isFrontConnected)
                {
                    float ca;
                    if (connectApplied.TryGetValue(id, out ca) && Time.unscaledTime - ca < 2f) return;   // late move from before it connected
                    applyingConnect = true;
                    try { mseg.frontConnection.Disconnect(); }
                    catch (Exception e) { Plugin.Log.LogWarning("Seatruck connection repair failed: " + e.Message); }
                    finally { applyingConnect = false; }
                    Plugin.Log.LogInfo("Seatruck connection repair: the other player moves " + TechName(go) + " on its own, so it is not connected on his PC; detached it here too.");
                    MarkIfDockTail(id, mseg, null);
                }
                if (IsDocked(go) && truckDockedIn.ContainsKey(id))            // test39: out of a Seatruck docking module
                {
                    UndockFromAnyBay(go, go.GetComponent<Dockable>());
                    truckDockedIn.Remove(id);
                    Plugin.Log.LogInfo("The other player drives " + TechName(go) + " out of the Seatruck docking module; undocked here too.");
                }
                if (IsDocked(go) && !pendingTruckDock.ContainsKey(id))         // its driver has taken it out of the bay
                {
                    var dk = go.GetComponent<Dockable>();
                    if (dk == null || !UndockSeatruckFromExpansion(go, dk, pos, rot)) ForceUndock(go, pos, rot);
                }
                st = new RemoteState { Go = go };
                var rb = BodyOf(go);
                st.WasKinematic = rb != null && rb.isKinematic;
                if (rb != null) { rb.isKinematic = true; }
                var seg = go.GetComponent<SeaTruckSegment>();
                if (seg != null) { try { seg.SetKinematicsLocked(true); } catch { } }
                remote[id] = st;
                Plugin.Log.LogInfo("The other player is moving " + TechName(go) + " [" + id + "].");
            }
            st.Pos = pos; st.Rot = rot; st.LastHeard = Time.unscaledTime;
            if (!st.Snapped || (st.Go.transform.position - pos).sqrMagnitude > 30f * 30f) { var cs = BeginCarry(st.Go); st.Go.transform.SetPositionAndRotation(pos, rot); st.Snapped = true; EndCarry(cs, "snapped to the driver's position"); }
            if (final) Release(id, st, flags);
        }

        private static void ApplyFlags(GameObject go, byte flags)
        {
            if ((flags & FlagDeployed) == 0) return;
            var c = go.GetComponent<Constructor>();
            if (c == null) return;
            Plugin.Instance.StartCoroutine(DeployWhenReady(c));
        }

        /// <summary>
        /// The game's Deploy only flips the unfold animation when the bay's animator is active. After a pick-up and
        /// redeploy the copy here is a freshly spawned bay whose animator is not running yet, so it ended up deployed
        /// (floating, working) but folded (0.14.10). Wait until it is active, then deploy it - again if needed.
        /// </summary>
        private static IEnumerator DeployWhenReady(Constructor c)
        {
            float until = Time.unscaledTime + 5f;
            while (c != null && Time.unscaledTime < until)
            {
                var anim = c.GetComponentInChildren<Animator>();
                if (c.gameObject.activeInHierarchy && (anim == null || (anim.isActiveAndEnabled && anim.isInitialized))) break;
                yield return null;
            }
            yield return null;
            if (c == null) yield break;
            // It can already count as deployed (from before it was picked up): then deploying again changes nothing and
            // the unfold never plays (0.14.11-test1). Fold it first, give the animator a few frames, then deploy.
            applyingDeploy = true;
            try { mDeploy.Invoke(c, new object[] { false }); } catch { }
            finally { applyingDeploy = false; }
            for (int i = 0; i < 3; i++) yield return null;
            if (c == null) yield break;
            applyingDeploy = true;
            try { mDeploy.Invoke(c, new object[] { true }); Plugin.Log.LogInfo("Vehicle bay from the other player deployed (unfolded)."); }
            catch (Exception e) { Plugin.Log.LogWarning("Vehicle bay deploy failed: " + e.Message); }
            finally { applyingDeploy = false; }
        }

        // test31: where the driver last left each vehicle / module (its final position), for the Seatruck dock.
        private struct FinalPose { public Vector3 Pos; public Quaternion Rot; public float At; }
        private static readonly System.Collections.Generic.Dictionary<string, FinalPose> lastFinal = new System.Collections.Generic.Dictionary<string, FinalPose>();

        private static void Release(string id, RemoteState st, byte flags)
        {
            lastFinal[id] = new FinalPose { Pos = st.Pos, Rot = st.Rot, At = Time.unscaledTime };
            remote.Remove(id);
            if (st.Go == null) { pendingTruckDock.Remove(id); return; }
            if (pendingTruckDock.ContainsKey(id)) { var cp = BeginCarry(st.Go); st.Go.transform.SetPositionAndRotation(st.Pos, st.Rot); EndCarry(cp, null); CompletePendingDock(id); return; }
            if (IsDocked(st.Go)) { EnsureCollides(st.Go, "left docked"); Plugin.Log.LogInfo("The other player left " + TechName(st.Go) + " [" + id + "] docked."); return; }
            if (HoldDockTail(id, st.Go, st.Pos, st.Rot)) return;
            var cr = BeginCarry(st.Go);
            st.Go.transform.SetPositionAndRotation(st.Pos, st.Rot);
            EndCarry(cr, null);
            var rb = BodyOf(st.Go);
            // A free vehicle is never kinematic. What it was when we started gliding it can be the docked state (it was
            // taken out of a dock just before), and restoring that left a Seatruck frozen for good: nobody could drive it
            // or get out of it again (0.14.9 could do this). Only non-vehicles (the Mobile Vehicle Bay) keep what they had.
            bool isVehicle = st.Go.GetComponent<Vehicle>() != null || st.Go.GetComponent<SeaTruckSegment>() != null || st.Go.GetComponent<Hoverbike>() != null;
            if (rb != null) { rb.isKinematic = isVehicle ? false : st.WasKinematic; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            var seg = st.Go.GetComponent<SeaTruckSegment>();
            if (seg != null) { try { seg.SetKinematicsLocked(false); seg.UpdateKinematicState(); } catch { } }
            ApplyFlags(st.Go, flags);
            EnsureCollides(st.Go, "when the other player let go of it");
            Plugin.Log.LogInfo("The other player left " + TechName(st.Go) + " [" + id + "] at " + st.Pos + ".");
        }

        // ---------------------------------------------------------------- test37: riding along
        // When the other player drives, this PC moves its copy of the truck - but the local player standing inside
        // was never moved with it: left behind in the dock when the other player undocked (test36 host log: stuck at
        // the docked cab's spot, "interior Base"), and dragged / bumped around when riding along at sea. Every move of
        // a truck the other player controls now moves the local player inside it by the same amount.
        private struct Carry { public bool On; public Vector3 Local; public Transform T; }
        private static float nextCarryLog;

        private static bool LocalPlayerInChain(SeaTruckSegment head)
        {
            var p = Player.main;
            if (p == null || head == null || p.GetMode() != Player.Mode.Normal || p.cinematicModeActive) return false;
            var chain = new System.Collections.Generic.List<SeaTruckSegment>();
            try { head.GetTruckChain(chain); } catch { }
            if (chain.Count == 0) chain.Add(head);
            // By position only: a stale "current interior" must never drag a player along from outside the truck.
            Vector3 pos = p.transform.position;
            foreach (var sg in chain)
            {
                if (sg == null) continue;
                float d;
                if (InsideSegment(sg, pos, out d)) return true;
            }
            return false;
        }

        private static Carry BeginCarry(GameObject go)
        {
            var c = new Carry();
            try
            {
                var seg = go != null ? go.GetComponent<SeaTruckSegment>() : null;
                if (seg == null || Player.main == null) return c;
                if (JoinerSpawn.Pending || !LocalPlayerSync.InWorld) return c;   // test38: still arriving: never carried
                if (!LocalPlayerInChain(FirstOf(seg))) return c;
                c.On = true; c.T = seg.transform; c.Local = seg.transform.InverseTransformPoint(Player.main.transform.position);
            }
            catch { }
            return c;
        }

        private static void EndCarry(Carry c, string why)
        {
            if (!c.On || c.T == null) return;
            try
            {
                var p = Player.main;
                if (p == null) return;
                Vector3 np = c.T.TransformPoint(c.Local);
                float moved = Vector3.Distance(np, p.transform.position);
                if (moved < 0.0005f) return;
                // test38: a copy that jumps this far was in the wrong place, not moving with us (test37 dragged a player
                // who was still loading in 24 m along with a module copy being put right).
                if (moved > 8f) { Plugin.Log.LogInfo("Riding along: not carried - " + TechName(c.T.gameObject) + " jumped " + moved.ToString("0.0") + " m (a correction, not a ride)."); return; }
                p.transform.position = np;
                if (why != null || (moved > 2f && Time.unscaledTime >= nextCarryLog))
                {
                    nextCarryLog = Time.unscaledTime + 5f;
                    Plugin.Log.LogInfo("Riding along: moved you " + moved.ToString("0.00") + " m with " + TechName(c.T.gameObject) + (why != null ? " (" + why + ")" : "") + ".");
                }
                // Out of a dock (or anywhere) the game may still have us "in the base": put us inside the truck.
                if (!(p.currentInterior is SeaTruckSegment))
                {
                    var seg = SegmentAt(np) ?? c.T.GetComponent<SeaTruckSegment>();
                    if (seg != null)
                    {
                        Traverse.Create(seg).Field("player").SetValue(p);
                        seg.PropagatePlayer();
                        p.EnterInterior(seg);
                        seg.UpdateKinematicState();
                        Plugin.Log.LogInfo("Riding along: you are now inside " + TechName(seg.gameObject) + " for the game (was " + (p.currentSub != null ? p.currentSub.name : "none") + ").");
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Riding along failed: " + e.Message); }
        }

        private static void TickRemote()
        {
            if (remote.Count == 0) return;
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            System.Collections.Generic.List<string> stale = null, attached = null;
            foreach (var kv in remote)
            {
                var st = kv.Value;
                PendingDock pdk;
                if (pendingTruckDock.TryGetValue(kv.Key, out pdk) && Time.unscaledTime - pdk.Since > 30f) { (stale = stale ?? new System.Collections.Generic.List<string>()).Add(kv.Key); continue; }
                if (st.Go == null || Time.unscaledTime - st.LastHeard > 3f) { (stale = stale ?? new System.Collections.Generic.List<string>()).Add(kv.Key); continue; }
                var cs = st.Go.GetComponent<SeaTruckSegment>();
                if (cs != null && cs.isFrontConnected) { (attached = attached ?? new System.Collections.Generic.List<string>()).Add(kv.Key); continue; }
                var t = st.Go.transform;
                var carry = BeginCarry(st.Go);
                t.SetPositionAndRotation(Vector3.Lerp(t.position, st.Pos, k), Quaternion.Slerp(t.rotation, st.Rot, k));
                EndCarry(carry, null);
            }
            if (stale != null) foreach (var id in stale) { var st = remote[id]; Release(id, st, 0); }
            if (attached != null) foreach (var id in attached) { var st = remote[id]; remote.Remove(id); Plugin.Log.LogInfo("Stopped moving " + TechName(st.Go) + " [" + id + "] here: it is connected now and carried by the module in front."); }
        }

        /// <summary>Leaving the session: give every held copy its physics back.</summary>
        public static void Reset()
        {
            foreach (var id in new System.Collections.Generic.List<string>(remote.Keys)) Release(id, remote[id], 0);
            sentLast.Clear(); trackedConstructors.Clear(); remoteTouched.Clear(); localTouched.Clear(); remoteDockedAt.Clear(); pendingTruckDock.Clear(); pendingDeploys.Clear(); lastSentDocked.Clear(); lastFrontSent.Clear(); connectApplied.Clear(); truckDockedIn.Clear(); heldDockTails.Clear(); trackedTail = null; heldCabPose.Clear(); heldCabLogged.Clear();
        }
    
        // ================================================================== moonpool docking (since 0.14.5)
        //
        // A docked vehicle is held in place by its docking bay every frame, so moving the copy did nothing while the
        // other side still had it docked - and docking itself was never shared. Every change of a Dockable's docked
        // state (Dockable.SetDocked) is now sent with the bay's position; the other side docks its copy into the
        // nearest bay there, or undocks it. A copy that is still docked here when its driver starts moving it is
        // undocked first (covers vehicles docked in the save before anyone joined).

        private static bool applyingDock;
        private static readonly System.Collections.Generic.Dictionary<string, float> remoteDockedAt = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Reflection.FieldInfo fDockBay = AccessTools.Field(typeof(Dockable), "bay");
        private static readonly System.Reflection.MethodInfo mSetDockedObject = AccessTools.PropertySetter(typeof(VehicleDockingBay), "dockedObject");

        // Vehicles the other player drove or docked recently: this machine's own docking bays must leave them alone.
        // A bay that docks a vehicle by itself also runs the "player docks" steps on the LOCAL player (sets the current
        // base, forces normal mode, starts the dock cutscene), which is what broke that player's input in 0.14.5.
        private static readonly System.Collections.Generic.Dictionary<string, float> remoteTouched = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> localTouched = new System.Collections.Generic.Dictionary<string, float>();
        private const float RemoteOwnSeconds = 10f;

        private static bool RecentlyRemote(string id)
        {
            float t; return id != null && remoteTouched.TryGetValue(id, out t) && Time.unscaledTime - t < RemoteOwnSeconds;
        }
        private static bool RecentlyLocal(string id)
        {
            float t; return id != null && (sentLast.ContainsKey(id) || (localTouched.TryGetValue(id, out t) && Time.unscaledTime - t < 3f));
        }
        private static bool IsRemoteOwned(Component c)
        {
            if (c == null) return false;
            string id = WorldSync.IdOf(c.transform);
            return id != null && (remote.ContainsKey(id) || (RecentlyRemote(id) && !RecentlyLocal(id)));
        }

        // Prefixes: a bay here does not start docking (nor dock) a vehicle the other player is handling.
        private static bool BayTriggerPrefix(Collider __0)
        {
            if (applyingDock || __0 == null) return true;
            var d = __0.GetComponentInParent<Dockable>();
            if (d != null && IsRemoteOwned(d)) { if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Bay here ignores " + TechName(d.gameObject) + " (the other player's)."); return false; }
            return true;
        }
        private static bool BayAreaPrefix(GameObject __0)
        {
            if (applyingDock || __0 == null) return true;
            var d = __0.GetComponentInParent<Dockable>();
            return d == null || !IsRemoteOwned(d);
        }
        private static bool BayDockPrefix(Dockable __0)
        {
            if (applyingDock || __0 == null) return true;
            if (IsRemoteOwned(__0)) { Plugin.Log.LogInfo("Stopped this base's bay from docking " + TechName(__0.gameObject) + " by itself (the other player's)."); return false; }
            return true;
        }

        internal static void InstallDocking(Harmony harmony)
        {
            foreach (var pair in new[] { new[] { "OnTriggerEnter", "BayTriggerPrefix" }, new[] { "LaunchbayAreaEnter", "BayAreaPrefix" }, new[] { "Dock", "BayDockPrefix" }, new[] { "OnUndockingComplete", "BayUndockCompletePrefix" } })
            {
                var bm = AccessTools.Method(typeof(VehicleDockingBay), pair[0]);
                if (bm != null) harmony.Patch(bm, prefix: new HarmonyMethod(typeof(VehicleSync), pair[1]));
                else Plugin.Log.LogWarning("VehicleSync: VehicleDockingBay." + pair[0] + " not found.");
            }
            var m = AccessTools.Method(typeof(Dockable), "SetDocked");
            if (m == null) { Plugin.Log.LogWarning("VehicleSync: Dockable.SetDocked not found; docking will not sync."); return; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(VehicleSync), "SetDockedPostfix"));
        }

        private static readonly System.Collections.Generic.Dictionary<string, bool> lastSentDocked = new System.Collections.Generic.Dictionary<string, bool>();

        private static void SetDockedPostfix(Dockable __instance, object __0)
        {
            if (__instance == null) return;
            string id = WorldSync.IdOf(__instance.transform);
            if (string.IsNullOrEmpty(id)) return;
            var bayComp = __0 as Component;
            bool docked = bayComp != null;
            // test32: remember the state even when it is not ours to send. Before, an undock applied for the other player
            // left "docked" as the last state here, so this player's NEXT dock looked like no change and was never sent
            // (test31 log: second dock on the host, nothing sent, the other PC pulled the whole chain round).
            if (!docked && heldCabPose.Remove(id)) Plugin.Log.LogInfo("Dock cab: released the hold on " + TechName(__instance.gameObject) + " (undocked).");
            // test39: a Prawn suit (or other vehicle) docking in a Seatruck docking module. That bay is not a base docking
            // bay, so the other PC docked it into the nearest base bay instead (or nowhere) and never showed it on the truck.
            var truckBay = __0 as SeaTruckDockingBay;
            string wasTruckModule;
            bool wasInTruck = truckDockedIn.TryGetValue(id, out wasTruckModule);
            if (docked && truckBay != null)
            {
                string mid = truckBay.truckSegment != null ? WorldSync.IdOf(truckBay.truckSegment.transform) : null;
                if (mid != null) truckDockedIn[id] = mid;
            }
            if (truckBay != null || (!docked && wasInTruck))
            {
                if (!docked) truckDockedIn.Remove(id);
                if (applyingDock || !WorldSync.CanSend || remote.ContainsKey(id) || (RecentlyRemote(id) && !RecentlyLocal(id))) { lastSentDocked[id] = docked; return; }
                string modId = docked ? truckDockedIn[id] : wasTruckModule;
                bool wasT; if (lastSentDocked.TryGetValue(id, out wasT) && wasT == docked) return;
                lastSentDocked[id] = docked;
                net.SendVehicleTruckDock(id, modId, docked);
                Plugin.Log.LogInfo((docked ? "Docked " : "Undocked ") + TechName(__instance.gameObject) + " [" + id + "] " + (docked ? "in" : "from") + " the Seatruck docking module [" + modId + "]; sent.");
                return;
            }
            if (applyingDock || !WorldSync.CanSend || remote.ContainsKey(id) || (RecentlyRemote(id) && !RecentlyLocal(id))) { lastSentDocked[id] = docked; return; }
            // Only a real change is sent. A Seatruck already sitting in its dock has SetDocked re-run on every load
            // step, which sent (and applied on the other side) the same dock a dozen times at startup (0.14.11-test6).
            bool was; if (lastSentDocked.TryGetValue(id, out was) && was == docked) return;
            lastSentDocked[id] = docked;
            Vector3 bayPos = docked ? bayComp.transform.position : __instance.transform.position;
            net.SendVehicleDock(id, docked, bayPos, __instance.transform.position, __instance.transform.rotation);
            Plugin.Log.LogInfo((docked ? "Docked " : "Undocked ") + TechName(__instance.gameObject) + " [" + id + "]" + (docked ? " in the bay at " + bayPos : "") + "; sent.");
        }

        private static readonly System.Reflection.MethodInfo mSetInTransition = AccessTools.PropertySetter(typeof(Dockable), "isInTransition");
        /// <summary>
        /// The game clears "docking / undocking in progress" in Dockable.OnUndockingComplete(player), a step skipped for
        /// the other player's copy (it moves the local player). Left set, it made this machine treat the vehicle as still
        /// docking for good, so it never shared its movement again when someone here drove it (0.14.9).
        /// </summary>
        private static void ClearTransition(Dockable d)
        {
            try { if (d != null && d.isInTransition && mSetInTransition != null) mSetInTransition.Invoke(d, new object[] { false }); } catch { }
        }

        /// <summary>Every segment of this Seatruck back to normal physics (not while the other player is gliding it).</summary>
        private static void UnfreezeTruck(SeaTruckSegment first)
        {
            if (first == null) return;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
            {
                if (s == null || FirstOf(s) != first) continue;
                string sid = WorldSync.IdOf(s.transform);
                if (sid != null && remote.ContainsKey(sid)) continue;
                var dk = s.GetComponent<Dockable>();
                if (dk != null && dk.isDocked) continue;
                ClearTransition(dk);
                try { s.SetKinematicsLocked(false); s.UpdateKinematicState(); } catch { }
                if (s.rb != null && s.rb.isKinematic) s.rb.isKinematic = false;
            }
        }

        private static VehicleDockingBay CurrentBay(Dockable d)
        {
            try { return fDockBay != null ? fDockBay.GetValue(d) as VehicleDockingBay : null; } catch { return null; }
        }

        private static bool IsDocked(GameObject go)
        {
            var d = go != null ? go.GetComponent<Dockable>() : null;
            return d != null && d.isDocked;
        }

        /// <summary>Take this copy out of whatever bay holds it, without anyone getting in or out.</summary>
        private static void ForceUndock(GameObject go, Vector3 pos, Quaternion rot)
        {
            var d = go.GetComponent<Dockable>();
            if (d == null || !d.isDocked) return;
            if (TruckBayHolding(d) != null) { UndockFromAnyBay(go, d); go.transform.SetPositionAndRotation(pos, rot); return; }   // test39
            applyingDock = true;
            try
            {
                var bay = CurrentBay(d);
                if (bay != null)
                {
                    bay.SetVehicleUndocked();
                    if (bay.GetDockedObject() == d && mSetDockedObject != null) mSetDockedObject.Invoke(bay, new object[] { null });
                }
                if (d.isDocked) d.SetDocked(null, default(Vehicle.DockType));
                ClearTransition(d);
                go.transform.SetPositionAndRotation(pos, rot);
                EnsureCollides(go, "after undocking");
                Plugin.Log.LogInfo("Undocked " + TechName(go) + " here too.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Undocking " + TechName(go) + " failed: " + e.Message); }
            finally { applyingDock = false; }
        }

        private static readonly System.Reflection.FieldInfo fExpLoading = AccessTools.Field(typeof(MoonpoolExpansionManager), "isLoading");
        private static readonly System.Reflection.FieldInfo fExpHead = AccessTools.Field(typeof(MoonpoolExpansionManager), "dockedHead");
        private static readonly System.Reflection.FieldInfo fExpTail = AccessTools.Field(typeof(MoonpoolExpansionManager), "tail");
        private static readonly System.Reflection.FieldInfo fExpFully = AccessTools.Field(typeof(MoonpoolExpansionManager), "isFullyDocked");
        private static readonly System.Reflection.MethodInfo mExpUndockTimelineDone = AccessTools.Method(typeof(MoonpoolExpansionManager), "OnUndockingTimelineCompleted");
        private static readonly System.Reflection.MethodInfo mExpFinishUndocking = AccessTools.Method(typeof(MoonpoolExpansionManager), "FinishUndocking");
        private static bool remoteSeatruckUndock;   // running the Seatruck dock's steps for the OTHER player's copy
        private static bool remoteSeatruckDock;

        // Those steps are written for the player who is in the Seatruck: they seat the local player at the controls
        // (SetPiloting), freeze / unfreeze their stats and enter piloting mode. On this machine the local player is NOT in
        // it - 0.14.8 teleported them into the pilot seat and left them stuck there. Skip every player part meanwhile.
        private static bool InRemoteSeatruckStep { get { return remoteSeatruckUndock || remoteSeatruckDock; } }
        private static bool SkipIfRemoteSeatruckStep() { return !InRemoteSeatruckStep; }

        /// <summary>
        /// Seatruck dock (moonpool expansion): the game prepares the dock when the Seatruck enters the bay's trigger
        /// (PrepDocking: which segment is the head, which is the tail) and then plays a docking timeline. The other
        /// player's copy never enters that trigger here, so the timeline ran with nothing prepared and the copy froze
        /// before its animation. Instead do what loading a save does: dock it, then PrepDocking with isLoading set, which
        /// jumps the timeline to its end and completes the dock at once - without the timeline's player steps.
        /// </summary>
        private static void DockSeatruckInstantly(VehicleDockingBay bay, MoonpoolExpansionManager exp, Dockable d)
        {
            // test30: the modules behind the cab were still connected to it here when the cab was locked in, so they
            // flipped round with it and reconnected behind it (on the driver's PC the dock takes them off first and they
            // stay in the tube). Remember where the driver left them and put them back there after the dock is done.
            var head = d != null ? d.truckSegment : null;
            var tail = new System.Collections.Generic.List<SeaTruckSegment>();
            var tailPos = new System.Collections.Generic.List<Vector3>();
            var tailRot = new System.Collections.Generic.List<Quaternion>();
            try
            {
                if (head != null)
                {
                    var chain = new System.Collections.Generic.List<SeaTruckSegment>();
                    head.GetTruckChain(chain);
                    // test31: the modules' transforms are NOT where the driver left them any more - while still connected
                    // they swung round with the cab as it was pulled into the dock (test30 log: modules across the dock,
                    // z 42-55, instead of in the tube). Use the final position the driver sent for each module.
                    int fromDriver = 0;
                    foreach (var sg in chain)
                    {
                        if (sg == null || sg == head) continue;
                        string sid = WorldSync.IdOf(sg.transform);
                        FinalPose fp;
                        bool have = sid != null && lastFinal.TryGetValue(sid, out fp) && Time.unscaledTime - fp.At < 30f;
                        tail.Add(sg);
                        if (have) { fp = lastFinal[sid]; tailPos.Add(fp.Pos); tailRot.Add(fp.Rot); fromDriver++; }
                        else { tailPos.Add(sg.transform.position); tailRot.Add(sg.transform.rotation); }
                    }
                    Plugin.Log.LogInfo("Seatruck dock: " + tail.Count + " modules behind the cab, " + fromDriver + " with the driver's final position.");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck dock: reading the modules failed: " + e.Message); }
            try
            {
                bay.SetVehicleDocked(d);
                bool was = fExpLoading != null && (bool)fExpLoading.GetValue(exp);
                if (fExpLoading != null) fExpLoading.SetValue(exp, true);
                remoteSeatruckDock = true;
                try { exp.PrepDocking(d); }
                finally { remoteSeatruckDock = false; if (fExpLoading != null) fExpLoading.SetValue(exp, was); }
                Plugin.Log.LogInfo("Seatruck docked here too (Seatruck dock, completed instantly).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck dock here failed: " + e.Message); }
            // test32: no more putting the modules back by force - the dock pulls the first one onto its own connector
            // (test31 log: "moved 11.9 m", then 3.6 m, then 5.9 m, fighting it). Modules are now detached on both PCs
            // the moment the driver's dock takes them (connection events), so they no longer swing round with the cab.
            if (tail.Count > 0) Plugin.Log.LogInfo("Seatruck dock: " + tail.Count + " modules were still connected to the cab here at lock-in; the dock detaches them.");
        }

        /// <summary>Detach the tail from the docked cab if it is still on, and put each module where the driver left it.</summary>
        private static void PlaceDockedTail(SeaTruckSegment head, System.Collections.Generic.List<SeaTruckSegment> tail,
            System.Collections.Generic.List<Vector3> pos, System.Collections.Generic.List<Quaternion> rot, string when)
        {
            try
            {
                bool wasOn = head != null && head.rearConnection != null && head.rearConnection.GetConnection() != null;
                if (wasOn) { gameDetachDepth++; try { head.rearConnection.Disconnect(); } finally { gameDetachDepth--; } }
                float worst = 0f;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < tail.Count; i++)
                {
                    var sg = tail[i]; if (sg == null) continue;
                    float off = Vector3.Distance(sg.transform.position, pos[i]);
                    worst = Mathf.Max(worst, off);
                    sg.transform.SetPositionAndRotation(pos[i], rot[i]);
                    if (sg.rb != null && !sg.rb.isKinematic) { sg.rb.velocity = Vector3.zero; sg.rb.angularVelocity = Vector3.zero; }
                    sb.Append(i > 0 ? ", " : "").Append(TechName(sg.gameObject)).Append(" moved ").Append(off.ToString("0.0")).Append(" m");
                }
                Plugin.Log.LogInfo("Seatruck dock, modules " + when + ": " + (wasOn ? "were still connected to the cab, detached; " : "not connected to the cab; ")
                    + "put back where the driver left them (" + sb + "; most " + worst.ToString("0.0") + " m).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck dock: placing the modules failed: " + e.Message); }
        }

        private static IEnumerator RecheckDockedTail(SeaTruckSegment head, System.Collections.Generic.List<SeaTruckSegment> tail,
            System.Collections.Generic.List<Vector3> pos, System.Collections.Generic.List<Quaternion> rot)
        {
            float[] at = { 0.5f, 2f, 5f };
            float t0 = Time.unscaledTime;
            foreach (float a in at)
            {
                while (Time.unscaledTime - t0 < a) yield return null;
                if (head == null || !head.isDocked) yield break;
                bool reconnected = head.rearConnection != null && head.rearConnection.GetConnection() != null;
                float worst = 0f;
                for (int i = 0; i < tail.Count; i++) if (tail[i] != null) worst = Mathf.Max(worst, Vector3.Distance(tail[i].transform.position, pos[i]));
                if (reconnected || worst > 3f) PlaceDockedTail(head, tail, pos, rot, a.ToString("0.0") + " s after docking (" + (reconnected ? "had reconnected to the cab" : "had moved " + worst.ToString("0.0") + " m") + ")");
                else Plugin.Log.LogInfo("Seatruck dock, modules " + a.ToString("0.0") + " s after docking: still apart from the cab, within " + worst.ToString("0.0") + " m of where the driver left them.");
            }
        }

        /// <summary>
        /// Undocking a Seatruck from the Seatruck dock: run the dock's own undock completion (reconnects the tail module,
        /// restores the colliders and doors), but its last step - the bay's OnUndockingComplete(player) - changes the
        /// LOCAL player's current base, so that step is replaced by a plain undock of the copy (see the prefix below).
        /// </summary>
        private static bool UndockSeatruckFromExpansion(GameObject go, Dockable d, Vector3 pos, Quaternion rot)
        {
            var bay = CurrentBay(d);
            var exp = bay != null && d.truckSegment != null && bay.MoonpoolExpansionEnabled() ? bay.expansionManager : null;
            if (exp == null || mExpUndockTimelineDone == null) return false;
            try
            {
                var head = fExpHead != null ? fExpHead.GetValue(exp) as SeaTruckSegment : null;
                if (head == null) return false;
                var carryOut = BeginCarry(go);
                if (carryOut.On) Plugin.Log.LogInfo("Riding along: you are inside " + TechName(go) + " as the other player takes it out of the dock; taking you with it.");
                if (fExpFully != null) fExpFully.SetValue(exp, false);
                // What StartUndocking does besides its animation: close the cab's rear hatch, switch the dock's docked-only
                // colliders off and the open-water ones back on, and so on. Skipped before, which left the Seatruck's back
                // open and the docked colliders in place on this machine (0.14.11-test1).
                var tr = Traverse.Create(exp);
                try { tr.Method("UpdateDockedOnlyColliders", false).GetValue(); } catch { }
                try { tr.Method("UpdateUndockedOnlyColliders", true).GetValue(); } catch { }
                try { tr.Method("HideSealGlass", true).GetValue(); } catch { }
                try { if (head.rearConnection != null && head.rearConnection.closedGo != null) head.rearConnection.closedGo.SetActive(true); } catch { }
                try { if (exp.entranceTriggerDocked != null) exp.entranceTriggerDocked.SetActive(false); } catch { }
                try { if (exp.moduleConnection != null) exp.moduleConnection.enabled = false; } catch { }
                try { tr.Method("UpdateDockingWaterPlane").GetValue(); } catch { }
                try { tr.Method("SetDockingRoomFlooding", false).GetValue(); } catch { }
                remoteSeatruckUndock = true;
                try { mExpUndockTimelineDone.Invoke(exp, null); }
                finally { remoteSeatruckUndock = false; }
                if (mExpFinishUndocking != null) { try { mExpFinishUndocking.Invoke(exp, null); } catch { } }
                if (d.isDocked) ForceUndock(go, pos, rot);
                ClearTransition(d);
                go.transform.SetPositionAndRotation(pos, rot);
                EndCarry(carryOut, "out of the dock");
                UnfreezeTruck(FirstOf(head));
                Plugin.Log.LogInfo("Seatruck undocked here too (Seatruck dock).");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck undock here failed: " + e.Message); return false; }
        }

        // While the Seatruck dock finishes an undock for the other player's copy, do not run the bay's player steps.
        private static bool BayUndockCompletePrefix(VehicleDockingBay __instance)
        {
            if (!remoteSeatruckUndock) return true;
            var d = __instance.GetDockedObject();
            if (d != null)
            {
                applyingDock = true;
                try
                {
                    __instance.SetVehicleUndocked();
                    if (__instance.GetDockedObject() == d && mSetDockedObject != null) mSetDockedObject.Invoke(__instance, new object[] { null });
                    if (d.isDocked) d.SetDocked(null, default(Vehicle.DockType));
                    ClearTransition(d);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Undock of the copy failed: " + e.Message); }
                finally { applyingDock = false; }
            }
            return false;
        }

        private static bool GetupPrefix(MoonpoolExpansionManager __instance)
        {
            var head = fExpHead != null ? fExpHead.GetValue(__instance) as SeaTruckSegment : null;
            if (head == null) return true;
            var p = Player.main;
            // Only the player actually sitting in this Seatruck gets up from its seat.
            return p != null && p.IsPilotingSeatruck();
        }

        private class PendingDock { public VehicleDockingBay Bay; public MoonpoolExpansionManager Exp; public Dockable D; public float Since; }
        private static readonly System.Collections.Generic.Dictionary<string, PendingDock> pendingTruckDock = new System.Collections.Generic.Dictionary<string, PendingDock>();

        /// <summary>The driver's stream for a Seatruck being docked has ended: lock the copy into the dock now.</summary>
        private static bool CompletePendingDock(string id)
        {
            PendingDock pd;
            if (!pendingTruckDock.TryGetValue(id, out pd)) return false;
            pendingTruckDock.Remove(id);
            if (pd.D == null || pd.Bay == null || pd.Exp == null) return true;
            applyingDock = true;
            try { DockSeatruckInstantly(pd.Bay, pd.Exp, pd.D); remoteDockedAt[id] = Time.unscaledTime; }
            finally { applyingDock = false; }
            FinalPose fp;
            if (pd.D != null && lastFinal.TryGetValue(id, out fp) && Time.unscaledTime - fp.At < 10f)
            {
                heldCabPose[id] = fp;
                heldCabLogged.Remove(id);
                pd.D.transform.SetPositionAndRotation(fp.Pos, fp.Rot);
                Plugin.Log.LogInfo("Dock cab: the other player's cab is held where his dock put it, " + fp.Pos.ToString("F1") + ".");
            }

            Plugin.Log.LogInfo("The other player's Seatruck finished docking; locked in here too.");
            return true;
        }

        // test36: a docked vehicle is put in place every frame by its docking bay (VehicleDockingBay.LateUpdate ->
        // UpdateDocking -> UpdateDockedPosition). On the PC that only copies the other player's dock, that put the cab
        // ~9 m back from where the driver's sits (x -266.4 against -257.6), and moving the cab or the dock's carrier
        // was undone every frame (test34/35). For the other player's docked cab, the bay now puts it at the driver's
        // final position instead; the spot the bay wanted is logged once.
        private static readonly System.Collections.Generic.Dictionary<string, FinalPose> heldCabPose = new System.Collections.Generic.Dictionary<string, FinalPose>();
        private static readonly System.Collections.Generic.HashSet<string> heldCabLogged = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Reflection.FieldInfo fBayStart = AccessTools.Field(typeof(VehicleDockingBay), "startPosition");

        private static bool BayDockedPositionPrefix(VehicleDockingBay __instance, Dockable __0, float __1)
        {
            try
            {
                if (__0 == null || heldCabPose.Count == 0) return true;
                string id = WorldSync.IdOf(__0.transform);
                FinalPose fp;
                if (id == null || !heldCabPose.TryGetValue(id, out fp)) return true;
                if (!__0.isDocked) { heldCabPose.Remove(id); return true; }
                if (heldCabLogged.Add(id))
                {
                    Vector3 end = __instance.dockingEndPos != null ? __instance.dockingEndPos.position : Vector3.zero;
                    Vector3 start = fBayStart != null ? (Vector3)fBayStart.GetValue(__instance) : Vector3.zero;
                    Vector3 want = Vector3.Lerp(start, end, __1);
                    Plugin.Log.LogInfo("Dock cab: the bay here wanted the other player's cab at " + want.ToString("F1") + " (start " + start.ToString("F1") + ", end " + end.ToString("F1")
                        + ", t " + __1.ToString("0.00") + "); holding it at the driver's " + fp.Pos.ToString("F1") + " (" + Vector3.Distance(want, fp.Pos).ToString("0.00") + " m apart).");
                }
                __0.transform.SetPositionAndRotation(fp.Pos, fp.Rot);
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dock cab hold failed: " + e.Message); return true; }
        }

        /// <summary>test41: hold the other player's docked cab at the position his PC has it at (the bay here would put it elsewhere).</summary>
        private static void HoldCabAt(string id, Dockable d, Vector3 pos, Quaternion rot, string why)
        {
            try
            {
                if (d == null || !d.isDocked || pos == Vector3.zero) return;
                float off = Vector3.Distance(d.transform.position, pos);
                heldCabPose[id] = new FinalPose { Pos = pos, Rot = rot, At = Time.unscaledTime };
                heldCabLogged.Remove(id);
                d.transform.SetPositionAndRotation(pos, rot);
                Plugin.Log.LogInfo("Dock cab (" + why + "): " + TechName(d.gameObject) + " [" + id + "] held at the other player's " + pos.ToString("F1") + " (was " + off.ToString("0.00") + " m off here).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dock cab hold (" + why + ") failed: " + e.Message); }
        }

        // ================================================================== test41: join snapshot
        //
        // Dock and connection changes are one-off events: one that happened while a player was still loading the world
        // was lost for good ("Seatruck dock from the other player for a vehicle not here"). When a joiner says it is in
        // the world (its inventory request), the host sends the current state of every loaded vehicle again: where it
        // is docked, which Seatruck module is connected to which, and where each free vehicle is.
        public static void HostSendJoinSnapshot(ulong joiner)
        {
            if (net == null || !net.IsHost || !LocalPlayerSync.InWorld) return;
            int docks = 0, truckDocks = 0, connects = 0, moves = 0;
            try
            {
                var ids = new System.Collections.Generic.List<string>(); var gos = new System.Collections.Generic.List<GameObject>();
                foreach (var d in UnityEngine.Object.FindObjectsOfType<Dockable>())
                {
                    if (d == null || !d.isDocked) continue;
                    string id = WorldSync.IdOf(d.transform); if (string.IsNullOrEmpty(id)) continue;
                    var tb = TruckBayHolding(d);
                    if (tb != null)
                    {
                        string mid = tb.truckSegment != null ? WorldSync.IdOf(tb.truckSegment.transform) : null;
                        if (mid == null) continue;
                        net.SendVehicleTruckDock(id, mid, true); truckDocks++;
                        continue;
                    }
                    var bay = CurrentBay(d);
                    if (bay == null) continue;
                    net.SendVehicleDock(id, true, bay.transform.position, d.transform.position, d.transform.rotation); docks++;
                }
                foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
                {
                    if (s == null || s.frontConnection == null) continue;
                    string rid = WorldSync.IdOf(s.transform); if (string.IsNullOrEmpty(rid)) continue;
                    var cur = s.frontConnection.GetConnection();
                    string fid = cur != null && cur.truckSegment != null ? WorldSync.IdOf(cur.truckSegment.transform) : "";
                    if (fid == null) continue;
                    if (s.isMainCab && fid == "") continue;   // a cab has nothing in front
                    net.SendVehicleConnect(rid, fid); connects++;
                }
                // Free vehicles: their position (final, so nothing glides). Not the ones somebody is driving (streamed
                // anyway), docked ones (sent above) or modules carried by the one in front.
                var seen = new System.Collections.Generic.HashSet<GameObject>();
                foreach (var v in UnityEngine.Object.FindObjectsOfType<Vehicle>()) AddFree(v.gameObject, ids, gos, seen);
                foreach (var h in UnityEngine.Object.FindObjectsOfType<Hoverbike>()) AddFree(h.gameObject, ids, gos, seen);
                foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>()) if (!s.isFrontConnected) AddFree(s.gameObject, ids, gos, seen);
                moves = ids.Count;
                if (ids.Count > 0) net.SendVehicleMove(ids, gos, true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Join snapshot failed: " + e.Message); }
            Plugin.Log.LogInfo("Join snapshot for " + joiner + ": sent " + docks + " base dock(s), " + truckDocks + " Seatruck-module dock(s), " + connects + " Seatruck connection(s), " + moves + " vehicle position(s).");
        }

        private static void AddFree(GameObject go, System.Collections.Generic.List<string> ids, System.Collections.Generic.List<GameObject> gos, System.Collections.Generic.HashSet<GameObject> seen)
        {
            if (go == null || !seen.Add(go)) return;
            string id = WorldSync.IdOf(go.transform);
            if (string.IsNullOrEmpty(id) || sentLast.ContainsKey(id) || remote.ContainsKey(id) || occupiedByOthers.ContainsKey(id)) return;
            var d = go.GetComponent<Dockable>(); if (d != null && (d.isDocked || d.isInTransition)) return;
            ids.Add(id); gos.Add(go);
        }

        // ---------------------------------------------------------------- test39: Seatruck docking module
        private static readonly System.Collections.Generic.Dictionary<string, string> truckDockedIn = new System.Collections.Generic.Dictionary<string, string>();
        private static readonly System.Reflection.MethodInfo mTruckBayDock = AccessTools.Method(typeof(SeaTruckDockingBay), "Dock", new[] { typeof(Dockable) });
        private static readonly System.Reflection.FieldInfo fTruckBayDocked = AccessTools.Field(typeof(SeaTruckDockingBay), "dockedObject");

        public static void OnVehicleTruckDock(string vid, string moduleId, bool docked)
        {
            if (!LocalPlayerSync.InWorld) return;
            remoteTouched[vid] = Time.unscaledTime;
            var go = WorldSync.Find(vid);
            var d = go != null ? go.GetComponent<Dockable>() : null;
            if (d == null) { Plugin.Log.LogInfo("Seatruck dock from the other player for a vehicle not here [" + vid + "]."); return; }
            var mg = string.IsNullOrEmpty(moduleId) ? null : WorldSync.Find(moduleId);
            var bay = mg != null ? mg.GetComponentInChildren<SeaTruckDockingBay>(true) : null;
            // Stop gliding it: from now on the module (or physics) owns its position.
            RemoteState st;
            if (remote.TryGetValue(vid, out st)) { remote.Remove(vid); }
            applyingDock = true;
            try
            {
                if (docked)
                {
                    if (bay == null) { Plugin.Log.LogWarning("The other player docked " + TechName(go) + " in a Seatruck docking module [" + moduleId + "] that is not here."); return; }
                    if (d.isDocked && TruckBayHolding(d) == bay)
                    {
                        truckDockedIn[vid] = moduleId;
                        EnsureCollides(go, "(already in that Seatruck module)");
                        Plugin.Log.LogInfo("Dock state: " + TechName(go) + " [" + vid + "] is already in that Seatruck docking module here; left as is.");
                        return;
                    }
                    if (d.isDocked) UndockFromAnyBay(go, d);
                    if (mTruckBayDock == null) { Plugin.Log.LogWarning("SeaTruckDockingBay.Dock not found."); return; }
                    mTruckBayDock.Invoke(bay, new object[] { d });
                    ClearTransition(d);
                    EnsureCollides(go, "after docking in the Seatruck module");
                    truckDockedIn[vid] = moduleId;
                    Plugin.Log.LogInfo("The other player docked " + TechName(go) + " in the Seatruck docking module; docked here too (at " + go.transform.position.ToString("F1") + ").");
                }
                else
                {
                    UndockFromAnyBay(go, d);
                    truckDockedIn.Remove(vid);
                    var rb = BodyOf(go); if (rb != null) rb.isKinematic = false;
                    EnsureCollides(go, "after leaving the Seatruck module");
                    Plugin.Log.LogInfo("The other player took " + TechName(go) + " out of the Seatruck docking module; undocked here too.");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck docking module sync failed: " + e.Message); }
            finally { applyingDock = false; }
        }

        // test40: docking turns a vehicle's collisions off (Dockable.OnDockingStart) and only its docking animation's end
        // turns them back on. The synced steps here skip that animation, so the other player's Prawn suit could stay
        // without collisions - swum straight through. Make sure it collides after every synced dock / undock / release.
        private static void EnsureCollides(GameObject go, string when)
        {
            try
            {
                if (go == null || go.GetComponent<SeaTruckSegment>() != null) return;
                var rb = go.GetComponent<Rigidbody>();
                if (rb == null || rb.detectCollisions) return;
                rb.detectCollisions = true;
                Plugin.Log.LogInfo("Collision: " + TechName(go) + " had its collisions off " + when + "; turned them back on.");
            }
            catch { }
        }

        /// <summary>Out of whatever holds it: a base docking bay (ForceUndock) or a Seatruck docking module.</summary>
        private static SeaTruckDockingBay TruckBayHolding(Dockable d)
        {
            if (d == null || fTruckBayDocked == null) return null;
            var tb = d.transform.parent != null ? d.transform.parent.GetComponentInParent<SeaTruckDockingBay>() : null;
            if (tb != null) return tb;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<SeaTruckDockingBay>()) if (b != null && (Dockable)fTruckBayDocked.GetValue(b) == d) return b;
            return null;
        }

        private static void UndockFromAnyBay(GameObject go, Dockable d)
        {
            var tb = TruckBayHolding(d);
            if (tb != null)
            {
                bool was = applyingDock; applyingDock = true;
                try
                {
                    if (fTruckBayDocked != null && (Dockable)fTruckBayDocked.GetValue(tb) == d) fTruckBayDocked.SetValue(tb, null);
                    if (d.isDocked) d.SetDocked(null, default(Vehicle.DockType));
                    go.transform.SetParent(null, true);
                    try { if (tb.ejectButton != null) tb.ejectButton.SetActive(false); if (tb.enterExosuitTrigger != null) tb.enterExosuitTrigger.SetActive(false); } catch { }
                    ClearTransition(d);
                }
                finally { applyingDock = was; }
                return;
            }
            ForceUndock(go, go.transform.position, go.transform.rotation);
        }

        public static void OnVehicleDock(string id, bool docked, Vector3 bayPos, Vector3 pos, Quaternion rot)
        {
            if (!LocalPlayerSync.InWorld) return;
            remoteTouched[id] = Time.unscaledTime;
            var go = WorldSync.Find(id);
            if (go == null) { Plugin.Log.LogInfo("Dock change for a vehicle not loaded here [" + id + "]."); return; }
            var d = go.GetComponent<Dockable>();
            if (d == null) return;

            // Stop gliding it first: from now on the bay (or physics) owns its position.
            RemoteState st;
            if (remote.TryGetValue(id, out st) && !(docked && d.truckSegment != null))
            {
                remote.Remove(id);
                var rb = BodyOf(go);
                if (rb != null && !docked) rb.isKinematic = false;
                var sg = go.GetComponent<SeaTruckSegment>();
                if (sg != null && !docked) { try { sg.SetKinematicsLocked(false); sg.UpdateKinematicState(); } catch { } }
            }

            if (!docked)
            {
                pendingTruckDock.Remove(id);
                if (!UndockSeatruckFromExpansion(go, d, pos, rot)) ForceUndock(go, pos, rot);
                remoteDockedAt.Remove(id); return;
            }

            VehicleDockingBay best = null; float bestD = 15f * 15f;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>())
            {
                float dd = (b.transform.position - bayPos).sqrMagnitude;
                if (dd < bestD) { bestD = dd; best = b; }
            }
            if (best == null) { Plugin.Log.LogWarning("The other player docked " + TechName(go) + " but there is no docking bay near " + bayPos + " here."); return; }
            if (best.GetDockedObject() != null && best.GetDockedObject() != d) { Plugin.Log.LogWarning("The other player docked " + TechName(go) + " but this bay already holds " + TechName(best.GetDockedObject().gameObject) + "."); return; }
            // test41: the join snapshot repeats docks that already happened here: leave it docked, only line it up.
            if (d.isDocked && CurrentBay(d) == best)
            {
                if (d.truckSegment != null) HoldCabAt(id, d, pos, rot, "already docked here");
                else Plugin.Log.LogInfo("Dock state: " + TechName(go) + " [" + id + "] is already docked in that bay here; left as is.");
                return;
            }
            applyingDock = true;
            try
            {
                if (d.isDocked && CurrentBay(d) != best) ForceUndock(go, pos, rot);
                applyingDock = true;
                var cd = BeginCarry(go);
                go.transform.SetPositionAndRotation(pos, rot);
                EndCarry(cd, null);
                // The bay's own Dock() is what the game runs at the end of docking: it parents the vehicle, runs the moonpool
                // expansion's docking for a Seatruck (0.14.6's direct SetVehicleDocked skipped that and left the Seatruck
                // frozen before its docking animation) and never touches the local player - those steps are in
                // UpdateDocking, which only runs for a bay that docks something by itself.
                var exp = d.truckSegment != null && best.MoonpoolExpansionEnabled() ? best.expansionManager : null;
                if (exp != null && remote.ContainsKey(id))
                {
                    // The driver is still in the seat and keeps sending the Seatruck's position while the dock's animation
                    // pulls it in: keep gliding it along, and lock it into the dock when that stream ends (smooth, as in 0.14.4).
                    pendingTruckDock[id] = new PendingDock { Bay = best, Exp = exp, D = d, Since = Time.unscaledTime };
                    Plugin.Log.LogInfo("The other player is docking " + TechName(go) + "; following its docking, will lock it in when it ends.");
                    return;
                }
                if (exp != null) { DockSeatruckInstantly(best, exp, d); HoldCabAt(id, d, pos, rot, "docked instantly"); }
                else
                {
                    try { best.Dock(d); }
                    catch (Exception e) { Plugin.Log.LogWarning("Bay Dock failed (" + e.Message + "); snapping it in instead."); best.SetVehicleDocked(d); }
                }
                remoteDockedAt[id] = Time.unscaledTime;
                Plugin.Log.LogInfo("The other player docked " + TechName(go) + " [" + id + "]; docked here too.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Docking " + TechName(go) + " here failed: " + e.Message); }
            finally { applyingDock = false; }
        }
    
        // ================================================================== occupied vehicles (since 0.14.7)
        //
        // Each player tells the others which vehicles they are at the controls of (driving, or sitting in one that is
        // docked). Getting into the driving seat of a vehicle someone else occupies is refused with a message.

        private static readonly System.Collections.Generic.HashSet<string> occupiedSent = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.Dictionary<string, ulong> occupiedByOthers = new System.Collections.Generic.Dictionary<string, ulong>();

        private static void UpdateOccupancy(System.Collections.Generic.ICollection<string> nowIds)
        {
            var add = new System.Collections.Generic.List<string>(); var rem = new System.Collections.Generic.List<string>();
            foreach (var id in nowIds) if (!occupiedSent.Contains(id)) add.Add(id);
            foreach (var id in occupiedSent) if (!nowIds.Contains(id)) rem.Add(id);
            foreach (var id in add) { occupiedSent.Add(id); net.SendVehicleOccupied(id, true); }
            foreach (var id in rem) { occupiedSent.Remove(id); net.SendVehicleOccupied(id, false); }
        }

        public static void OnVehicleOccupied(ulong who, string id, bool occupied)
        {
            if (occupied) occupiedByOthers[id] = who; else { ulong w; if (occupiedByOthers.TryGetValue(id, out w) && w == who) occupiedByOthers.Remove(id); }
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Vehicle [" + id + "] " + (occupied ? "occupied" : "freed") + " by " + who);
        }

        /// <summary>
        /// The seat of the vehicle this player is at the controls of, if it is loaded here: the Prawn suit's and
        /// Snowfox's player position, the Seatruck cab's pilot position. Their avatar is put there (since 0.14.8), instead
        /// of wherever their own body transform happened to be relative to our copy of the vehicle.
        /// </summary>
        public static Transform SeatFor(ulong who) { string k; return SeatFor(who, out k); }

        /// <summary>As above; kind is the player animation for that seat: "exosuit", "seatruck" or "hoverbike".</summary>
        public static Transform SeatFor(ulong who, out string kind)
        {
            kind = null;
            if (occupiedByOthers.Count == 0) return null;
            foreach (var kv in occupiedByOthers)
            {
                if (kv.Value != who) continue;
                var go = WorldSync.Find(kv.Key);
                if (go == null) continue;
                var v = go.GetComponent<Vehicle>(); if (v != null && v.playerPosition != null) { kind = "exosuit"; return v.playerPosition.transform; }
                var h = go.GetComponent<Hoverbike>(); if (h != null && h.playerPosition != null) { kind = "hoverbike"; return h.playerPosition.transform; }
                var m = go.GetComponent<SeaTruckMotor>(); if (m != null && m.pilotPosition != null) { kind = "seatruck"; return m.pilotPosition; }
            }
            return null;
        }

        /// <summary>
        /// Seat self-check (log): where a body's root and head sit relative to the seat, in the seat's own axes
        /// (x right, y up, z forward, metres). The driver logs its real player, the other side its avatar of them; the
        /// two lines should match. Logged once per body and seat, 3 s after sitting down.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> seatDiagSeat = new System.Collections.Generic.Dictionary<string, int>();

        public static void SeatDiag(string who, string kind, Transform seat, Transform root, Transform head, Transform hips, ref float next)
        {
            if (seat == null || root == null) return;
            int sid = seat.GetInstanceID(); int last;
            if (!seatDiagSeat.TryGetValue(who, out last) || last != sid) { seatDiagSeat[who] = sid; next = Time.unscaledTime + 3f; }
            if (Time.unscaledTime < next) return;
            next = float.MaxValue; // once per seat: 3 s after sitting down
            Vector3 r = seat.InverseTransformPoint(root.position);
            float yaw = Mathf.DeltaAngle(0f, (Quaternion.Inverse(seat.rotation) * root.rotation).eulerAngles.y);
            string h = head != null ? F(seat.InverseTransformPoint(head.position)) : "n/a";
            string hp = hips != null ? F(seat.InverseTransformPoint(hips.position)) : "n/a";
            Plugin.Log.LogInfo("Seat check [" + who + ", " + (kind ?? "?") + "]: root " + F(r) + " turned " + yaw.ToString("0") + " deg, head " + h + ", hips " + hp + " (relative to the seat)");
            if (head != null) Plugin.Log.LogInfo("Seat check [" + who + "] animation: " + DescribeAnim(head.GetComponentInParent<Animator>(), root, head));
        }

        /// <summary>
        /// What a body's animator is actually playing (0.14.11-test8 diagnostics): per layer the clips and their weights,
        /// every bool parameter that is on and every float that is not zero, and where the body object and the head bone
        /// sit relative to the root. Compared between the driver and their avatar to find why the avatar's seated pose
        /// differs from the driver's.
        /// </summary>
        public static string DescribeAnim(Animator a, Transform root, Transform head)
        {
            if (a == null) return "no animator";
            var sb = new System.Text.StringBuilder();
            try
            {
                sb.Append("body at ").Append(F(root.InverseTransformPoint(a.transform.position)));
                if (head != null) sb.Append(", head bone local ").Append(F(head.localPosition)).Append(" / in body ").Append(F(a.transform.InverseTransformPoint(head.position)));
                sb.Append(", rootMotion ").Append(a.applyRootMotion).Append(", speed ").Append(a.speed.ToString("0.00")).Append(", enabled ").Append(a.enabled && a.isActiveAndEnabled);
                for (int l = 0; l < a.layerCount; l++)
                {
                    sb.Append(" | L").Append(l).Append(' ').Append(a.GetLayerName(l)).Append(" w=").Append(a.GetLayerWeight(l).ToString("0.00")).Append(':');
                    var clips = a.GetCurrentAnimatorClipInfo(l);
                    for (int c = 0; c < clips.Length; c++)
                        if (clips[c].clip != null && clips[c].weight > 0.01f) sb.Append(' ').Append(clips[c].clip.name).Append('(').Append(clips[c].weight.ToString("0.00")).Append(')');
                    if (a.IsInTransition(l))
                    {
                        var next = a.GetNextAnimatorClipInfo(l);
                        sb.Append(" ->");
                        for (int c = 0; c < next.Length; c++) if (next[c].clip != null) sb.Append(' ').Append(next[c].clip.name);
                    }
                }
                sb.Append(" | on:");
                var ps = a.parameters;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.type == AnimatorControllerParameterType.Bool && a.GetBool(p.nameHash)) sb.Append(' ').Append(p.name);
                    else if (p.type == AnimatorControllerParameterType.Float) { float f = a.GetFloat(p.nameHash); if (Mathf.Abs(f) > 0.001f) sb.Append(' ').Append(p.name).Append('=').Append(f.ToString("0.00")); }
                    else if (p.type == AnimatorControllerParameterType.Int) { int v = a.GetInteger(p.nameHash); if (v != 0) sb.Append(' ').Append(p.name).Append('=').Append(v); }
                }
            }
            catch (System.Exception e) { sb.Append(" (error: ").Append(e.Message).Append(')'); }
            return sb.ToString();
        }
        private static string F(Vector3 v) { return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")"; }

        private static float nextLocalSeatDiag;
        /// <summary>The driver's own real player, for comparison with the other side's avatar of them.</summary>
        private static void LocalSeatDiag()
        {
            var p = Player.main; if (p == null) return;
            Transform seat = null; string kind = null;
            if (p.currentMountedVehicle != null && p.currentMountedVehicle.playerPosition != null) { seat = p.currentMountedVehicle.playerPosition.transform; kind = "exosuit"; }
            else if (p.IsPilotingSeatruck())
            {
                var seg = p.GetComponentInParent<SeaTruckSegment>(); var m = seg != null ? FirstOf(seg).GetComponent<SeaTruckMotor>() : null;
                if (m != null) { seat = m.pilotPosition; kind = "seatruck"; }
            }
            else foreach (var b in bikes) if (b != null && b.isPiloting && b.playerPosition != null) { seat = b.playerPosition.transform; kind = "hoverbike"; break; }
            if (seat == null) return;
            Transform head = null, hips = null;
            try { var a = p.GetComponentInChildren<Animator>(); if (a != null) { head = a.GetBoneTransform(HumanBodyBones.Head); hips = a.GetBoneTransform(HumanBodyBones.Hips); } } catch { }
            if (head == null && SNCameraRoot.main != null) head = SNCameraRoot.main.transform;
            SeatDiag("You", kind, seat, p.transform, head, hips, ref nextLocalSeatDiag);
        }

        /// <summary>
        /// Where the driver's head sits relative to the seat point, in the seat's axes (right, up, forward), from the
        /// driver-side seat checks of 0.14.11-test3/test4: Seatruck (0, 0.11, -0.15), Prawn suit (0, 0.10, -0.15).
        /// </summary>
        public static Vector3 SeatHeadOffset(string kind)
        {
            switch (kind)
            {
                case "seatruck": return new Vector3(0f, 0.11f, -0.15f);
                case "exosuit": return new Vector3(0f, 0.10f, -0.15f);
                default: return new Vector3(0f, 0.10f, -0.15f);
            }
        }

        /// <summary>A player left: whatever they occupied is free.</summary>
        public static void OnPlayerLeft(ulong who)
        {
            var ids = new System.Collections.Generic.List<string>();
            foreach (var kv in occupiedByOthers) if (kv.Value == who) ids.Add(kv.Key);
            foreach (var id in ids) occupiedByOthers.Remove(id);
        }

        private static string OccupantOf(Component c)
        {
            if (c == null || occupiedByOthers.Count == 0) return null;
            // A Seatruck is occupied as a whole: check every segment's id up the chain and the cab.
            for (var t = c.transform; t != null; t = t.parent)
            {
                var uid = t.GetComponent<UniqueIdentifier>();
                ulong who;
                if (uid != null && occupiedByOthers.TryGetValue(uid.Id, out who))
                {
                    string name = null;
                    try { var rp = Plugin.Instance != null && Plugin.Instance.Players != null ? Plugin.Instance.Players.Get(who) : null; name = rp != null ? rp.Name : null; } catch { }
                    return string.IsNullOrEmpty(name) ? "another player" : name;
                }
            }
            return null;
        }

        private static float nextRefusalMsg;
        private static bool Refuse(Component c)
        {
            if (applyingDock || !(net != null && net.IsInSession)) return false;
            string who = OccupantOf(c);
            if (who == null) return false;
            if (Time.unscaledTime >= nextRefusalMsg)
            {
                nextRefusalMsg = Time.unscaledTime + 2f;
                try { ErrorMessage.AddMessage(who + " is driving this."); } catch { }
                Plugin.Log.LogInfo("Did not let you in: " + who + " is driving " + TechName(c.gameObject) + ".");
            }
            return true;
        }

        private static bool VehicleClickPrefix(Vehicle __instance) { return !Refuse(__instance); }
        private static bool VehicleEnterPrefix(Vehicle __instance) { return !Refuse(__instance); }
        private static bool HoverbikeEnterPrefix(Hoverbike __instance) { return !Refuse(__instance); }
        private static bool ChairClickPrefix(PilotingChair __instance)
        {
            var seg = __instance.GetComponentInParent<SeaTruckSegment>();
            return seg == null || !Refuse(FirstOf(seg));
        }
        private static bool TruckPilotPrefix(SeaTruckMotor __instance)
        {
            if (ModuleHandleBlocked(__instance, true)) return false;   // test32: a module's own handle, someone inside
            bool refuse = Refuse(__instance);
            if (refuse) Plugin.Log.LogWarning("StartPiloting refused late (after the climb-in began); the wheel gate should have caught this.");
            return !refuse;
        }

        /// <summary>Refuse the roof hatch only when it would put us in the driver's seat (the game's own test).</summary>
        private static bool HatchEnterPrefix(SeaTruckSegment __instance)
        {
            try
            {
                var rc = __instance.rearConnection;
                bool walkable = Traverse.Create(__instance).Method("IsWalkable").GetValue<bool>();
                bool pilots = (__instance.isMainCab && rc != null && !rc.occupied) || !walkable;
                if (!pilots) return true;
                return !Refuse(__instance);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hatch check failed: " + e.Message); return true; }
        }

        private static void WheelAllowedPostfix(SeaTruckMotor __instance, ref bool __result)
        {
            if (__result && (Refuse(__instance) || ModuleHandleBlocked(__instance, false))) __result = false;
        }

        // ---------------------------------------------------------------- test28: no detaching with someone inside
        // Pulling the detach lever while another player was inside the Seatruck dropped that player out of the
        // truck on their PC (walking on the sea floor as if dry). The lever is now refused while any other player
        // is inside any segment of this truck chain. The local player alone never blocks it (the game handles us).

        /// <summary>
        /// Name of a player inside any segment of this truck chain, or null. test31: ANY player, the local one included
        /// (the swim handles are on the outside, so whoever pulls them is not inside).
        /// </summary>
        private static string OtherPlayerInsideChain(SeaTruckSegment seg, out string detail)
        {
            detail = null;
            if (seg == null || !(net != null && net.IsInSession)) return null;
            var chain = new System.Collections.Generic.List<SeaTruckSegment>();
            try { FirstOf(seg).GetTruckChain(chain); } catch { }
            if (chain.Count == 0) chain.Add(seg);
            var me = Player.main;
            if (me != null)
            {
                foreach (var s in chain)
                {
                    if (s == null) continue;
                    float d;
                    if ((me.currentInterior as SeaTruckSegment) == s || InsideSegment(s, me.transform.position, out d))
                    {
                        detail = "you are inside " + TechName(s.gameObject);
                        return "You are";
                    }
                }
            }
            var reg = Plugin.Instance != null ? Plugin.Instance.Players : null;
            if (reg == null || reg.Count == 0) return null;
            foreach (var rp in reg.All)
            {
                if (rp == null || !rp.HasPose) continue;
                Vector3 pos = rp.Position;
                foreach (var s in chain)
                {
                    if (s == null) continue;
                    float outside;
                    if (InsideSegment(s, pos, out outside))
                    {
                        detail = rp.Name + " inside " + TechName(s.gameObject) + " (" + outside.ToString("0.00") + " m inside the bounds edge)";
                        return rp.Name + " is";
                    }
                }
            }
            return null;
        }

        /// <summary>Is a world point inside the segment's interior box (with a 0.5 m margin)? "depth" is how far in.</summary>
        internal static bool InsideSegment(SeaTruckSegment s, Vector3 p, out float depth)
        {
            depth = 0f;
            var b = OrientedBounds.ToWorldBounds(s.transform, s.interiorBounds);
            Vector3 ext = b.extents;
            if (ext.sqrMagnitude < 0.01f)
            {
                // No interior box on this segment: fall back to a 2.5 m sphere around it.
                float d = Vector3.Distance(s.transform.position, p);
                depth = 2.5f - d;
                return d <= 2.5f;
            }
            Vector3 lp = Quaternion.Inverse(b.rotation) * (p - b.position);
            const float margin = 0.5f;
            float dx = ext.x + margin - Mathf.Abs(lp.x), dy = ext.y + margin - Mathf.Abs(lp.y), dz = ext.z + margin - Mathf.Abs(lp.z);
            depth = Mathf.Min(dx, Mathf.Min(dy, dz));
            return depth >= 0f;
        }

        private static float nextDetachMsg;
        private static bool DetachLeverPrefix(SeaTruckSegment __instance)
        {
            DetachTrace("detach lever / handle (OnClickDetachLever)", __instance);
            try
            {
                string detail;
                string who = OtherPlayerInsideChain(__instance, out detail);
                if (who == null)
                {
                    if (net != null && net.IsInSession) Plugin.Log.LogInfo("Detach lever on " + TechName(__instance.gameObject) + ": nobody else inside, detaching.");
                    return true;
                }
                if (Time.unscaledTime >= nextDetachMsg)
                {
                    nextDetachMsg = Time.unscaledTime + 2f;
                    try { ErrorMessage.AddMessage(who + " inside this Seatruck. Can't detach modules now."); } catch { }
                }
                Plugin.Log.LogInfo("Detach refused on " + TechName(__instance.gameObject) + ": " + detail + ".");
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Detach check failed: " + e.Message); return true; }
        }

        // test30: the swim handles on top of a module detach it too, and they do not go through the lever's
        // OnClickDetachLever (test29's log showed a split with no lever line). Every player-made detach ends in
        // SeaTruckConnection.Disconnect, so that is gated as well. The game's own detaches (docking, a destroyed
        // segment, piloting a module, the dock taking the tail) run inside the methods below and are let through.
        private static int gameDetachDepth;
        private static void GameDetachEnter() { gameDetachDepth++; }
        private static Exception GameDetachExit(Exception __exception) { if (gameDetachDepth > 0) gameDetachDepth--; return __exception; }

        private static bool ConnectionDisconnectPrefix(SeaTruckConnection __instance)
        {
            try
            {
                if (__instance == null || __instance.GetConnection() == null) return true;
                DetachTrace("SeaTruckConnection.Disconnect [" + __instance.connectionType + "]", __instance.truckSegment);
                if (gameDetachDepth > 0 || applyingDock || InRemoteSeatruckStep || applyingConnect) return true;
                var seg = __instance.truckSegment;
                string detail;
                string who = OtherPlayerInsideChain(seg, out detail);
                if (who == null)
                {
                    if (net != null && net.IsInSession) Plugin.Log.LogInfo("Seatruck connection on " + (seg != null ? TechName(seg.gameObject) : "?") + " (" + __instance.connectionType + ") released by the player: nobody else inside, detaching.");
                    return true;
                }
                if (Time.unscaledTime >= nextDetachMsg)
                {
                    nextDetachMsg = Time.unscaledTime + 2f;
                    try { ErrorMessage.AddMessage(who + " inside this Seatruck. Can't detach modules now."); } catch { }
                }
                Plugin.Log.LogInfo("Detach refused (handle / connection) on " + (seg != null ? TechName(seg.gameObject) : "?") + ": " + detail + ".");
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Connection detach check failed: " + e.Message); return true; }
        }

        // test31: test30 logged nothing at all when the handles were used, so every detach-related call is now logged
        // with who called it (always, in a session or not), and the block sits at the lowest level as well:
        // SeaTruckConnection.SetConnectedTo(null) is the one place the game breaks a connection.
        private static readonly AccessTools.FieldRef<SeaTruckConnection, SeaTruckConnection> fConnection =
            AccessTools.FieldRefAccess<SeaTruckConnection, SeaTruckConnection>("connection");

        private static string Callers()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                var sb = new System.Text.StringBuilder();
                int n = 0;
                for (int i = 0; i < st.FrameCount && n < 4; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null || m.DeclaringType == typeof(VehicleSync)) continue;
                    string tn = m.DeclaringType != null ? m.DeclaringType.Name : "?";
                    if (tn.StartsWith("DMD") || tn.Contains("Harmony")) continue;
                    if (n++ > 0) sb.Append(" < ");
                    sb.Append(tn).Append('.').Append(m.Name);
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static float nextHoverLog;
        private static void DetachTrace(string what, Component c)
        {
            try
            {
                Plugin.Log.LogInfo("Detach trace: " + what + " on " + (c != null ? TechName(c.gameObject) : "?") + " (game flow " + gameDetachDepth
                    + (applyingDock ? ", applying dock" : "") + (InRemoteSeatruckStep ? ", remote dock step" : "") + "), called from " + Callers() + ".");
            }
            catch { }
        }

        private static void DetachCallPrefix(SeaTruckSegment __instance) { DetachTrace("SeaTruckSegment.Detach", __instance); }
        private static void DetachFrontCallPrefix(SeaTruckSegment __instance) { DetachTrace("SeaTruckSegment.DetachFront", __instance); }

        private static bool SetConnectedToPrefix(SeaTruckConnection __instance, SeaTruckConnection otherConnection)
        {
            try
            {
                if (otherConnection != null || __instance == null) return true;
                var cur = fConnection(__instance);
                if (cur == null) return true;                                   // nothing to break
                DetachTrace("SeaTruckConnection.SetConnectedTo(null) [" + __instance.connectionType + "]", __instance.truckSegment);
                if (gameDetachDepth > 0 || applyingDock || InRemoteSeatruckStep || applyingConnect) return true;
                string detail;
                string who = OtherPlayerInsideChain(__instance.truckSegment, out detail);
                if (who == null) return true;
                if (Time.unscaledTime >= nextDetachMsg)
                {
                    nextDetachMsg = Time.unscaledTime + 2f;
                    try { ErrorMessage.AddMessage(who + " inside this Seatruck. Can't detach modules now."); } catch { }
                }
                Plugin.Log.LogInfo("Detach refused (connection) on " + TechName(__instance.truckSegment.gameObject) + ": " + detail + ".");
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("SetConnectedTo check failed: " + e.Message); return true; }
        }

        // ---------------------------------------------------------------- test32: module handles
        // test31's trace showed what the "handles on top you can swim with" are: each module's own steering handle.
        // Grabbing it starts piloting THAT module, and the game's first step is DetachFront - taking it off the truck.
        // test30 had let StartPiloting through as a game detach. Now a connected module's handle is closed while
        // anyone is inside any part of that truck.
        private static float nextHandleMsg;
        private static bool ModuleHandleBlocked(SeaTruckMotor m, bool clicked)
        {
            if (m == null || applyingDock || InRemoteSeatruckStep) return false;
            var seg = m.GetComponent<SeaTruckSegment>();
            if (seg == null || seg.isMainCab || !seg.isFrontConnected) return false;
            string detail;
            string who = OtherPlayerInsideChain(seg, out detail);
            if (who == null) return false;
            if (clicked && Time.unscaledTime >= nextHandleMsg)
            {
                nextHandleMsg = Time.unscaledTime + 2f;
                try { ErrorMessage.AddMessage(who + " inside this Seatruck. Can't take this module off now."); } catch { }
                Plugin.Log.LogInfo("Module handle refused on " + TechName(seg.gameObject) + ": " + detail + ".");
            }
            return true;
        }
        private static bool SteeringClickPrefix(SeaTruckMotor __instance) { return !ModuleHandleBlocked(__instance, true); }

        // ---------------------------------------------------------------- test32: connection events
        // Connecting and detaching modules was never shared: the other PC kept its copies connected (doors open,
        // modules dragged along) while this one had them apart, so a player could fall out through an open door.
        // Every change of a module's front connection made here is sent; the other PC applies the same change.
        private static bool applyingConnect;
        private static readonly System.Collections.Generic.Dictionary<string, string> lastFrontSent = new System.Collections.Generic.Dictionary<string, string>();
        private static readonly System.Collections.Generic.Dictionary<string, float> connectApplied = new System.Collections.Generic.Dictionary<string, float>();

        private static string FrontIdOf(SeaTruckSegment s)
        {
            var other = s != null && s.frontConnection != null ? s.frontConnection.GetConnection() : null;
            string fid = other != null && other.truckSegment != null ? WorldSync.IdOf(other.truckSegment.transform) : null;
            return fid ?? "";
        }

        private static void ConnectionChangedPostfix(SeaTruckSegment __instance)
        {
            try
            {
                if (__instance == null) return;
                string id = WorldSync.IdOf(__instance.transform);
                if (string.IsNullOrEmpty(id)) return;
                string frontId = FrontIdOf(__instance);
                string was;
                if (lastFrontSent.TryGetValue(id, out was) && was == frontId) return;
                lastFrontSent[id] = frontId;
                if (applyingConnect || applyingDock || InRemoteSeatruckStep || !WorldSync.CanSend || net == null || !net.IsInSession) return;
                // A truck the other player is handling: his PC reports its connections.
                if (IsRemoteOwned(__instance)) return;
                net.SendVehicleConnect(id, frontId);
                Plugin.Log.LogInfo("Seatruck connection sent: " + TechName(__instance.gameObject) + " [" + id.Substring(0, Math.Min(8, id.Length)) + "] "
                    + (frontId.Length == 0 ? "detached from the module in front" : "connected behind [" + frontId.Substring(0, Math.Min(8, frontId.Length)) + "]") + ".");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck connection send failed: " + e.Message); }
        }

        // While driving: send this truck's connections when starting and every 10 s, so a PC that got out of step
        // (a missed event, a save from an older build) is put right. Sending a connection it already has does nothing.
        private static SeaTruckSegment resyncHead;
        private static float nextResync;
        private static void ResyncChain(SeaTruckSegment first)
        {
            if (first == null || net == null || !net.IsInSession || !WorldSync.CanSend) return;
            if (first == resyncHead && Time.unscaledTime < nextResync) return;
            resyncHead = first; nextResync = Time.unscaledTime + 10f;
            try
            {
                var chain = new System.Collections.Generic.List<SeaTruckSegment>();
                first.GetTruckChain(chain);
                int n = 0;
                foreach (var sg in chain)
                {
                    if (sg == null || sg == first) continue;
                    string id = WorldSync.IdOf(sg.transform), fid = FrontIdOf(sg);
                    if (string.IsNullOrEmpty(id) || fid.Length == 0) continue;
                    net.SendVehicleConnect(id, fid); n++;
                }
                if (n > 0 && Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Seatruck connections re-sent for the truck you drive (" + n + ").");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seatruck connection resync failed: " + e.Message); }
        }

        public static void OnVehicleConnect(string rearId, string frontId)
        {
            if (!LocalPlayerSync.InWorld || string.IsNullOrEmpty(rearId)) return;
            var rg = WorldSync.Find(rearId);
            var rear = rg != null ? rg.GetComponent<SeaTruckSegment>() : null;
            if (rear == null || rear.frontConnection == null) { Plugin.Log.LogInfo("Seatruck connection from the other player for a module not here [" + rearId + "]."); return; }
            connectApplied[rearId] = Time.unscaledTime;
            lastFrontSent[rearId] = frontId ?? "";
            applyingConnect = true;
            try
            {
                var cur = rear.frontConnection.GetConnection();
                if (string.IsNullOrEmpty(frontId))
                {
                    if (cur == null) { Plugin.Log.LogInfo("Seatruck connection from the other player: " + TechName(rg) + " detached - already apart here."); MarkIfDockTail(rearId, rear, null); return; }
                    var frontSeg = cur.truckSegment;
                    rear.frontConnection.Disconnect();
                    Plugin.Log.LogInfo("Seatruck connection from the other player: " + TechName(rg) + " detached here too (door closed: " + DoorClosed(rear) + ").");
                    MarkIfDockTail(rearId, rear, frontSeg);
                    return;
                }
                var fg = WorldSync.Find(frontId);
                var front = fg != null ? fg.GetComponent<SeaTruckSegment>() : null;
                if (front == null || front.rearConnection == null) { Plugin.Log.LogInfo("Seatruck connection from the other player: the module in front [" + frontId + "] is not here."); return; }
                if (cur != null && cur.truckSegment == front) return;   // already so here (the 10 s resync lands here)
                if (cur != null) rear.frontConnection.Disconnect();
                if (front.rearConnection.GetConnection() != null) front.rearConnection.Disconnect();
                if (remote.ContainsKey(rearId)) remote.Remove(rearId);
                ReleaseDockTailHold(rearId, rear);
                rear.frontConnection.SetConnectedTo(front.rearConnection);
                Plugin.Log.LogInfo("Seatruck connection from the other player: " + TechName(rg) + " connected behind " + TechName(fg) + " here too.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Applying the other player's Seatruck connection failed: " + e.Message); }
            finally { applyingConnect = false; }
        }

        private static string DoorClosed(SeaTruckSegment s)
        {
            try { return s.frontConnection.closedGo != null ? s.frontConnection.closedGo.activeSelf.ToString() : "n/a"; } catch { return "?"; }
        }

        // ---------------------------------------------------------------- test33: the docked tail
        private static SeaTruckSegment trackedTail;
        private static float trackedTailUntil;
        // On the other PC: modules the driver's dock took off, held still in the tube (tail id -> cab id).
        private static readonly System.Collections.Generic.Dictionary<string, string> heldDockTails = new System.Collections.Generic.Dictionary<string, string>();

        private static void DockTailPostfix(SeaTruckSegment newTail)
        {
            try
            {
                if (newTail == null || applyingDock || InRemoteSeatruckStep || net == null || !net.IsInSession) return;
                if (IsRemoteOwned(newTail)) return;
                trackedTail = newTail;
                trackedTailUntil = Time.unscaledTime + 20f;
                Plugin.Log.LogInfo("Dock tail: your dock took " + TechName(newTail.gameObject) + " off the cab; sending its movement into the tube for 20 s.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dock tail tracking failed: " + e.Message); }
        }

        /// <summary>Hold a module the other player's dock took off still where his PC put it (the dock holds it there).</summary>
        private static bool HoldDockTail(string id, GameObject go, Vector3 pos, Quaternion rot)
        {
            string cabId;
            if (!heldDockTails.TryGetValue(id, out cabId)) return false;
            var cg = WorldSync.Find(cabId);
            var cd = cg != null ? cg.GetComponent<Dockable>() : null;
            if (cd == null || !(cd.isDocked || pendingTruckDock.ContainsKey(cabId))) { heldDockTails.Remove(id); return false; }
            go.transform.SetPositionAndRotation(pos, rot);
            var seg = go.GetComponent<SeaTruckSegment>();
            if (seg != null) { try { seg.SetKinematicsLocked(true); } catch { } }
            var rb = BodyOf(go);
            if (rb != null) { rb.isKinematic = true; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Plugin.Log.LogInfo("Dock tail: " + TechName(go) + " held in the dock tube at " + pos.ToString("F1") + " (where the driver's dock put it).");
            return true;
        }

        /// <summary>
        /// Was this module just taken off by the other player's dock? Yes if the cab it was on (or, not knowing that, a
        /// main cab within 30 m) is docked or being docked by the other player. Then hold it in the tube once placed.
        /// </summary>
        private static void MarkIfDockTail(string id, SeaTruckSegment seg, SeaTruckSegment formerFront)
        {
            try
            {
                if (seg == null || heldDockTails.ContainsKey(id)) return;
                SeaTruckSegment cab = null;
                if (formerFront != null) cab = FirstOf(formerFront);
                else
                {
                    float best = 30f;
                    foreach (var s in UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>())
                    {
                        if (s == null || !s.isMainCab) continue;
                        float d = Vector3.Distance(s.transform.position, seg.transform.position);
                        if (d < best) { best = d; cab = s; }
                    }
                }
                if (cab == null) return;
                string cabId = WorldSync.IdOf(cab.transform);
                var cdk = cab.GetComponent<Dockable>();
                if (cabId == null || cdk == null || !(cdk.isDocked || pendingTruckDock.ContainsKey(cabId))) return;
                heldDockTails[id] = cabId;
                Plugin.Log.LogInfo("Dock tail: " + TechName(seg.gameObject) + " was taken off by the other player's dock; it will be held in the tube once his PC has placed it.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dock tail check failed: " + e.Message); }
        }

        private static void ReleaseDockTailHold(string id, SeaTruckSegment seg)
        {
            if (!heldDockTails.Remove(id)) return;
            try { if (seg != null) { seg.SetKinematicsLocked(false); seg.UpdateKinematicState(); } } catch { }
        }

        private static void PatchGameDetach(Harmony harmony, Type t, string m, Type[] args)
        {
            var mi = args != null ? AccessTools.Method(t, m, args) : AccessTools.Method(t, m);
            if (mi == null) { Plugin.Log.LogWarning("VehicleSync: " + t.Name + "." + m + " not found (game detach pass-through)."); return; }
            harmony.Patch(mi, prefix: new HarmonyMethod(typeof(VehicleSync), "GameDetachEnter"), finalizer: new HarmonyMethod(typeof(VehicleSync), "GameDetachExit"));
        }

        private static void DetachHoverPostfix(SeaTruckSegment __instance)
        {
            if (Time.unscaledTime >= nextHoverLog) { nextHoverLog = Time.unscaledTime + 10f; DetachTrace("pointing at the detach handle (OnHoverDetachLever)", __instance); }
            try
            {
                string detail;
                string who = OtherPlayerInsideChain(__instance, out detail);
                if (who == null) return;
                HandReticle.main.SetIcon(HandReticle.IconType.HandDeny, 1f);
                HandReticle.main.SetText(HandReticle.TextType.Hand, who + " inside - can't detach", false, GameInput.Button.None);
            }
            catch { }
        }

        private static void PatchPrefix(Harmony harmony, Type t, string m, Type[] args, string prefix)
        {
            var mi = args != null ? AccessTools.Method(t, m, args) : AccessTools.Method(t, m);
            if (mi != null) harmony.Patch(mi, prefix: new HarmonyMethod(typeof(VehicleSync), prefix));
            else Plugin.Log.LogWarning("VehicleSync: " + t.Name + "." + m + " not found.");
        }

        internal static void InstallOccupancy(Harmony harmony)
        {
            PatchPrefix(harmony, typeof(Vehicle), "OnHandClick", null, "VehicleClickPrefix");
            PatchPrefix(harmony, typeof(Vehicle), "EnterVehicle", null, "VehicleEnterPrefix");
            PatchPrefix(harmony, typeof(Exosuit), "EnterVehicle", null, "VehicleEnterPrefix");
            PatchPrefix(harmony, typeof(Hoverbike), "EnterVehicle", Type.EmptyTypes, "HoverbikeEnterPrefix");
            PatchPrefix(harmony, typeof(PilotingChair), "OnHandClick", null, "ChairClickPrefix");
            PatchPrefix(harmony, typeof(SeaTruckMotor), "StartPiloting", Type.EmptyTypes, "TruckPilotPrefix");
            // test20: the Seatruck's steering wheel is the real way in. Its click starts the climb-in cinematic and only
            // calls StartPiloting when that ends, so refusing StartPiloting alone left the player frozen in the
            // cinematic. Closing the wheel itself (hover and click both ask this) stops it before anything starts.
            // test21: the roof hatch also leads straight into the driver's seat (SeaTruckSegment.EnterHatch starts
            // piloting and then the climb-in animation), so it needs the same gate before anything starts.
            PatchPrefix(harmony, typeof(SeaTruckSegment), "EnterHatch", null, "HatchEnterPrefix");
            var allowed = AccessTools.Method(typeof(SeaTruckMotor), "AllowedToUseSteeringWheel");
            if (allowed != null) harmony.Patch(allowed, postfix: new HarmonyMethod(typeof(VehicleSync), "WheelAllowedPostfix"));
            else Plugin.Log.LogWarning("VehicleSync: SeaTruckMotor.AllowedToUseSteeringWheel not found.");
            // Player steps the Seatruck dock runs, skipped while it runs for the other player's copy.
            PatchPrefix(harmony, typeof(SeaTruckMotor), "SetPiloting", new[] { typeof(bool), typeof(bool) }, "SkipIfRemoteSeatruckStep");
            PatchPrefix(harmony, typeof(Player), "EnterPilotingMode", null, "SkipIfRemoteSeatruckStep");
            PatchPrefix(harmony, typeof(Player), "FreezeStats", Type.EmptyTypes, "SkipIfRemoteSeatruckStep");
            PatchPrefix(harmony, typeof(Player), "UnfreezeStats", Type.EmptyTypes, "SkipIfRemoteSeatruckStep");
            PatchPrefix(harmony, typeof(Player), "SetCurrentSub", null, "SkipIfRemoteSeatruckStep");
            PatchPrefix(harmony, typeof(MoonpoolExpansionManager), "StartGetupSequence", Type.EmptyTypes, "GetupPrefix");
            // test28: no detaching modules while another player is inside the truck.
            PatchPrefix(harmony, typeof(SeaTruckSegment), "OnClickDetachLever", null, "DetachLeverPrefix");
            var hov = AccessTools.Method(typeof(SeaTruckSegment), "OnHoverDetachLever");
            if (hov != null) harmony.Patch(hov, postfix: new HarmonyMethod(typeof(VehicleSync), "DetachHoverPostfix"));
            else Plugin.Log.LogWarning("VehicleSync: SeaTruckSegment.OnHoverDetachLever not found.");
            Plugin.Log.LogInfo("Seatruck detach lever: blocked while another player is inside (test28).");
            PatchGameDetach(harmony, typeof(MoonpoolExpansionManager), "DockTail", null);
            PatchGameDetach(harmony, typeof(SeaTruckSegment), "OnDockingStart", null);
            PatchGameDetach(harmony, typeof(SeaTruckSegment), "OnKill", null);
            PatchGameDetach(harmony, typeof(SeaTruckMotor), "StartPiloting", Type.EmptyTypes);
            PatchPrefix(harmony, typeof(SeaTruckConnection), "Disconnect", Type.EmptyTypes, "ConnectionDisconnectPrefix");
            PatchPrefix(harmony, typeof(SeaTruckConnection), "SetConnectedTo", new[] { typeof(SeaTruckConnection) }, "SetConnectedToPrefix");
            PatchPrefix(harmony, typeof(SeaTruckSegment), "Detach", Type.EmptyTypes, "DetachCallPrefix");
            PatchPrefix(harmony, typeof(SeaTruckSegment), "DetachFront", Type.EmptyTypes, "DetachFrontCallPrefix");
            Plugin.Log.LogInfo("Seatruck detach (test31): blocked at the connection itself while anyone is inside; every detach call is traced.");
            PatchPrefix(harmony, typeof(SeaTruckMotor), "OnClickSteeringWheel", null, "SteeringClickPrefix");
            var occ = AccessTools.Method(typeof(SeaTruckSegment), "OnConnectionChanged");
            if (occ != null) harmony.Patch(occ, postfix: new HarmonyMethod(typeof(VehicleSync), "ConnectionChangedPostfix"));
            else Plugin.Log.LogWarning("VehicleSync: SeaTruckSegment.OnConnectionChanged not found; module connections will not sync.");
            Plugin.Log.LogInfo("Seatruck (test32): module handles closed while anyone is inside; module connections shared; only the front piece of a connected group is streamed.");
            var dt = AccessTools.Method(typeof(MoonpoolExpansionManager), "DockTail");
            if (dt != null) harmony.Patch(dt, postfix: new HarmonyMethod(typeof(VehicleSync), "DockTailPostfix"));
            Plugin.Log.LogInfo("Seatruck dock tail (test33): the driver streams the modules into the tube; the other PC holds them there.");
            PatchPrefix(harmony, typeof(VehicleDockingBay), "UpdateDockedPosition", null, "BayDockedPositionPrefix");
            Plugin.Log.LogInfo("Seatruck dock cab (test36): the other player's docked cab is held at the driver's position.");
            Plugin.Log.LogInfo("Seatruck handles / connections: detaching blocked while another player is inside (test30).");
        }
    }
}
