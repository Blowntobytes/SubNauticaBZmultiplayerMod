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

        public RemotePlayer(ulong steamId, string name, bool isVr)
        {
            SteamId = steamId;
            Name = name;
            IsVR = isVr;
        }

        /// <summary>The display-only item currently in this avatar's hand, for the alignment tuner. Null when empty.</summary>
        public Transform HeldModel { get { return heldModel != null ? heldModel.transform : null; } }

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

        public void Update()
        {
            if (!hasPose) return;
            if (!LocalPlayerSync.InWorld) { SetVisible(false); return; }

            // Decay velocity if packets stop arriving (player standing still sends identical positions anyway).
            if (Time.unscaledTime - lastReceive > 0.5f) localVelocity = Vector3.Lerp(localVelocity, Vector3.zero, Time.deltaTime * 4f);

            float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);

            if (diver == null && !diverFailed)
            {
                var d = new DiverAvatar(SteamId);
                if (d.TryBuild(Name)) { diver = d; DestroyPlaceholder(); envKnown = false; }
                else diverFailed = Player.main != null; // only give up once the player existed and cloning still failed
            }

            Transform headT;
            if (diver != null)
            {
                diver.SetVisible(true);
                diver.Apply(ref target, localVelocity, k);
                UpdateEnvironment(diver.Root != null ? diver.Root.gameObject : null, target.BodyPos);
                UpdateHeldItem();
                headT = diver.Head != null ? diver.Head : diver.Root;
                UpdatePlate(diver.Root, headT != null ? headT.position + Vector3.up * 0.35f : target.HeadPos + Vector3.up * 0.35f);
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
                if (font != null) { plateText.font = font; plate.GetComponent<MeshRenderer>().material = font.material; }
            }
            if (plate.transform.parent != parent) { plate.transform.SetParent(parent, true); plate.transform.localScale = Vector3.one; }
            if (plateText.text != Name) plateText.text = Name;
            plate.transform.position = pos;
            Transform viewer = SNCameraRoot.main != null ? SNCameraRoot.main.transform : (Camera.main != null ? Camera.main.transform : null);
            // TextMesh reads correctly when its +Z points away from the viewer (viewer looks along the text's forward).
            if (viewer != null) plate.transform.rotation = Quaternion.LookRotation(plate.transform.position - viewer.position, Vector3.up);
            plate.transform.localScale = Vector3.one; // never inherit a mirrored/scaled parent
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
            if (plate != null && plate.activeSelf != v) plate.SetActive(v);
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
