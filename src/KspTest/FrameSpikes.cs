using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace KspTest
{
    /// <summary>
    /// Trajectories' main-thread time per rendered frame, on any build: Harmony stopwatches on its scenario's Update
    /// and FixedUpdate (what it does on the main thread runs inside them), and within those on the prediction's
    /// per-frame increment, the force cache's cell fills, the aerodynamic model's construction and a background
    /// fill's context capture, where the build has them. Frames over SpikeMs are kept with their breakdown. Calls
    /// outside the scenario's Update (the harness's own PredictNow) are not counted.
    /// </summary>
    public static class FrameSpikes
    {
        public const double SpikeMs = 4.0;

        private enum Part { Update, Compute, Fill, Model, Capture, Begin, Continue, Ground }

        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, Part> Parts = new Dictionary<MethodBase, Part>();
        private static bool active;
        private static int depth, frame = -1, mainThread;
        private static readonly double[] cur = new double[8];
        private static int curFills, curCalls;
        private static double curAltKm, curWarp, curFrameMs;
        private static readonly List<float> totals = new List<float>();
        private static readonly StringBuilder rows = new StringBuilder();
        private static int spikeRows;

        /// <summary>Patches what the loaded Trajectories has; returns the names patched.</summary>
        public static string Start(bool allocHooks = false)
        {
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            harmony = harmony ?? new Harmony("KspTest.FrameSpikes");
            var patched = new List<string>();
            void Hook(string typeName, string method, Part part)
            {
                Type t = AccessTools.TypeByName(typeName);
                MethodInfo m = t == null ? null : AccessTools.DeclaredMethod(t, method);
                if (m == null || Parts.ContainsKey(m))
                    return;
                Parts[m] = part;
                harmony.Patch(m, new HarmonyMethod(typeof(FrameSpikes), nameof(Before)), new HarmonyMethod(typeof(FrameSpikes), nameof(After)));
                patched.Add(t.Name + "." + method);
            }
            Hook("Trajectories.Trajectories", "Update", Part.Update);
            Hook("Trajectories.Trajectories", "FixedUpdate", Part.Update);
            Hook("Trajectories.Trajectory", "ComputeTrajectory", Part.Compute);
            Hook("Trajectories.AeroForceCache", "ComputeCacheEntry", Part.Fill);
            Hook("Trajectories.AeroDynamicModelFactory", "GetModel", Part.Model);
            Hook("Trajectories.FARModel", "CaptureFillContext", Part.Capture);
            Hook("Trajectories.AeroDynamicModel", "BeginPrediction", Part.Begin);
            Hook("Trajectories.AeroDynamicModel", "ContinuePrediction", Part.Continue);
            Hook("Trajectories.Trajectory", "GetGroundAltitude", Part.Ground);
            // allocation attribution (allocHooks; slows the hot paths): heap growth across each call, by method
            if (allocHooks)
            {
                AllocHook("Trajectories.AeroDynamicModel", "GetForces");
                AllocHook("Trajectories.AeroForceCache", "GetForce");
                AllocHook("Trajectories.FARModel", "DensityScale");
                AllocHook("Trajectories.StockAeroUtil", "GetDensity");
                AllocHook("Trajectories.DescentProfile", "GetAngleOfAttack");
                AllocHook("Trajectories.DescentProfile", "GetBankRad");
                AllocHook("Trajectories.Trajectory", "GetGroundAltitude");
                AllocHook("Trajectories.Trajectory", "CalculateRotatedPosition");
                AllocHook("Trajectories.Trajectory", "BankForce");
                AllocHook("Trajectories.Trajectory", "RK4Step");
                AllocHook("Trajectories.Trajectory", "ComputeTrajectory");
                AllocHook("Trajectories.TargetSteer", "Update");
                AllocHook("Trajectories.SmartAssHold", "Update");
                AllocHook("Trajectories.MapOverlay", "Update");
                AllocHook("Trajectories.FlightOverlay", "Update");
                AllocHook("Trajectories.NavBallOverlay", "Update");
                AllocHook("Trajectories.AeroDynamicModel", "BeginPrediction");
                AllocHook("Trajectories.AeroDynamicModel", "ContinuePrediction");
            }
            allocBytes.Clear();
            allocCalls.Clear();
            totals.Clear();
            rows.Clear();
            rows.Append("frame,realSec,altKm,timeScale,frameMs,trajMs,computeMs,fills,fillMs,modelMs,captureMs,calls,beginMs,continueMs,groundMs,gc\n");
            spikeRows = 0;
            frame = -1;
            Array.Clear(cur, 0, cur.Length);
            curFills = curCalls = curGc = 0;
            allocUpdate = allocCompute = allocSkipped = 0;
            gameAlloc = 0;
            lastUsed = -1;
            gameGcs = trajGcs = 0;
            gcFrameMs.Clear();
            allFrameMs.Clear();
            active = true;
            return string.Join(", ", patched);
        }

        private static readonly Dictionary<MethodBase, string> allocNames = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<string, long> allocBytes = new Dictionary<string, long>(), allocCalls = new Dictionary<string, long>();

        // Every overload of typeName.method gets AllocBefore/AllocAfter (once).
        private static void AllocHook(string typeName, string method)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null)
                return;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != method || m.IsAbstract || m.ContainsGenericParameters || allocNames.ContainsKey(m))
                    continue;
                allocNames[m] = t.Name + "." + method;
                try
                {
                    harmony.Patch(m, new HarmonyMethod(typeof(AllocPatch), nameof(AllocPatch.Before)), new HarmonyMethod(typeof(AllocPatch), nameof(AllocPatch.After)));
                }
                catch (Exception) { allocNames.Remove(m); }
            }
        }

        // Its own class: Harmony shares one __state between the patches of a class, and some of these methods carry the
        // timing patches too.
        private static class AllocPatch
        {
            internal static void Before(out long __state) => __state = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

            internal static void After(MethodBase __originalMethod, long __state)
            {
                if (!active || depth <= 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                    return;
                long d = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - __state;
                if (!allocNames.TryGetValue(__originalMethod, out string name))
                    return;
                allocCalls.TryGetValue(name, out long c);
                allocCalls[name] = c + 1;
                if (d > 0)   // a collection inside makes it negative: not counted
                {
                    allocBytes.TryGetValue(name, out long bytes);
                    allocBytes[name] = bytes + d;
                }
            }
        }

        private static void Before(MethodBase __originalMethod, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return;
            if (Parts.TryGetValue(__originalMethod, out Part p) && p == Part.Update)
            {
                if (UnityEngine.Time.frameCount != frame)
                    EndFrame();
                if (depth == 0)
                {
                    gcAtUpdateStart = GC.CollectionCount(0);
                    monoAtUpdateStart = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                }
                ++depth;
            }
            else if (p == Part.Compute && depth > 0)
            {
                gcAtComputeStart = GC.CollectionCount(0);
                monoAtComputeStart = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            }
        }

        // Collections (gen 0 count) that ran inside Trajectories' updates this frame, and the count when one began.
        private static int curGc, gcAtUpdateStart, gcAtComputeStart;

        // Managed heap growth inside Trajectories' updates and inside the prediction's increments (bytes, summed over
        // the intervals no collection ran in), and how many intervals a collection spoiled.
        private static long monoAtUpdateStart, monoAtComputeStart, allocUpdate, allocCompute, allocSkipped;

        private static void After(MethodBase __originalMethod, long __state)
        {
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return;
            if (!Parts.TryGetValue(__originalMethod, out Part p))
                return;
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            if (p == Part.Update)
            {
                --depth;
                if (depth == 0)
                {
                    cur[(int)Part.Update] += ms;
                    ++curCalls;
                    int gcs = GC.CollectionCount(0) - gcAtUpdateStart;
                    curGc += gcs;
                    trajGcs += gcs;
                    if (gcs == 0)
                        allocUpdate += UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - monoAtUpdateStart;
                    else
                        ++allocSkipped;
                }
                return;
            }
            if (depth <= 0)
                return;   // not inside Trajectories' own update: the harness asking
            if (p == Part.Compute && GC.CollectionCount(0) == gcAtComputeStart)
                allocCompute += UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - monoAtComputeStart;
            cur[(int)p] += ms;
            if (p == Part.Fill)
                ++curFills;
        }

        // The whole game, sampled once a frame (at Trajectories' first update of it): heap growth from frame to frame,
        // all code, over the frames no collection ran in; every collection, with the length of the frame it ran in
        // (unscaledDeltaTime at the next frame's start); and every frame's length.
        private static long gameAlloc, lastUsed = -1;

        /// <summary> The whole game's heap growth over the frames sampled so far (MB). </summary>
        public static double GameAllocMB => gameAlloc / 1048576.0;
        private static int lastGcCount, gameGcs, trajGcs;
        private static readonly List<float> gcFrameMs = new List<float>(), allFrameMs = new List<float>();

        private static void SampleGame()
        {
            long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int gcNow = GC.CollectionCount(0);
            float frameMs = UnityEngine.Time.unscaledDeltaTime * 1000f;
            if (lastUsed >= 0)
            {
                allFrameMs.Add(frameMs);
                if (gcNow == lastGcCount)
                    gameAlloc += Math.Max(0, used - lastUsed);
                else
                {
                    gameGcs += gcNow - lastGcCount;
                    gcFrameMs.Add(frameMs);
                    GcWatch.CollectionFrame(frameMs, lastUsed, used);
                }
            }
            lastUsed = used;
            lastGcCount = gcNow;
        }

        private static void EndFrame()
        {
            SampleGame();
            if (frame >= 0 && curCalls > 0)
            {
                double total = cur[(int)Part.Update];
                totals.Add((float)total);
                if (total > SpikeMs)
                {
                    ++spikeRows;
                    var inv = CultureInfo.InvariantCulture;
                    rows.Append(string.Format(inv, "{0},{1:F1},{2:F2},{3:F2},{4:F1},{5:F2},{6:F2},{7},{8:F2},{9:F2},{10:F2},{11},{12:F2},{13:F2},{14:F2},{15}\n",
                        frame, UnityEngine.Time.realtimeSinceStartup, curAltKm, curWarp, curFrameMs, total, cur[(int)Part.Compute], curFills,
                        cur[(int)Part.Fill], cur[(int)Part.Model], cur[(int)Part.Capture], curCalls,
                        cur[(int)Part.Begin], cur[(int)Part.Continue], cur[(int)Part.Ground], curGc));
                }
            }
            frame = UnityEngine.Time.frameCount;
            Array.Clear(cur, 0, cur.Length);
            curFills = curCalls = curGc = 0;
            var v = FlightGlobals.ActiveVessel;
            curAltKm = v != null ? v.altitude / 1000.0 : double.NaN;
            curWarp = UnityEngine.Time.timeScale;
            curFrameMs = UnityEngine.Time.unscaledDeltaTime * 1000.0;
        }

        /// <summary>Stops counting, records the distribution under prefix and writes frame_spikes.csv.</summary>
        public static void Stop(TestContext ctx, string prefix)
        {
            if (!active)
                return;
            EndFrame();
            active = false;
            int n = totals.Count;
            ctx.Record(prefix + "trajFramesCounted", n);
            if (n == 0)
                return;
            float[] s = totals.ToArray();
            Array.Sort(s);
            float Pct(double q) => s[Math.Min(n - 1, (int)(q * n))];
            ctx.Record(prefix + "trajFrameMsP50", Math.Round(Pct(0.50), 2));
            ctx.Record(prefix + "trajFrameMsP95", Math.Round(Pct(0.95), 2));
            ctx.Record(prefix + "trajFrameMsP99", Math.Round(Pct(0.99), 2));
            ctx.Record(prefix + "trajFrameMsMax", Math.Round(s[n - 1], 1));
            foreach (double over in new[] { 4.0, 8.0, 16.0, 33.0, 100.0 })
                ctx.Record(prefix + "trajFramesOver" + over.ToString("F0", CultureInfo.InvariantCulture) + "ms", s.Count(x => x > over));
            ctx.Record(prefix + "trajFrameMsTotalSec", Math.Round(s.Sum(x => (double)x) / 1000.0, 1));
            ctx.Record(prefix + "trajAllocMB", Math.Round(allocUpdate / 1048576.0, 1));
            ctx.Record(prefix + "trajAllocPredictionMB", Math.Round(allocCompute / 1048576.0, 1));
            ctx.Record(prefix + "trajAllocKBPerFrame", Math.Round(allocUpdate / 1024.0 / n, 2));
            ctx.Record(prefix + "trajAllocFramesSkipped", allocSkipped);
            // Trajectories against the whole game over the same frames
            ctx.Record(prefix + "gameAllocMB", Math.Round(gameAlloc / 1048576.0, 1));
            ctx.Record(prefix + "trajAllocSharePct", gameAlloc > 0 ? Math.Round(100.0 * allocUpdate / gameAlloc, 1) : double.NaN);
            ctx.Record(prefix + "gameGcs", gameGcs);
            ctx.Record(prefix + "gcsInTrajectories", trajGcs);
            if (allFrameMs.Count > 0)
            {
                float[] fa = allFrameMs.ToArray();
                Array.Sort(fa);
                ctx.Record(prefix + "gameFrameMsP50", Math.Round(fa[fa.Length / 2], 1));
                ctx.Record(prefix + "gameFrameMsP99", Math.Round(fa[Math.Min(fa.Length - 1, (int)(0.99 * fa.Length))], 1));
            }
            if (gcFrameMs.Count > 0)
            {
                float[] gf = gcFrameMs.ToArray();
                Array.Sort(gf);
                ctx.Record(prefix + "gcFrameMsP50", Math.Round(gf[gf.Length / 2], 1));
                ctx.Record(prefix + "gcFrameMsMax", Math.Round(gf[gf.Length - 1], 1));
            }
            foreach (var kv in allocBytes.OrderByDescending(kv => kv.Value))
                ctx.Record(prefix + "alloc_" + kv.Key, $"{kv.Value / 1048576.0:F1} MB in {allocCalls[kv.Key]} calls");
            Directory.CreateDirectory(ctx.ResultsDir);
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "frame_spikes.csv"), rows.ToString());
            ctx.Record(prefix + "spikeRowsWritten", spikeRows);
        }
    }
}
