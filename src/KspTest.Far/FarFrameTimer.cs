using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// FAR's main-thread cost per physics frame in flight: the wings' FixedUpdate (FARWingAerodynamicModel and its
    /// overrides, timed once however deep the base calls go) and FARVesselAero.FixedUpdate (the body sections), summed
    /// per physics frame. Stopwatch ticks; the timing itself is the same in every FAR build, so builds compare.
    /// </summary>
    public static class FarFrameTimer
    {
        private static Harmony _harmony;
        private static int _wingDepth, _vesselDepth;
        private static double _key = double.NaN;
        private static long _wing, _vessel, _wingCalls;

        /// <summary>Per physics frame since the last Reset: wing ticks, vessel ticks, wing FixedUpdate calls.</summary>
        public static readonly List<long[]> Frames = new List<long[]>();

        public static bool EnsurePatched()
        {
            if (_harmony != null) return true;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var wingTypes = new[] { "ferram4.FARWingAerodynamicModel", "ferram4.FARControllableSurface" };
            List<MethodInfo> wings = wingTypes.Select(Reflect.FindType).Where(t => t != null)
                                              .Select(t => t.GetMethod("FixedUpdate", any)).Where(m => m != null).ToList();
            MethodInfo vessel = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero")?.GetMethod("FixedUpdate", any);
            if (wings.Count == 0 || vessel == null) return false;
            _harmony = new Harmony("KspTest.FarFrameTimer");
            foreach (MethodInfo m in wings)
                _harmony.Patch(m, new HarmonyMethod(typeof(FarFrameTimer), nameof(WingPrefix)), new HarmonyMethod(typeof(FarFrameTimer), nameof(WingPostfix)));
            _harmony.Patch(vessel, new HarmonyMethod(typeof(FarFrameTimer), nameof(VesselPrefix)), new HarmonyMethod(typeof(FarFrameTimer), nameof(VesselPostfix)));
            return true;
        }

        public static void Reset()
        {
            Frames.Clear();
            _key = double.NaN;
            _wing = _vessel = _wingCalls = 0;
        }

        public static long Frequency => Stopwatch.Frequency;

        private static void Roll()
        {
            double t = Time.fixedTime;
            if (t == _key) return;
            if (!double.IsNaN(_key)) Frames.Add(new[] { _wing, _vessel, _wingCalls });
            _key = t;
            _wing = _vessel = _wingCalls = 0;
        }

        private static void WingPrefix(out long __state) => __state = _wingDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;

        private static void WingPostfix(long __state)
        {
            --_wingDepth;
            if (__state < 0) return;
            long dt = Stopwatch.GetTimestamp() - __state;
            Roll();
            _wing += dt;
            ++_wingCalls;
        }

        private static void VesselPrefix(out long __state) => __state = _vesselDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;

        private static void VesselPostfix(long __state)
        {
            --_vesselDepth;
            if (__state < 0) return;
            long dt = Stopwatch.GetTimestamp() - __state;
            Roll();
            _vessel += dt;
        }
    }
}
