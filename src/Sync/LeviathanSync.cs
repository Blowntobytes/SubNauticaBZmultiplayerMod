using System.Collections.Generic;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Leviathans, first version (test24): the host's leviathans are the leviathans.
    ///
    /// Every PC runs its own creature AI, so the same Chelicerate or Shadow Leviathan swam somewhere different for
    /// each player. The host sends where each leviathan it has loaded is, 5 times a second. A joiner that has the
    /// same leviathan loaded (same id from the shared save) stops simulating its body - its rigidbody goes
    /// kinematic - and glides it to the host's position. When the host stops reporting one (it swam out of the
    /// host's range, or died there), it is handed back to the joiner's own AI after a moment.
    ///
    /// Not yet: attacks and damage are still each player's own, and leviathans that spawned after the world was
    /// shared have different ids on each PC. Both are logged so we can see how often that happens.
    /// </summary>
    public static class LeviathanSync
    {
        private static SteamNet net;
        private static float nextSend, nextScan, nextReport;
        private static readonly List<Creature> hostList = new List<Creature>();

        private class Follow
        {
            public Creature C; public Rigidbody Rb; public bool WasKinematic;
            public Vector3 Pos; public Quaternion Rot; public float Last; public bool Held;
        }
        private static readonly Dictionary<string, Follow> follows = new Dictionary<string, Follow>();
        private static readonly Dictionary<string, Creature> localById = new Dictionary<string, Creature>();
        private static readonly HashSet<string> unmatchedLogged = new HashSet<string>();
        private static int received, matched;

        public static void Install(SteamNet steamNet) { net = steamNet; }

        public static bool IsLeviathan(Creature c)
        {
            return c is Chelicerate || c is ShadowLeviathan || c is VoidLeviathan || c is Leviathan;
        }

        private static string IdOf(Component c)
        {
            var u = c != null ? c.GetComponent<UniqueIdentifier>() : null;
            return u != null ? u.Id : null;
        }

        public static void Update()
        {
            if (net == null || !net.IsInSession || !LocalPlayerSync.InWorld) { ReleaseAll("left the session"); return; }
            if (net.IsHost) HostUpdate(); else JoinerUpdate();
        }

        private static void Scan()
        {
            hostList.Clear(); localById.Clear();
            foreach (var c in Object.FindObjectsOfType<Creature>())
            {
                if (c == null || !IsLeviathan(c) || !c.isActiveAndEnabled) continue;
                var lm = c.liveMixin;
                if (lm != null && !lm.IsAlive()) continue;
                string id = IdOf(c);
                if (string.IsNullOrEmpty(id)) continue;
                hostList.Add(c);
                localById[id] = c;
            }
        }

        private static void HostUpdate()
        {
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; Scan(); }
            if (Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + 0.2f;
                hostList.RemoveAll(c => c == null);
                if (hostList.Count > 0) net.SendLeviathans(hostList);
            }
            if (Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 30f;
                var sb = new System.Text.StringBuilder("Leviathans loaded here (host, sending): " + hostList.Count);
                sb.Append("   | big creatures within 250 m: ").Append(NearbyCensus());
                foreach (var c in hostList) sb.Append(" | ").Append(c.GetType().Name).Append(' ').Append(IdOf(c)).Append(" at ").Append(c.transform.position.ToString("F0"));
                Plugin.Log.LogInfo(sb.ToString());
            }
        }

        public static void OnLeviathans(List<KeyValuePair<string, Pose>> list)
        {
            if (net == null || net.IsHost) return;
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; Scan(); }
            foreach (var kv in list)
            {
                received++;
                Follow f;
                if (!follows.TryGetValue(kv.Key, out f) || f.C == null)
                {
                    Creature c;
                    if (!localById.TryGetValue(kv.Key, out c) || c == null)
                    {
                        if (unmatchedLogged.Add(kv.Key)) Plugin.Log.LogInfo("Leviathan " + kv.Key + " from host at " + kv.Value.position.ToString("F0") + " is not loaded here (out of range, or a different spawn).");
                        continue;
                    }
                    f = new Follow { C = c, Rb = c.GetComponent<Rigidbody>() };
                    if (f.Rb != null) f.WasKinematic = f.Rb.isKinematic;
                    f.Held = true;
                    follows[kv.Key] = f;
                    Plugin.Log.LogInfo("Leviathan " + c.GetType().Name + " " + kv.Key + ": following the host (was " + (c.transform.position - kv.Value.position).magnitude.ToString("F0") + " m off).");
                }
                matched++;
                f.Pos = kv.Value.position; f.Rot = kv.Value.rotation; f.Last = Time.unscaledTime;
            }
        }

        private static void JoinerUpdate()
        {
            var drop = new List<string>();
            float k = Mathf.Clamp01(Time.deltaTime * 6f);
            foreach (var kv in follows)
            {
                var f = kv.Value;
                if (f.C == null) { drop.Add(kv.Key); continue; }
                if (Time.unscaledTime - f.Last > 2f) { Release(f, kv.Key, "host stopped reporting it"); drop.Add(kv.Key); continue; }
                if (f.Rb != null && !f.Rb.isKinematic) f.Rb.isKinematic = true;
                var t = f.C.transform;
                if ((t.position - f.Pos).sqrMagnitude > 400f) t.position = f.Pos;   // >20 m: snap
                else t.position = Vector3.Lerp(t.position, f.Pos, k);
                t.rotation = Quaternion.Slerp(t.rotation, f.Rot, k);
            }
            foreach (var id in drop) follows.Remove(id);
            if (Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 30f;
                Plugin.Log.LogInfo("Leviathans: following " + follows.Count + " from the host (" + matched + " of " + received + " updates matched here)   | big creatures within 250 m: " + NearbyCensus());
                matched = received = 0;
            }
        }

        /// <summary>Diagnostic (test25): which creature types are near this player, so we learn what counts as a leviathan here.</summary>
        private static string NearbyCensus()
        {
            try
            {
                var me = Player.main != null ? Player.main.transform.position : Vector3.zero;
                var counts = new SortedDictionary<string, int>();
                foreach (var c in Object.FindObjectsOfType<Creature>())
                {
                    if (c == null || (c.transform.position - me).sqrMagnitude > 250f * 250f) continue;
                    var lm = c.liveMixin;
                    if (lm != null && lm.maxHealth < 300f) continue;   // skip small fauna
                    string n = c.GetType().Name;
                    int v; counts.TryGetValue(n, out v); counts[n] = v + 1;
                }
                if (counts.Count == 0) return "none";
                var sb = new System.Text.StringBuilder();
                foreach (var kv in counts) sb.Append(kv.Key).Append(" x").Append(kv.Value).Append(' ');
                return sb.ToString().Trim();
            }
            catch (System.Exception e) { return "census failed: " + e.Message; }
        }

        private static void Release(Follow f, string id, string why)
        {
            if (f.C != null && f.Rb != null) f.Rb.isKinematic = f.WasKinematic;
            if (f.C != null) Plugin.Log.LogInfo("Leviathan " + f.C.GetType().Name + " " + id + ": back to its own AI (" + why + ").");
        }

        private static void ReleaseAll(string why)
        {
            if (follows.Count == 0) return;
            foreach (var kv in follows) Release(kv.Value, kv.Key, why);
            follows.Clear();
            unmatchedLogged.Clear();
        }
    }
}
