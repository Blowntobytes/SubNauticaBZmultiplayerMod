using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BZMultiplayer.VR
{
    /// <summary>
    /// Sprinting, via a postfix on GameInput.GetIsRunning (read by the player motor every frame).
    ///
    ///  - Auto sprint (Options > Multiplayer, top): always sprint; hold the game's Sprint button to walk instead.
    ///  - VR walking: SubmersedVR's controllers count as a gamepad, and the game makes a gamepad sprint whenever the
    ///    stick is past 90%, so a VR player at full stick always sprinted. In VR (game run mode "hold to run") only
    ///    the Sprint button sprints now, or full stick if VR/FullStickSprint is turned on.
    ///  - VR speeds (land), set separately so each can be tuned without changing the other:
    ///      walk   = game walk x VR/WalkSpeedMultiplier   (motor max speeds)
    ///      sprint = game sprint x VR/SprintSpeedMultiplier (the motor's sprint modifier is rescaled to compensate
    ///               for the walk multiplier, so moving the walk slider never changes sprint speed)
    ///    Swimming uses VR/SwimSpeedMultiplier. Flat-screen players are untouched.
    /// </summary>
    public static class SprintAssist
    {
        private const float StickSprintThreshold = 0.85f;
        private const float LogInterval = 20f;
        private static float nextLog;

        // ---- VR speed multiplier
        private class MotorBase { public float fwd, back, strafe, vert, fwdSprint, strafeSprint, waterAccel, lastSetFwd = -1f; }
        private static readonly Dictionary<int, MotorBase> bases = new Dictionary<int, MotorBase>();
        private static Vector3 lastPos;
        private static bool havePos;
        private static float measuredSpeed;

        /// <summary>Called from Plugin.Update: applies the VR speed multipliers and measures ground speed for the log.</summary>
        public static void Tick()
        {
            var player = Player.main;
            if (player == null || player.playerController == null) { havePos = false; return; }
            bool vr = VRBridge.IsVRActive;
            float test = Mathf.Clamp(Plugin.TestSpeed.Value, 1f, 10f); // playtesting boost, everyone
            float walk = (vr ? Mathf.Clamp(Plugin.VRWalkSpeed.Value, 0.5f, 10f) : 1f) * test;
            float sprint = (vr ? Mathf.Clamp(Plugin.VRSprintSpeed.Value, 0.5f, 10f) : 1f) * test;
            float swim = (vr ? Mathf.Clamp(Plugin.VRSwimSpeed.Value, 0.5f, 10f) : 1f) * test;
            Scale(player.playerController.groundController, walk, sprint);
            Scale(player.playerController.underWaterController, swim, swim);

            Vector3 pos = player.transform.position;
            float dt = Time.deltaTime;
            if (havePos && dt > 0.0001f)
            {
                float s = new Vector2(pos.x - lastPos.x, pos.z - lastPos.z).magnitude / dt;
                if (s < 50f) measuredSpeed = Mathf.Lerp(measuredSpeed, s, 0.1f);
            }
            lastPos = pos; havePos = true;
        }

        private static void Scale(PlayerMotor m, float mult, float sprintMult)
        {
            if (m == null) return;
            MotorBase b;
            int id = m.GetInstanceID();
            if (!bases.TryGetValue(id, out b)) { b = new MotorBase(); Capture(b, m); bases[id] = b; }
            // The game changed the speeds itself since we last set them: take its values as the new base.
            else if (b.lastSetFwd >= 0f && Mathf.Abs(m.forwardMaxSpeed - b.lastSetFwd) > 0.0001f) Capture(b, m);

            float want = b.fwd * mult;
            float sprintScale = sprintMult / mult;   // sprint speed = max speed x modifier
            float wantSprint = b.fwdSprint * sprintScale;
            if (Mathf.Abs(m.forwardMaxSpeed - want) < 0.0001f && Mathf.Abs(m.forwardSprintModifier - wantSprint) < 0.0001f) { b.lastSetFwd = want; return; }
            m.forwardSprintModifier = wantSprint;
            m.strafeSprintModifier = b.strafeSprint * sprintScale;
            m.forwardMaxSpeed = want;
            m.backwardMaxSpeed = b.back * mult;
            m.strafeMaxSpeed = b.strafe * mult;
            m.verticalMaxSpeed = b.vert * mult;
            // Underwater the player only ever reaches accel / drag, so a higher ceiling alone changes nothing: the
            // acceleration has to grow with it.
            if (m is UnderwaterMotor) m.waterAcceleration = b.waterAccel * mult;
            b.lastSetFwd = want;
            Plugin.Log.LogInfo("Movement on " + m.GetType().Name + ": " + (m is UnderwaterMotor ? "swim" : "walk") + " x" + mult.ToString("F2") + " (" + want.ToString("F2") + " m/s), sprint x" + sprintMult.ToString("F2") + " (" + (want * wantSprint).ToString("F2") + " m/s).");
        }

        private static void Capture(MotorBase b, PlayerMotor m)
        {
            b.fwd = m.forwardMaxSpeed; b.back = m.backwardMaxSpeed; b.strafe = m.strafeMaxSpeed; b.vert = m.verticalMaxSpeed;
            if (b.waterAccel <= 0f) b.waterAccel = m.waterAcceleration;
            // The game only sets the sprint modifiers once, so keep the first values we saw.
            if (b.fwdSprint <= 0f) { b.fwdSprint = m.forwardSprintModifier; b.strafeSprint = m.strafeSprintModifier; }
            b.lastSetFwd = -1f;
        }

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(GameInput), "GetIsRunning");
            if (target == null) { Plugin.Log.LogWarning("Sprint: GameInput.GetIsRunning not found; auto sprint and VR stick sprint disabled."); return; }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(SprintAssist), "Postfix"));
            Plugin.Log.LogInfo("Sprint assist installed (auto sprint " + (Plugin.AutoSprint.Value ? "on" : "off") + ", VR stick sprint " + (Plugin.VRStickSprint.Value ? "on" : "off") + ").");
        }

        private static void Postfix(ref bool __result)
        {
            if (Plugin.AutoSprint.Value)
            {
                // Sprint unless the player holds Sprint, which walks instead.
                __result = !GameInput.GetButtonHeld(GameInput.Button.Sprint);
                LogVR(__result);
                return;
            }
            if (VRBridge.IsVRActive && GameInput.GetRunMode() == 0)
            {
                // SubmersedVR's controllers count as a gamepad, and on a gamepad the game sprints whenever the stick is
                // past 90% - so a VR player could never walk at full stick. In VR only the Sprint button (or the
                // optional full-stick setting) sprints.
                bool sprint = GameInput.GetButtonHeld(GameInput.Button.Sprint);
                if (!sprint && Plugin.VRStickSprint.Value)
                {
                    Vector3 d = GameInput.GetMoveDirection();
                    sprint = new Vector2(d.x, d.z).magnitude >= StickSprintThreshold;
                }
                __result = sprint;
            }
            LogVR(__result);
        }

        /// <summary>Occasional VR movement line in the log, so speed problems can be checked after a session.</summary>
        private static void LogVR(bool running)
        {
            if (Time.unscaledTime < nextLog || Player.main == null || !VRBridge.IsVRActive) return;
            Vector3 d = GameInput.GetMoveDirection();
            float stick = new Vector2(d.x, d.z).magnitude;
            if (stick < 0.1f) return;
            nextLog = Time.unscaledTime + LogInterval;
            float speed = measuredSpeed;
            Plugin.Log.LogInfo("VR movement: stick " + stick.ToString("F2") + ", sprinting " + running + ", speed " + speed.ToString("F1") + " m/s" + (Player.main.IsUnderwater() ? " (swimming)" : ""));
        }
    }
}
