using System;
using System.Reflection;

namespace KspTest.Coverage
{
    /// <summary>
    /// Stands in for AltCover.Recorder.Instance.Visit(moduleId, hitPointId) at every instrumented point (CovPatch rewrites
    /// the call sites after AltCover instruments them): the first visit to a point goes on to AltCover, later ones return
    /// at once. AltCover 9 records only the first visit anyway (without --all), but reaches that answer through
    /// allocations and string-keyed dictionaries on every visit, which under Mono's stop-the-world GC made FAR's
    /// multithreaded sweeps ~100x slower than uninstrumented.
    /// </summary>
    public static class FastVisit
    {
        private const int Size = 1 << 16;

        // per module (its id an interned literal, so compared by reference), the points visited; copy-on-write so
        // Visit reads without a lock
        private static volatile string[] _modules = new string[0];
        private static volatile bool[][] _seen = new bool[0][];
        private static readonly object Gate = new object();
        private static Action<string, int> _visit;

        public static void Visit(string moduleId, int hitPointId)
        {
            bool[] seen = Seen(moduleId);
            if ((uint)hitPointId < (uint)seen.Length)
            {
                if (seen[hitPointId])
                    return;
                Recorder()(moduleId, hitPointId);
                seen[hitPointId] = true;   // after the visit is recorded: a race only visits twice
                return;
            }
            Recorder()(moduleId, hitPointId);
        }

        private static bool[] Seen(string moduleId)
        {
            string[] modules = _modules;
            bool[][] seen = _seen;
            for (int i = 0; i < modules.Length; i++)
                if (ReferenceEquals(modules[i], moduleId))
                    return seen[i];
            lock (Gate)
            {
                modules = _modules;
                seen = _seen;
                for (int i = 0; i < modules.Length; i++)
                    if (ReferenceEquals(modules[i], moduleId))
                        return seen[i];
                var m2 = new string[modules.Length + 1];
                var s2 = new bool[seen.Length + 1][];
                Array.Copy(modules, m2, modules.Length);
                Array.Copy(seen, s2, seen.Length);
                m2[modules.Length] = moduleId;
                s2[seen.Length] = new bool[Size];
                _seen = s2;      // the arrays before the ids: a reader that finds the id finds its array
                _modules = m2;
                return s2[seen.Length];
            }
        }

        private static Action<string, int> Recorder()
        {
            Action<string, int> v = _visit;
            if (v != null)
                return v;
            Type t = Type.GetType("AltCover.Recorder.Instance, AltCover.Recorder.g", true);
            MethodInfo m = t.GetMethod("Visit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int) }, null)
                           ?? throw new MissingMethodException("AltCover.Recorder.Instance", "Visit");
            return _visit = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), m);
        }
    }
}
