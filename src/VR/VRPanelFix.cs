using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace BZMultiplayer.VR
{
    /// <summary>
    /// Tries to clear the SteamVR "starting" panel that stays in the headset after joining through a Steam invite.
    ///
    /// Safety rules, after 0.11.9 and 0.11.10 crashed the game:
    /// - Only the OpenVR binding in SteamVR.dll (the one SubmersedVR drives) is used. The game also ships an older copy
    ///   of Valve.VR.OpenVR in Assembly-CSharp-firstpass (IVRApplications_006) whose output-buffer methods take a
    ///   managed string that native code then writes into. 0.11.10 found that copy first and crashed calling them.
    /// - Only a fixed whitelist of methods is called, each looked up by its exact signature. Every one takes plain
    ///   numbers or an input string (which SteamVR.dll's wrapper copies to native memory and frees itself) and returns
    ///   a number, bool or enum. Nothing asks native code to fill a buffer; nothing reconnects, relaunches or opens
    ///   the dashboard.
    /// - Nothing runs until SteamVR reports a successful init, every call is wrapped on its own, and only one run
    ///   happens per join.
    /// - Can be switched off with the VR / PanelFix config entry.
    /// </summary>
    public static class VRPanelFix
    {
        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        // App keys SteamVR could be waiting on: Steam's own key for Below Zero, and the key the game's SteamVR
        // plugin generates for itself.
        private static readonly string[] AppKeys =
        {
            "steam.app.848450",
            "application.generated.unity.subnauticabelowzero.exe",
        };

        private const int Attempts = 10;
        private const float Interval = 2f;
        private const int VerboseAttempts = 2;

        private static bool running;
        private static bool resolved;
        private static Assembly steamVrAssembly;
        private static Type openVrType;

        /// <summary>Start one run of the fix (does nothing if one is already running or VR is off).</summary>
        public static void Schedule()
        {
            if (running) return;
            if (Plugin.VRPanelFixEnabled != null && !Plugin.VRPanelFixEnabled.Value) return;
            if (!VRBridge.IsVRActive) return;
            running = true;
            Plugin.Instance.StartCoroutine(Run());
        }

        private static IEnumerator Run()
        {
            try
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    yield return new WaitForSecondsRealtime(Interval);
                    if (!VRBridge.IsVRActive) yield break;
                    try { Attempt(attempt, attempt < VerboseAttempts); }
                    catch (Exception ex) { Plugin.Log.LogWarning("VR panel fix: attempt " + attempt + " skipped: " + ex.Message); }
                }
                Plugin.Log.LogInfo("VR panel fix: finished.");
            }
            finally { running = false; }
        }

        private static void Attempt(int attempt, bool verbose)
        {
            if (!Resolve()) return;
            if (!SteamVRInitialized())
            {
                if (verbose) Plugin.Log.LogInfo("VR panel fix: SteamVR not initialised yet, waiting.");
                return;
            }

            uint pid = (uint)Process.GetCurrentProcess().Id;
            object apps = GetStatic("Applications");
            object compositor = GetStatic("Compositor");
            object overlay = GetStatic("Overlay");

            if (verbose)
            {
                Plugin.Log.LogInfo("VR panel fix: attempt " + attempt + ", our pid " + pid
                    + ", scene focus pid " + Call(compositor, "GetCurrentSceneFocusProcess", new Type[0])
                    + ", last frame renderer pid " + Call(compositor, "GetLastFrameRenderer", new Type[0])
                    + ", can render scene " + Call(compositor, "CanRenderScene", new Type[0])
                    + ", dashboard visible " + Call(overlay, "IsDashboardVisible", new Type[0]));
            }

            foreach (string key in AppKeys)
            {
                object keyPid = Call(apps, "GetApplicationProcessId", new[] { typeof(string) }, key);
                object cancelled = Call(apps, "CancelApplicationLaunch", new[] { typeof(string) }, key);
                object identified = Call(apps, "IdentifyApplication", new[] { typeof(uint), typeof(string) }, pid, key);
                if (verbose)
                    Plugin.Log.LogInfo("VR panel fix: '" + key + "' process " + keyPid + ", CancelApplicationLaunch " + cancelled + ", IdentifyApplication " + identified);
            }
        }

        /// <summary>Find Valve.VR.OpenVR in SteamVR.dll only. Never the older copy in Assembly-CSharp-firstpass.</summary>
        private static bool Resolve()
        {
            if (resolved) return openVrType != null;
            resolved = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; } catch { continue; }
                if (!string.Equals(name, "SteamVR", StringComparison.OrdinalIgnoreCase)) continue;
                Type t = asm.GetType("Valve.VR.OpenVR", false);
                if (t == null) continue;
                steamVrAssembly = asm;
                openVrType = t;
                break;
            }
            if (openVrType == null)
                Plugin.Log.LogInfo("VR panel fix: SteamVR.dll's OpenVR binding not found; skipping (the fix never uses the game's older copy).");
            else
                Plugin.Log.LogInfo("VR panel fix: using OpenVR from " + steamVrAssembly.GetName().Name + " " + steamVrAssembly.GetName().Version + ".");
            return openVrType != null;
        }

        private static bool SteamVRInitialized()
        {
            Type steamVr = steamVrAssembly.GetType("Valve.VR.SteamVR", false);
            if (steamVr == null) return false;
            FieldInfo f = steamVr.GetField("initializedState", BindingFlags.Static | BindingFlags.Public);
            if (f == null) return false;
            object state = f.GetValue(null);
            return state != null && state.ToString() == "InitializeSuccess";
        }

        private static object GetStatic(string property)
        {
            try
            {
                PropertyInfo p = openVrType.GetProperty(property, BindingFlags.Static | BindingFlags.Public);
                return p != null ? p.GetValue(null, null) : null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("VR panel fix: OpenVR." + property + " unavailable: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Invoke one whitelisted method by exact signature. Returns a short text result, or "n/a" when the interface
        /// or method is missing or the signature is not the expected one.
        /// </summary>
        private static string Call(object target, string method, Type[] signature, params object[] args)
        {
            if (target == null) return "n/a";
            try
            {
                if (target.GetType().Assembly != steamVrAssembly) return "n/a";
                MethodInfo m = target.GetType().GetMethod(method, PublicInstance, null, signature, null);
                if (m == null) return "n/a";
                Type rt = m.ReturnType;
                if (!(rt == typeof(uint) || rt == typeof(bool) || rt.IsEnum)) return "n/a";
                object result = m.Invoke(target, args);
                return result == null ? "null" : result.ToString();
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return "error (" + inner.GetType().Name + ": " + inner.Message + ")";
            }
        }
    }
}
