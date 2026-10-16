using System;
using System.Collections;
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
    /// Where each rendered frame's time goes, from marks the frame passes through:
    /// <list type="bullet">
    /// <item>fixed: the physics steps' scripts, from TimingManager's earliest FixedUpdate stage to its latest, per step;</item>
    /// <item>physx: from a step's last script to the next step's first (or to Update): PhysX's simulation, collision
    /// callbacks, WaitForFixedUpdate coroutines;</item>
    /// <item>update: TimingManager's earliest Update stage to its latest LateUpdate stage (Update, LateUpdate,
    /// coroutines);</item>
    /// <item>render: from there to WaitForEndOfFrame: culling, cameras, OnGUI;</item>
    /// <item>present: from there to the next frame's first mark: the swap (and a VSync wait), the incremental
    /// collector's slices, Unity's early-frame work.</item>
    /// </list>
    /// byMethod: also every Update, FixedUpdate, LateUpdate and OnGUI on a MonoBehaviour (stock and every mod) and
    /// PartModule OnUpdate/OnFixedUpdate, timed exclusive of the hooked calls inside them (as AllocSurvey enumerates
    /// them), by method and by assembly; the hooks cost about a microsecond a call, which the phases include.
    /// Main thread only. Writes frame_profile.csv, one row a frame.
    /// </summary>
    public static class FrameProfile
    {
        private enum Ph { Fixed, Physx, Update, Render, Present }
        private const int NPh = 5;

        private static bool active, byMethod;
        private static int mainThread, frame = -1;
        private static long tFixedStart, tFixedEnd = -1, tUpdateStart = -1, tLateEnd = -1, tEndOfFrame = -1;
        private static int steps;
        private static readonly double[] cur = new double[NPh];
        private static readonly StringBuilder rows = new StringBuilder();
        private static readonly List<double[]> frames = new List<double[]>();   // frameMs, steps, phases..., altKm
        private static UnityEngine.GameObject host;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static readonly TimingManager.UpdateAction fixedEarly = FixedEarly, fixedLate = FixedLate, updateEarly = UpdateEarly, lateLate = LateLate;

        /// <summary>Starts; returns what it hooked (byMethod: how many methods).</summary>
        public static string Start(bool byMethods)
        {
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            byMethod = byMethods;
            rows.Clear();
            rows.Append("frame,realSec,altKm,timeScale,frameMs,steps,fixedMs,physxMs,updateMs,renderMs,presentMs,gcs\n");
            frames.Clear();
            frame = -1;
            tFixedEnd = tUpdateStart = tLateEnd = tEndOfFrame = -1;
            Array.Clear(cur, 0, NPh);
            steps = 0;
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, fixedEarly);
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, fixedLate);
            TimingManager.UpdateAdd(TimingManager.TimingStage.ObscenelyEarly, updateEarly);
            TimingManager.LateUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, lateLate);
            host = new UnityEngine.GameObject("KspTest.FrameProfile");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<EndOfFrameMark>();
            active = true;
            return byMethod ? HookMethods() + " methods" : "phases";
        }

        private class EndOfFrameMark : UnityEngine.MonoBehaviour
        {
            private IEnumerator Start()
            {
                var w = new UnityEngine.WaitForEndOfFrame();
                while (true)
                {
                    yield return w;
                    if (active && frame == UnityEngine.Time.frameCount && tLateEnd >= 0)
                    {
                        tEndOfFrame = Stopwatch.GetTimestamp();
                        cur[(int)Ph.Render] += (tEndOfFrame - tLateEnd) * MsPerTick;
                    }
                }
            }
        }

        // The first mark of a frame closes the last one: its present time runs to here.
        private static void Mark(long now)
        {
            if (UnityEngine.Time.frameCount == frame)
                return;
            if (frame >= 0 && tEndOfFrame >= 0)
                cur[(int)Ph.Present] += (now - tEndOfFrame) * MsPerTick;
            EndFrame();
            frame = UnityEngine.Time.frameCount;
            tFixedEnd = tUpdateStart = tLateEnd = tEndOfFrame = -1;
        }

        private static void FixedEarly()
        {
            if (!active) return;
            long now = Stopwatch.GetTimestamp();
            Mark(now);
            if (tFixedEnd >= 0)
                cur[(int)Ph.Physx] += (now - tFixedEnd) * MsPerTick;
            tFixedStart = now;
            ++steps;
        }

        private static void FixedLate()
        {
            if (!active) return;
            long now = Stopwatch.GetTimestamp();
            cur[(int)Ph.Fixed] += (now - tFixedStart) * MsPerTick;
            tFixedEnd = now;
        }

        private static void UpdateEarly()
        {
            if (!active) return;
            long now = Stopwatch.GetTimestamp();
            Mark(now);
            if (tFixedEnd >= 0)
                cur[(int)Ph.Physx] += (now - tFixedEnd) * MsPerTick;
            tFixedEnd = -1;
            tUpdateStart = now;
        }

        private static void LateLate()
        {
            if (!active || tUpdateStart < 0) return;
            tLateEnd = Stopwatch.GetTimestamp();
            cur[(int)Ph.Update] += (tLateEnd - tUpdateStart) * MsPerTick;
        }

        private static int curGcStart;
        private static double[] curAsm = new double[0];

        private static void EndFrame()
        {
            int gcNow = GC.CollectionCount(0);
            if (frame >= 0)
            {
                var v = FlightGlobals.ActiveVessel;
                double alt = v != null ? v.altitude / 1000.0 : double.NaN;
                double frameMs = UnityEngine.Time.unscaledDeltaTime * 1000.0;   // the frame that just ended
                var inv = CultureInfo.InvariantCulture;
                rows.Append(string.Format(inv, "{0},{1:F1},{2:F2},{3:F2},{4:F1},{5},{6:F2},{7:F2},{8:F2},{9:F2},{10:F2},{11}\n",
                    frame, UnityEngine.Time.realtimeSinceStartup, alt, UnityEngine.Time.timeScale, frameMs, steps,
                    cur[0], cur[1], cur[2], cur[3], cur[4], gcNow - curGcStart));
                frames.Add(new[] { frameMs, steps, cur[0], cur[1], cur[2], cur[3], cur[4], alt, gcNow - curGcStart });
            }
            curGcStart = gcNow;
            Array.Clear(cur, 0, NPh);
            steps = 0;
        }

        // ---- by method ----

        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, int> keyOf = new Dictionary<MethodBase, int>();
        private static readonly List<string> names = new List<string>();
        private static readonly List<int> asmOf = new List<int>();
        private static readonly List<string> asmNames = new List<string>();
        private static double[] ms = new double[0];
        private static long[] calls = new long[0];
        private static readonly Stack<int> stack = new Stack<int>();
        private static long last;

        private static readonly string[] Unity = { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };
        private static readonly string[] Module = { "OnUpdate", "OnFixedUpdate" };

        private static int HookMethods()
        {
            harmony = harmony ?? new Harmony("KspTest.FrameProfile");
            var asms = new List<Assembly> { typeof(Part).Assembly };
            foreach (var la in AssemblyLoader.loadedAssemblies)
                if (!asms.Contains(la.assembly))
                    asms.Add(la.assembly);
            var prefix = new HarmonyMethod(typeof(FrameProfile), nameof(Before));
            var postfix = new HarmonyMethod(typeof(FrameProfile), nameof(After));
            foreach (Assembly asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                string asmName = asm.GetName().Name;
                foreach (Type t in types)
                {
                    if (t.IsGenericTypeDefinition || t.IsAbstract && t.IsSealed || t == typeof(EndOfFrameMark))
                        continue;
                    bool behaviour, module;
                    try
                    {
                        behaviour = typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t);
                        module = typeof(PartModule).IsAssignableFrom(t);
                    }
                    catch (Exception) { continue; }
                    if (!behaviour)
                        continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetParameters().Length != 0 || keyOf.ContainsKey(m))
                            continue;
                        if (!Unity.Contains(m.Name) && !(module && Module.Contains(m.Name)))
                            continue;
                        try
                        {
                            if (m.GetMethodBody() == null)
                                continue;
                            int a = asmNames.IndexOf(asmName);
                            if (a < 0) { a = asmNames.Count; asmNames.Add(asmName); }
                            keyOf[m] = names.Count;
                            names.Add(asmName + "/" + t.Name + "." + m.Name);
                            asmOf.Add(a);
                            harmony.Patch(m, prefix, postfix);
                        }
                        catch (Exception)
                        {
                            keyOf.Remove(m);
                            names.RemoveAt(names.Count - 1);
                            asmOf.RemoveAt(asmOf.Count - 1);
                        }
                    }
                }
            }
            ms = new double[names.Count];
            calls = new long[names.Count];
            curAsm = new double[asmNames.Count];
            stack.Clear();
            return names.Count;
        }

        // Exclusive time: on entering a hooked call its caller's time so far is the caller's; on leaving, the callee's.
        private static void Before(MethodBase __originalMethod)
        {
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return;
            if (!keyOf.TryGetValue(__originalMethod, out int key))
                return;
            long now = Stopwatch.GetTimestamp();
            if (stack.Count > 0)
                Charge(stack.Peek(), now);
            stack.Push(key);
            last = now;
        }

        private static void After(MethodBase __originalMethod)
        {
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread || stack.Count == 0)
                return;
            if (!keyOf.TryGetValue(__originalMethod, out int key) || stack.Peek() != key)
                return;
            long now = Stopwatch.GetTimestamp();
            Charge(key, now);
            ++calls[key];
            stack.Pop();
            last = now;
        }

        private static void Charge(int key, long now)
        {
            double d = (now - last) * MsPerTick;
            ms[key] += d;
            curAsm[asmOf[key]] += d;
        }

        // ---- results ----

        /// <summary>Stops; records the means per frame under prefix and writes frame_profile.csv.</summary>
        public static void Stop(TestContext ctx, string prefix)
        {
            if (!active)
                return;
            Mark(Stopwatch.GetTimestamp());
            active = false;
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.ObscenelyEarly, fixedEarly);
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.BetterLateThanNever, fixedLate);
            TimingManager.UpdateRemove(TimingManager.TimingStage.ObscenelyEarly, updateEarly);
            TimingManager.LateUpdateRemove(TimingManager.TimingStage.BetterLateThanNever, lateLate);
            if (host != null)
                UnityEngine.Object.Destroy(host);
            if (harmony != null && byMethod)
                harmony.UnpatchAll(harmony.Id);
            Directory.CreateDirectory(ctx.ResultsDir);
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "frame_profile.csv"), rows.ToString());
            int n = frames.Count;
            ctx.Record(prefix + "profileFrames", n);
            if (n == 0)
                return;
            var inv = CultureInfo.InvariantCulture;
            double Mean(int i) => frames.Average(f => f[i]);
            double totalSteps = frames.Sum(f => f[1]);
            string[] ph = { "fixed", "physx", "update", "render", "present" };
            ctx.Record(prefix + "profileFrameMsMean", Math.Round(Mean(0), 2));
            ctx.Record(prefix + "profileStepsPerFrame", Math.Round(Mean(1), 2));
            for (int i = 0; i < NPh; ++i)
                ctx.Record(prefix + "profile_" + ph[i] + "MsPerFrame", Math.Round(Mean(2 + i), 2));
            if (totalSteps > 0)
            {
                ctx.Record(prefix + "profile_fixedMsPerStep", Math.Round(frames.Sum(f => f[2]) / totalSteps, 3));
                ctx.Record(prefix + "profile_physxMsPerStep", Math.Round(frames.Sum(f => f[3]) / totalSteps, 3));
            }
            // by altitude band, the same split
            foreach (var band in new[] { (999.0, 70.0), (70.0, 30.0), (30.0, 10.0), (10.0, -1.0) })
            {
                var b = frames.Where(f => f[7] < band.Item1 && f[7] >= band.Item2).ToList();
                if (b.Count == 0)
                    continue;
                var sb = new StringBuilder();
                sb.AppendFormat(inv, "{0} frames, {1:F1} ms, {2:F1} steps", b.Count, b.Average(f => f[0]), b.Average(f => f[1]));
                for (int i = 0; i < NPh; ++i)
                    sb.AppendFormat(inv, ", {0} {1:F1}", ph[i], b.Average(f => f[2 + i]));
                ctx.Record(prefix + "profileBand_" + (band.Item2 < 0 ? 0 : band.Item2).ToString("F0", inv) + "-" + band.Item1.ToString("F0", inv), sb.ToString());
            }
            if (!byMethod)
                return;
            double nf = n;
            ctx.Record(prefix + "profileHookedCallsPerFrame", Math.Round(calls.Sum() / nf, 0));
            var byAsm = new double[asmNames.Count];
            for (int i = 0; i < names.Count; ++i)
                byAsm[asmOf[i]] += ms[i];
            foreach (int a in Enumerable.Range(0, asmNames.Count).OrderByDescending(a => byAsm[a]).Take(20))
                ctx.Record(prefix + "profileAsm_" + asmNames[a], Math.Round(byAsm[a] / nf, 3));
            foreach (int i in Enumerable.Range(0, names.Count).OrderByDescending(i => ms[i]).Take(40))
                ctx.Record(prefix + "profileMethod_" + names[i], string.Format(inv, "{0:F3} ms/frame, {1:F0} calls/frame", ms[i] / nf, calls[i] / nf));
        }
    }
}
