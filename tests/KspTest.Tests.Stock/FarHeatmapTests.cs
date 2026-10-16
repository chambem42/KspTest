using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>FAR's editor heat map sweep, multithreaded, under the ThreadCanary.</summary>
    public static class FarHeatmapTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// What the multithreaded heat map sweep's workers touch that they must not. In the editor with the craft
        /// loaded, runs the sweep (Multithread on, auto-fit off) `sweeps` times with ThreadCanary armed and reports:
        /// FloatCurve instances evaluated from worker threads - which ones overlapped (two threads inside the same
        /// curve at once, the suspected native crash), by how many threads, from where; CelestialBody atmosphere calls
        /// from workers; errors and exceptions logged from any thread; and, while each sweep runs, the objects the
        /// per-slot engine solver clones share (FAR's EditorEngineDeck._clones[engine][slot]) and any Unity object a
        /// clone reaches. Writes hazards.md. Discovery only: passes unless a sweep cannot be started or does not
        /// finish. Progress (cells done) is logged every 10 s; a sweep past its timeout is cancelled so KSP can exit.
        /// Params: craft, craftDir, nMach (24), nAlt (16), machMin, machMax (2.5), altMinKm, altMaxKm (20), sweeps (2),
        /// timeoutSec (600), canary ("off,on": one sweep per entry, the canary armed or not, overriding sweeps),
        /// sample (true: count calls per worker thread in FAR, SolverEngines and RealFuels solver code, so a sweep
        /// that stops progressing for stallSec (30) reports what its workers keep calling, then fails without
        /// cancelling - a cancel joins the stuck workers on the main thread).
        /// </summary>
        [UITest("Far_HeatmapHazards", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", CraftDir = "VAB", TimeoutSec = 1800)]
        public static IEnumerator Far_HeatmapHazards(TestContext ctx)
        {
            yield return Wait.Seconds(10);   // FAR voxelizes the ship
            Type editorGui = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            object gui = editorGui?.GetProperty("Instance", Any)?.GetValue(null, null);
            Assert.True(gui != null, "FAR's editor GUI instance");
            object heatmaps = editorGui.GetField("_heatmaps", Any)?.GetValue(gui);
            Assert.True(heatmaps != null, "FAR's heat map GUI (a FAR build with the heat maps)");
            Type hm = heatmaps.GetType();
            void Set(string field, object value)
            {
                FieldInfo f = hm.GetField(field, Any);
                if (f == null) throw new UITestFailure($"HeatmapsGUI has no field {field}");
                f.SetValue(heatmaps, value);
            }
            // Every range field set: the GUI keeps the last session's settings (a speed axis in m/s, say).
            Set("_multithread", true);
            Set("_autoMaxAlt", false);
            Set("_autoMaxSpeed", false);
            Set("_speedInMS", false);
            Set("_nMach", ctx.Param("nMach", "24"));
            Set("_nAlt", ctx.Param("nAlt", "16"));
            Set("_machMin", ctx.Param("machMin", "0.15"));
            Set("_machMax", ctx.Param("machMax", "2.5"));
            Set("_altMinKm", ctx.Param("altMinKm", "0"));
            Set("_altMaxKm", ctx.Param("altMaxKm", "20"));
            MethodInfo start = hm.GetMethod("StartSweep", Any, null, Type.EmptyTypes, null);
            Assert.True(start != null, "HeatmapsGUI.StartSweep()");
            PropertyInfo running = hm.GetProperty("IsSweepRunning", Any);
            object deck = hm.GetField("_deck", Any)?.GetValue(heatmaps);
            FieldInfo clonesField = deck?.GetType().GetField("_clones", Any);
            var engines = deck?.GetType().GetField("_engines", Any)?.GetValue(deck) as IList;
            ctx.Record("engines", engines == null ? "?" : string.Join(", ", engines.Cast<PartModule>().Select(m => $"{m.part.partInfo.name}/{m.GetType().Name}").Distinct()));
            ctx.Record("processors", Environment.ProcessorCount);

            ThreadCanary.EnsurePatched();
            if (ctx.Param("sample", "true") != "false")
            {
                int patched = CallSampler.Patch(new[] { "FerramAerospaceResearch", "SolverEngines", "RealFuels" }, t =>
                {
                    string ns = t.Namespace ?? "", name = t.FullName ?? "";
                    if (t.Assembly.GetName().Name == "RealFuels") return name.Contains("Solver") || name.Contains("ModuleEnginesRF");
                    if (t.Assembly.GetName().Name == "SolverEngines") return true;
                    return ns == "ferram4" || ns == "FerramAerospaceResearch" || ns.StartsWith("FerramAerospaceResearch.FARAeroComponents") ||
                           ns.StartsWith("FerramAerospaceResearch.FARGUI.FAREditorGUI");
                });
                ctx.Record("sampledMethods", patched);
            }
            double timeout = ctx.ParamDouble("timeoutSec", 600), stallSec = ctx.ParamDouble("stallSec", 30);
            string[] plan = (ctx.Param("canary") ?? string.Join(",", Enumerable.Repeat("on", (int)ctx.ParamDouble("sweeps", 2))))
                            .Split(',').Select(x => x.Trim()).ToArray();
            int sweeps = plan.Length;
            FieldInfo cellsDone = hm.GetField("_cellsDone", Any), cellsTotal = hm.GetField("_cellsTotal", Any);
            FieldInfo cancel = hm.GetField("_cancelRequested", Any);
            var shared = new SortedDictionary<string, int>();
            int clonesSeen = 0, slotsSeen = 0;
            ThreadCanary.Arm();
            ThreadCanary.Disarm();   // clears; armed per sweep below
            for (int s = 0; s < sweeps; ++s)
            {
                bool armed = plan[s] != "off";
                ctx.Step($"Sweep {s + 1} of {sweeps}, multithreaded, canary {(armed ? "on" : "off")}");
                if (armed) ThreadCanary.Resume(); else ThreadCanary.Disarm();
                object started = start.Invoke(heatmaps, null);
                string status = hm.GetField("_statusMessage", Any)?.GetValue(heatmaps) as string;
                Assert.True(!(started is bool b) || b, $"sweep {s + 1} started ({status})");
                if (s == 0)
                    ctx.Record("engines", engines == null ? "?" : string.Join(", ", engines.Cast<PartModule>().Select(m => $"{m.part.partInfo.name}/{m.GetType().Name}").Distinct()));
                float t0 = Time.realtimeSinceStartup, lastLog = t0, lastChange = t0;
                object lastDone = null;
                bool walked = false;
                while ((bool)running.GetValue(heatmaps, null))
                {
                    float now = Time.realtimeSinceStartup;
                    object done = cellsDone?.GetValue(heatmaps);
                    if (!Equals(done, lastDone)) { lastDone = done; lastChange = now; }
                    if (now - lastChange > stallSec)
                    {
                        // Stuck: what do the workers keep calling? No cancel - it would join them on the main thread.
                        var a = CallSampler.Snapshot();
                        float ts = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - ts < 3) yield return null;
                        List<string> busy = CallSampler.Busy(a, CallSampler.Snapshot());
                        ctx.Record($"sweep{s + 1}Stalled", $"{done} of {cellsTotal?.GetValue(heatmaps)} cells, no progress for {stallSec:F0} s; {busy.Count} worker threads busy");
                        for (int k = 0; k < busy.Count; ++k)
                            ctx.Record($"sweep{s + 1}Busy{k + 1}", busy[k]);
                        ThreadCanary.Disarm();
                        WriteReport(ctx, engines, plan, clonesSeen, slotsSeen, shared, busy);
                        throw new UITestFailure($"sweep {s + 1} kept progressing ({done} of {cellsTotal?.GetValue(heatmaps)} cells, then nothing for {stallSec:F0} s; {busy.Count} worker threads still busy)");
                    }
                    if (now - lastLog >= 10)
                    {
                        lastLog = now;
                        Log.Info($"heatmap sweep {s + 1}: {cellsDone?.GetValue(heatmaps)} of {cellsTotal?.GetValue(heatmaps)} cells after {now - t0:F0} s");
                    }
                    if (now - t0 > timeout)
                    {
                        ctx.Record($"sweep{s + 1}Stuck", $"{cellsDone?.GetValue(heatmaps)} of {cellsTotal?.GetValue(heatmaps)} cells after {now - t0:F0} s");
                        cancel?.SetValue(heatmaps, true);
                        float tc = Time.realtimeSinceStartup;
                        while ((bool)running.GetValue(heatmaps, null) && Time.realtimeSinceStartup - tc < 60)
                            yield return null;
                        ctx.Record($"sweep{s + 1}CancelJoined", !(bool)running.GetValue(heatmaps, null));
                        ThreadCanary.Disarm();
                        WriteReport(ctx, engines, plan, clonesSeen, slotsSeen, shared, null);
                        throw new UITestFailure($"sweep {s + 1} finished within {timeout} s ({cellsDone?.GetValue(heatmaps)} of {cellsTotal?.GetValue(heatmaps)} cells)");
                    }
                    if (!walked && clonesField?.GetValue(deck) is object[][] clones)
                    {
                        walked = true;
                        for (int ei = 0; ei < clones.Length; ++ei)
                        {
                            object[] perSlot = clones[ei];
                            if (perSlot == null) continue;
                            ++clonesSeen;
                            slotsSeen = Math.Max(slotsSeen, perSlot.Length);
                            string engine = engines != null && ei < engines.Count ? ((PartModule)engines[ei]).part.partInfo.name : $"engine {ei}";
                            foreach (string item in ThreadCanary.SharedBetween(perSlot))
                            {
                                string key = $"{engine}: {item}";
                                shared.TryGetValue(key, out int n);
                                shared[key] = n + 1;
                            }
                        }
                    }
                    yield return null;
                }
                ctx.Record($"sweep{s + 1}Sec", $"{Math.Round(Time.realtimeSinceStartup - t0, 1)} (canary {(armed ? "on" : "off")})");
                ctx.Record($"sweep{s + 1}ClonesWalked", walked);
            }
            ThreadCanary.Disarm();
            WriteReport(ctx, engines, plan, clonesSeen, slotsSeen, shared, null);
        }

        private static void WriteReport(TestContext ctx, IList engines, string[] plan, int clonesSeen, int slotsSeen,
                                        SortedDictionary<string, int> shared, List<string> busy)
        {
            int sweeps = plan.Length;
            var curves = ThreadCanary.WorkerCurves();
            var bodies = ThreadCanary.WorkerBodyCalls();
            var logged = ThreadCanary.LoggedErrors();
            int overlapped = curves.Count(c => c.overlapped);
            ctx.Record("workerCurves", curves.Count);
            ctx.Record("overlappedCurves", overlapped);
            ctx.Record("workerBodyCallSites", bodies.Count);
            ctx.Record("sharedCloneObjects", shared.Count);
            ctx.Record("loggedErrors", logged.Count);

            var md = new StringBuilder("# Heat map sweep: what the worker threads touch\n\n");
            md.Append($"Craft {ctx.Param("craft", "Mk2 SSTO Big")}; engines: {(engines == null ? "?" : string.Join(", ", engines.Cast<PartModule>().Select(m => $"{m.part.partInfo.name} ({m.GetType().Name})").Distinct()))}; ");
            md.Append($"{sweeps} multithreaded sweeps (canary {string.Join(", ", plan)}), grid {ctx.Param("nMach", "24")} x {ctx.Param("nAlt", "16")}; {Environment.ProcessorCount} processors; ");
            md.Append($"engine solver clones walked for {clonesSeen} engines x {slotsSeen} slots.\n\n");
            md.Append($"## FloatCurves evaluated on worker threads: {curves.Count}, of which {overlapped} overlapped\n\n");
            md.Append("Overlapped = two threads inside the same curve's Evaluate at once (a Unity AnimationCurve underneath, not safe to share).\n\n");
            md.Append("| overlapped | threads | calls | first worker caller |\n|---|---|---|---|\n");
            foreach (var c in curves.GroupBy(c => (c.overlapped, c.caller)).OrderByDescending(g => g.Key.overlapped).ThenByDescending(g => g.Max(x => x.threads)))
                md.Append($"| {(c.Key.overlapped ? "**yes**" : "no")} | {c.Max(x => x.threads)} | {c.Sum(x => x.calls)} ({c.Count()} curves) | {c.Key.caller} |\n");
            md.Append($"\n## CelestialBody atmosphere calls from worker threads: {bodies.Count} call sites\n\n");
            foreach (var kv in bodies.OrderByDescending(kv => kv.Value))
                md.Append($"- {kv.Key}: {kv.Value}\n");
            md.Append($"\n## Shared between the per-slot engine solver clones: {shared.Count}\n\n");
            foreach (var kv in shared)
                md.Append($"- {kv.Key}\n");
            if (busy != null)
            {
                md.Append($"\n## Stuck: what each busy worker thread called in 3 s after progress stopped\n\n");
                foreach (string line in busy)
                    md.Append($"- {line}\n");
            }
            md.Append($"\n## Errors and exceptions logged during the sweeps: {logged.Count}\n\n```\n");
            md.Append(string.Join("\n", logged.Take(200))).Append("\n```\n");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "hazards.md"), md.ToString());
        }
    }
}
