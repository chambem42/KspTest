using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KSPUITest
{
    /// <summary>
    /// How often WaypointManager's WaypointData.CacheWaypointData rebuilds its by-contract and by-body groups. A
    /// rebuild clears customWaypoints.waypointByContract, which bumps that list's _version, and nothing else touches
    /// the list: a call whose _version moved rebuilt. Also counts the calls that did work (lastCacheUpdate moved)
    /// against the ones the once-per-fixedTime guard turned away, and what FinePrint's waypoint list holds.
    /// </summary>
    public static class WaypointProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static Harmony harmony;
        private static Type dataType;
        private static FieldInfo fCustom, fSites, fByContract, fLastUpdate, fWaypointData, fVersion;
        private static bool active;
        public static int Calls, Worked, Rebuilt;

        /// <summary>Hooks CacheWaypointData; false when WaypointManager is not loaded.</summary>
        public static bool Start()
        {
            Assembly wpm = AssemblyLoader.loadedAssemblies.FirstOrDefault(a => a.assembly.GetName().Name == "WaypointManager")?.assembly;
            dataType = wpm?.GetType("WaypointManager.WaypointData");
            if (dataType == null)
                return false;
            fCustom = dataType.GetField("customWaypoints", All);
            fSites = dataType.GetField("siteWaypoints", All);
            fLastUpdate = dataType.GetField("lastCacheUpdate", All);
            fWaypointData = dataType.GetField("waypointData", All);
            fByContract = fCustom.FieldType.GetField("waypointByContract", All);
            fVersion = fByContract.FieldType.GetField("_version", All);
            if (harmony == null)
            {
                harmony = new Harmony("KSPUITest.WaypointProbe");
                harmony.Patch(dataType.GetMethod("CacheWaypointData", All),
                    new HarmonyMethod(typeof(WaypointProbe), nameof(Before)), new HarmonyMethod(typeof(WaypointProbe), nameof(After)));
            }
            Calls = Worked = Rebuilt = 0;
            active = true;
            return true;
        }

        public static void Stop() => active = false;

        private static int Version() => (int)fVersion.GetValue(fByContract.GetValue(fCustom.GetValue(null)));

        private static void Before(out (int version, double last) __state)
        {
            __state = active ? (Version(), (double)fLastUpdate.GetValue(null)) : (0, 0.0);
        }

        private static void After((int version, double last) __state)
        {
            if (!active)
                return;
            ++Calls;
            if ((double)fLastUpdate.GetValue(null) != __state.last)
                ++Worked;
            if (Version() != __state.version)
                ++Rebuilt;
        }

        // Who added each waypoint: the scene and the first frames outside FinePrint and Harmony, by waypoint.
        private static readonly System.Collections.Generic.Dictionary<FinePrint.Waypoint, string> addedBy =
            new System.Collections.Generic.Dictionary<FinePrint.Waypoint, string>();
        private static Harmony addHarmony;

        /// <summary>Records the caller of every FinePrint.WaypointManager.AddWaypoint from now on.</summary>
        public static void HookAdds()
        {
            if (addHarmony != null)
                return;
            addHarmony = new Harmony("KSPUITest.WaypointProbe.Adds");
            try
            {
                addHarmony.Patch(typeof(FinePrint.WaypointManager).GetMethod("AddWaypoint", All),
                    new HarmonyMethod(typeof(WaypointProbe), nameof(BeforeAdd)));
            }
            catch (Exception e) { Log.Info("WaypointProbe: cannot hook AddWaypoint: " + e.Message); }
        }

        private static void BeforeAdd(FinePrint.Waypoint waypoint)
        {
            if (waypoint == null)
                return;
            var frames = new System.Diagnostics.StackTrace(1, false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
            string stack = string.Join(" < ", frames.Select(f => f.GetMethod())
                .Where(m => m != null && m.DeclaringType != typeof(FinePrint.WaypointManager) && !(m.DeclaringType?.FullName ?? "").Contains("Harmony"))
                .Take(4).Select(m => (m.DeclaringType?.Assembly.GetName().Name ?? "?") + "/" + m.DeclaringType?.Name + "." + m.Name));
            addedBy[waypoint] = HighLogic.LoadedScene + ": " + stack;
        }

        /// <summary>Every waypoint FinePrint holds, one line each, with who added it.</summary>
        public static System.Collections.Generic.List<string> Describe()
        {
            var lines = new System.Collections.Generic.List<string>();
            var fp = FinePrint.WaypointManager.Instance();
            if (fp == null)
                return lines;
            foreach (var w in fp.Waypoints)
            {
                if (w == null)
                    continue;
                addedBy.TryGetValue(w, out string by);
                lines.Add($"'{w.name}' id={w.id} celestialName='{w.celestialName}' resolvesTo={w.celestialBody?.bodyName} " +
                          $"lat={w.latitude:F3} lon={w.longitude:F3} alt={w.altitude:F0} custom={w.isCustom} clustered={w.isClustered} " +
                          $"onSurface={w.isOnSurface} contract={(w.contractReference != null)} addedBy=[{by ?? "before the hook"}]");
            }
            return lines;
        }

        /// <summary>What FinePrint holds and what WaypointManager cached and grouped, as "name = value" pairs.</summary>
        public static string Contents()
        {
            var fp = FinePrint.WaypointManager.Instance();
            if (fp == null)
                return "no FinePrint.WaypointManager";
            var all = fp.Waypoints;
            int contract = all.Count(w => w != null && w.contractReference != null);
            int notNavigatable = all.Count(w => w != null && !w.isNavigatable);
            int sun = all.Count(w => w?.celestialBody != null && w.celestialBody.bodyName == "Sun");
            int cached = ((IDictionary)fWaypointData.GetValue(null)).Count;
            int custom = ((IList)fByContract.GetValue(fCustom.GetValue(null))).Count;
            int sites = ((IList)fByContract.GetValue(fSites.GetValue(null))).Count;
            return $"finePrint = {all.Count}, withContract = {contract}, notNavigatable = {notNavigatable}, onSun = {sun}, " +
                   $"cached = {cached}, customGroup = {custom}, siteGroup = {sites}";
        }
    }
}
