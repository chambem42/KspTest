using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KSPUITest
{
    /// <summary>
    /// Counts calls into FAR's prediction (FARVesselAero.SimulateAeroPropertiesAt, or SimulateAeroProperties on a FAR
    /// without it) by the first calling method outside FAR: who else is predicting while a test measures the flight.
    /// </summary>
    public static class FarCallWatch
    {
        private static Harmony _harmony;
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public static bool EnsurePatched()
        {
            if (_harmony != null) return true;
            Type t = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
            MethodInfo target = t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                 .FirstOrDefault(m => m.Name == "SimulateAeroPropertiesAt")
                                ?? t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     .FirstOrDefault(m => m.Name == "SimulateAeroProperties");
            if (target == null) return false;
            _harmony = new Harmony("KSPUITest.FarCallWatch");
            _harmony.Patch(target, new HarmonyMethod(typeof(FarCallWatch), nameof(Prefix)));
            return true;
        }

        /// <summary>Calls are still counted, but return without predicting (their out values left at zero).</summary>
        public static bool Block;

        private static bool Prefix()
        {
            string caller = "?";
            foreach (StackFrame f in new StackTrace(1, false).GetFrames() ?? new StackFrame[0])
            {
                MethodBase m = f.GetMethod();
                string asm = m?.DeclaringType?.Assembly.GetName().Name ?? "";
                if (m == null || asm == "FerramAerospaceResearch" || asm.StartsWith("0Harmony") || asm == "HarmonySharedState" ||
                    m.DeclaringType == typeof(FarCallWatch))
                    continue;
                caller = $"{asm}:{m.DeclaringType?.Name}.{m.Name}";
                break;
            }
            Counts.TryGetValue(caller, out int n);
            Counts[caller] = n + 1;
            return !Block;
        }

        public static void Reset() => Counts.Clear();

        /// <summary>Calls since the last Reset, by caller, most first; "" when none.</summary>
        public static string Summary() =>
            string.Join("; ", Counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
    }
}
