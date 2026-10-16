using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Finds what worker threads touch that they must not, while armed: FloatCurve.Evaluate (a Unity AnimationCurve
    /// underneath, whose evaluation cache is not safe to use from two threads at once - the likely native crash),
    /// CelestialBody's atmosphere methods, and anything logged as an error or exception from a thread. For each
    /// curve instance it records which threads evaluated it and whether two evaluations ever overlapped, with the
    /// calling method of the first; plus CloneGraph, which finds objects shared between per-thread copies.
    /// </summary>
    public static class ThreadCanary
    {
        private static Harmony _harmony;
        private static volatile bool _armed;
        private static int _mainThread;

        private sealed class CurveUse
        {
            public int Active;           // evaluations in progress now
            public int MaxOverlap;       // most at once
            public long Calls;
            public readonly ConcurrentDictionary<int, byte> Threads = new ConcurrentDictionary<int, byte>();
            public string Caller;        // first worker caller
        }

        private static readonly ConcurrentDictionary<object, CurveUse> Seen = new ConcurrentDictionary<object, CurveUse>(new RefEq());
        private static readonly ConcurrentDictionary<string, long> BodyCalls = new ConcurrentDictionary<string, long>();
        private static readonly ConcurrentQueue<string> Logged = new ConcurrentQueue<string>();

        public static bool EnsurePatched()
        {
            if (_harmony != null) return true;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _harmony = new Harmony("KSPUITest.ThreadCanary");
            MethodInfo eval = AccessTools.Method(typeof(FloatCurve), nameof(FloatCurve.Evaluate), new[] { typeof(float) });
            _harmony.Patch(eval, new HarmonyMethod(typeof(ThreadCanary), nameof(CurvePrefix)),
                           new HarmonyMethod(typeof(ThreadCanary), nameof(CurvePostfix)));
            foreach (string name in new[] { "GetPressure", "GetTemperature", "GetDensity", "GetSpeedOfSound", "GetFullTemperature", "GetAtmoThermalStats" })
                foreach (MethodInfo m in typeof(CelestialBody).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == name))
                    _harmony.Patch(m, new HarmonyMethod(typeof(ThreadCanary), nameof(BodyPrefix)));
            Application.logMessageReceivedThreaded += OnLog;
            return true;
        }

        public static void Arm()
        {
            Seen.Clear();
            BodyCalls.Clear();
            while (Logged.TryDequeue(out _)) { }
            _armed = true;
        }

        public static void Disarm() => _armed = false;

        /// <summary>Arms again without clearing what was recorded.</summary>
        public static void Resume() => _armed = true;

        private static bool OnWorker => Thread.CurrentThread.ManagedThreadId != _mainThread;

        private static string Caller(int skip)
        {
            var sb = new StringBuilder();
            StackFrame[] frames = new StackTrace(skip, false).GetFrames() ?? new StackFrame[0];
            int n = 0;
            foreach (StackFrame f in frames)
            {
                MethodBase m = f.GetMethod();
                if (m == null || m.DeclaringType == typeof(ThreadCanary) || m.DeclaringType?.Namespace == "HarmonyLib") continue;
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
                if (++n == 4) break;
            }
            return sb.ToString();
        }

        private static void CurvePrefix(FloatCurve __instance, out CurveUse __state)
        {
            __state = null;
            if (!_armed) return;
            CurveUse u = Seen.GetOrAdd(__instance, _ => new CurveUse());
            int active = Interlocked.Increment(ref u.Active);
            int max;
            while (active > (max = u.MaxOverlap) && Interlocked.CompareExchange(ref u.MaxOverlap, active, max) != max) { }
            Interlocked.Increment(ref u.Calls);
            int id = Thread.CurrentThread.ManagedThreadId;
            if (u.Threads.TryAdd(id, 0) && id != _mainThread && u.Caller == null)
                u.Caller = Caller(2);
            __state = u;
        }

        private static void CurvePostfix(CurveUse __state)
        {
            if (__state != null) Interlocked.Decrement(ref __state.Active);
        }

        private static void BodyPrefix(MethodBase __originalMethod)
        {
            if (!_armed || !OnWorker) return;
            string key = $"CelestialBody.{__originalMethod.Name} from {Caller(2)}";
            BodyCalls.AddOrUpdate(key, 1, (_, n) => n + 1);
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!_armed || type == LogType.Log || type == LogType.Warning) return;
            string first = (stackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
            Logged.Enqueue($"[{type}{(OnWorker ? ", worker thread" : "")}] {condition} {(string.IsNullOrEmpty(first) ? "" : "@ " + first)}");
        }

        /// <summary>Curves evaluated by worker threads: (overlapped, threads, calls, first worker caller), most threads first.</summary>
        public static List<(bool overlapped, int threads, long calls, string caller)> WorkerCurves() =>
            Seen.Values.Where(u => u.Threads.Keys.Any(t => t != _mainThread))
                .Select(u => (u.MaxOverlap > 1, u.Threads.Count, Interlocked.Read(ref u.Calls), u.Caller ?? "?"))
                .OrderByDescending(x => x.Item1).ThenByDescending(x => x.Item2).ToList();

        public static Dictionary<string, long> WorkerBodyCalls() => BodyCalls.ToDictionary(kv => kv.Key, kv => kv.Value);

        public static List<string> LoggedErrors() => Logged.ToList();

        /// <summary>
        /// Objects reachable from more than one of the given copies (per-thread clones that must not share mutable
        /// state), as "Type at path (reached from N copies)". Strings, boxed primitives, Types, delegates' targets and
        /// Unity objects are not walked; a Unity object reached is reported on its own, since a worker must not use it.
        /// </summary>
        public static List<string> SharedBetween(IList<object> copies, int maxDepth = 8)
        {
            var reach = new Dictionary<object, (HashSet<int> from, string path)>(new RefEq());
            var unity = new List<string>();
            for (int i = 0; i < copies.Count; ++i)
                if (copies[i] != null)
                    WalkGraph(copies[i], copies[i].GetType().Name, i, 0, maxDepth, reach, unity, new HashSet<object>(new RefEq()));
            var shared = reach.Where(kv => kv.Value.from.Count > 1)
                              .Select(kv => $"{kv.Key.GetType().FullName} at {kv.Value.path} (reached from {kv.Value.from.Count} copies)")
                              .ToList();
            shared.AddRange(unity.Distinct());
            return shared;
        }

        private static void WalkGraph(object o, string path, int copy, int depth, int maxDepth,
                                      Dictionary<object, (HashSet<int> from, string path)> reach, List<string> unity, HashSet<object> seen)
        {
            if (o == null || depth > maxDepth || !seen.Add(o)) return;
            Type t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is Type || o is MemberInfo) return;
            if (o is UnityEngine.Object uo)
            {
                unity.Add($"Unity object {t.Name} at {path}");
                return;
            }
            if (depth > 0 && !t.IsValueType)
            {
                if (!reach.TryGetValue(o, out var r)) reach[o] = r = (new HashSet<int>(), path);
                r.from.Add(copy);
            }
            if (o is Delegate) return;
            if (o is Array arr)
            {
                int i = 0;
                foreach (object e in arr)
                {
                    if (i >= 64) break;
                    WalkGraph(e, $"{path}[{i++}]", copy, depth + 1, maxDepth, reach, unity, seen);
                }
                return;
            }
            if (o is IList list)
            {
                for (int i = 0; i < Math.Min(list.Count, 64); ++i)
                    WalkGraph(list[i], $"{path}[{i}]", copy, depth + 1, maxDepth, reach, unity, seen);
            }
            for (Type ty = t; ty != null && ty != typeof(object); ty = ty.BaseType)
                foreach (FieldInfo f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string)) continue;
                    object v;
                    try { v = f.GetValue(o); }
                    catch (Exception) { continue; }
                    WalkGraph(v, path + "." + f.Name, copy, depth + 1, maxDepth, reach, unity, seen);
                }
        }

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
