using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace KspTest
{
    /// <summary>
    /// Who allocates, game-wide: every Update, FixedUpdate, LateUpdate and OnGUI declared on a MonoBehaviour (stock,
    /// and every loaded mod), and PartModule OnUpdate/OnFixedUpdate overrides, get a Harmony prefix/postfix that takes
    /// the managed heap's growth across the call. Exclusive: what a nested hooked call allocated is its own, not its
    /// caller's. Calls a collection ran inside are not counted. Main thread only. Unity's heap counter moves in
    /// blocks, so single calls read 0 or a block, but over many calls the totals are fair.
    /// </summary>
    public static class AllocSurvey
    {
        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, int> keyOf = new Dictionary<MethodBase, int>();
        private static readonly List<string> names = new List<string>();
        private static long[] bytes = new long[0], calls = new long[0];
        private static bool active;
        private static int mainThread;

        // the hooked calls in progress: heap at entry, collection count at entry, child allocations so far
        private struct Frame { internal long Start; internal int Gc; internal long Child; }
        private static readonly Stack<Frame> stack = new Stack<Frame>();

        private static readonly string[] Unity = { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };
        private static readonly string[] Module = { "OnUpdate", "OnFixedUpdate" };

        /// <summary>
        /// Patches everything it can; returns how many methods. deep: assemblies whose every method (but property
        /// accessors, operators and ones under 24 bytes of IL) is hooked too, to find what in them allocates.
        /// </summary>
        public static int Start(string[] deep = null)
        {
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            harmony = harmony ?? new Harmony("KspTest.AllocSurvey");
            var asms = new List<Assembly> { typeof(Part).Assembly };
            foreach (var la in AssemblyLoader.loadedAssemblies)
                if (!asms.Contains(la.assembly) && la.assembly != typeof(AllocSurvey).Assembly)
                    asms.Add(la.assembly);
            var prefix = new HarmonyMethod(typeof(AllocSurvey), nameof(Before));
            var postfix = new HarmonyMethod(typeof(AllocSurvey), nameof(After));
            foreach (Assembly asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                string asmName = asm.GetName().Name;
                foreach (Type t in types)
                {
                    // abstract base classes stay: their Update runs for every subclass that does not override it
                    if (t.IsGenericTypeDefinition || t.IsAbstract && t.IsSealed)
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
                            keyOf[m] = names.Count;
                            names.Add(asmName + "/" + t.Name + "." + m.Name);
                            harmony.Patch(m, prefix, postfix);
                        }
                        catch (Exception)
                        {
                            keyOf.Remove(m);
                            names.RemoveAt(names.Count - 1);
                        }
                    }
                }
            }
            // Inside MechJeb's OnFlyByWire (94 KB a physics step on the first survey): every Drive, and VesselState's
            // per-step refresh and its parts.
            Assembly mj = asms.FirstOrDefault(a => a.GetName().Name == "MechJeb2");
            if (mj != null)
            {
                string[] stateSteps = { "Update", "UpdateVelocityAndCoM", "UpdateBasicInfo", "UpdateRCSThrustAndTorque", "AnalyzeParts",
                                        "UpdateResourceRequirements", "UpdateMoIAndAngularMom" };
                Type[] mjTypes;
                try { mjTypes = mj.GetTypes(); }
                catch (ReflectionTypeLoadException e) { mjTypes = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in mjTypes)
                {
                    // the thrust controller's Drive was 92 KB a physics step: everything in it
                    if (t.Name == "MechJebModuleThrustController")
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (m.Name != "Drive")
                                HookMethod(m, "MechJeb2/ThrustController." + m.Name, prefix, postfix);
                    bool state = t.Name == "VesselState" || t.DeclaringType?.Name == "VesselState";
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (m.Name == "Drive" || state && stateSteps.Contains(m.Name))
                            HookMethod(m, "MechJeb2/" + (t.DeclaringType != null ? t.DeclaringType.Name + "+" : "") + t.Name + "." + m.Name, prefix, postfix);
                }
            }

            if (deep != null)
                foreach (Assembly asm in asms)
                {
                    string asmName = asm.GetName().Name;
                    if (!deep.Contains(asmName, StringComparer.OrdinalIgnoreCase))
                        continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    foreach (Type t in types)
                    {
                        if (t.IsGenericTypeDefinition)
                            continue;
                        string typeName = t.DeclaringType != null ? t.DeclaringType.Name + "+" + t.Name : t.Name;
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.IsSpecialName || m.IsAbstract || m.ContainsGenericParameters)
                                continue;
                            try
                            {
                                MethodBody body = m.GetMethodBody();
                                if (body == null || body.GetILAsByteArray().Length < 24)
                                    continue;
                            }
                            catch (Exception) { continue; }
                            HookMethod(m, asmName + "/" + typeName + "." + m.Name, prefix, postfix);
                        }
                    }
                }

            // stock calls the thrust controller makes every step
            foreach (var (type, method) in new[] { (typeof(Vessel), "hasEnabledRCSModules"), (typeof(ActionGroupList), "SetGroup"),
                                                    (typeof(ScreenMessages), "PostScreenMessage") })
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == method)
                        HookMethod(m, "Assembly-CSharp/" + type.Name + "." + method, prefix, postfix);

            // What runs inside FlightInputHandler.FixedUpdate: the active vessel's flight control callbacks (autopilots).
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                HookDelegate(v.OnFlyByWire, "OnFlyByWire", prefix, postfix);
                HookDelegate(v.OnPreAutopilotUpdate, "OnPreAutopilotUpdate", prefix, postfix);
                HookDelegate(v.OnAutopilotUpdate, "OnAutopilotUpdate", prefix, postfix);
                HookDelegate(v.OnPostAutopilotUpdate, "OnPostAutopilotUpdate", prefix, postfix);
            }
            // who posts the screen messages (27 KB a call on the earlier surveys), and what they say
            MessageCallers.Clear();
            foreach (MethodInfo m in typeof(ScreenMessages).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "PostScreenMessage"))
                try { harmony.Patch(m, new HarmonyMethod(typeof(MessageCallers), nameof(MessageCallers.Before))); }
                catch (Exception) { }

            bytes = new long[names.Count];
            calls = new long[names.Count];
            stack.Clear();
            active = true;
            return names.Count;
        }

        private static void HookMethod(MethodInfo m, string name, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            if (m == null || m.IsAbstract || m.ContainsGenericParameters || keyOf.ContainsKey(m))
                return;
            try
            {
                if (m.GetMethodBody() == null)
                    return;
                keyOf[m] = names.Count;
                names.Add(name);
                harmony.Patch(m, prefix, postfix);
            }
            catch (Exception)
            {
                keyOf.Remove(m);
                names.RemoveAt(names.Count - 1);
            }
        }

        private static void HookDelegate(Delegate d, string label, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            if (d == null)
                return;
            foreach (Delegate each in d.GetInvocationList())
            {
                MethodInfo m = each.Method;
                if (m == null || keyOf.ContainsKey(m) || m.ContainsGenericParameters)
                    continue;
                try
                {
                    if (m.GetMethodBody() == null)
                        continue;
                    keyOf[m] = names.Count;
                    names.Add(m.DeclaringType?.Assembly.GetName().Name + "/" + m.DeclaringType?.Name + "." + m.Name + " (" + label + ")");
                    harmony.Patch(m, prefix, postfix);
                }
                catch (Exception)
                {
                    keyOf.Remove(m);
                    names.RemoveAt(names.Count - 1);
                }
            }
        }

        private static class MessageCallers
        {
            internal static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            internal static readonly Dictionary<string, string> Sample = new Dictionary<string, string>();

            internal static void Clear() { Counts.Clear(); Sample.Clear(); }

            internal static void Before(object[] __args)
            {
                if (!active)
                    return;
                string caller = new System.Diagnostics.StackTrace(1, false).GetFrames()?.Select(f => f.GetMethod())
                    .Where(m => m != null && m.DeclaringType != typeof(ScreenMessages) && m.DeclaringType != typeof(MessageCallers)
                                && !(m.DeclaringType?.Assembly.GetName().Name ?? "").StartsWith("Harmony"))
                    .Select(m => m.DeclaringType?.Assembly.GetName().Name + "/" + m.DeclaringType?.Name + "." + m.Name)
                    .FirstOrDefault() ?? "?";
                Counts.TryGetValue(caller, out int n);
                Counts[caller] = n + 1;
                if (!Sample.ContainsKey(caller))
                {
                    object first = __args != null && __args.Length > 0 ? __args[0] : null;
                    string text = first is ScreenMessage sm ? sm.message : first?.ToString() ?? "";
                    Sample[caller] = text.Length > 80 ? text.Substring(0, 80) : text;
                }
            }
        }

        private static int stackFrame = -1;

        // __state: the stack depth at entry, -1 when not counted. A call that throws never runs its postfix and leaves
        // its frame behind; the next postfix below it pops back to its own depth, and a new rendered frame starts empty
        // (Unity does not nest these calls across frames).
        private static void Before(out int __state)
        {
            __state = -1;
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return;
            if (UnityEngine.Time.frameCount != stackFrame)
            {
                stackFrame = UnityEngine.Time.frameCount;
                stack.Clear();
            }
            __state = stack.Count;
            stack.Push(new Frame { Start = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), Gc = GC.CollectionCount(0) });
        }

        private static void After(MethodBase __originalMethod, int __state)
        {
            if (__state < 0)
                return;
            while (stack.Count > __state + 1)
                stack.Pop();
            if (stack.Count != __state + 1)
                return;
            Frame f = stack.Pop();
            if (!keyOf.TryGetValue(__originalMethod, out int k))
                return;
            ++calls[k];
            if (GC.CollectionCount(0) != f.Gc)
                return;   // a collection inside: neither this call nor its caller can be measured
            long inclusive = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - f.Start;
            if (inclusive < 0)
                return;
            bytes[k] += Math.Max(0, inclusive - f.Child);
            if (stack.Count > 0)
            {
                Frame parent = stack.Pop();
                parent.Child += inclusive;
                stack.Push(parent);
            }
        }

        /// <summary>Stops counting; records the top entries under prefix and writes alloc_survey.csv (all of them).</summary>
        public static void Stop(TestContext ctx, string prefix, double gameAllocMB)
        {
            if (!active)
                return;
            active = false;
            var order = Enumerable.Range(0, names.Count).Where(i => calls[i] > 0).OrderByDescending(i => bytes[i]).ToList();
            double attributed = order.Sum(i => (double)bytes[i]) / 1048576.0;
            ctx.Record(prefix + "surveyMethods", names.Count);
            ctx.Record(prefix + "surveyAttributedMB", Math.Round(attributed, 1));
            if (!double.IsNaN(gameAllocMB))
                ctx.Record(prefix + "surveyUnattributedMB", Math.Round(gameAllocMB - attributed, 1));
            var inv = CultureInfo.InvariantCulture;
            var csv = new StringBuilder("method,MB,calls,bytesPerCall\n");
            foreach (int i in order)
                csv.Append(string.Format(inv, "{0},{1:F2},{2},{3:F1}\n", names[i], bytes[i] / 1048576.0, calls[i], (double)bytes[i] / calls[i]));
            Directory.CreateDirectory(ctx.ResultsDir);
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "alloc_survey.csv"), csv.ToString());
            // by assembly
            foreach (var g in order.GroupBy(i => names[i].Substring(0, names[i].IndexOf('/'))).OrderByDescending(g => g.Sum(i => bytes[i])).Take(15))
                ctx.Record(prefix + "survey_asm_" + g.Key, string.Format(inv, "{0:F1} MB", g.Sum(i => bytes[i]) / 1048576.0));
            foreach (var kv in MessageCallers.Counts.OrderByDescending(kv => kv.Value).Take(10))
                ctx.Record(prefix + "screenMsgFrom_" + kv.Key, kv.Value + " calls: " + MessageCallers.Sample[kv.Key].Replace("\n", " "));
            foreach (int i in order.Take(40))
                ctx.Record(prefix + "survey_" + names[i], string.Format(inv, "{0:F1} MB in {1} calls", bytes[i] / 1048576.0, calls[i]));
        }
    }
}
