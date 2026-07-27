using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RecNetPlugin.Patches;

// Amplitude analytics: the client ships telemetry to Amplitude, which a self-hosted setup has no use
// for (and which leaks play data off-box). Prefix every event-logging entrypoint and swallow the call
// so nothing is ever queued, batched or sent.
//
// One knob, see [Analytics] in the .cfg:
//   Disable Amplitude Analytics -> default true; skip every AmplitudeAnalyticsClient.Log* method.
//
// Target resolution: AmplitudeAnalytics.AmplitudeAnalyticsClient in RecRoom.Analytics.Runtime.dll,
// concrete (it derives from SingletonMonoBehaviour<T>, so there is no abstract-interface dispatch
// trap here — see gotcha 3 in CLAUDE.md). All five Log* names are UNobfuscated in the 20230414 build.
// They are still strings, so a rename shows up only as a HarmonyX "Could not find method" in
// LogOutput.log, not as a build error — the per-method [AMPLITUDE] blocked log line below is the real
// proof a hook is live.
//
// Why all five: blocking LogEventAsync alone was not enough — a session's room_stats/perf_stats
// events still went out, and the LogEventAsync prefix never logged at all, so that entrypoint was
// never even called. Those events are the pre-serialized batch the client parks in the
// `pending_room_stats` PlayerPref (visible in the DUID-PROBE log), which points at
// LogSerializedEventAsync rather than LogEventAsync.
[HarmonyPatch]
public static class AmplitudePatch
{
    private static readonly HashSet<string> _loggedBlocked = new();

    // Returns false to skip the original. Logs once per entrypoint so LogOutput.log shows which door
    // the client actually used.
    private static bool Block(string entrypoint)
    {
        if (_loggedBlocked.Add(entrypoint))
            Plugin.Log.LogInfo($"[AMPLITUDE] analytics disabled — blocked {entrypoint}");

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AmplitudeAnalytics.AmplitudeAnalyticsClient), "LogEventAsync")]
    private static bool LogEventAsyncPrefix() =>
        Plugin.DisableAmplitudeAnalytics.Value && Block("LogEventAsync");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AmplitudeAnalytics.AmplitudeAnalyticsClient), "LogPrevSessionEventAsync")]
    private static bool LogPrevSessionEventAsyncPrefix() =>
        Plugin.DisableAmplitudeAnalytics.Value && Block("LogPrevSessionEventAsync");

    // The likely culprit for the room_stats/perf_stats batch — takes the already-serialized
    // Dictionary<string, object> that gets parked in `pending_room_stats`.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AmplitudeAnalytics.AmplitudeAnalyticsClient), "LogSerializedEventAsync")]
    private static bool LogSerializedEventAsyncPrefix() =>
        Plugin.DisableAmplitudeAnalytics.Value && Block("LogSerializedEventAsync");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AmplitudeAnalytics.AmplitudeAnalyticsClient), "LogIdentifyAsync")]
    private static bool LogIdentifyAsyncPrefix() =>
        Plugin.DisableAmplitudeAnalytics.Value && Block("LogIdentifyAsync");

    // The odd one out: static, and it returns a promise instead of void. Skipping it with a null
    // __result would hand the caller something it will chain .Then() on, so we substitute an
    // already-resolved promise — the call looks like it succeeded instantly. If we cannot build one,
    // we let the original run rather than risk a null-deref at quit time.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AmplitudeAnalytics.AmplitudeAnalyticsClient), "LogOutOfSessionEvent")]
    private static bool LogOutOfSessionEventPrefix(ref LAHBDKNMNHN __result)
    {
        if (!Plugin.DisableAmplitudeAnalytics.Value)
            return true;

        var resolved = ResolvedPromise();
        if (resolved == null)
            return true;

        __result = resolved;
        return Block("LogOutOfSessionEvent");
    }

    private static bool _promiseResolved;
    private static MethodInfo _resolvedPromiseGetter;

    // Finds the concrete Promise class's static `Resolved` property getter without hardcoding its
    // obfuscated name. Among the static, 0-param property getters in RecRoom.Promises.Runtime that
    // return the promise interface there are exactly two: the real (obfuscated) property getter and
    // the compiler-generated `get_<Name>_k__BackingField`. The backing field may be null if the
    // property initialises lazily, so we drop it by its compiler-generated name — which the
    // obfuscator leaves alone — and keep the other one.
    private static LAHBDKNMNHN ResolvedPromise()
    {
        if (!_promiseResolved)
        {
            _promiseResolved = true;

            var candidates = typeof(LAHBDKNMNHN).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(m => m.ReturnType == typeof(LAHBDKNMNHN)
                            && m.GetParameters().Length == 0
                            && m.IsSpecialName
                            && !m.Name.EndsWith("_k__BackingField"))
                .ToList();

            if (candidates.Count == 1)
                _resolvedPromiseGetter = candidates[0];
            else
                Plugin.Log.LogWarning(
                    $"[AMPLITUDE] expected exactly one resolved-promise getter, found {candidates.Count} " +
                    "— LogOutOfSessionEvent will NOT be blocked");
        }

        if (_resolvedPromiseGetter == null)
            return null;

        return _resolvedPromiseGetter.Invoke(null, null) as LAHBDKNMNHN;
    }
}
