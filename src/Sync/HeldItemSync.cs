using System.Collections;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// What each player is holding (scanner, knife, builder, flashlight, seaglide, a fish...). The local player's held
    /// tech type is broadcast whenever it changes, and every player's avatar carries a display-only copy of that
    /// prefab in its right hand.
    /// </summary>
    public static class HeldItemSync
    {
        private static SteamNet net;
        private static TechType lastSent = TechType.None;
        private static float nextRefresh;

        public static void Install(SteamNet steamNet) { net = steamNet; }

        public static void Update()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld) { lastSent = TechType.None; return; }

            TechType held = TechType.None;
            try
            {
                var inv = Inventory.main;
                var tool = inv != null ? inv.GetHeldTool() : null;
                if (tool != null && tool.pickupable != null) held = tool.pickupable.GetTechType();
            }
            catch { }

            // Re-announce now and then so someone who joined after we picked it up still sees it.
            bool refresh = Time.unscaledTime >= nextRefresh;
            if (held == lastSent && !refresh) return;
            nextRefresh = Time.unscaledTime + 5f;
            lastSent = held;
            net.SendHeldItem((int)held);
        }

        // ------------------------------------------------------------------ the model in a remote player's hand

        /// <summary>Builds a display-only copy of an item and parents it to the avatar's tool attachment point.</summary>
        public static IEnumerator SpawnHeldModel(TechType tt, Transform attach, TaskResult<GameObject> result)
        {
            var spawned = new TaskResult<GameObject>();
            yield return CraftData.InstantiateFromPrefabAsync(tt, spawned, false);
            var go = spawned.Get();
            if (go == null) yield break;
            if (attach == null) { Object.Destroy(go); yield break; }

            go.SetActive(false);
            Strip(go);
            go.transform.SetParent(attach, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.SetActive(true);
            result.Set(go);
        }

        /// <summary>Leave only what draws: no physics, no pickup logic, no identity of its own.</summary>
        private static void Strip(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }

            var behaviours = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = behaviours.Length - 1; i >= 0; i--)
            {
                var b = behaviours[i];
                if (b == null) continue;
                if (b is SkyApplier) { b.enabled = true; continue; }   // keep it lit like the rest of the avatar
                Object.Destroy(b);
            }

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.gameObject.layer = 0;
            }
        }
    }
}
