using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine.Scripting;

namespace KspTest
{
    /// <summary>
    /// What the garbage collector does during a test: its mode (incremental or not, the time slice per frame), a row
    /// for every frame a collection finished in (gc_frames.csv, from FrameSpikes' per-frame sampling), and the
    /// explicit GC.Collect calls with their callers.
    /// </summary>
    public static class GcWatch
    {
        private static Harmony harmony;
        private static bool active;
        private static readonly StringBuilder rows = new StringBuilder();
        private static int explicitCalls;
        private static readonly Dictionary<string, int> callers = new Dictionary<string, int>();

        public static void Start(TestContext ctx, string prefix, double sliceMs)
        {
            ctx.Record(prefix + "gcIncremental", GarbageCollector.isIncremental);
            ctx.Record(prefix + "gcMode", GarbageCollector.GCMode.ToString());
            ctx.Record(prefix + "gcSliceMsDefault", GarbageCollector.incrementalTimeSliceNanoseconds / 1e6);
            if (!double.IsNaN(sliceMs))
            {
                GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(sliceMs * 1e6);
                ctx.Record(prefix + "gcSliceMs", GarbageCollector.incrementalTimeSliceNanoseconds / 1e6);
            }
            if (harmony == null)
            {
                harmony = new Harmony("KspTest.GcWatch");
                foreach (MethodInfo m in typeof(GC).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Collect"))
                {
                    try { harmony.Patch(m, new HarmonyMethod(typeof(GcWatch), nameof(BeforeCollect))); }
                    catch (Exception e) { Log.Info($"GcWatch: cannot hook {m}: {e.Message}"); }
                }
            }
            rows.Clear();
            rows.Append("realSec,timeScale,frameMs,heapMB,usedBeforeMB,usedAfterMB,explicitSoFar\n");
            explicitCalls = 0;
            callers.Clear();
            active = true;
        }

        private static void BeforeCollect()
        {
            if (!active)
                return;
            ++explicitCalls;
            // the first frame outside GC and Harmony's stub
            string caller = new StackTrace(2, false).GetFrames()?.Select(f => f.GetMethod())
                .Where(m => m != null && m.DeclaringType != typeof(GC))
                .Select(m => m.DeclaringType?.Assembly.GetName().Name + "/" + m.DeclaringType?.Name + "." + m.Name)
                .FirstOrDefault() ?? "?";
            callers.TryGetValue(caller, out int n);
            callers[caller] = n + 1;
        }

        /// <summary>From FrameSpikes: a frame a collection finished in; heap figures in bytes.</summary>
        public static void CollectionFrame(float frameMs, long usedBefore, long usedAfter)
        {
            if (!active)
                return;
            rows.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F2},{2:F1},{3:F0},{4:F0},{5:F0},{6}\n",
                UnityEngine.Time.realtimeSinceStartup, UnityEngine.Time.timeScale, frameMs,
                UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576.0, usedBefore / 1048576.0, usedAfter / 1048576.0,
                explicitCalls));
        }

        public static void Stop(TestContext ctx, string prefix)
        {
            if (!active)
                return;
            active = false;
            ctx.Record(prefix + "gcExplicitCalls", explicitCalls);
            foreach (var kv in callers.OrderByDescending(kv => kv.Value).Take(10))
                ctx.Record(prefix + "gcExplicitFrom_" + kv.Key, kv.Value);
            ctx.Record(prefix + "gcHeapMB", Math.Round(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576.0));
            Directory.CreateDirectory(ctx.ResultsDir);
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "gc_frames.csv"), rows.ToString());
        }
    }
}
