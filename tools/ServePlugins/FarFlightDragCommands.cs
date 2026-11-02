using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// FAR's drag on the editor craft in flight: launched, engines lit at a throttle, gear up, placed at a Mach and
    /// altitude and held at each angle of attack by an external torque (controls neutral, so no trim drag), the force
    /// FAR applied averaged over the frames, per part and in total, with the engines' thrust.
    /// </summary>
    public static class FarFlightDragCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [ServeCommand("farflightdrag", "[point=16000:2] [aoas=0,3,5,7] [throttle=1] [settle=50] [frames=50] [area=0 (FAR's)] [top=12] " +
                                       "[back=on]: the editor craft flown at the point, held at each AoA with controls neutral and gear up: " +
                                       "Mach, q, drag and lift (kN, and Cd and Cl on the area), the kinematic drag check, thrust, drag by " +
                                       "part kind and the parts with the most drag")]
        public static IEnumerator FarFlightDrag(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string craftPath = Nav.LoadedCraft ?? throw new InvalidOperationException("load a craft first");
            double D(string k, double d) => q[k] != null ? double.Parse(q[k], Inv) : d;
            string[] pt = (q["point"] ?? "16000:2").Split(':');
            double altM = double.Parse(pt[0], Inv), mach = double.Parse(pt[1], Inv);
            double[] aoas = (q["aoas"] ?? "0,3,5,7").Split(',').Select(s => double.Parse(s, Inv)).ToArray();
            double throttle = D("throttle", 1);
            int settle = (int)D("settle", 50), frames = (int)D("frames", 50), top = (int)D("top", 12);
            double area = D("area", 0);
            answer["craft"] = craftPath;

            ctx.Step("farflightdrag: launch");
            yield return Nav.EnsureState(ctx, StartState.Flight, craftPath, "Runway");
            yield return Wait.Seconds(5);
            Vessel v = FlightGlobals.ActiveVessel ?? throw new InvalidOperationException("no vessel in flight");
            // FAR's own reference area (its wing area) unless given
            if (area <= 0)
                area = FarApi.VesselStat(v, "VesselRefArea");
            answer["areaM2"] = Math.Round(area, 2);
            bool[] predictors = FarFlightProbe.QuietOtherPredictors();
            Flight.IgnoreMaxTemperature(true);
            var results = new List<object>();
            try
            {
                Ctrl.Sas(false);
                Ctrl.Stage();
                Ctrl.Attach();
                Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: (float)throttle);
                foreach (double aoa in aoas)
                {
                    var c = new FarFlightProbe.Condition
                    {
                        Name = "aoa " + aoa, AltM = altM, Mach = mach, AoADeg = aoa, Lat = 0, Lon = LevelDash.DaylightLon(v.mainBody, altM)
                    };
                    ctx.Step($"farflightdrag: {c}");
                    yield return FarFlightProbe.Place(c);
                    Ctrl.RetractGear();
                    Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: (float)throttle);
                    yield return FarFlightProbe.SpoolAt(c, throttle);
                    yield return FarFlightProbe.Place(c);
                    FarFlightProbe.SetEngineThrottle(FlightGlobals.ActiveVessel, throttle);

                    var samples = new List<FarFlightProbe.Sample>();
                    var perPart = new Dictionary<Part, double[]>();
                    double thrust = 0;
                    int n = 0;
                    for (int i = 0; i < settle + frames; ++i)
                    {
                        int before = samples.Count;
                        yield return FarFlightProbe.Hold(c, i == 0 ? settle : 0, 1, samples, pin: true, predict: false);
                        if (i == 0 || samples.Count == before) continue;
                        v = FlightGlobals.ActiveVessel;
                        Vector3 velN = ((Vector3)v.srf_velocity).normalized;
                        Vector3 liftDir = Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, velN).normalized;
                        foreach (Part p in v.parts)
                        {
                            Vector3 f = PartForce(p);
                            if (!perPart.TryGetValue(p, out double[] a)) perPart[p] = a = new double[2];
                            a[0] += -Vector3.Dot(f, velN);
                            a[1] += Vector3.Dot(f, liftDir);
                        }
                        thrust += v.parts.SelectMany(p => p.Modules.OfType<ModuleEngines>()).Sum(e => (double)e.finalThrust);
                        ++n;
                    }
                    if (n == 0)
                    {
                        results.Add(new Dictionary<string, object> { ["aoa"] = aoa, ["error"] = "no samples" });
                        continue;
                    }
                    var used = samples.Skip(samples.Count - n).ToList();
                    double qk = used.Average(s => s.QkPa);
                    double drag = used.Average(s => s.Flight[0]), lift = used.Average(s => s.Flight[1]);
                    var kin = used.Where(s => !double.IsNaN(s.Kinematic[0])).Select(s => s.Kinematic[0]).ToList();
                    var kinds = perPart.GroupBy(kv => Kind(kv.Key))
                                       .OrderByDescending(g => g.Sum(kv => kv.Value[0]))
                                       .ToDictionary(g => g.Key, g => (object)$"n {g.Count()}, drag {g.Sum(kv => kv.Value[0]) / n:0.0} kN, lift {g.Sum(kv => kv.Value[1]) / n:0} kN");
                    results.Add(new Dictionary<string, object>
                    {
                        ["aoaSet"] = aoa,
                        ["aoa"] = Math.Round(used.Average(s => s.AoADeg), 3),
                        ["mach"] = Math.Round(used.Average(s => s.Mach), 4),
                        ["altM"] = Math.Round(used.Average(s => s.AltM), 0),
                        ["qKPa"] = Math.Round(qk, 3),
                        ["dragKN"] = Math.Round(drag, 2),
                        ["liftKN"] = Math.Round(lift, 1),
                        ["Cd"] = Math.Round(drag / (qk * area), 5),
                        ["Cl"] = Math.Round(lift / (qk * area), 5),
                        ["kinematicDragKN"] = kin.Count == 0 ? double.NaN : Math.Round(kin.Average() + thrust / n, 2),
                        ["thrustKN"] = Math.Round(thrust / n, 1),
                        ["engineThrottle"] = Math.Round(FarFlightProbe.EngineThrottleOf(FlightGlobals.ActiveVessel), 3),
                        ["massT"] = Math.Round(FlightGlobals.ActiveVessel.totalMass, 2),
                        ["byKind"] = kinds,
                        ["topParts"] = perPart.OrderByDescending(kv => kv.Value[0]).Take(top)
                                              .Select(kv => $"{ServeSession.Id(kv.Key)} drag {kv.Value[0] / n:0.0} lift {kv.Value[1] / n:0.0}").ToList(),
                    });
                }
            }
            finally
            {
                Ctrl.Detach();
                FarFlightProbe.ResumeOtherPredictors(predictors);
            }
            answer["points"] = results;
            if (!string.Equals(q["back"], "off", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Step("farflightdrag: back to the SPH");
                yield return Nav.EnsureState(ctx, StartState.Sph, craftPath);
                Ugui.DismissPopups();
            }
        }

        // FAR's force on the part this frame (kN, world): its voxel (body) force, and its wing force unless shielded
        private static Vector3 PartForce(Part p)
        {
            Vector3 f = Vector3.zero;
            foreach (PartModule m in p.Modules)
            {
                Type t = m.GetType();
                if (IsFarWing(t))
                {
                    if (t.GetField("isShielded")?.GetValue(m) is bool b && b) continue;
                    f += (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                }
                else if (t.Name == "FARAeroPartModule")
                {
                    f += (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                }
            }
            return f;
        }

        private static bool IsFarWing(Type t)
        {
            for (; t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "FARWingAerodynamicModel") return true;
            return false;
        }

        private static string Kind(Part p)
        {
            string n = p.partInfo.name;
            if (p.Modules.Contains("FARControllableSurface")) return "control surfaces";
            if (p.Modules.Contains("FARWingAerodynamicModel")) return "wing panels";
            if (p.Modules.OfType<ModuleEngines>().Any()) return "engines";
            if (p.Modules.Contains("ModuleResourceIntake") || n.Contains("Intake")) return "intakes";
            if (n.Contains("RFTank")) return "tanks (fuselage, nacelles)";
            return "other";
        }
    }
}
