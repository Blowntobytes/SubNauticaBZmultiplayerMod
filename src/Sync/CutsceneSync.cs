using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BZMultiplayer.Net;
using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Cutscene / cinematic sync: when one player triggers a world-level cinematic (Marg, alien arches, artifacts...),
    /// the other players experience it too. Personal cinematics (bulkheads, base doors, drop pod, vehicle entry/exit)
    /// are NOT synced.
    ///
    /// Pull-in (config Gameplay / CutscenePullIn, default on, since 0.14.0): the other player is taken to where the
    /// cutscene happens, the area is given time to load, the same cinematic is started on their own player, and when
    /// it ends they are put back exactly where they were (position, facing, base/vehicle they were in). Players who
    /// are piloting, in a Prawn suit, or already in a cinematic are not pulled in; they get the old behaviour: the
    /// world-side animation only (and the story audio, which StorySync forwards either way).
    /// </summary>
    public static class CutsceneSync
    {
        private static SteamNet net;
        private static bool applying;

        /// <summary>True while a cutscene from the other player is being brought to this player (travelling there, playing, coming back).</summary>
        public static bool Busy { get { return pulling || applying; } }

        /// <summary>
        /// Animation names that should NOT be synced — these are personal (entering/exiting things).
        /// Matched by prefix so e.g. "bulkhead" catches "bulkhead_open", "bulkhead_close" etc.
        /// </summary>
        private static readonly string[] BlockedPrefixes = new[]
        {
            "drop_pod",         // drop pod hatch
            "droppod",          // drop pod (no underscore variant: droppod_enter_cin etc.)
            "bulkhead",         // base bulkhead doors
            "door",             // generic doors
            "hatch",            // hatches
            "enter",            // entering vehicles/bases
            "exit",             // exiting vehicles/bases
            "climb",            // ladders
            "use_chair",        // sitting
            "bench",            // bench sitting
            "moonpool",         // moonpool docking
            "cyclops",          // cyclops interactions
            "seatruck",         // seatruck docking/entering
            "dock",             // docking
            "snowfox",          // snowfox mount/dismount
            "prawn",            // prawn suit
            "exosuit",          // exosuit
            "jukebox",          // jukebox use
            "shower",           // shower use
            "bed",              // bed use
            "toilet",           // toilet use
            "vending",          // vending machine
            "coffee",           // coffee machine
            "fabricat",         // fabricator
            "terminal",         // terminal use
            "constructor",      // mobile vehicle bay
            "maproom",          // map room / scanner room
            "charging",         // charging station
        };

        /// <summary>
        /// Matched anywhere in the name (since 0.14.2): the land base bulkhead is "surfacebasedoor_frontopen", which the
        /// prefix list above missed, so the other player was pulled across the map to watch a door open.
        /// </summary>
        private static readonly string[] BlockedParts = new[]
        {
            "door", "bulkhead", "hatch", "dock", "seatruck", "moonpool", "ladder", "airlock",
        };

        /// <summary>
        /// A cutscene that belongs to a base, a Seatruck or a vehicle is someone using that thing (doors, docking,
        /// getting in or out): personal, whatever its name.
        /// </summary>
        private static bool IsOnBaseOrVehicle(Component c)
        {
            if (c == null) return false;
            try
            {
                if (c.GetComponentInParent<Base>() != null) return true;
                if (c.GetComponentInParent<SubRoot>() != null) return true;
                if (c.GetComponentInParent<Vehicle>() != null) return true;
                if (c.GetComponentInParent<SeaTruckSegment>() != null) return true;
            }
            catch { }
            return false;
        }

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;

            var start = AccessTools.Method(typeof(PlayerCinematicController), "StartCinematicMode",
                new[] { typeof(Player) });
            if (start != null)
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(CutsceneSync), "CinematicStartPostfix"));
            else
                Plugin.Log.LogWarning("CutsceneSync: PlayerCinematicController.StartCinematicMode(Player) not found.");

            Plugin.Log.LogInfo("CutsceneSync installed.");
        }

        // ------------------------------------------------------------------ helpers

        private static string GetAnimName(PlayerCinematicController controller)
        {
            try
            {
                var field = AccessTools.Field(typeof(PlayerCinematicController), "playerViewAnimationName");
                if (field != null) return field.GetValue(controller) as string;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The name a cutscene is known by on both machines: its player animation, or - for timeline cutscenes such as
        /// Marg's first encounter, which have no player animation - the name of its object (e.g. "marge_intro_cin").
        /// </summary>
        private static string SyncName(PlayerCinematicController c)
        {
            if (c == null) return null;
            string a = GetAnimName(c);
            return string.IsNullOrEmpty(a) ? c.gameObject.name.Replace("(Clone)", "").Trim() : a;
        }

        private static readonly FieldInfo fTriggerController = AccessTools.Field(typeof(CinematicModeTriggerBase), "cinematicController");
        private static readonly FieldInfo fTriggerType = AccessTools.Field(typeof(CinematicModeTriggerBase), "triggerType");
        private static readonly FieldInfo fTriggerGoal = AccessTools.Field(typeof(CinematicModeTriggerBase), "storyGoal");
        private static readonly MethodInfo mTriggerStart = AccessTools.Method(typeof(CinematicModeTriggerBase), "StartCinematicMode", new[] { typeof(Player) });

        /// <summary>The trigger that starts this cinematic, if any (cutscenes are rare, so a full search is fine).</summary>
        private static CinematicModeTriggerBase TriggerOf(PlayerCinematicController c)
        {
            if (c == null || fTriggerController == null) return null;
            foreach (var t in Resources.FindObjectsOfTypeAll<CinematicModeTriggerBase>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (ReferenceEquals(fTriggerController.GetValue(t), c)) return t;
            }
            return null;
        }

        /// <summary>A story cutscene: started by a story goal (Marg) or tied to one.</summary>
        private static bool IsStoryTrigger(CinematicModeTriggerBase t)
        {
            if (t == null) return false;
            try
            {
                if (fTriggerType != null && Convert.ToInt32(fTriggerType.GetValue(t)) == 2) return true;   // TriggerType.StoryGoal
                return fTriggerGoal != null && !string.IsNullOrEmpty(fTriggerGoal.GetValue(t) as string);
            }
            catch { return false; }
        }

        private static bool IsBlocked(string animName)
        {
            if (string.IsNullOrEmpty(animName)) return true; // unknown animation — safer to block
            string lower = animName.ToLowerInvariant();
            for (int i = 0; i < BlockedPrefixes.Length; i++)
            {
                if (lower.StartsWith(BlockedPrefixes[i])) return true;
            }
            for (int i = 0; i < BlockedParts.Length; i++)
            {
                if (lower.Contains(BlockedParts[i])) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers: identification

        /// <summary>
        /// Build a scene-hierarchy path like "marge_intro(Clone)/PlayerCinematicController" for objects
        /// that lack a UniqueIdentifier.  Prefixed with "path:" so the receive side knows to look up
        /// by path rather than by UniqueIdentifier.
        /// </summary>
        private static string ScenePath(Component c)
        {
            if (c == null) return null;
            var parts = new System.Collections.Generic.List<string>();
            for (var t = c.transform; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return "path:" + string.Join("/", parts.ToArray());
        }

        // ------------------------------------------------------------------ local event

        private static readonly System.Collections.Generic.Dictionary<string, float> notSharedLogged = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>Say why a cinematic stayed local, once per name every 30 s, so a missing scene can be read off the log.</summary>
        private static void NotShared(string why, string name)
        {
            string key = name ?? "null";
            float last;
            if (notSharedLogged.TryGetValue(key, out last) && Time.unscaledTime - last < 30f) return;
            notSharedLogged[key] = Time.unscaledTime;
            Plugin.Log.LogInfo("Cutscene not shared (" + why + "): " + key);
        }

        private static void CinematicStartPostfix(PlayerCinematicController __instance)
        {
            if (applying || !WorldSync.CanSend) return;
            if (__instance == null) return;

            string name = SyncName(__instance);
            bool story = IsStoryTrigger(TriggerOf(__instance));
            // The game's scripted story scenes are all named "*_cin" (marge_intro_cin, sanctuary_cube_cin...). Alan's
            // sanctuary cube scene has no story-goal trigger, sits on a precursor structure and was being filtered
            // out as "personal": the other player never got it. A "_cin" scene that is not a door/dock/entry is story.
            if (!story && !string.IsNullOrEmpty(name) && name.EndsWith("_cin", StringComparison.OrdinalIgnoreCase) && !IsBlocked(name)) story = true;
            if (!story)
            {
                // Creature grabs and attacks, and picking things up, use cinematics too: those are never shared.
                if (__instance.GetComponentInParent<Creature>() != null || __instance.GetComponentInParent<Pickupable>() != null)
                {
                    NotShared("creature/pickup", name);
                    return;
                }
                if (string.IsNullOrEmpty(GetAnimName(__instance)))
                {
                    NotShared("no animation name", name);
                    return;
                }
                if (IsBlocked(name)) { NotShared("blocked name", name); return; }
                if (IsOnBaseOrVehicle(__instance)) { NotShared("on a base or vehicle", name); return; }
            }

            string id = WorldSync.IdOf(__instance);
            if (string.IsNullOrEmpty(id))
                id = ScenePath(__instance);
            if (string.IsNullOrEmpty(id)) return;

            net.SendCutscene(id, name ?? "", __instance.transform.position);
            Plugin.Log.LogInfo("Cutscene sent: " + name + (story ? " (story)" : "") + " id=" + id);
        }

        // ------------------------------------------------------------------ remote event

        public static void OnCutscene(string objectId, string animName, Vector3? where)
        {
            if (!LocalPlayerSync.InWorld || string.IsNullOrEmpty(objectId)) return;

            if (string.IsNullOrEmpty(animName)) return;
            // Also checked here, so a door / dock cutscene from an older sender is ignored too.
            if (IsBlocked(animName)) { if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Cutscene from the other player ignored (personal): " + animName); return; }
            Plugin.Log.LogInfo("Cutscene from the other player: " + animName + ".");

            // Already watching one (for example the story goal started this same cutscene here too): leave it alone.
            if (Player.main != null && Player.main.cinematicModeActive) { Plugin.Log.LogInfo("Cutscene " + animName + ": you are already in a cutscene."); return; }

            string whyNot = PullInBlocker();
            if (whyNot == null && where.HasValue)
            {
                Plugin.Instance.StartCoroutine(PullIn(objectId, animName, where.Value));
                return;
            }
            if (whyNot != null) Plugin.Log.LogInfo("Cutscene " + animName + ": not pulling you in (" + whyNot + "); playing it in the world only.");
            PlayWorldOnly(objectId, animName);
        }

        // ------------------------------------------------------------------ pull-in

        private static bool pulling;

        private static string PullInBlocker()
        {
            if (!Plugin.CutscenePullIn.Value) return "turned off in the config";
            var p = Player.main;
            if (p == null) return "no player";
            if (pulling) return "already in one";
            if (p.cinematicModeActive) return "already in a cinematic";
            if (p.isPiloting) return "piloting";
            if (p.inExosuit) return "in a Prawn suit";
            return null;
        }

        private static void PutPlayer(Vector3 pos, Quaternion rot)
        {
            var p = Player.main;
            if (p == null) return;
            p.transform.SetPositionAndRotation(pos, rot);
            var rb = p.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = pos; rb.velocity = Vector3.zero; }
        }

        /// <summary>Hold the player at a spot until the world around it has loaded (or the time runs out).</summary>
        private static System.Collections.IEnumerator HoldUntilLoaded(Vector3 pos, Quaternion rot, float maxSeconds)
        {
            // The terrain under the spot is still streaming in, so the player is held here and cannot be hurt: falling
            // through unloaded ground, a rock loading into him, or a cold snap on the way used to cost real health.
            var p = Player.main;
            var live = p != null ? p.liveMixin : null;
            bool wasInvincible = live != null && live.invincible;
            if (live != null) live.invincible = true;
            float start = Time.unscaledTime;
            float until = start + maxSeconds, earliest = start + 1f;
            bool settled = false;
            try
            {
                while (Time.unscaledTime < until)
                {
                    PutPlayer(pos, rot);
                    var lws = LargeWorldStreamer.main;
                    if (Time.unscaledTime >= earliest && (lws == null || lws.IsWorldSettled())) { settled = true; break; }
                    yield return null;
                }
                // One more moment so the physics has a loaded world to stand on before health matters again.
                for (int i = 0; i < 10; i++) { PutPlayer(pos, rot); yield return null; }
            }
            finally
            {
                if (live != null) live.invincible = wasInvincible;
                Plugin.Log.LogInfo("Cutscene travel: area " + (settled ? "loaded" : "NOT settled, went on anyway") + " after " + (Time.unscaledTime - start).ToString("F1") + " s.");
            }
        }

        private static PlayerCinematicController FindController(string objectId, string name, Vector3 near)
        {
            GameObject go = objectId.StartsWith("path:") ? GameObject.Find(objectId.Substring(5)) : WorldSync.Find(objectId);
            if (go != null)
            {
                var c = go.GetComponent<PlayerCinematicController>() ?? go.GetComponentInChildren<PlayerCinematicController>(true);
                if (c != null && SyncName(c) == name) return c;
            }
            // Objects spawned by the story (Marg's scene) get different ids on each machine: match the cinematic by its
            // name, nearest to where the other player's one is.
            PlayerCinematicController best = null; float bestD = float.MaxValue;
            foreach (var c in Resources.FindObjectsOfTypeAll<PlayerCinematicController>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || SyncName(c) != name) continue;
                float d = (c.transform.position - near).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best != null && bestD < 100f * 100f ? best : null;
        }

        /// <summary>Start it the way the game would: through its trigger when it has one (puts away the held tool, runs the
        /// trigger's start/end events), otherwise on the controller.</summary>
        private static bool StartHere(PlayerCinematicController ctrl)
        {
            var p = Player.main;
            var trig = TriggerOf(ctrl);
            if (trig != null && mTriggerStart != null)
            {
                if (!ctrl.gameObject.activeInHierarchy) ctrl.gameObject.SetActive(true);
                if (!trig.gameObject.activeInHierarchy) trig.gameObject.SetActive(true);
                object ok = mTriggerStart.Invoke(trig, new object[] { p });
                if (ok is bool && (bool)ok) return true;
                Plugin.Log.LogInfo("Cutscene: its trigger would not start it here; starting it directly.");
            }
            if (!ctrl.gameObject.activeInHierarchy) ctrl.gameObject.SetActive(true);
            ctrl.StartCinematicMode(p);
            return true;
        }

        private static System.Collections.IEnumerator PullIn(string objectId, string animName, Vector3 where)
        {
            pulling = true;
            var p = Player.main;
            Vector3 homePos = p.transform.position;
            Quaternion homeRot = p.transform.rotation;
            SubRoot homeSub = p.currentSub;
            bool moved = false;

            // 1. Go there (only if we are not already close) and let the area load.
            if ((homePos - where).sqrMagnitude > 30f * 30f)
            {
                Plugin.Log.LogInfo("Cutscene " + animName + ": taking you to it (" + Mathf.RoundToInt(Vector3.Distance(homePos, where)) + " m away).");
                if (homeSub != null) p.SetCurrentSub(null);
                moved = true;
                yield return HoldUntilLoaded(where + Vector3.up * 1.5f, homeRot, 20f);
            }

            // 2. Start the same cinematic on this player.
            var ctrl = FindController(objectId, animName, where);
            bool started = false;
            if (Player.main != null && Player.main.cinematicModeActive)
            {
                // The game started it by itself once the area loaded (the story goal is already shared).
                started = true;
                Plugin.Log.LogInfo("Cutscene " + animName + ": it started here on its own.");
            }
            else if (ctrl != null && !ctrl.cinematicModeActive && Player.main != null && !Player.main.cinematicModeActive)
            {
                applying = true;
                try { started = StartHere(ctrl); Plugin.Log.LogInfo("Cutscene " + animName + ": playing it for you too."); }
                catch (Exception e) { Plugin.Log.LogWarning("Cutscene " + animName + " could not start here: " + e.Message); }
                finally { applying = false; }
            }
            else Plugin.Log.LogWarning("Cutscene " + animName + ": " + (ctrl == null ? "not found in this world" : "already running") + "; playing it in the world only.");

            if (!started) PlayWorldOnly(objectId, animName);
            else
            {
                // 3. Wait for it to finish (it can take a moment to actually begin).
                float begin = Time.unscaledTime;
                while (Time.unscaledTime - begin < 3f && Player.main != null && !Player.main.cinematicModeActive && (ctrl == null || !ctrl.cinematicModeActive)) yield return null;
                float limit = Time.unscaledTime + 300f;
                while (Time.unscaledTime < limit && Player.main != null && ((ctrl != null && ctrl.cinematicModeActive) || Player.main.cinematicModeActive)) yield return null;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            // 4. Back to where we were.
            if (moved && Player.main != null)
            {
                Plugin.Log.LogInfo("Cutscene " + animName + ": taking you back.");
                if (homeSub != null) Player.main.SetCurrentSub(homeSub);
                yield return HoldUntilLoaded(homePos, homeRot, 20f);
            }
            pulling = false;
        }

        // ------------------------------------------------------------------ world-only fallback

        private static void PlayWorldOnly(string objectId, string animName)
        {
            applying = true;
            WorldSync.applyingRemote = true;
            try
            {
                GameObject go;
                if (objectId.StartsWith("path:"))
                {
                    go = GameObject.Find(objectId.Substring(5));
                }
                else
                {
                    go = WorldSync.Find(objectId);
                }
                if (go == null)
                {
                    Plugin.Log.LogWarning("CutsceneSync: object not found: " + objectId);
                    return;
                }

                // Play only the world-side animation — do NOT call StartCinematicMode on the local player.
                // This means the remote player sees the object animate but is not teleported or camera-locked.
                bool played = false;

                // Try PlayableDirector (timeline-based cutscenes like alien arches, frozen leviathan)
                // Use reflection to avoid compile-time dependency on UnityEngine.DirectorModule
                var directorType = Type.GetType("UnityEngine.Playables.PlayableDirector, UnityEngine.DirectorModule");
                if (directorType != null)
                {
                    var director = go.GetComponent(directorType);
                    if (director == null) director = go.GetComponentInChildren(directorType);
                    if (director != null)
                    {
                        var playMethod = directorType.GetMethod("Play", Type.EmptyTypes);
                        if (playMethod != null) { playMethod.Invoke(director, null); played = true; }
                    }
                }

                // Try Animator trigger
                var animator = go.GetComponent<Animator>();
                if (animator == null) animator = go.GetComponentInChildren<Animator>();
                if (animator != null && !string.IsNullOrEmpty(animName))
                {
                    try { animator.SetTrigger(animName); played = true; }
                    catch { }
                }

                if (played) Plugin.Log.LogInfo("Cutscene world anim played: " + objectId + " anim=" + animName);
                else Plugin.Log.LogInfo("Cutscene object found but no director/animator: " + objectId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CutsceneSync apply failed: " + e.Message);
            }
            finally
            {
                applying = false;
                WorldSync.applyingRemote = false;
            }
        }
    }
}
