using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Weather (test24): the host's weather is the weather.
    ///
    /// Below Zero keeps a timeline of upcoming weather events (WeatherManager.weatherTimeline) and every PC rolls it
    /// with its own random numbers, so each player got different weather. The host sends its timeline; a joiner
    /// stops rolling its own (ExtendWeatherTimeline / Populate are skipped) and plays the host's instead.
    ///
    /// Node start times are Time.time on the sender - seconds since that game was launched, different on every PC -
    /// so they travel as offsets from "now" and are rebuilt on arrival.
    /// </summary>
    public static class WeatherSync
    {
        private static SteamNet net;
        private static float nextSend;
        private static bool haveHostTimeline;
        private static string lastSentSig, lastAppliedSig, lastLoggedEvent;

        public static void Install(Harmony harmony, SteamNet steamNet)
        {
            net = steamNet;
            Patch(harmony, "ExtendWeatherTimeline");
            var populate = AccessTools.Method(typeof(WeatherTimeline), "Populate");
            if (populate != null) harmony.Patch(populate, prefix: new HarmonyMethod(typeof(WeatherSync), "JoinerSkipPrefix"));
            else Plugin.Log.LogWarning("WeatherSync: WeatherTimeline.Populate not found.");
        }

        private static void Patch(Harmony harmony, string m)
        {
            var mi = AccessTools.Method(typeof(WeatherManager), m);
            if (mi != null) harmony.Patch(mi, prefix: new HarmonyMethod(typeof(WeatherSync), "JoinerSkipPrefix"));
            else Plugin.Log.LogWarning("WeatherSync: WeatherManager." + m + " not found.");
        }

        private static bool JoinerControlled { get { return net != null && net.IsInSession && !net.IsHost && haveHostTimeline; } }

        /// <summary>A joiner with the host's timeline does not roll weather of its own.</summary>
        private static bool JoinerSkipPrefix()
        {
            // test25: never leave the game with an empty timeline (its AdvanceSimulation throws every frame on one).
            if (!JoinerControlled) return true;
            var wm = WeatherManager.main;
            var nodes = wm != null && wm.weatherTimeline != null ? wm.weatherTimeline.Nodes : null;
            return nodes == null || nodes.Count == 0;
        }

        public static void Reset() { haveHostTimeline = false; lastAppliedSig = null; lastSentSig = null; }

        public static void Update()
        {
            if (net == null || !net.IsInSession) { if (haveHostTimeline) Reset(); return; }
            if (!net.IsHost || !LocalPlayerSync.InWorld) return;
            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + 5f;
            var wm = WeatherManager.main;
            if (wm == null || wm.weatherTimeline == null) return;
            var nodes = wm.weatherTimeline.Nodes;
            if (nodes == null || nodes.Count == 0) return;
            net.SendWeather(nodes, Time.time);

            // Log only when the timeline actually changes, so the log shows each new plan once.
            var sig = Signature(nodes);
            if (sig != lastSentSig) { lastSentSig = sig; Plugin.Log.LogInfo("Weather sent: " + Describe(nodes)); }
        }

        private static string Signature(List<WeatherEvent> nodes)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in nodes) sb.Append(n.eventData != null ? n.eventData.weatherId : "?").Append(Mathf.RoundToInt(n.duration)).Append(';');
            return sb.ToString();
        }

        private static string Describe(List<WeatherEvent> nodes)
        {
            var sb = new System.Text.StringBuilder();
            float now = Time.time;
            for (int i = 0; i < nodes.Count && i < 4; i++)
            {
                var n = nodes[i];
                sb.Append(i == 0 ? "" : ", ").Append(n.eventData != null ? n.eventData.weatherId : "?")
                  .Append(" (starts ").Append((n.startTime - now).ToString("F0")).Append(" s, lasts ").Append(n.duration.ToString("F0")).Append(" s)");
            }
            if (nodes.Count > 4) sb.Append(" +").Append(nodes.Count - 4).Append(" more");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ joiner side

        public struct Node
        {
            public string EventId, SetName;
            public float StartOffset, Duration;
            public float[] P; // 13 WeatherParameters values
        }

        public static void OnWeather(List<Node> nodes)
        {
            if (net == null || net.IsHost || !LocalPlayerSync.InWorld) return;   // test25: not while the world is still loading
            var wm = WeatherManager.main;
            if (wm == null || wm.weatherTimeline == null || nodes.Count == 0) return;
            try
            {
                var list = wm.weatherTimeline.Nodes;
                var built = new List<WeatherEvent>();
                float now = Time.time;
                int missing = 0;
                foreach (var n in nodes)
                {
                    WeatherEventData data; WeatherEventDataSet set;
                    Find(wm, n.EventId, n.SetName, out data, out set);
                    if (data == null) { missing++; continue; }
                    var ev = new WeatherEvent(data, set, now + n.StartOffset, n.Duration);
                    if (ev.parameters == null) ev.parameters = new WeatherParameters();
                    SetParams(ev.parameters, n.P);
                    built.Add(ev);
                }
                if (built.Count == 0) { Plugin.Log.LogWarning("Weather from host: none of " + nodes.Count + " events exist here."); return; }
                list.Clear();
                list.AddRange(built);
                bool first = !haveHostTimeline;
                haveHostTimeline = true;
                var sig = Signature(built);
                if (first || sig != lastAppliedSig)
                {
                    lastAppliedSig = sig;
                    Plugin.Log.LogInfo((first ? "Weather: now following the host. " : "Weather updated from host: ") + Describe(built)
                                       + (missing > 0 ? " (" + missing + " unknown event(s) skipped)" : ""));
                }
                var cur = wm.GetCurrentWeatherConditions();
                string curId = cur != null && cur.eventData != null ? cur.eventData.weatherId : null;
                if (curId != lastLoggedEvent) { lastLoggedEvent = curId; Plugin.Log.LogInfo("Weather now: " + (curId ?? "none")); }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Applying host weather failed: " + e.Message); }
        }

        private static void Find(WeatherManager wm, string id, string setName, out WeatherEventData data, out WeatherEventDataSet set)
        {
            data = null; set = null;
            if (wm.allWeatherEventDatas != null)
                foreach (var d in wm.allWeatherEventDatas) if (d != null && d.weatherId == id) { data = d; break; }
            if (wm.allWeatherProfiles != null)
                foreach (var p in wm.allWeatherProfiles)
                {
                    if (p == null || p.setTunings == null) continue;
                    foreach (var t in p.setTunings)
                    {
                        if (t == null || t.weatherSet == null) continue;
                        if (t.weatherSet.name == setName) set = t.weatherSet;
                        if (data == null && t.weatherSet.weatherEventData != null)
                            foreach (var d in t.weatherSet.weatherEventData) if (d != null && d.weatherId == id) data = d;
                    }
                }
        }

        public static float[] GetParams(WeatherParameters w)
        {
            if (w == null) return new float[13];
            return new[] { w.temperature, w.windSpeed, w.windDir, w.cloudCoverage, w.fogDensity, w.fogHeight, w.smokinessIntensity,
                           w.snowIntensity, w.rainIntensity, w.hailIntensity, w.lightningIntensity, w.meteorIntensity, w.auroraBorealisIntensity };
        }

        private static void SetParams(WeatherParameters w, float[] p)
        {
            if (p == null || p.Length < 13) return;
            w.temperature = p[0]; w.windSpeed = p[1]; w.windDir = p[2]; w.cloudCoverage = p[3]; w.fogDensity = p[4]; w.fogHeight = p[5];
            w.smokinessIntensity = p[6]; w.snowIntensity = p[7]; w.rainIntensity = p[8]; w.hailIntensity = p[9];
            w.lightningIntensity = p[10]; w.meteorIntensity = p[11]; w.auroraBorealisIntensity = p[12];
        }
    }
}
