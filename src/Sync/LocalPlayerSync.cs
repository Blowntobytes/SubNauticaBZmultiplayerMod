using UnityEngine;
using BZMultiplayer.Net;
using BZMultiplayer.VR;

namespace BZMultiplayer.Sync
{
    /// <summary>Samples the local player's body/head/hands and ships a PlayerPose at SendRate Hz.</summary>
    public sealed class LocalPlayerSync
    {
        private readonly SteamNet net;
        private float nextSend;

        public LocalPlayerSync(SteamNet net) { this.net = net; }

        public static bool InWorld
        {
            get { return Player.main != null && Player.mainObject != null && Player.mainObject.activeInHierarchy; }
        }

        public void Update()
        {
            if (!net.IsInSession || !InWorld) return;
            float now = Time.unscaledTime;
            if (now < nextSend) return;
            int rate = Mathf.Clamp(Plugin.SendRate.Value, 5, 60);
            nextSend = now + 1f / rate;

            var pose = new PlayerPose();
            pose.SteamId = net.SelfId;
            pose.Time = now;
            Sample(ref pose);
            net.SendPose(ref pose);
        }

        private static void Sample(ref PlayerPose p)
        {
            var player = Player.main;
            var body = player.transform;
            p.BodyPos = body.position;
            p.BodyRot = body.rotation; // replaced below with head yaw once we know where the head is
            if (player.IsUnderwater()) p.Flags |= PoseFlags.Underwater;
            else if (player.IsSwimming()) p.Flags |= PoseFlags.Swimming;
            if (player.currentMountedVehicle != null) p.Flags |= PoseFlags.InVehicle;

            Transform head, left, right;
            if (VRBridge.TryGetRig(out head, out left, out right))
            {
                p.Flags |= PoseFlags.VR;
                p.HeadPos = head.position;
                p.HeadRot = head.rotation;
                if (left != null && right != null)
                {
                    p.Flags |= PoseFlags.HasHands;
                    p.HandLPos = left.position; p.HandLRot = left.rotation;
                    p.HandRPos = right.position; p.HandRRot = right.rotation;
                }
            }
            else
            {
                Transform cam = null;
                if (SNCameraRoot.main != null) cam = SNCameraRoot.main.transform;
                else if (Camera.main != null) cam = Camera.main.transform;
                if (cam != null) { p.HeadPos = cam.position; p.HeadRot = cam.rotation; }
                else { p.HeadPos = body.position + Vector3.up * 0.7f; p.HeadRot = body.rotation; }
            }

            // Body facing = head yaw. In VR the Player transform does not turn with the headset (SubmersedVR turns
            // the child body instead), and on flat screen the two agree anyway.
            Vector3 fwd = p.HeadRot * Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f) p.BodyRot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }
    }
}
