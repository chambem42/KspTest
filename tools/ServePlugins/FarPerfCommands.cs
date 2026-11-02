using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// What FAR's whole-wing aspect ratio costs, on the editor craft, by reflection: with FARDebugValues.wholeWingAspectRatio
    /// off and on (interleaved rounds), the time of one pass of every wing's SpanChainedAR (the call each wing makes once
    /// per physics frame) right after the wing groups are invalidated (a voxel update: the groups are built again) and
    /// with them cached, and of one step of the instant editor sim, cached and right after an invalidation.
    /// </summary>
    public static class FarPerfCommands
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Get(object o, string name)
        {
            if (o == null)
                return null;
            for (Type t = o is Type st ? st : o.GetType(); t != null; t = t.BaseType)
            {
                object target = o is Type ? null : o;
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null)
                    return f.GetValue(target);
                PropertyInfo p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(target, null);
            }
            return null;
        }

        private static Type FindType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        private static double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

        private static double Median(List<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        [ServeCommand("farperf", "[rounds=3] [rebuilds=40] [passes=2000] [sims=200] [aoa=4] [mach=0.5]: time FAR's wing " +
                                 "AR calls, every wing's interaction update (a voxel update's) and the editor sim with " +
                                 "whole-wing aspect ratio off and on (us)")]
        public static IEnumerator FarPerf(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            // a profile's patches outlive the plugin that made them (Harmony keeps them): take every one off first
            int unpatched = 0;
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(m);
                foreach (string owner in info.Owners.Where(o => o.StartsWith("KspTest.FarPerf.")).ToList())
                {
                    new Harmony(owner).Unpatch(m, HarmonyPatchType.All, owner);
                    unpatched++;
                }
            }
            answer["unpatched"] = unpatched;

            var ship = EditorLogic.fetch?.ship;
            if (ship == null)
            {
                answer["error"] = "no editor craft";
                yield break;
            }

            int rounds = int.Parse(q["rounds"] ?? "3");
            int rebuilds = int.Parse(q["rebuilds"] ?? "40");
            int passes = int.Parse(q["passes"] ?? "2000");
            int sims = int.Parse(q["sims"] ?? "200");

            Type values = FindType("FerramAerospaceResearch.FARDebugValues");
            FieldInfo toggle = values?.GetField("wholeWingAspectRatio", Any);
            Type wiType = FindType("ferram4.FARWingInteraction");
            MethodInfo invalidate = wiType?.GetMethod("InvalidateWingGroups", Any, null, Type.EmptyTypes, null);
            MethodInfo spanAR = wiType?.GetMethod("SpanChainedAR", Any);
            PropertyInfo wingKey = wiType?.GetProperty("WingKey", Any);
            if (toggle == null || invalidate == null || spanAR == null)
            {
                answer["error"] = "this FAR has no whole-wing aspect ratio switch";
                yield break;
            }

            // every wing module: its interaction and its own aspect ratio, the argument FAR passes
            var wings = new List<(object wi, object ar)>();
            var modules = new List<PartModule>();
            int controls = 0;
            foreach (Part p in ship.parts)
            foreach (PartModule m in p.Modules)
            {
                for (Type t = m.GetType(); t != null; t = t.BaseType)
                    if (t.Name == "FARWingAerodynamicModel")
                    {
                        object wi = Get(m, "WingInteraction");
                        if (wi != null)
                        {
                            wings.Add((wi, Get(m, "transformed_AR")));
                            modules.Add(m);
                        }
                        if (m.GetType().Name == "FARControllableSurface")
                            controls++;
                        break;
                    }
            }

            object gui = Get(FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI"), "Instance");
            object sim = Get(gui, "_instantSim");
            MethodInfo steady = sim?.GetType().GetMethod("GetClCdCmSteady", Any);
            Type inType = FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSimInput");
            object input = Activator.CreateInstance(inType);
            inType.GetField("alpha", Any)?.SetValue(input, double.Parse(q["aoa"] ?? "4"));
            inType.GetField("machNumber", Any)?.SetValue(input, double.Parse(q["mach"] ?? "0.5"));
            object[] simArgs = { input, null, true, false };

            for (float waited = 0; steady != null && waited < 60; waited += 0.5f)
            {
                steady.Invoke(sim, simArgs);
                if (Math.Abs((double)Get(simArgs[1], "Cl")) > 1e-9)
                    break;
                yield return new UnityEngine.WaitForSecondsRealtime(0.5f);
            }

            // what a voxel update does to every wing in flight (FARAeroPartModule.ForceLegacyAeroUpdates): its
            // interactions found again, which starts its vessel's groups anew
            MethodInfo update = modules.Count == 0 ? null : modules[0].GetType().GetMethod("UpdateThisWingInteractions", Any);

            void Update()
            {
                foreach (PartModule m in modules)
                    update.Invoke(m, null);
            }

            void Pass()
            {
                foreach ((object wi, object ar) in wings)
                    spanAR.Invoke(wi, new[] { ar });
            }

            // the cost of reflection alone: the same number of Invoke calls of a method that does nothing
            MethodInfo empty = typeof(FarPerfCommands).GetMethod(nameof(Nothing), Any);
            var sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < passes; i++)
                foreach ((object wi, object ar) in wings)
                    empty.Invoke(null, new[] { ar });
            sw.Stop();
            double invokeUs = Us(sw.ElapsedTicks) / passes;

            bool profile = q["profile"] == "1";
            bool original = (bool)toggle.GetValue(null);
            var results = new Dictionary<bool, Dictionary<string, List<double>>>();
            foreach (bool on in new[] { false, true })
                results[on] = new Dictionary<string, List<double>>
                {
                    ["passAfterInvalidate"] = new List<double>(),
                    ["passCached"] = new List<double>(),
                    ["update"] = new List<double>(),
                    ["passAfterUpdate"] = new List<double>(),
                    ["simAfterInvalidate"] = new List<double>(),
                    ["simCached"] = new List<double>(),
                };
            int groups = 0;
            double cl0 = double.NaN, cl1 = double.NaN;
            try
            {
                for (int r = 0; r < rounds; r++)
                    foreach (bool on in new[] { false, true })
                    {
                        Dictionary<string, List<double>> res = results[on];
                        toggle.SetValue(null, on);
                        invalidate.Invoke(null, null);
                        Pass();
                        steady?.Invoke(sim, simArgs);
                        if (on)
                            cl1 = (double)Get(simArgs[1], "Cl");
                        else
                            cl0 = (double)Get(simArgs[1], "Cl");

                        for (int i = 0; i < rebuilds; i++)
                        {
                            invalidate.Invoke(null, null);
                            sw.Reset();
                            sw.Start();
                            Pass();
                            sw.Stop();
                            res["passAfterInvalidate"].Add(Us(sw.ElapsedTicks));
                        }

                        for (int i = 0; update != null && i < Math.Max(1, rebuilds / 4); i++)
                        {
                            sw.Reset();
                            sw.Start();
                            Update();
                            sw.Stop();
                            res["update"].Add(Us(sw.ElapsedTicks));
                            sw.Reset();
                            sw.Start();
                            Pass();
                            sw.Stop();
                            res["passAfterUpdate"].Add(Us(sw.ElapsedTicks));
                        }

                        for (int k = 0; k < 5; k++)
                        {
                            sw.Reset();
                            sw.Start();
                            for (int i = 0; i < passes; i++)
                                Pass();
                            sw.Stop();
                            res["passCached"].Add(Us(sw.ElapsedTicks) / passes);
                        }

                        if (steady != null)
                        {
                            for (int i = 0; i < Math.Max(1, sims / 10); i++)
                            {
                                invalidate.Invoke(null, null);
                                sw.Reset();
                                sw.Start();
                                steady.Invoke(sim, simArgs);
                                sw.Stop();
                                res["simAfterInvalidate"].Add(Us(sw.ElapsedTicks));
                            }

                            for (int k = 0; k < 5; k++)
                            {
                                sw.Reset();
                                sw.Start();
                                for (int i = 0; i < sims; i++)
                                    steady.Invoke(sim, simArgs);
                                sw.Stop();
                                res["simCached"].Add(Us(sw.ElapsedTicks) / sims);
                            }
                        }

                        if (on && wingKey != null)
                            groups = wings.Select(w => wingKey.GetValue(w.wi, null)).Where(k => k != null).Distinct().Count();
                        yield return null;
                    }

                // the patches' own cost would be in the times above: patch after them, and profile switched on only
                if (profile)
                {
                    PatchAll(wiType);
                    toggle.SetValue(null, true);
                    Spent.Clear();
                    for (int i = 0; i < rebuilds; i++)
                    {
                        invalidate.Invoke(null, null);
                        profiling = true;
                        Pass();
                        profiling = false;
                    }
                }
            }
            finally
            {
                toggle.SetValue(null, original);
                invalidate.Invoke(null, null);
            }

            answer["craft"] = ship.shipName;
            answer["parts"] = ship.parts.Count;
            answer["wings"] = wings.Count;
            answer["controls"] = controls;
            answer["groups"] = groups;
            answer["Cl"] = new Dictionary<string, object> { ["off"] = cl0, ["on"] = cl1 };
            answer["invokeOverheadPerPassUs"] = invokeUs;
            foreach (bool on in new[] { false, true })
                answer[on ? "on" : "off"] = results[on].ToDictionary(
                    kv => kv.Key,
                    kv => (object)new Dictionary<string, object>
                    {
                        ["median"] = Median(kv.Value),
                        ["min"] = kv.Value.Count == 0 ? double.NaN : kv.Value.Min(),
                        ["n"] = kv.Value.Count,
                    });
            answer["farAssembly"] = spanAR.DeclaringType.Assembly.Location;
            // the panels' outline sizes (corners), where this FAR keeps them
            var sizes = wings.Select(w => Get(w.wi, "panel")).Where(pn => pn != null)
                .Select(pn => (Get(pn, "Outline") as Array)?.Length ?? 0).ToList();
            if (sizes.Count > 0)
                answer["panelCorners"] = new Dictionary<string, object>
                {
                    ["n"] = sizes.Count, ["outlineMean"] = sizes.Average(), ["outlineMax"] = sizes.Max(),
                };
            if (profile)
            {
                int perPass = Math.Max(1, rebuilds);
                answer["profile"] = Spent.OrderByDescending(kv => kv.Value.ticks).Take(40)
                    .Select(kv => $"{Us(kv.Value.ticks) / perPass,10:F1} us {kv.Value.calls / (double)perPass,9:F1} calls  {kv.Key}")
                    .ToList();
                Spent.Clear();
                harmony.UnpatchAll(harmony.Id);
                harmony = null;
            }
        }

        private static void Nothing(object ar) { }

        // inclusive time and calls per method while profiling (a recursive method counts each level)
        private static bool profiling;
        private static Harmony harmony;
        private static readonly Dictionary<string, (long ticks, long calls)> Spent = new Dictionary<string, (long, long)>();

        private static void PatchAll(Type wiType)
        {
            if (harmony != null)
                return;
            harmony = new Harmony("KspTest.FarPerf." + Guid.NewGuid());
            var pre = new HarmonyMethod(typeof(FarPerfCommands).GetMethod(nameof(Enter), Any));
            var post = new HarmonyMethod(typeof(FarPerfCommands).GetMethod(nameof(Leave), Any));
            var types = new List<Type> { wiType };
            for (int i = 0; i < types.Count; i++)
                types.AddRange(types[i].GetNestedTypes(Any));
            foreach (Type t in types)
            {
                if (t.IsGenericTypeDefinition || t.IsInterface)
                    continue;
                foreach (MethodInfo m in t.GetMethods(Any | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters || m.GetMethodBody() == null)
                        continue;
                    try { harmony.Patch(m, pre, post); }
                    catch (Exception) { }
                }
            }
        }

        private static void Enter(out long __state) => __state = profiling ? Stopwatch.GetTimestamp() : 0;

        private static void Leave(MethodBase __originalMethod, long __state)
        {
            if (!profiling || __state == 0)
                return;
            string key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            Spent.TryGetValue(key, out var v);
            Spent[key] = (v.ticks + Stopwatch.GetTimestamp() - __state, v.calls + 1);
        }
    }
}
