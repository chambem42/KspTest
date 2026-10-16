using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace KSPUITest
{
    /// <summary>
    /// Where are the worker threads? Every method (with a body, not generic) of the types matching a filter in the
    /// named assemblies gets a prefix counting calls per thread (worker threads only). Two snapshots some seconds
    /// apart show which methods a stuck thread keeps calling - the body of a loop that does not end.
    /// </summary>
    public static class CallSampler
    {
        private static Harmony _harmony;
        private static int _mainThread;
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, long>> PerThread =
            new ConcurrentDictionary<int, ConcurrentDictionary<string, long>>();
        [ThreadStatic] private static ConcurrentDictionary<string, long> _mine;

        /// <summary>Patches the methods of types in the given assemblies whose full name passes the filter; returns how many.</summary>
        public static int Patch(IEnumerable<string> assemblyNames, Func<Type, bool> typeFilter)
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _harmony = _harmony ?? new Harmony("KSPUITest.CallSampler");
            var prefix = new HarmonyMethod(typeof(CallSampler), nameof(Tick));
            var names = new HashSet<string>(assemblyNames);
            int n = 0;
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                if (!names.Contains(la.assembly.GetName().Name)) continue;
                Type[] types;
                try { types = la.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in types)
                {
                    if (t.IsGenericTypeDefinition || t.IsInterface || !typeFilter(t)) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null || m.ContainsGenericParameters) continue;
                        try { _harmony.Patch(m, prefix); ++n; }
                        catch (Exception) { }
                    }
                }
            }
            return n;
        }

        private static void Tick(MethodBase __originalMethod)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThread) return;
            if (_mine == null)
            {
                _mine = new ConcurrentDictionary<string, long>();
                PerThread[Thread.CurrentThread.ManagedThreadId] = _mine;
            }
            string key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            _mine.AddOrUpdate(key, 1, (_, c) => c + 1);
        }

        /// <summary>Calls so far, per worker thread and method.</summary>
        public static Dictionary<int, Dictionary<string, long>> Snapshot() =>
            PerThread.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(x => x.Key, x => x.Value));

        /// <summary>Per thread that made calls between the two snapshots: its methods by calls made in between, most first.</summary>
        public static List<string> Busy(Dictionary<int, Dictionary<string, long>> a, Dictionary<int, Dictionary<string, long>> b, int top = 12)
        {
            var lines = new List<string>();
            foreach (var kv in b.OrderBy(k => k.Key))
            {
                a.TryGetValue(kv.Key, out var before);
                var deltas = kv.Value.Select(x => (x.Key, d: x.Value - (before != null && before.TryGetValue(x.Key, out long v) ? v : 0)))
                                     .Where(x => x.d > 0).OrderByDescending(x => x.d).ToList();
                if (deltas.Count == 0) continue;
                lines.Add($"thread {kv.Key}: " + string.Join(", ", deltas.Take(top).Select(x => $"{x.Key} x{x.d}")));
            }
            return lines;
        }
    }
}
