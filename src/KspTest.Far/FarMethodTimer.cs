using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace KspTest
{
    /// <summary>
    /// Inclusive time and calls of named FAR methods on the main thread (every overload a type declares under the
    /// name; a method's calls into itself are timed once). The timing's own cost lands in the callers of timed
    /// methods, the same in every build that makes the same calls, so builds compare method by method.
    /// </summary>
    public static class FarMethodTimer
    {
        private static Harmony _harmony;
        private static int _mainThread;
        private static readonly Dictionary<MethodBase, int> Index = new Dictionary<MethodBase, int>();
        private static readonly List<string> Names = new List<string>();
        private static long[] _ticks = new long[0], _calls = new long[0];
        private static int[] _depth = new int[0];

        /// <summary>Patches "Type.Method" (type by full name, method by name) for each entry; returns the ones found.</summary>
        public static List<string> Patch(IEnumerable<string> methods)
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _harmony = _harmony ?? new Harmony("KspTest.FarMethodTimer");
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var found = new List<string>();
            foreach (string full in methods)
            {
                int dot = full.LastIndexOf('.');
                System.Type t = Reflect.FindType(full.Substring(0, dot));
                string name = full.Substring(dot + 1);
                List<MethodInfo> ms = t?.GetMethods(any).Where(m => m.Name == name && m.GetMethodBody() != null).ToList();
                if (ms == null || ms.Count == 0) continue;
                string key = t.Name + "." + name;
                int i = Names.IndexOf(key);
                if (i < 0)
                {
                    i = Names.Count;
                    Names.Add(key);
                }
                foreach (MethodInfo m in ms)
                {
                    if (Index.ContainsKey(m)) continue;
                    Index[m] = i;
                    _harmony.Patch(m, new HarmonyMethod(typeof(FarMethodTimer), nameof(Prefix)), new HarmonyMethod(typeof(FarMethodTimer), nameof(Postfix)));
                }
                found.Add(key);
            }
            _ticks = new long[Names.Count];
            _calls = new long[Names.Count];
            _depth = new int[Names.Count];
            return found;
        }

        public static void Reset()
        {
            for (int i = 0; i < _ticks.Length; ++i)
                _ticks[i] = _calls[i] = 0;
        }

        /// <summary>Per method since the last Reset: "name calls ticks", with a header giving the tick rate.</summary>
        public static IEnumerable<string> Report()
        {
            yield return $"# ticks per second {Stopwatch.Frequency}; per method: name calls ticks (inclusive, main thread)";
            for (int i = 0; i < Names.Count; ++i)
                yield return $"{Names[i]} {_calls[i]} {_ticks[i]}";
        }

        private static void Prefix(MethodBase __originalMethod, out long __state)
        {
            __state = -1;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || !Index.TryGetValue(__originalMethod, out int i)) return;
            if (_depth[i]++ == 0) __state = Stopwatch.GetTimestamp();
        }

        private static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || !Index.TryGetValue(__originalMethod, out int i)) return;
            --_depth[i];
            if (__state < 0) return;
            _ticks[i] += Stopwatch.GetTimestamp() - __state;
            ++_calls[i];
        }
    }
}
