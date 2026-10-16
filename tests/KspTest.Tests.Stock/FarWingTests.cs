using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>Wing by wing, FAR's flight against its prediction at the same state (builds with per-slot scratch).</summary>
    public static class FarWingTests
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int PredictionSlotOnMainThread = 63;

        private static readonly string[] WingFields = { "stall", "Cl", "Cd", "AoAmax", "rawAoAmax", "liftslope", "FinalLiftSlope", "ClIncrementFromRear", "effective_AR" };
        private static readonly string[] InteractionFields = { "EffectiveUpstreamStall", "EffectiveUpstreamAoA", "EffectiveUpstreamAoAMax", "EffectiveUpstreamLiftSlope", "EffectiveUpstreamInfluence" };

        /// <summary>
        /// Which wing does the prediction model differently from the flight? Flying at the first cross-condition
        /// target (surfaces locked, shielding resettled, stall zeroed, attitude held for `settle` frames), in one frame
        /// the vessel's own state is predicted on the main thread (the prediction slot 63), and every wing's working
        /// state in the flight's slot 0 is set against slot 63: stall, Cl, Cd, stall angle, lift slope, the upstream
        /// values read from its neighbors. Each wing's lift difference is estimated as q S dCl. Writes wings.md, largest
        /// difference first. Params: craft, settle (75).
        /// </summary>
        [UITest("Far_WingBreakdown", StartsAt = StartState.Flight, Craft = "Stearwing A300", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator Far_WingBreakdown(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            var inv = CultureInfo.InvariantCulture;
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            Flight.IgnoreMaxTemperature(true);
            Ctrl.SetThrottle(0f);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            FarApi.SetMaxDeflect(v, 0);
            var c = new FarFlightProbe.Condition { Name = "subsonic", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 0, Lon = -60 };

            ctx.Step($"Flying at {c}");
            yield return FarFlightProbe.Place(c);
            Ctrl.RetractGear();
            var none = new List<FarFlightProbe.Sample>();
            yield return FarFlightProbe.Hold(c, 400, 0, none, predict: false);
            var settled = new bool[1];
            yield return FarApi.ResettleShielding(FlightGlobals.ActiveVessel, settled);
            yield return FarFlightProbe.Place(c);
            FarApi.ResetStall(FlightGlobals.ActiveVessel);
            yield return FarFlightProbe.Hold(c, (int)ctx.ParamDouble("settle", 75), 0, none, predict: false);

            ctx.Step("Predict the own state, compare slot 0 with slot 63");
            v = FlightGlobals.ActiveVessel;
            FarApi.ClearMemo(v);
            FarApi.PredictAtFull(v, v.srf_velocity, v.latitude, v.longitude, v.altitude, Planetarium.GetUniversalTime(), out Vector3 fPred, out _);
            FarApi.FlightForceAndTorque(v, out Vector3 fFlight, out _);
            double q = v.dynamicPressurekPa;
            Vector3 velN = ((Vector3)v.srf_velocity).normalized;
            Vector3 liftDir = Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, velN).normalized;
            double liftFlight = Vector3.Dot(fFlight, liftDir), liftPred = Vector3.Dot(fPred, liftDir);
            ctx.Record("liftKN", $"flight {liftFlight:F1}, prediction {liftPred:F1} ({100 * (liftPred - liftFlight) / liftFlight:+0.0;-0.0} %)");
            ctx.Record("shielded", FarApi.ShieldedWings(v));

            var rows = new List<(string name, bool shielded, double s, Dictionary<string, (double f, double p)> vals, double dLift)>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    object s0 = Slot(m, 0), s63 = Slot(m, PredictionSlotOnMainThread);
                    if (s0 == null) continue;
                    object wi = FindField(m.GetType(), "wingInteraction")?.GetValue(m);
                    object i0 = wi == null ? null : Slot(wi, 0), i63 = wi == null ? null : Slot(wi, PredictionSlotOnMainThread);
                    var vals = new Dictionary<string, (double f, double p)>();
                    foreach (string n in WingFields)
                        vals[n] = (Num(s0, n), Num(s63, n));
                    foreach (string n in InteractionFields)
                        vals[n] = (Num(i0, n), Num(i63, n));
                    double area = Convert.ToDouble(FindField(m.GetType(), "S")?.GetValue(m) ?? double.NaN);
                    bool shielded = FindField(m.GetType(), "isShielded")?.GetValue(m) is bool b && b;
                    double dLift = shielded ? 0 : q * area * (vals["Cl"].p - vals["Cl"].f);
                    rows.Add(($"{p.partInfo.name}#{p.flightID % 10000}", shielded, area, vals, dLift));
                }
            FarFlightProbe.ResumeOtherPredictors(trajectories);
            Assert.True(rows.Count > 0, "wings with per-slot scratch (a FAR build with thread slots)");
            ctx.Record("wings", rows.Count);
            ctx.Record("sumWingLiftDiffKN", Math.Round(rows.Sum(r => r.dLift), 2));

            var md = new StringBuilder($"# Wing by wing: flight (slot 0) against prediction (slot 63)\n\n");
            md.Append($"Vessel {v.vesselName} at {c}, q {q:F2} kPa. Lift: flight {liftFlight:F1} kN, prediction {liftPred:F1} kN. ");
            md.Append($"Sum over unshielded wings of q S dCl: {rows.Sum(r => r.dLift):F1} kN.\n\n");
            string[] cols = WingFields.Concat(InteractionFields).ToArray();
            md.Append("| wing | shielded | S | q S dCl kN | " + string.Join(" | ", cols) + " |\n|" + string.Concat(Enumerable.Repeat("---|", cols.Length + 4)) + "\n");
            foreach (var r in rows.OrderByDescending(r => Math.Abs(r.dLift)))
            {
                md.Append($"| {r.name} | {(r.shielded ? "yes" : "")} | {r.s.ToString("0.###", inv)} | {r.dLift.ToString("+0.00;-0.00", inv)} | ");
                md.Append(string.Join(" | ", cols.Select(n =>
                {
                    var (f, pr) = r.vals[n];
                    string F(double x) => double.IsNaN(x) ? "-" : x.ToString("0.####", inv);
                    return f.Equals(pr) ? F(f) : $"**{F(f)} / {F(pr)}**";
                })));
                md.Append(" |\n");
            }
            md.Append("\nCells where the two differ show flight / prediction.\n");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "wings.md"), md.ToString());
        }

        /// <summary>
        /// Which part's voxel sections does the prediction model differently from the flight? Flying at the first
        /// cross-condition target (set up as Far_WingBreakdown), in one frame: each part's body force FAR applied in flight
        /// (FARAeroPartModule.worldSpaceAeroForce, which includes an intake's ram drag), its flight local velocity and
        /// its transform scale; then the vessel's own state predicted on the main thread with FarSectionCapture armed,
        /// giving each part's section force in the prediction and the local velocity it was given. Writes body.md, lift
        /// and drag per part, largest lift difference first. Params: craft, settle (75).
        /// </summary>
        [UITest("Far_BodyBreakdown", StartsAt = StartState.Flight, Craft = "Stearwing A300", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator Far_BodyBreakdown(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            var inv = CultureInfo.InvariantCulture;
            Assert.True(FarSectionCapture.EnsurePatched(), "FAR's SimulatedForceContext.ApplyForce to capture");
            FarFlightProbe.HoldMode = ctx.Param("hold", "torque");
            ctx.Record("holdMode", FarFlightProbe.HoldMode);
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            Flight.IgnoreMaxTemperature(true);
            Ctrl.SetThrottle(0f);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            FarApi.SetMaxDeflect(v, 0);
            var c = new FarFlightProbe.Condition { Name = "subsonic", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 0, Lon = -60 };

            ctx.Step($"Flying at {c}");
            yield return FarFlightProbe.Place(c);
            Ctrl.RetractGear();
            var none = new List<FarFlightProbe.Sample>();
            yield return FarFlightProbe.Hold(c, 400, 0, none, predict: false);
            var settled = new bool[1];
            yield return FarApi.ResettleShielding(FlightGlobals.ActiveVessel, settled);
            yield return FarFlightProbe.Place(c);
            FarApi.ResetStall(FlightGlobals.ActiveVessel);
            yield return FarFlightProbe.Hold(c, (int)ctx.ParamDouble("settle", 75), 0, none, predict: false);

            ctx.Step("Flight body force per part, then the prediction's per part");
            v = FlightGlobals.ActiveVessel;
            Vector3 velN = ((Vector3)v.srf_velocity).normalized;
            Vector3 liftDir = Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, velN).normalized;
            var flight = new Dictionary<Part, (Vector3 force, Vector3 localVel, Vector3 scale)>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.GetType().Name != "FARAeroPartModule") continue;
                    var f = (Vector3)m.GetType().GetField("worldSpaceAeroForce").GetValue(m);
                    var lv = FindField(m.GetType(), "partLocalVel")?.GetValue(m) is Vector3 x ? x : Vector3.zero;
                    flight[p] = (f, lv, p.partTransform != null ? p.partTransform.lossyScale : Vector3.one);
                }
            FarApi.ClearMemo(v);
            FarSectionCapture.Arm();
            FarApi.PredictAtFull(v, v.srf_velocity, v.latitude, v.longitude, v.altitude, Planetarium.GetUniversalTime(), out Vector3 fPred, out _);
            FarSectionCapture.Disarm();
            FarApi.FlightForceAndTorque(v, out Vector3 fFlight, out _);
            FarFlightProbe.ResumeOtherPredictors(trajectories);

            double Lift(Vector3 f) => Vector3.Dot(f, liftDir);
            double Drag(Vector3 f) => -Vector3.Dot(f, velN);
            var rows = new List<(string name, double lf, double lp, double df, double dp, Vector3 lvf, Vector3 lvp, Vector3 scale, int calls)>();
            foreach (var kv in flight)
            {
                FarSectionCapture.Parts.TryGetValue(kv.Key, out var pp);
                Vector3 pf = pp != null ? (Vector3)pp.Force : Vector3.zero;
                if (kv.Value.force == Vector3.zero && pf == Vector3.zero) continue;
                rows.Add(($"{kv.Key.partInfo.name}#{kv.Key.flightID % 10000}", Lift(kv.Value.force), Lift(pf), Drag(kv.Value.force), Drag(pf),
                          kv.Value.localVel, pp?.LocalVel ?? Vector3.zero, kv.Value.scale, pp?.Calls ?? 0));
            }
            double bodyLiftF = rows.Sum(r => r.lf), bodyLiftP = rows.Sum(r => r.lp);
            ctx.Record("totalLiftKN", $"flight {Lift(fFlight):F1}, prediction {Lift(fPred):F1}");
            ctx.Record("bodyLiftKN", $"flight {bodyLiftF:F1}, prediction {bodyLiftP:F1} (difference {bodyLiftP - bodyLiftF:+0.0;-0.0})");
            ctx.Record("bodyDragKN", $"flight {rows.Sum(r => r.df):F1}, prediction {rows.Sum(r => r.dp):F1}");
            ctx.Record("partsWithBodyForce", rows.Count);

            string V(Vector3 x) => $"({x.x.ToString("0.##", inv)}, {x.y.ToString("0.##", inv)}, {x.z.ToString("0.##", inv)})";
            var md = new StringBuilder("# Body sections: flight against prediction, per part\n\n");
            md.Append($"Vessel {v.vesselName} at {c}. Total lift flight {Lift(fFlight):F1} kN, prediction {Lift(fPred):F1} kN. ");
            md.Append($"Body (voxel sections) lift flight {bodyLiftF:F1}, prediction {bodyLiftP:F1}; drag flight {rows.Sum(r => r.df):F1}, prediction {rows.Sum(r => r.dp):F1} kN.\n\n");
            md.Append("| part | lift flight | lift pred | d lift | drag flight | drag pred | local vel flight | local vel pred | transform scale | section calls |\n|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in rows.OrderByDescending(r => Math.Abs(r.lp - r.lf)))
                md.Append($"| {r.name} | {r.lf:F2} | {r.lp:F2} | {r.lp - r.lf:+0.00;-0.00} | {r.df:F2} | {r.dp:F2} | {V(r.lvf)} | {V(r.lvp)} | {V(r.scale)} | {r.calls} |\n");
            // The wings: each one's flight force (worldSpaceForce, shielded ones apply none) against the force its last
            // PrecomputeCenterOfLift call returned in the prediction.
            // The velocity each wing flies in: its rigidbody's (flight) against the vessel's (prediction), as the angle
            // between them and the part's velocity along the wing's normal relative to the vessel's.
            Vector3 vesselVel = v.srf_velocity;
            var wingRows = new List<(string name, bool shielded, double lf, double lp, double df, double dp, int calls, double angleDeg, double normalMs)>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (FindField(m.GetType(), "worldSpaceForce") == null || FindField(m.GetType(), "wingInteraction") == null) continue;
                    bool shielded = FindField(m.GetType(), "isShielded")?.GetValue(m) is bool b && b;
                    Vector3 wf = shielded ? Vector3.zero : (Vector3)FindField(m.GetType(), "worldSpaceForce").GetValue(m);
                    FarSectionCapture.Wings.TryGetValue(m, out var pw);
                    double angle = double.NaN, normal = double.NaN;
                    if (p.Rigidbody != null)
                    {
                        Vector3 pv = p.Rigidbody.velocity + (Vector3)Krakensbane.GetFrameVelocity();
                        angle = Vector3.Angle(pv, vesselVel);
                        normal = Vector3.Dot(pv - vesselVel, p.partTransform.forward);
                    }
                    wingRows.Add(($"{p.partInfo.name}#{p.flightID % 10000}", shielded, Lift(wf), Lift((Vector3)pw.force), Drag(wf), Drag((Vector3)pw.force), pw.calls, angle, normal));
                }
            ctx.Record("wingLiftKN", $"flight {wingRows.Sum(r => r.lf):F1}, prediction {wingRows.Sum(r => r.lp):F1}");
            ctx.Record("wingDragKN", $"flight {wingRows.Sum(r => r.df):F1}, prediction {wingRows.Sum(r => r.dp):F1}");
            md.Append($"\n## Wings: flight force against the prediction's last PrecomputeCenterOfLift\n\nLift flight {wingRows.Sum(r => r.lf):F1}, prediction {wingRows.Sum(r => r.lp):F1}; ");
            md.Append($"drag flight {wingRows.Sum(r => r.df):F1}, prediction {wingRows.Sum(r => r.dp):F1} kN.\n\n");
            md.Append("| wing | shielded | lift flight | lift pred | d lift | drag flight | drag pred | calls | part vel vs vessel vel, deg | part vel along wing normal, m/s |\n|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in wingRows.OrderByDescending(r => Math.Abs(r.lp - r.lf)))
                md.Append($"| {r.name} | {(r.shielded ? "yes" : "")} | {r.lf:F2} | {r.lp:F2} | {r.lp - r.lf:+0.00;-0.00} | {r.df:F2} | {r.dp:F2} | {r.calls} | {r.angleDeg:F3} | {r.normalMs:+0.000;-0.000} |\n");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "body.md"), md.ToString());
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        // The per-slot scratch object of a wing module or wing interaction, or null.
        private static object Slot(object o, int slot)
        {
            if (o == null) return null;
            var arr = FindField(o.GetType(), "_scratchSlots")?.GetValue(o) as Array;
            return arr != null && slot < arr.Length ? arr.GetValue(slot) : null;
        }

        private static double Num(object o, string field)
        {
            if (o == null) return double.NaN;
            object v = FindField(o.GetType(), field)?.GetValue(o);
            if (v == null)
            {
                PropertyInfo p = o.GetType().GetProperty(field, Inst);
                v = p?.GetValue(o, null);
            }
            try { return v == null ? double.NaN : Convert.ToDouble(v); }
            catch (Exception) { return double.NaN; }
        }
    }
}
