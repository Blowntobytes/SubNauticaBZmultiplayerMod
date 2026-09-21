using System;
using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// A remote player rendered with the game's own diver model: a clone of the local player's body object
    /// (Animator + FullBodyBipedIK), stripped of gameplay components. The animator is driven by the remote
    /// player's velocity/state, the head bone follows their head rotation, and for VR players the arms are
    /// solved by FinalIK toward their tracked hands.
    /// </summary>
    public sealed class DiverAvatar
    {
        private readonly ulong steamId;
        private GameObject root;      // world anchor = remote Player transform
        private GameObject body;      // cloned body
        private Animator animator;
        private FullBodyBipedIK ik;
        private Transform headBone;
        private Transform neckBone;
        private float bodyFollow;     // 0 = only the head turns (land), 1 = body lies along the swim direction
        private Vector3 swimAxis = Vector3.forward; // world direction the body points (head -> forward) while swimming
        private Quaternion headBoneOffset = Quaternion.identity;
        private Transform handL, handR;
        private Transform toolAttach;
        private Vector3 headLift;     // smoothed offset applied so the model's head meets the tracked head
        private Vector3 smoothedPos;
        private bool hasPos;

        private static readonly HashSet<Type> KeepBehaviours = new HashSet<Type>();

        public Transform Root { get { return root != null ? root.transform : null; } }
        /// <summary>Where the game hangs a held tool on this skeleton (the "attach1" bone under the right hand).</summary>
        public Transform ToolAttach { get { return toolAttach; } }
        public Transform Head { get { return headBone; } }
        public bool IsBuilt { get { return body != null; } }

        public DiverAvatar(ulong steamId) { this.steamId = steamId; }

        /// <summary>Returns false if the local player body isn't available yet (caller falls back to primitives).</summary>
        public bool TryBuild(string name)
        {
            var player = Player.main;
            if (player == null || player.armsController == null) return false;
            GameObject src = player.armsController.gameObject;
            if (src == null) return false;

            try
            {
                if (KeepBehaviours.Count == 0)
                {
                    KeepBehaviours.Add(typeof(FullBodyBipedIK));
                    KeepBehaviours.Add(typeof(SkyApplier));
                }

                root = new GameObject("BZMP_Diver_" + steamId);
                Transform pt = player.transform;
                bool fromTemplate = BodyTemplate.IsReady;
                if (fromTemplate)
                {
                    body = BodyTemplate.Spawn(root.transform);
                }
                else
                {
                    // Fallback: clone the live body (on a VR machine this may be twisted / missing hands).
                    body = UnityEngine.Object.Instantiate(src);
                    body.name = "body";
                    body.transform.SetParent(root.transform, false);
                    body.transform.localPosition = pt.InverseTransformPoint(src.transform.position);
                    body.transform.localRotation = Quaternion.Inverse(pt.rotation) * src.transform.rotation;
                    body.transform.localScale = src.transform.localScale;
                }
                body.SetActive(false);

                StripComponents(body);

                // Make every part of the suit visible to us (the local copy hides/shadow-only's several pieces).
                int layer = LayerMask.NameToLayer("Default");
                foreach (var t in body.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer < 0 ? 0 : layer;
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null) smr.updateWhenOffscreen = true;
                }

                animator = body.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.applyRootMotion = false;
                }

                ik = body.GetComponent<FullBodyBipedIK>();
                headBone = FindHeadBone(body.transform);
                toolAttach = FindBone(body.transform, "attach1") ?? FindBone(body.transform, "hand_R");
                if (headBone != null)
                    foreach (var t in headBone.GetComponentsInChildren<Transform>(true))
                        if (t != headBone && t.name.Equals("neck", StringComparison.OrdinalIgnoreCase)) { neckBone = t; break; }

                handL = new GameObject("handTargetL").transform; handL.SetParent(root.transform, false);
                handR = new GameObject("handTargetR").transform; handR.SetParent(root.transform, false);

                if (ik != null)
                {
                    ik.enabled = true;
                    ik.solver.IKPositionWeight = 1f;
                    ik.solver.leftHandEffector.target = handL;
                    ik.solver.rightHandEffector.target = handR;
                    SetHandWeights(0f);
                    if (headBone != null) ik.solver.OnPostUpdate += ApplyHeadRotation;
                }

                // Head bone orientation relative to the body's facing, in the untouched pose (root is still identity here).
                if (headBone != null) headBoneOffset = Quaternion.Inverse(root.transform.rotation) * headBone.rotation;

                body.SetActive(true);

                Plugin.Log.LogInfo("Diver avatar built for " + name + " (template " + fromTemplate + ", animator " + (animator != null) + ", ik " + (ik != null) + ", head " + (headBone != null ? headBone.name : "none") + ")");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Diver avatar build failed, falling back to placeholder: " + e);
                Destroy();
                return false;
            }
        }

        private static void StripComponents(GameObject go)
        {
            // Several passes: components with [RequireComponent] refuse to go until their dependants are gone.
            for (int pass = 0; pass < 4; pass++)
            {
                bool removedAny = false;
                var all = go.GetComponentsInChildren<Component>(true);
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    var c = all[i];
                    if (c == null) continue;
                    if (c is Transform || c is Renderer || c is MeshFilter || c is Animator) continue;
                    if (c is MonoBehaviour && KeepBehaviours.Contains(c.GetType())) continue;
                    bool kill = c is MonoBehaviour || c is Collider || c is Rigidbody || c is Joint
                                || c is AudioSource || c is Light || c is Camera;
                    if (!kill) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); removedAny = true; }
                    catch (Exception) { }
                }
                if (!removedAny) break;
            }
        }

        private static bool skeletonLogged;

        /// <summary>
        /// The diver rig is head-rooted: export_skeleton/head_rig, with neck/chest/spine/hips hanging below it.
        /// So head_rig IS the head bone; the game rotates it with the camera and the animator's land state
        /// counter-bends the spine (view_pitch) so the legs stay vertical, while the swim state lets the body trail.
        /// </summary>
        private static Transform FindBone(Transform rootT, string name)
        {
            foreach (var t in rootT.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static Transform FindHeadBone(Transform rootT)
        {
            var all = rootT.GetComponentsInChildren<Transform>(true);
            if (!skeletonLogged)
            {
                skeletonLogged = true;
                var sb = new System.Text.StringBuilder("Body skeleton:");
                foreach (var t in all) { if (t.GetComponent<Renderer>() == null) sb.Append(' ').Append(t.name); }
                Plugin.Log.LogInfo(sb.ToString());
            }
            foreach (var t in all) if (t.name.Equals("head_rig", StringComparison.OrdinalIgnoreCase)) return t;
            foreach (var t in all) if (t.name.Equals("head", StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private void SetHandWeights(float w)
        {
            if (ik == null) return;
            ik.solver.leftHandEffector.positionWeight = w;
            ik.solver.leftHandEffector.rotationWeight = w;
            ik.solver.rightHandEffector.positionWeight = w;
            ik.solver.rightHandEffector.rotationWeight = w;
        }

        private Quaternion wantedHeadRot;
        private bool hasHeadRot;

        private void ApplyHeadRotation()
        {
            if (headBone == null || !hasHeadRot) return;
            Quaternion neckBefore = neckBone != null ? neckBone.rotation : Quaternion.identity;
            headBone.rotation = wantedHeadRot * headBoneOffset;
            if (neckBone != null)
            {
                // Land: keep the animated (upright) body, only the head turns.
                // Water: take the upright pose and swing it so the body extends directly behind the look direction
                // (prone when looking level, trailing upward when looking down).
                // Body extends directly behind the swim direction; the head alone follows the gaze.
                Quaternion prone = Quaternion.FromToRotation(Vector3.down, -swimAxis) * neckBefore;
                neckBone.rotation = Quaternion.Slerp(neckBefore, prone, bodyFollow);
            }
        }

        /// <summary>Called every frame with the smoothed pose. k = smoothing factor for this frame.</summary>
        public void Apply(ref PlayerPose target, Vector3 localVelocity, float k)
        {
            if (root == null) return;
            Transform rt = root.transform;

            if (!hasPos) { smoothedPos = target.BodyPos; hasPos = true; }
            smoothedPos = Vector3.Lerp(smoothedPos, target.BodyPos, k);
            rt.rotation = Quaternion.Slerp(rt.rotation, target.BodyRot, k);

            bool underwater = (target.Flags & PoseFlags.Underwater) != 0;
            bool inVehicle = (target.Flags & PoseFlags.InVehicle) != 0;
            bodyFollow = Mathf.Lerp(bodyFollow, underwater && !inVehicle ? 1f : 0f, k * 0.5f);
            UpdateSwimAxis(ref target, localVelocity, k);

            // Head: rotation always from the remote head; for VR also lift the whole model so heads line up.
            // Headset roll would tilt the whole hanging body; keep only the look direction.
            Vector3 lookDir = target.HeadRot * Vector3.forward;
            Quaternion headNoRoll = lookDir.sqrMagnitude > 0.0001f && Mathf.Abs(Vector3.Dot(lookDir.normalized, Vector3.up)) < 0.999f
                ? Quaternion.LookRotation(lookDir, Vector3.up) : target.HeadRot;
            wantedHeadRot = Quaternion.Slerp(hasHeadRot ? wantedHeadRot : headNoRoll, headNoRoll, k);
            hasHeadRot = true;
            if (headBone != null && target.IsVR)
            {
                // Residual between where the model's head ended up last frame and where the tracked head is.
                Vector3 residual = target.HeadPos - headBone.position;
                headLift += residual * (k * 0.5f);
                headLift.x = Mathf.Clamp(headLift.x, -0.3f, 0.3f);
                headLift.z = Mathf.Clamp(headLift.z, -0.3f, 0.3f);
                headLift.y = Mathf.Clamp(headLift.y, -0.6f, 0.6f);
            }
            else headLift = Vector3.Lerp(headLift, Vector3.zero, k);
            rt.position = smoothedPos + headLift;

            // Hands: VR players get IK toward their controllers; flat players use the animation.
            bool hands = target.HasHands && ik != null;
            if (hands)
            {
                handL.position = Vector3.Lerp(handL.position, target.HandLPos, k);
                handL.rotation = Quaternion.Slerp(handL.rotation, target.HandLRot, k);
                handR.position = Vector3.Lerp(handR.position, target.HandRPos, k);
                handR.rotation = Quaternion.Slerp(handR.rotation, target.HandRRot, k);
                SetHandWeights(1f);
            }
            else SetHandWeights(0f);

            // Animator: swim/walk blend from velocity, water state from flags, look pitch from head.
            if (animator != null)
            {
                float pitch = Mathf.DeltaAngle(0f, (Quaternion.Inverse(rt.rotation) * target.HeadRot).eulerAngles.x);
                SafeAnimator.SetBool(animator, "in_water", underwater);
                SafeAnimator.SetBool(animator, "on_surface", !underwater && (target.Flags & PoseFlags.Swimming) != 0);
                SafeAnimator.SetBool(animator, "on_ground", !underwater && (target.Flags & PoseFlags.Swimming) == 0);
                SafeAnimator.SetBool(animator, "onGround", !underwater && (target.Flags & PoseFlags.Swimming) == 0);
                SafeAnimator.SetBool(animator, "moving", localVelocity.magnitude > 0.1f);
                SafeAnimator.SetFloat(animator, "move_speed", localVelocity.magnitude);
                SafeAnimator.SetFloat(animator, "move_speed_x", localVelocity.x);
                SafeAnimator.SetFloat(animator, "move_speed_y", localVelocity.y);
                SafeAnimator.SetFloat(animator, "move_speed_z", localVelocity.z);
                SafeAnimator.SetFloat(animator, "view_pitch", pitch); // positive = looking down, same as MainCameraControl.GetCameraPitch
                SafeAnimator.SetBool(animator, "in_seamoth", inVehicle);
            }
        }

        /// <summary>
        /// Swim direction: the player's world velocity when moving; when stopped, keep the last heading but let the
        /// body settle to horizontal. Yaw always tracks the body facing so a stationary diver still turns with the player.
        /// </summary>
        private void UpdateSwimAxis(ref PlayerPose target, Vector3 localVelocity, float k)
        {
            Vector3 worldVel = target.BodyRot * localVelocity;
            Vector3 flatFacing = target.BodyRot * Vector3.forward; flatFacing.y = 0f;
            if (flatFacing.sqrMagnitude < 0.0001f) flatFacing = Vector3.forward;
            flatFacing.Normalize();

            Vector3 wanted;
            if (worldVel.magnitude > 0.6f)
            {
                wanted = worldVel.normalized;
                // Don't let a gentle vertical drift stand the diver on end: bias toward the level heading.
                wanted = Vector3.Slerp(flatFacing, wanted, Mathf.Clamp01(Mathf.Abs(wanted.y) * 1.5f + 0.5f));
            }
            else
            {
                // Idle: level body along the current facing.
                wanted = flatFacing;
            }
            swimAxis = Vector3.Slerp(swimAxis, wanted, k * 0.6f).normalized;
        }

        public void SetVisible(bool v)
        {
            if (root != null && root.activeSelf != v) root.SetActive(v);
        }

        public void Destroy()
        {
            if (ik != null && headBone != null) ik.solver.OnPostUpdate -= ApplyHeadRotation;
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null; body = null; animator = null; ik = null; headBone = null; toolAttach = null;
        }
    }
}
