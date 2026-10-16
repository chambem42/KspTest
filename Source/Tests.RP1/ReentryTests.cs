using System;
using System.Collections;
using System.IO;
using KSPUITest;
using KSPUITest.MechJeb;
using KSPUITest.RP1;

namespace KSPUITest.Tests.RP1
{
    /// <summary>
    /// The editor reentry analysis (FAR fork's Reentry tab) against a flown KCT reentry simulation of the
    /// same setup. Reentry_Predict writes predicted_*.csv, Reentry_Analyse sweeps the heatmap, Reentry_Fly
    /// holds an angle of attack through the entry with MechJeb SmartASS and writes the flown telemetry, and
    /// Reentry_Borderline does all three for the grid's borderline cell; the CSVs land in the results folder
    /// for an offline diff (Notes/ReentrySim/compare_traces.py).
    /// Params: craft (default Mk2 SSTO Big), peKm, apKm, entryAltKm, aoa, attitude (Nose first / Heat shield first),
    /// endAltKm (stop the flight below this), flightTimeoutSec, physWarp, rcs (on/off: off makes the elevator input the pure aero trim),
    /// propellant (engine propellant remaining, % of capacity, applied on both sides).
    /// </summary>
    public static class ReentryTests
    {
        private static SimSpec ReentryFromApoapsis(TestContext ctx) => new SimSpec
        {
            StartInOrbit = true, Mode = "Reentry",
            PeKm = ctx.ParamDouble("peKm", 90), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
            EntryAltKm = ctx.ParamDouble("entryAltKm", 100), IncDeg = 0, Attitude = ShieldFirst(ctx) ? "Retrograde" : "Prograde", LogTelemetry = true,
            PropellantPct = ctx.Param("propellant") != null ? ctx.ParamDouble("propellant", 0) : (double?)null,
        };

        /// <summary>The "attitude" param: "Heat shield first" flies the entry retrograde, anything else nose first.</summary>
        private static bool ShieldFirst(TestContext ctx) => ctx.Param("attitude", "Nose first").StartsWith("Heat", StringComparison.OrdinalIgnoreCase);

        /// <summary>Bank: roof's angle from local up around the airspeed (0 = roof to the sky, 180 = inverted).</summary>
        private static double Bank(Vessel v)
        {
            var velNorm = v.srf_velocity.normalized;
            var roof = UnityEngine.Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, velNorm);
            var up = UnityEngine.Vector3.ProjectOnPlane((v.CoMD - v.mainBody.position).normalized, velNorm);
            return UnityEngine.Vector3.SignedAngle(up, roof, velNorm);
        }

        private static void CopyToResults(TestContext ctx, string path, string key)
        {
            if (path == null || !File.Exists(path)) return;
            File.Copy(path, Path.Combine(ctx.ResultsDir, Path.GetFileName(path)), true);
            ctx.Record(key, Path.GetFileName(path));
            string parts = path.Replace(".csv", "") + ".parts.csv";
            if (File.Exists(parts)) File.Copy(parts, Path.Combine(ctx.ResultsDir, Path.GetFileName(parts)), true);
        }

        [UITest("Reentry_Predict", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 600)]
        public static IEnumerator Reentry_Predict(TestContext ctx)
        {
            Ugui.DismissPopups();
            var spec = ReentryFromApoapsis(ctx);
            if (ctx.Param("debug") != null)
                Reflect.SetStatic(Reflect.FindType("FerramAerospaceResearch.Reentry.FarEditorAero"), "DebugTrim", true);
            ctx.Step("Predict the entry on FAR's Reentry tab");
            yield return Rp1.PredictReentry(spec.PeKm.Value, spec.ApKm.Value, ctx.ParamDouble("aoa", 42), ctx.Param("attitude", "Nose first"), spec.PropellantPct);
            ctx.Record("summary", Rp1.LastSummary);
            yield return Snap.Shot(ctx, "prediction");
            Assert.False(Rp1.LastSummary.StartsWith("Prediction failed") || Rp1.LastSummary.StartsWith("No prediction"), Rp1.LastSummary);
            CopyToResults(ctx, Rp1.LastPredictionPath, "predictedCsv");
        }

        /// <summary>
        /// The thermal model replayed along a flown telemetry CSV: params flownCsv (path), aoa, attitude, propellant.
        /// The written prediction's trajectory is the flight's; diff its parts trace against the flight's to see
        /// what the thermal model alone gets wrong.
        /// </summary>
        [UITest("Reentry_Replay", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 600)]
        public static IEnumerator Reentry_Replay(TestContext ctx)
        {
            Ugui.DismissPopups();
            string csv = ctx.Param("flownCsv") ?? throw new UITestFailure("flownCsv param (path to the flown telemetry CSV) is required");
            if (!File.Exists(csv)) throw new UITestFailure("flownCsv not found: " + csv);
            double aoa = ctx.ParamDouble("aoa", 20);
            double? offset = FlownAtmoTempOffset(csv);
            ctx.Record("atmoTempOffset", offset.HasValue ? offset.Value.ToString("F2") : "");
            ctx.Step($"Replay {Path.GetFileName(csv)} at {aoa} deg on FAR's Reentry tab");
            yield return Rp1.ReplayReentry(csv, aoa, ctx.Param("attitude", "Nose first"),
                                           ctx.Param("propellant") != null ? ctx.ParamDouble("propellant", 0) : (double?)null, offset);
            ctx.Record("summary", Rp1.LastSummary);
            yield return Snap.Shot(ctx, "replay");
            Assert.False(Rp1.LastSummary.StartsWith("Replay failed") || Rp1.LastSummary.StartsWith("No replay"), Rp1.LastSummary);
            CopyToResults(ctx, Rp1.LastPredictionPath, "replayCsv");
            File.Copy(csv, Path.Combine(ctx.ResultsDir, Path.GetFileName(csv)), true);
            string parts = csv.Replace(".csv", "") + ".parts.csv";
            if (File.Exists(parts)) File.Copy(parts, Path.Combine(ctx.ResultsDir, Path.GetFileName(parts)), true);
        }

        /// <summary>The flight's atmosphere temperature offset (K) at the peak of heating (75 km), from its telemetry CSV; null without the column.</summary>
        private static double? FlownAtmoTempOffset(string csv)
        {
            string[] header = null;
            double best = double.NaN, bestDist = double.MaxValue;
            foreach (string line in File.ReadLines(csv))
            {
                if (header == null) { header = line.Split(','); continue; }
                int io = Array.IndexOf(header, "atmoTempOffset"), ia = Array.IndexOf(header, "altitude");
                if (io < 0 || ia < 0) return null;
                string[] c = line.Split(',');
                if (c.Length <= Math.Max(io, ia) || !double.TryParse(c[io], out double o) || !double.TryParse(c[ia], out double alt)) continue;
                double dist = Math.Abs(alt - 75000);
                if (dist < bestDist) { bestDist = dist; best = o; }
            }
            return double.IsNaN(best) ? (double?)null : best;
        }

        /// <summary>The AoA x Pe heatmap sweep; params peMinKm/peMaxKm/peSteps/aoaMin/aoaMax/aoaSteps/apKm/attitude/propellant.</summary>
        [UITest("Reentry_Analyse", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 900)]
        public static IEnumerator Reentry_Analyse(TestContext ctx)
        {
            Ugui.DismissPopups();
            ctx.Step("Sweep AoA x Pe on FAR's Reentry tab");
            var t0 = DateTime.Now;
            yield return Rp1.AnalyseReentry(ctx.ParamDouble("peMinKm", -40), ctx.ParamDouble("peMaxKm", 80), (int)ctx.ParamDouble("peSteps", 20),
                                            ctx.ParamDouble("aoaMin", 0), ctx.ParamDouble("aoaMax", 45), (int)ctx.ParamDouble("aoaSteps", 20),
                                            ctx.ParamDouble("apKm", 250), ctx.Param("attitude", "Nose first"),
                                            ctx.Param("propellant") != null ? ctx.ParamDouble("propellant", 0) : (double?)null);
            ctx.Record("sweepSeconds", Math.Round((DateTime.Now - t0).TotalSeconds, 1));
            ctx.Record("summary", Rp1.LastSummary);
            yield return Snap.Shot(ctx, "heatmap");
            Assert.False(Rp1.LastSummary.StartsWith("Analysis failed"), Rp1.LastSummary);
        }

        [UITest("Reentry_Fly", Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 3000)]
        public static IEnumerator Reentry_Fly(TestContext ctx)
        {
            Ugui.DismissPopups();
            yield return FlyEntry(ctx, ReentryFromApoapsis(ctx), ctx.ParamDouble("aoa", 42), ShieldFirst(ctx), liftDown: false,
                                  holdAttitude: (ctx.Param("hold", "on") ?? "on").ToLowerInvariant() != "off", endAltKm: ctx.ParamDouble("endAltKm", 45), keyPrefix: "");
        }

        /// <summary>One tuning scenario: a craft, the grid to sweep, which cell to fly and how to fly it.</summary>
        private sealed class Scenario
        {
            public string Name, Craft, Pick = "near";
            public Scenario Named(string name) { var c = (Scenario)MemberwiseClone(); c.Name = name; return c; }
            public bool Default = true;               // off: runs only when the "scenario" param names it
            public bool ShieldFirst, HoldAttitude = true;
            public bool RoofUpAndDown = true;         // off for a free-trimming capsule: it cannot choose its roll
            public bool? DescentMode;                 // RO's CoM shifter, for capsules
            public double ApKm, PeMinKm, PeMaxKm, AoAMin, AoAMax;
            /// <summary>Where over the planet the entry happens: with inclination and LAN, the frame stock's occlusion is judged in.</summary>
            public double IncDeg, LanDeg, ArgPeDeg;
            public int PeSteps, AoASteps;
            public double? PropellantPct;
            public double EndAltKm = 45;
        }

        // Hardcoded so a run is reproducible; select a subset with the "scenario" param (substring of Name),
        // which also reaches the scenarios that are off by default.
        private static readonly Scenario[] Scenarios =
        {
            // A capsule has no pitch control: it flies its trim angle, 0 deg until the centre of mass is shifted.
            new Scenario { Name = "capsule-trim", Craft = "Mk1-3 Test", ShieldFirst = true, HoldAttitude = false, RoofUpAndDown = false, DescentMode = false,
                           ApKm = 250, PeMinKm = -40, PeMaxKm = 70, PeSteps = 20, AoAMin = 0, AoAMax = 0, AoASteps = 1, Pick = "near", EndAltKm = 20 },
            // Descent mode on: the CoM offset limit sets the trim angle; the editor solves the limit for each AoA column
            // and the flight is set up with that limit, so the trim it settles at is the check.
            new Scenario { Name = "capsule-descent", Craft = "Mk1-3 Test", ShieldFirst = true, HoldAttitude = false, RoofUpAndDown = false, DescentMode = true,
                           ApKm = 250, PeMinKm = -40, PeMaxKm = 70, PeSteps = 20, AoAMin = -30, AoAMax = 0, AoASteps = 20, Pick = "aoa:-10", EndAltKm = 20 },
            // The spaceplane holds its angle with elevons and RCS; fly the surviving cell closest to a limit.
            // The spaceplane holds its angle with elevons and RCS; fly the surviving cell closest to a limit ...
            new Scenario { Name = "ssto-near", Craft = "Mk2 SSTO Big", ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "near", PropellantPct = 0, EndAltKm = 45 },
            // ... and, when the grid has one, the cell that just loses a single part, to check the loss altitude.
            new Scenario { Name = "ssto-lost", Craft = "Mk2 SSTO Big", ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "lost", PropellantPct = 0, EndAltKm = 45 },
            // The same grid at several fixed angles, each at its own borderline periapsis: the AoA dependence of the model.
            // ~35 min for the three, so off by default: -Params "scenario=ssto-aoa".
            new Scenario { Name = "ssto-aoa20", Craft = "Mk2 SSTO Big", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "aoa:20", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "ssto-aoa30", Craft = "Mk2 SSTO Big", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "aoa:30", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "ssto-aoa40", Craft = "Mk2 SSTO Big", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "aoa:40", PropellantPct = 0, EndAltKm = 45 },
            // The same craft without the structural spine that shrouds the engine cluster (and the gear on it): the
            // engines see the flow directly, so this is the occlusion model with the cones mostly out of the picture.
            new Scenario { Name = "ssto-aoa30-noshroud", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "aoa:30", PropellantPct = 0, EndAltKm = 45 },
            // ... and its hottest entry the shrouded craft survives (Pe 60 at 40 deg, the sweep's best cell), to see
            // whether the bare engines burn in flight at all; the editor loses them in every cell of this grid.
            new Scenario { Name = "ssto-aoa40-noshroud", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            // The same entry flown over different parts of the planet and at different inclinations. Stock judges its
            // convection occlusion in a world-fixed frame, so the bare engines' shading should differ run to run
            // while the tank and wings repeat; "scenario=heading" runs the set (about 7 min each).
            new Scenario { Name = "heading-inc0-argpe0", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            // 45: the world-frame rectangle test repeats every quarter turn, so 90 and 180 are equivalent to 0 for it
            new Scenario { Name = "heading-inc0-argpe45", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, ArgPeDeg = 45,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc0-argpe90", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, ArgPeDeg = 90,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc0-argpe135", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, ArgPeDeg = 135,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc0-argpe180", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, ArgPeDeg = 180,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc30", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, IncDeg = 30,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc60", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, IncDeg = 60,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            new Scenario { Name = "heading-inc90", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, IncDeg = 90,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
            // retrograde: the planet's rotation adds to the airspeed instead of taking from it
            new Scenario { Name = "heading-inc180", Craft = "Mk2 SSTO Big NoShroud", Default = false, ShieldFirst = false, HoldAttitude = true, IncDeg = 180,
                           ApKm = 150, PeMinKm = 60, PeMaxKm = 100, PeSteps = 20, AoAMin = 20, AoAMax = 45, AoASteps = 20, Pick = "cell:40,60", PropellantPct = 0, EndAltKm = 45 },
        };

        /// <summary>
        /// The round trip that tunes the model, for each hardcoded scenario in one game session: sweep the
        /// grid on FAR's Reentry tab, pick a borderline cell, predict it in detail with its CSV, then fly exactly
        /// that entry in a KCT simulation with telemetry. Both CSVs land in the results folder for
        /// `python RP-1/Notes/ReentrySim/compare_traces.py --pred ... --flown ...`; every record is prefixed with
        /// the scenario name. Param scenario = substrings ('+'-separated; run-test.ps1 splits params on commas) of the
        /// scenarios to run (default: those on
        /// by default); repeat = how many times to fly each (2nd and later carry a "-r2", "-r3" suffix), to measure
        /// the run-to-run spread of one cell.
        /// </summary>
        [UITest("Reentry_Borderline", Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk1-3 Test", TimeoutSec = 7200)]
        public static IEnumerator Reentry_Borderline(TestContext ctx)
        {
            string filter = ctx.Param("scenario");
            string[] filters = filter == null ? null : filter.Split('+');
            int repeat = Math.Max(1, (int)ctx.ParamDouble("repeat", 1));
            int ran = 0;
            var failed = new System.Collections.Generic.List<string>();
            var selected = new System.Collections.Generic.List<Scenario>();
            foreach (var sc in Scenarios)
            {
                // a filter that names a scenario exactly takes just that one; otherwise it is a substring
                bool take = filters == null ? sc.Default : Array.Exists(filters, f =>
                    Array.Exists(Scenarios, o => string.Equals(o.Name, f.Trim(), StringComparison.OrdinalIgnoreCase))
                        ? string.Equals(sc.Name, f.Trim(), StringComparison.OrdinalIgnoreCase)
                        : sc.Name.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (!take) continue;
                for (int r = 1; r <= repeat; ++r) selected.Add(r == 1 ? sc : sc.Named(sc.Name + "-r" + r));
            }
            foreach (var sc in selected)
            {
                ctx.Scenario(sc.Name, ran + 1, selected.Count);
                // A scenario that cannot be set up (no such cell in its grid) is recorded and the rest still run.
                var run = RunScenario(ctx, sc);
                bool alive = true;
                while (alive)
                {
                    try { alive = run.MoveNext(); }
                    catch (UITestFailure e)
                    {
                        Log.Info($"[{sc.Name}] failed: {e.Message}");
                        ctx.Record(sc.Name + ".error", e.Message);
                        failed.Add(sc.Name);
                        alive = false;
                    }
                    if (alive) yield return run.Current;
                }
                ++ran;
            }
            Assert.True(ran > 0, $"a scenario matching '{filter}'");
            Assert.True(failed.Count == 0, "scenarios without a failure (failed: " + string.Join(", ", failed) + ")");
        }

        private static IEnumerator RunScenario(TestContext ctx, Scenario sc)
        {
            string k = sc.Name + ".";
            ctx.Step($"[{sc.Name}] Open {sc.Craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(sc.Craft));
            Ugui.DismissPopups();
            if (sc.DescentMode.HasValue) ctx.Record(k + "descentModeShifters", Rp1.SetDescentMode(sc.DescentMode.Value));

            ctx.Step($"[{sc.Name}] Sweep AoA x Pe on FAR's Reentry tab");
            string attitude = sc.ShieldFirst ? "Heat shield first" : "Nose first";
            yield return Rp1.AnalyseReentry(sc.PeMinKm, sc.PeMaxKm, sc.PeSteps, sc.AoAMin, sc.AoAMax, sc.AoASteps, sc.ApKm, attitude, sc.PropellantPct, bothRoof: sc.RoofUpAndDown, incDeg: sc.IncDeg);
            ctx.Record(k + "sweepSummary", Rp1.LastSummary);
            Assert.False(Rp1.LastSummary.StartsWith("Analysis failed"), Rp1.LastSummary);
            Cubes.Dump(ctx, sc.Name + ".cubes.editor.csv");   // the occlusion geometry the sweep ran on
            yield return Snap.Shot(ctx, "heatmap");

            ctx.Step($"[{sc.Name}] Pick the '{sc.Pick}' cell and predict it");
            bool crewed = false;
            foreach (var p in EditorLogic.fetch.ship.Parts) if (p.CrewCapacity > 0) { crewed = true; break; }
            var cell = Rp1.PickBorderlineCell(sc.Pick, crewed ? 12 : 30);
            Assert.NotNull(cell, $"a '{sc.Pick}' cell in the grid");
            Log.Info("borderline cell: " + cell);
            ctx.Record(k + "cell", cell.ToString());
            ctx.Record(k + "cellPeKm", cell.PeKm);
            ctx.Record(k + "cellAoA", cell.AoA);
            ctx.Record(k + "cellAoAHeld", Math.Round(cell.AoAHeld, 1));
            ctx.Record(k + "cellRoofDown", cell.LiftDown);
            ctx.Record(k + "cellLiftUpward", cell.LiftUpward);
            ctx.Record(k + "cellComOffsetPercent", double.IsNaN(cell.ComOffsetPercent) ? (object)"" : Math.Round(cell.ComOffsetPercent * 100, 1));
            ctx.Record(k + "predictedSkinPct", Math.Round(cell.PeakSkinFrac * 100, 1));
            ctx.Record(k + "predictedIntPct", Math.Round(cell.PeakInternalFrac * 100, 1));
            ctx.Record(k + "predictedG", Math.Round(cell.PeakG, 2));
            ctx.Record(k + "predictedLostParts", cell.LostParts);
            ctx.Record(k + "predictedLimitingPart", cell.LimitingPart);
            yield return Rp1.PredictCell(cell);
            ctx.Record(k + "predictSummary", Rp1.LastSummary);
            yield return Snap.Shot(ctx, "prediction");
            CopyToResults(ctx, Rp1.LastPredictionPath, k + "predictedCsv");

            // A cell that trims through the CoM shifter is flown with that offset limit set on the craft.
            if (!double.IsNaN(cell.ComOffsetPercent))
            {
                Rp1.SetDescentMode(true);
                ctx.Record(k + "comShiftersSet", Rp1.SetComOffsetLimit(cell.ComOffsetPercent));
            }

            ctx.Step($"[{sc.Name}] Fly that entry in a KCT simulation");
            var spec = new SimSpec
            {
                // Start at the top of the atmosphere, where the editor's trace starts. A start at 105 km ("nothing
                // happens above it") skips 1000 s of 3-5 kN drag: the craft arrived at 105 km 22 m/s faster and
                // descending 5 m/s slower than the editor's, spent 9 % longer between 95 and 55 km and its interior
                // ran 4 points hotter. "startAltKm=105" for the quick version. Set explicitly every time - the
                // dialog remembers its last value otherwise.
                StartInOrbit = true, Mode = "Reentry", PeKm = cell.PeKm, Arrival = "Apoapsis", ApKm = sc.ApKm, EntryAltKm = ctx.ParamDouble("startAltKm", 140),
                IncDeg = sc.IncDeg, LanDeg = sc.LanDeg, ArgPeDeg = sc.ArgPeDeg,   // all three every time: the dialog remembers its last values
                // A fixed start time: where the sim puts the vessel over the planet moves with the clock, and with it
                // anything stock judges in a world frame. A day after the golden save's own time; "simUT=0" for now.
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = sc.ShieldFirst ? "Retrograde" : "Prograde", LogTelemetry = true, PropellantPct = sc.PropellantPct,
            };
            ctx.Record(k + "incDeg", sc.IncDeg); ctx.Record(k + "lanDeg", sc.LanDeg); ctx.Record(k + "argPeDeg", sc.ArgPeDeg);
            ctx.Record(k + "simUT", spec.UT.HasValue ? (object)spec.UT.Value : "now");
            yield return FlyEntry(ctx, spec, cell.AoA, sc.ShieldFirst, cell.LiftDown, sc.HoldAttitude, sc.EndAltKm, k);
        }

        /// <summary>
        /// Simulates the entry and holds an angle of attack through it with MechJeb SmartASS (SVEL+ nose first, SVEL-
        /// shield first; roll 180 for lift down), logging until breakup, the end altitude or the vehicle is slow.
        /// Records the flown peaks and copies the telemetry into the results folder.
        /// </summary>
        private static IEnumerator FlyEntry(TestContext ctx, SimSpec spec, double aoa, bool shieldFirst, bool liftDown, bool holdAttitude, double endAltKm, string keyPrefix)
        {
            string k = keyPrefix ?? "";
            double flightTimeout = ctx.ParamDouble("flightTimeoutSec", 2400);
            // Rate, not index; above 4x the harness extends the table. 4x, not 16x: at 16x the stack joints jitter
            // under load (the SSTO tank's front face read 49-61 m2 against a steady 47.4 at 1x and 47.8 at 4x, and
            // the AoA hold overshoots late in the entry), worth ~2 points on the interior; 4x and 1x agree.
            double physWarp = ctx.ParamDouble("physWarp", 4);

            ctx.Step("Simulate the reentry");
            // The propellant load applied on the editor ship directly as well as through the sim setup, so a FAR
            // build without the reentry API (an A/B against upstream) still flies the same mass.
            if (spec.PropellantPct.HasValue) ctx.Record(k + "propellantDirect", Rp1.ApplyPropellantLoadDirect(spec.PropellantPct.Value));
            ctx.Record(k + "padUT", Math.Round(Planetarium.GetUniversalTime(), 1));   // the clock the flight scene loads with
            yield return Rp1.ConfigureSim(spec);
            yield return Rp1.StartSim();
            Assert.Equal("Reentry", Rp1.SimMode, "SimOrbitMode");
            var v = FlightGlobals.ActiveVessel;
            // The world frame stock judges its occlusion in: the body sits at directRotAngle in world coordinates
            // once the frame is rotating (CelestialBody.CBUpdate), frozen at the planet's rotation angle when
            // OrbitPhysicsManager switched the frame; InverseRotAngle grows from there.
            ctx.Record(k + "frame.rotating", v.mainBody.inverseRotation);
            ctx.Record(k + "frame.directRotAngle", Math.Round(v.mainBody.directRotAngle, 3));
            ctx.Record(k + "frame.inverseRotAngle", Math.Round(Planetarium.InverseRotAngle, 3));
            ctx.Record(k + "frame.rotationAngle", Math.Round(v.mainBody.rotationAngle, 3));
            ctx.Record(k + "frame.ut", Math.Round(Planetarium.GetUniversalTime(), 1));
            int partsStart = v.parts.Count;
            ctx.Record(k + "spawnAltKm", Math.Round(v.altitude / 1000, 1));
            ctx.Record(k + "partsStart", partsStart);

            ctx.Step(holdAttitude
                         ? $"Hold {aoa} deg AoA through the entry (SmartASS {(shieldFirst ? "SVEL-" : "SVEL+")}{(liftDown ? ", rolled 180" : "")})"
                         : "Let the vehicle find its own trim through the entry (no SAS, no RCS)");
            // The craft spawns with its gear as the editor saved it; the editor analysed it up, and deployed gear
            // stretches the vessel's bounding box, which sets FAR's voxel size for every part. Retract before the
            // voxelization settles (the test craft save with it up already; gearStates below says).
            bool gearUp = (ctx.Param("gearUp", "on") ?? "on").ToLowerInvariant() != "off";
            ctx.Record(k + "gearUp", gearUp ? Ctrl.RetractGear() : 0);
            // FAR voxelizes the fresh vessel in the background from the part transforms. Doing that while RCS is
            // slewing the ship to the AoA skews every part's projected areas (the voxel frame and the meshes are
            // sampled at different attitudes), so let it finish while the vessel still sits quietly on RP-1's
            // prograde hold. The thrusters sit in a stage; switch them on directly rather than staging into them.
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            Cubes.Dump(ctx, k + "cubes.flight.csv");   // the same geometry as the game re-voxelized it
            ctx.Record(k + "gearStates", Ctrl.GearStates());
            bool rcs = holdAttitude && (ctx.Param("rcs", "on") ?? "on").ToLowerInvariant() != "off";
            if (rcs) ctx.Record(k + "rcsThrusters", Ctrl.EnableRcs());
            else v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            ctx.Record(k + "rcs", rcs);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);   // RP-1 left SAS holding prograde/retrograde
            // Parts over their limit stay on (as they do in the editor's solver), so the log shows how far over they
            // go and the parts behind them keep the same occlusion; "ignoreMaxTemp=off" for real losses instead.
            bool ignoreMaxTemp = (ctx.Param("ignoreMaxTemp", "on") ?? "on").ToLowerInvariant() != "off";
            Flight.IgnoreMaxTemperature(ignoreMaxTemp);
            ctx.Record(k + "ignoreMaxTemp", ignoreMaxTemp);
            // RCS burns through its propellant at high physics warp (the hold works the thrusters harder per
            // physics step); with no draw the mass also stays what the editor assumed. "infinitePropellant=off" for real.
            bool infProp = (ctx.Param("infinitePropellant", "on") ?? "on").ToLowerInvariant() != "off";
            Flight.InfinitePropellant(infProp);
            ctx.Record(k + "infinitePropellant", infProp);
            if (holdAttitude) Mj.SmartAssSurfaceProgradeOffset(aoa, roll: liftDown ? 180 : 0, retrograde: shieldFirst);
            else { try { Mj.SmartAssOff(); } catch (UITestFailure) { } }
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            double maxSkinFrac = 0, maxIntFrac = 0, minAlt = double.MaxValue, maxG = 0;
            string hottestSkin = "", hottestInt = "";
            double firstOverAlt = double.NaN;   // where the first part crossed a limit (with the cheat on, where it would have been lost)
            string firstOverPart = "";
            int partsEnd = partsStart;
            double nextLog = 0, aoaAt90 = double.NaN, bankAt90 = double.NaN, aoaAt60 = double.NaN;
            // FAR's prediction API sampled along the entry (what its editor analysis and the prediction API report for
            // the state being flown; the flight's own aero torque is zero at trim), to a CSV next to the telemetry.
            double nextApi = 0;
            var api = new System.Text.StringBuilder("missionTime,altitude,srfSpeed,mach,dynPreskPa,aoa,pitchInput,farApiPitchTorqueKNm,farApiLiftKN,farApiDragKN\n");

            yield return Wait.Until(() =>
            {
                var av = FlightGlobals.ActiveVessel;
                if (av == null || Flight.Dead) return true;
                if (av.missionTime >= nextApi && av.altitude < 100000)
                {
                    nextApi = av.missionTime + 10;
                    double tq = FarApi.PredictedPitchTorqueKNm(av, out double apiLift, out double apiDrag);
                    api.Append(string.Join(",", new[] { av.missionTime.ToString("F1"), av.altitude.ToString("F0"), av.srfSpeed.ToString("F1"), av.mach.ToString("F2"),
                        av.dynamicPressurekPa.ToString("F4"), Flight.AoA.ToString("F2"), av.ctrlState.pitch.ToString("F3"), tq.ToString("F2"), apiLift.ToString("F2"), apiDrag.ToString("F2") })).Append('\n');
                }
                partsEnd = av.parts.Count;
                minAlt = Math.Min(minAlt, av.altitude);
                maxG = Math.Max(maxG, av.geeForce);
                foreach (var p in av.parts)
                {
                    if (p.skinMaxTemp > 0 && p.skinTemperature / p.skinMaxTemp > maxSkinFrac) { maxSkinFrac = p.skinTemperature / p.skinMaxTemp; hottestSkin = p.partInfo?.name ?? p.name; }
                    if (p.maxTemp > 0 && p.temperature / p.maxTemp > maxIntFrac) { maxIntFrac = p.temperature / p.maxTemp; hottestInt = p.partInfo?.name ?? p.name; }
                    if (double.IsNaN(firstOverAlt) && (p.skinMaxTemp > 0 && p.skinTemperature > p.skinMaxTemp || p.maxTemp > 0 && p.temperature > p.maxTemp))
                    {
                        firstOverAlt = av.altitude;
                        firstOverPart = p.partInfo?.name ?? p.name;
                    }
                }
                if (double.IsNaN(aoaAt90) && av.altitude < 90000) { aoaAt90 = Flight.AoA; bankAt90 = Bank(av); }
                if (double.IsNaN(aoaAt60) && av.altitude < 60000) aoaAt60 = Flight.AoA;
                if (av.missionTime >= nextLog)
                {
                    nextLog = av.missionTime + 30;
                    Log.Info($"entry: alt={av.altitude / 1000:F1} km srf={av.srfSpeed:F0} m/s aoa={Flight.AoA:F1} bank={Bank(av):F0} pitch={av.ctrlState.pitch:F2} g={av.geeForce:F2} parts={partsEnd} skin={maxSkinFrac * 100:F0}% ({hottestSkin}) int={maxIntFrac * 100:F0}% ({hottestInt})");
                }
                if (partsEnd < partsStart * 0.7 || partsEnd < partsStart && partsStart <= 4) return true;   // broke up (any loss on a tiny craft)
                if (av.altitude < endAltKm * 1000) return true;
                if (av.altitude < 90000 && av.srfSpeed < 2000) return true;
                return false;
            }, flightTimeout, "reentry to end (breakup, slow, or below the end altitude)");
            if (physWarp > 1) Flight.WarpOff();
            try { Mj.SmartAssOff(); } catch (UITestFailure) { }
            if (!Flight.Dead) Cubes.Dump(ctx, k + "cubes.flight-end.csv");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, k + "farapi.csv"), api.ToString());

            ctx.Record(k + "aoaAt90Km", Math.Round(aoaAt90, 1));
            ctx.Record(k + "bankAt90Km", Math.Round(bankAt90, 1));
            ctx.Record(k + "aoaAt60Km", Math.Round(aoaAt60, 1));
            ctx.Record(k + "partsEnd", partsEnd);
            ctx.Record(k + "brokeUp", partsEnd < partsStart * 0.7 || partsEnd < partsStart && partsStart <= 4 || Flight.Dead);
            ctx.Record(k + "partsLost", partsStart - partsEnd);
            ctx.Record(k + "firstOverLimitAltKm", double.IsNaN(firstOverAlt) ? (object)"" : Math.Round(firstOverAlt / 1000, 1));
            ctx.Record(k + "firstOverLimitPart", firstOverPart);
            ctx.Record(k + "minAltKm", Math.Round(minAlt / 1000, 1));
            ctx.Record(k + "maxG", Math.Round(maxG, 2));
            ctx.Record(k + "maxSkinPct", Math.Round(maxSkinFrac * 100, 1));
            ctx.Record(k + "hottestSkin", hottestSkin);
            ctx.Record(k + "maxIntPct", Math.Round(maxIntFrac * 100, 1));
            ctx.Record(k + "hottestInt", hottestInt);
            var end = FlightGlobals.ActiveVessel;
            if (end != null) { ctx.Record(k + "endAltKm", Math.Round(end.altitude / 1000, 1)); ctx.Record(k + "endSrfSpeed", Math.Round(end.srfSpeed)); }

            ctx.Step("Collect the telemetry");
            string log = Rp1.NewestReentryLog();
            Assert.NotNull(log, "RP-1_ReentryLogs csv written");
            var lines = Rp1.ReadLines(log);
            ctx.Record(k + "telemetryRows", lines.Length - 1);
            Assert.True(lines.Length > 100, $"telemetry has rows ({lines.Length - 1})");
            CopyToResults(ctx, log, k + "flownCsv");
        }
    }
}
