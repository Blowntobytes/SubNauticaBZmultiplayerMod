using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// A remote player's presence in the world. Uses the game's diver model (DiverAvatar) when the local player
    /// body is available to clone; otherwise a placeholder of primitives. Poses are smoothed toward the newest
    /// snapshot; velocity for the animator is derived from consecutive snapshots.
    /// </summary>
    public sealed class RemotePlayer
    {
        public readonly ulong SteamId;
        public string Name;
        public bool IsVR;

        private PlayerPose target, previous;
        private bool hasPose, hasPrevious;
        private float lastReceive = -1f;

        /// <summary>test41: the latest pose received from this player (for the joiner spawn), if one arrived recently.</summary>
        public bool TryGetLastPose(out PlayerPose pose, float maxAge)
        {
            pose = target;
            return hasPose && lastReceive >= 0f && Time.unscaledTime - lastReceive <= maxAge;
        }
        private Vector3 localVelocity;

        private DiverAvatar diver;
        private bool diverFailed;

        // placeholder
        private GameObject root, body, head, handL, handR;
        private Vector3 bodyAxis = Vector3.up;

        private GameObject plate;
        private TextMesh plateText;

        // What this player is holding, and the display-only copy of it in the avatar's hand.
        private TechType heldType = TechType.None, shownHeld = TechType.None;
        private GameObject heldModel;
        private int heldFlags;
        private bool heldPending;

        // Habitat lighting: the avatar needs the sky of the base it is inside, like the local player's body.
        private GameObject currentEnv;
        private bool envKnown;
        private float nextEnvCheck;
        private float nextEnvResend;

        public int AgeMs { get { return lastReceive < 0 ? -1 : Mathf.RoundToInt((Time.unscaledTime - lastReceive) * 1000f); } }
        /// <summary>Restart the silence clock without a packet (used while a level load keeps the game from receiving).</summary>
        public void Touch() { if (lastReceive >= 0) lastReceive = Time.unscaledTime; }
        public Vector3 Position { get { return target.BodyPos; } }
        public bool HasPose { get { return hasPose; } }

        public RemotePlayer(ulong steamId, string name, bool isVr)
        {
            SteamId = steamId;
            Name = name;
            IsVR = isVr;
        }

        /// <summary>The display-only item currently in this avatar's hand, for the alignment tuner. Null when empty.</summary>
        public Transform HeldModel { get { return heldModel != null ? heldModel.transform : null; } }

        /// <summary>The right elbow while a flat-screen avatar's arm is raised for a tool, else null.</summary>
        public Transform ArmBone { get { return diver != null ? diver.ArmBone : null; } }

        /// <summary>The avatar's left hand while it shows the PDA (for the tuner), else null.</summary>
        public Transform PdaHand { get { return diver != null ? diver.PdaHand : null; } }

        /// <summary>What this avatar is showing right now (may lag HeldType by a load).</summary>
        public TechType ShownHeld { get { return shownHeld; } }

        public void SetHeldItem(TechType tt, int flags)
        {
            heldFlags = flags;
            ApplyHeldFlags();
            if (heldType == tt) return;
            heldType = tt;
        }

        private void ApplyHeldFlags()
        {
            if (heldModel == null) return;
            var light = heldModel.GetComponent<HeldItemSync.HeldLight>();
            if (light != null) light.Set((heldFlags & HeldItemSync.LitFlag) != 0);
        }

        /// <summary>Keep the model in the avatar's hand in step with what the player is holding.</summary>
        private void UpdateHeldItem()
        {
            if (diver == null || diver.ToolAttach == null || heldPending) return;
            // The PDA (test12) lives in the avatar's own rig; no prefab to spawn.
            diver.SetPda(heldType == TechType.PDA);
            if (heldType == TechType.PDA)
            {
                if (shownHeld != TechType.PDA)
                {
                    if (heldModel != null) { UnityEngine.Object.Destroy(heldModel); heldModel = null; }
                    shownHeld = TechType.PDA;
                    Plugin.Log.LogInfo(Name + " is holding PDA");
                }
                return;
            }
            if (shownHeld == heldType && (heldModel != null || heldType == TechType.None)) return;

            if (heldModel != null) { UnityEngine.Object.Destroy(heldModel); heldModel = null; }
            shownHeld = heldType;
            if (heldType == TechType.None) return;

            heldPending = true;
            var slot = new TaskResult<GameObject>();
            var want = heldType;
            Plugin.Instance.StartCoroutine(BuildHeld(want, slot));
        }

        private System.Collections.IEnumerator BuildHeld(TechType want, TaskResult<GameObject> slot)
        {
            yield return HeldItemSync.SpawnHeldModel(want, diver != null ? diver.ToolAttach : null, slot);
            heldPending = false;
            var go = slot.Get();
            if (go == null) yield break;
            // The player may have swapped tools while the prefab was loading.
            if (want != heldType || diver == null) { UnityEngine.Object.Destroy(go); yield break; }
            if (heldModel != null) UnityEngine.Object.Destroy(heldModel);
            heldModel = go;
            ApplyHeldFlags();

            Plugin.Log.LogInfo(Name + " is holding " + want);
        }

        public void PushPose(ref PlayerPose p)
        {
            if (hasPose && p.Time < target.Time) return; // out-of-order unreliable packet
            if (hasPose)
            {
                previous = target;
                hasPrevious = true;
                float dt = p.Time - previous.Time;
                if (dt > 0.001f && dt < 1f)
                {
                    Vector3 v = (p.BodyPos - previous.BodyPos) / dt;
                    Vector3 lv = Quaternion.Inverse(p.BodyRot) * v;
                    localVelocity = Vector3.Lerp(localVelocity, lv, 0.5f);
                }
            }
            target = p;
            hasPose = true;
            lastReceive = Time.unscaledTime;
            if (p.IsVR != IsVR) IsVR = p.IsVR;
        }

        // ---------------------------------------------------------------- test31: passengers anchored to the truck
        private string anchorCachedId;
        private Transform anchorCached;
        private bool anchoredShown;
        private Vector3 lBody, lHead, lHandL, lHandR;
        private Quaternion rBody, rHead, rHandL, rHandR;
        private float nextAnchorLog;

        private bool AnchorPose(ref PlayerPose shown)
        {
            if (string.IsNullOrEmpty(target.AnchorId)) return false;
            if (anchorCachedId != target.AnchorId || anchorCached == null)
            {
                anchorCachedId = target.AnchorId;
                var go = WorldSync.Find(target.AnchorId);
                anchorCached = go != null ? go.transform : null;
                Plugin.Log.LogInfo("Passenger " + Name + ": standing in Seatruck module [" + target.AnchorId + "]" + (anchorCached != null ? ", drawn relative to it here." : ", which is not in this world here; drawn at the world position."));
            }
            if (anchorCached == null) return false;

            Quaternion inv = Quaternion.Inverse(target.AnchorRot);
            Vector3 tb = inv * (target.BodyPos - target.AnchorPos), th = inv * (target.HeadPos - target.AnchorPos);
            Vector3 tl = inv * (target.HandLPos - target.AnchorPos), tr = inv * (target.HandRPos - target.AnchorPos);
            Quaternion qb = inv * target.BodyRot, qh = inv * target.HeadRot, ql = inv * target.HandLRot, qr = inv * target.HandRRot;
            float k = anchoredShown ? 1f - Mathf.Exp(-Time.deltaTime * 14f) : 1f;
            lBody = Vector3.Lerp(lBody, tb, k); lHead = Vector3.Lerp(lHead, th, k); lHandL = Vector3.Lerp(lHandL, tl, k); lHandR = Vector3.Lerp(lHandR, tr, k);
            rBody = anchoredShown ? Quaternion.Slerp(rBody, qb, k) : qb; rHead = anchoredShown ? Quaternion.Slerp(rHead, qh, k) : qh;
            rHandL = anchoredShown ? Quaternion.Slerp(rHandL, ql, k) : ql; rHandR = anchoredShown ? Quaternion.Slerp(rHandR, qr, k) : qr;
            anchoredShown = true;

            Transform a = anchorCached;
            shown.BodyPos = a.TransformPoint(lBody); shown.BodyRot = a.rotation * rBody;
            shown.HeadPos = a.TransformPoint(lHead); shown.HeadRot = a.rotation * rHead;
            shown.HandLPos = a.TransformPoint(lHandL); shown.HandLRot = a.rotation * rHandL;
            shown.HandRPos = a.TransformPoint(lHandR); shown.HandRRot = a.rotation * rHandR;

            if (Time.unscaledTime >= nextAnchorLog)
            {
                nextAnchorLog = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo("Passenger " + Name + " anchored: local " + lBody.ToString("F2") + ", the module here is "
                    + Vector3.Distance(a.position, target.AnchorPos).ToString("0.00") + " m from where it is on their PC; avatar drawn "
                    + Vector3.Distance(shown.BodyPos, target.BodyPos).ToString("0.00") + " m from their world position.");
            }
            return true;
        }

        /// <summary>Apply the sky of whatever habitat the avatar is standing in (or the outdoor sky when it leaves).</summary>
        private void UpdateEnvironment(GameObject avatarRoot, Vector3 pos)
        {
            if (avatarRoot == null || Time.unscaledTime < nextEnvCheck) return;
            nextEnvCheck = Time.unscaledTime + 0.5f;
            var env = EnvironmentTracker.FindAt(pos);
            // Re-send even when the habitat has not changed: building or deconstructing a piece rebuilds the base,
            // and an avatar that only heard about its sky once was left unlit in there after dark.
            bool changed = !envKnown || env != currentEnv;
            if (!changed && Time.unscaledTime < nextEnvResend) return;
            nextEnvResend = Time.unscaledTime + 3f;
            currentEnv = env;
            envKnown = true;
            try { SkyEnvironmentChanged.Broadcast(avatarRoot, env); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Sky update failed: " + e.Message); }
        }

        private float nextSeatDiag;
        private float nextPassengerDiag;   // test30

        public void Update()
        {
            if (!hasPose) return;
            if (!LocalPlayerSync.InWorld) { SetVisible(false); return; }
            // While this player is in a cutscene the camera is scripted and everyone's copy of the scene puts their
            // player in the same spot, so the other avatars end up inside the view (and inside each other): hide them.
            if (Player.main != null && Player.main.cinematicModeActive) { SetVisible(false); return; }

            // Decay velocity if packets stop arriving (player standing still sends identical positions anyway).
            if (Time.unscaledTime - lastReceive > 0.5f) localVelocity = Vector3.Lerp(localVelocity, Vector3.zero, Time.deltaTime * 4f);

            float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);

            if (diver == null && !diverFailed)
            {
                var d = new DiverAvatar(SteamId);
                if (d.TryBuild(Name)) { diver = d; DestroyPlaceholder(); envKnown = false; }
                else diverFailed = Player.main != null; // only give up once the player existed and cloning still failed
            }

            // In a vehicle: sit in its seat on our copy of it (the whole pose is moved rigidly, so head and hands keep their
            // offsets), and follow it without smoothing so the avatar stays glued to the moving vehicle.
            PlayerPose shown = target;
            string seatKind;
            var seat = VehicleSync.SeatFor(SteamId, out seatKind);
            if (diver != null) diver.SetSeat(seat != null ? seatKind : null);
            if (seat != null)
            {
                Vector3 delta = seat.position - target.BodyPos;
                Quaternion turn = seat.rotation * Quaternion.Inverse(target.BodyRot);
                shown.BodyPos = seat.position; shown.BodyRot = seat.rotation;
                shown.HeadPos = seat.position + turn * (target.HeadPos - target.BodyPos); shown.HeadRot = turn * target.HeadRot;
                shown.HandLPos = seat.position + turn * (target.HandLPos - target.BodyPos); shown.HandLRot = turn * target.HandLRot;
                shown.HandRPos = seat.position + turn * (target.HandRPos - target.BodyPos); shown.HandRRot = turn * target.HandRRot;
                shown.Flags |= PoseFlags.InVehicle;
                k = 1f;
            }

            // test31: standing inside a Seatruck module on their PC: redraw the pose relative to our copy of that module,
            // smoothed in the module's own space, so the avatar moves with the truck instead of floating behind or ahead.
            if (seat == null && (target.Flags & PoseFlags.Anchored) != 0 && AnchorPose(ref shown)) k = 1f;
            else anchoredShown = false;

            Transform headT;
            if (diver != null)
            {
                // test27: SetVisible, not diver.SetVisible - the nameplate is switched off with the avatar during any
                // cutscene (a joiner now always climbs out of the drop pod, which is one) and only this brings it back.
                SetVisible(true);
                diver.Apply(ref shown, seat != null || anchoredShown ? Vector3.zero : localVelocity, k);   // test31: no world-speed extrapolation when anchored
                if (seat != null && diver.Root != null)
                {
                    // Seated: put the avatar's HEAD where the driver's head really is in that seat, not its root on the
                    // seat point. The seat point is where the game puts the player body and then bends it into the chair
                    // with its own seated animation; the avatar has no such animation, so a root on the seat point stood
                    // the model up with its feet on the seat (head 1.69 m up - 0.14.11-test3/test4 seat check). The
                    // driver's own seat check says the head sits 0.11 m above and 0.15 m behind the seat point.
                    diver.Root.rotation = seat.rotation;
                    Vector3 wantHead = seat.TransformPoint(VehicleSync.SeatHeadOffset(seatKind));
                    Transform hb = diver.HeadBone;
                    if (hb != null) diver.Root.position += wantHead - hb.position;
                    else diver.Root.position = wantHead;
                    VehicleSync.SeatDiag("Avatar " + Name, seatKind, seat, diver.Root, diver.HeadBone, diver.Hips, ref nextSeatDiag);
                }
                if (seat == null && diver.Root != null) VehicleSync.PassengerDiag("Avatar " + Name, target.BodyPos, diver.Root.position, ref nextPassengerDiag);
                UpdateEnvironment(diver.Root != null ? diver.Root.gameObject : null, shown.BodyPos);
                UpdateHeldItem();
                diver.SetArmRaise(!IsVR && heldModel != null && shownHeld != TechType.PDA);
                headT = diver.Head != null ? diver.Head : diver.Root;
                UpdatePlate(diver.Root, headT != null ? headT.position + Vector3.up * 0.35f : shown.HeadPos + Vector3.up * 0.35f);
                return;
            }

            if (root == null) BuildPlaceholder();
            SetVisible(true);

            root.transform.position = Vector3.Lerp(root.transform.position, target.BodyPos, k);
            root.transform.rotation = Quaternion.Slerp(root.transform.rotation, target.BodyRot, k);
            head.transform.position = Vector3.Lerp(head.transform.position, target.HeadPos, k);
            head.transform.rotation = Quaternion.Slerp(head.transform.rotation, target.HeadRot, k);

            bool hands = target.HasHands;
            handL.SetActive(hands);
            handR.SetActive(hands);
            if (hands)
            {
                handL.transform.position = Vector3.Lerp(handL.transform.position, target.HandLPos, k);
                handL.transform.rotation = Quaternion.Slerp(handL.transform.rotation, target.HandLRot, k);
                handR.transform.position = Vector3.Lerp(handR.transform.position, target.HandRPos, k);
                handR.transform.rotation = Quaternion.Slerp(handR.transform.rotation, target.HandRRot, k);
            }

            bool swimming = (target.Flags & PoseFlags.Underwater) != 0 && (target.Flags & PoseFlags.InVehicle) == 0;
            Vector3 axis = Vector3.up;
            if (swimming)
            {
                Vector3 fwd = head.transform.forward; fwd.y *= 0.6f;
                axis = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.up;
            }
            bodyAxis = Vector3.Slerp(bodyAxis, axis, k).normalized;
            body.transform.position = head.transform.position - bodyAxis * 0.5f;
            body.transform.rotation = Quaternion.FromToRotation(Vector3.up, bodyAxis);

            UpdatePlate(root.transform, head.transform.position + Vector3.up * 0.35f);
        }

        // ------------------------------------------------------------------ nameplate

        private float nextPlateLog;

        private void UpdatePlate(Transform parent, Vector3 pos)
        {
            if (plate == null)
            {
                plate = new GameObject("nameplate");
                plateText = plate.AddComponent<TextMesh>();
                plateText.text = Name;
                plateText.characterSize = 0.06f;
                plateText.fontSize = 48;
                plateText.anchor = TextAnchor.LowerCenter;
                plateText.alignment = TextAlignment.Center;
                plateText.color = Color.white;
                var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (font != null)
                {
                    plateText.font = font;
                    // test25: drawn after everything and without the depth test, like the long-range markers, so fog,
                    // snow, the water surface or the avatar's own helmet can't hide it up close.
                    var m = new Material(font.material);
                    m.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                    m.renderQueue = 4001;
                    plate.GetComponent<MeshRenderer>().material = m;
                }
            }
            // test26: in VR the world camera does not show it; draw it like the long-range markers, with SubmersedVR's
            // HUD camera (UI layer, its own copy of the headset's view) - unparented, placed in that camera's space.
            Transform rig = null, uiRig = null; Camera uiCam = null;
            // test27: back to the world camera in VR too (as in every build before test26); the name was never
            // missing because of the camera, it had been switched off. The HUD-camera path is kept, disabled.
            bool vrUi = false;
            Transform wantParent = vrUi ? null : parent;
            if (plate.transform.parent != wantParent) { plate.transform.SetParent(wantParent, true); plate.transform.localScale = Vector3.one; }
            int wantLayer = vrUi ? 5 : 0;
            if (plate.layer != wantLayer) { plate.layer = wantLayer; if (vrUi) Plugin.Log.LogInfo("Nameplate " + Name + ": drawn with the " + (vrUi ? "VR HUD camera '" + uiCam.name + "'" : "world camera") + "."); }
            if (plateText.text != Name) plateText.text = Name;
            Transform viewer = SNCameraRoot.main != null ? SNCameraRoot.main.transform : (Camera.main != null ? Camera.main.transform : null);
            // TextMesh reads correctly when its +Z points away from the viewer (viewer looks along the text's forward).
            Quaternion rot = viewer != null ? Quaternion.LookRotation(pos - viewer.position, Vector3.up) : plate.transform.rotation;
            if (vrUi)
            {
                plate.transform.position = uiRig.TransformPoint(rig.InverseTransformPoint(pos));
                plate.transform.rotation = uiRig.rotation * Quaternion.Inverse(rig.rotation) * rot;
            }
            else { plate.transform.position = pos; plate.transform.rotation = rot; }
            plate.transform.localScale = Vector3.one; // never inherit a mirrored/scaled parent
            if (Time.unscaledTime >= nextPlateLog && viewer != null)
            {
                nextPlateLog = Time.unscaledTime + 60f;
                var mr = plate.GetComponent<MeshRenderer>();
                Plugin.Log.LogInfo("Nameplate " + Name + ": " + (plate.transform.position - viewer.position).magnitude.ToString("F0") + " m away, active " + plate.activeInHierarchy
                    + ", renderer " + (mr != null && mr.enabled) + ", layer " + plate.layer + ", text '" + plateText.text + "'.");
            }
        }

        // ------------------------------------------------------------------ placeholder avatar

        private void BuildPlaceholder()
        {
            root = new GameObject("BZMP_Remote_" + SteamId);
            root.transform.position = target.BodyPos;
            root.transform.rotation = target.BodyRot;
            body = Primitive(PrimitiveType.Capsule, "body", new Color(0.15f, 0.55f, 0.95f), new Vector3(0.32f, 0.42f, 0.22f));
            head = Primitive(PrimitiveType.Sphere, "head", new Color(0.95f, 0.75f, 0.25f), Vector3.one * 0.26f);
            handL = Primitive(PrimitiveType.Cube, "handL", new Color(0.2f, 0.9f, 0.4f), new Vector3(0.07f, 0.04f, 0.12f));
            handR = Primitive(PrimitiveType.Cube, "handR", new Color(0.9f, 0.3f, 0.3f), new Vector3(0.07f, 0.04f, 0.12f));
            head.transform.position = target.HeadPos;
            handL.transform.position = target.HandLPos;
            handR.transform.position = target.HandRPos;
            Plugin.Log.LogInfo("Spawned placeholder avatar for " + Name);
        }

        private GameObject Primitive(PrimitiveType type, string name, Color color, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = scale;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            var rend = go.GetComponent<Renderer>();
            var shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
            if (shader != null) rend.material = new Material(shader);
            rend.material.color = color;
            return go;
        }

        private void DestroyPlaceholder()
        {
            if (plate != null) plate.transform.SetParent(null, true);
            if (root != null) Object.Destroy(root);
            root = body = head = handL = handR = null;
        }

        private void SetVisible(bool v)
        {
            if (diver != null) diver.SetVisible(v);
            if (root != null && root.activeSelf != v) root.SetActive(v);
            if (plate != null && plate.activeSelf != v) { plate.SetActive(v); Plugin.Log.LogInfo("Nameplate " + Name + (v ? " shown" : " hidden") + "."); }
        }

        public void Destroy()
        {
            if (heldModel != null) { UnityEngine.Object.Destroy(heldModel); heldModel = null; }
            if (diver != null) { diver.Destroy(); diver = null; }
            if (plate != null) { Object.Destroy(plate); plate = null; }
            DestroyPlaceholder();
        }
    }
}
