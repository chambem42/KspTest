using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KSPUITest;
using UnityEngine;

namespace KSPUITest.Tests.Stock
{
    /// <summary>
    /// Is FAR's voxelization deterministic? The same craft, not moving, voxelized n times over: in the editor
    /// (EditorGUI.RequestUpdateVoxel) and then on the runway (FARVesselAero.VesselUpdateEvent). Each voxelization's
    /// result is FAR's own log line ("voxel total vol: T filled vol: F"); the test counts the distinct outcomes per
    /// scene. Flights of the Aeris 4A came out in one of two aircraft (filled volume about 27.11 or 26.84 m3 after the
    /// gear came up, Cd a few percent apart), which one decided by the voxelization at load.
    /// Params: n (30), scenes ("editor,flight"), jitterM / jitterDeg (0: in the editor, move and turn the craft by up
    /// to this much before each voxelization, as one launch's pose differs from another's), seed.
    /// partJitterM (0: move each part on its own by up to this much), partJitterOne (1: one part per voxelization, in turn).
    /// </summary>
    public static class FarVoxelTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex VoxelLine = new Regex(@"voxel total vol: ([\d.Ee+-]+) filled vol: ([\d.Ee+-]+)");

        // FAR logs from its voxelization threads; collect on any thread
        private sealed class Capture
        {
            public readonly List<(double total, double filled)> Lines = new List<(double, double)>();

            public void OnLog(string message, string stack, LogType type)
            {
                Match m = VoxelLine.Match(message);
                if (!m.Success) return;
                lock (Lines) Lines.Add((double.Parse(m.Groups[1].Value, Inv), double.Parse(m.Groups[2].Value, Inv)));
            }

            public int Count { get { lock (Lines) return Lines.Count; } }
            public (double total, double filled) Last { get { lock (Lines) return Lines[Lines.Count - 1]; } }
        }

        [UITest("Far_VoxelRepeat", StartsAt = StartState.Sph, Craft = "Aeris 4A", CraftDir = "SPH", TimeoutSec = 1800)]
        public static IEnumerator Far_VoxelRepeat(TestContext ctx)
        {
            int n = (int)ctx.ParamDouble("n", 30);
            var scenes = (ctx.Param("scenes", "editor,flight") ?? "").Split(',').Select(s => s.Trim()).ToList();
            ctx.Record("farBuild", FarSim.FarVersion());
            var capture = new Capture();
            Application.logMessageReceivedThreaded += capture.OnLog;
            var csv = new StringBuilder("scene,i,totalVol,filledVol,moved\n");
            try
            {
                if (scenes.Contains("editor"))
                {
                    ctx.Step("Editor: voxelize " + n + " times");
                    Ugui.DismissPopups();
                    yield return Wait.Until(FarSim.Ready, 60, "FAR editor sim ready", shot: false);
                    yield return Wait.Seconds(8);
                    MethodInfo request = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI")?.GetMethod("RequestUpdateVoxel", BindingFlags.Public | BindingFlags.Static)
                                         ?? throw new UITestFailure("FAR EditorGUI.RequestUpdateVoxel not found");
                    // jitter: before each voxelization move the whole craft by up to jitterM and turn it by up to
                    // jitterDeg about a random axis, from where it loaded - the few-ulp differences in the vessel's
                    // pose that one launch has over another
                    double jitterM = ctx.ParamDouble("jitterM", 0), jitterDeg = ctx.ParamDouble("jitterDeg", 0);
                    ctx.Record("editor.jitter", string.Format(Inv, "{0} m, {1} deg", jitterM, jitterDeg));
                    Transform root = EditorLogic.RootPart.transform;
                    Vector3 pos0 = root.position;
                    Quaternion rot0 = root.rotation;
                    var rng = new System.Random((int)ctx.ParamDouble("seed", 1));
                    // part jitter: each part moved on its own by up to partJitterM from where it sits relative to its
                    // parent (as parts settle differently on their joints from one launch to the next); with
                    // partJitterOne one part per voxelization, in turn, to find the part that tips the result
                    double partJitterM = ctx.ParamDouble("partJitterM", 0);
                    bool one = ctx.Param("partJitterOne", "0") == "1";
                    ctx.Record("editor.partJitter", string.Format(Inv, "{0} m{1}", partJitterM, one ? ", one part at a time" : ", every part"));
                    List<Part> shipParts = EditorLogic.fetch.ship.parts.ToList();
                    var local0 = shipParts.ToDictionary(p => p, p => p.transform.localPosition);
                    var got = new List<(double, double)>();
                    for (int i = 0; i < n; ++i)
                    {
                        string moved = "";
                        if (jitterM > 0 || jitterDeg > 0)
                        {
                            Vector3 axis = new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)).normalized;
                            Vector3 shift = new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)) * (float)(2 * jitterM);
                            root.SetPositionAndRotation(pos0 + shift, Quaternion.AngleAxis((float)((rng.NextDouble() - 0.5) * 2 * jitterDeg), axis) * rot0);
                            moved = "craft";
                        }
                        if (partJitterM > 0)
                        {
                            foreach (Part p in shipParts) p.transform.localPosition = local0[p];
                            IEnumerable<Part> which = one ? new[] { shipParts[i % shipParts.Count] } : (IEnumerable<Part>)shipParts;
                            foreach (Part p in which)
                                p.transform.localPosition = local0[p] + new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)) * (float)(2 * partJitterM);
                            moved = one ? shipParts[i % shipParts.Count].partInfo.name + "#" + (i % shipParts.Count) : "all";
                        }
                        if (moved != "") yield return Wait.Frames(2);
                        int before = capture.Count;
                        request.Invoke(null, null);
                        yield return Wait.Until(() => capture.Count > before, 60, $"editor voxelization {i + 1}", shot: false);
                        yield return Wait.Frames(10);
                        var r = capture.Last;
                        got.Add(r);
                        csv.Append($"editor,{i},{r.total.ToString("R", Inv)},{r.filled.ToString("R", Inv)},{moved}\n");
                    }
                    root.SetPositionAndRotation(pos0, rot0);
                    foreach (Part p in shipParts) p.transform.localPosition = local0[p];
                    Summarize(ctx, "editor", got);
                }

                if (scenes.Contains("flight"))
                {
                    ctx.Step("Flight: voxelize " + n + " times on the runway");
                    yield return Nav.EnsureState(ctx, StartState.Flight, ctx.CraftPath("Aeris 4A", "SPH"), "Runway");
                    yield return Wait.FlightReady(120);
                    Vessel v = FlightGlobals.ActiveVessel;
                    yield return Wait.Until(() => FarSim.Voxelized(v), 60, "FAR voxels", shot: false);
                    yield return Wait.Seconds(5);
                    VesselModule fva = v.GetComponents<VesselModule>().FirstOrDefault(m => m.GetType().Name == "FARVesselAero")
                                       ?? throw new UITestFailure("no FARVesselAero on the vessel");
                    MethodInfo update = fva.GetType().GetMethod("VesselUpdateEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                        ?? throw new UITestFailure("FARVesselAero.VesselUpdateEvent not found");
                    var got = new List<(double, double)>();
                    for (int i = 0; i < n; ++i)
                    {
                        int before = capture.Count;
                        update.Invoke(fva, new object[] { v });
                        yield return Wait.Until(() => capture.Count > before, 60, $"flight voxelization {i + 1}", shot: false);
                        yield return Wait.Frames(10);
                        var r = capture.Last;
                        got.Add(r);
                        csv.Append($"flight,{i},{r.total.ToString("R", Inv)},{r.filled.ToString("R", Inv)},\n");
                    }
                    Summarize(ctx, "flight", got);
                }
            }
            finally
            {
                Application.logMessageReceivedThreaded -= capture.OnLog;
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "voxel_repeat.csv"), csv.ToString());
            }
        }

        // the distinct outcomes (to FAR's logged precision) and how often each came up
        private static void Summarize(TestContext ctx, string scene, List<(double total, double filled)> got)
        {
            var groups = got.GroupBy(g => (Math.Round(g.total, 6), Math.Round(g.filled, 6)))
                            .OrderByDescending(g => g.Count()).ToList();
            ctx.Record(scene + ".distinct", groups.Count);
            ctx.Record(scene + ".outcomes", string.Join("; ", groups.Select(g =>
                string.Format(Inv, "filled {0:F6} (total {1:F6}) x{2}", g.Key.Item2, g.Key.Item1, g.Count()))));
            double lo = got.Min(g => g.filled), hi = got.Max(g => g.filled);
            ctx.Record(scene + ".filledSpreadPct", Math.Round(100 * (hi - lo) / hi, 4));
        }
    }
}
