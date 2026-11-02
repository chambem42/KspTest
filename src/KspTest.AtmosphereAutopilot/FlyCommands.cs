using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspTest.AtmosphereAutopilot
{
    /// <summary>Far_ArCruise's flight for the craft in the editor, from a Serve session, and back to the editor.</summary>
    public static class FlyCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [ServeCommand("fly", "the editor's craft launched (a KCT sim in RP-1), air-started level at point=ALT:MACH (3000:0.8) on AtmosphereAutopilot's cruise " +
                             "(course 90), climbed to climbTo=ALT:MACH (7000:0.8; none) and cruised again; each cruise steady (60 m, 4 m/s, 3 m/s for steadySec=8, " +
                             "or settleTimeoutSec=120) then measured over windowSec=10: AoA (mean, SD), pitch input, vertical speed, speed, g; the climb's time, rate, " +
                             "peak input; parts lost. hyperWarp=4 (1: real time). back=off stays in flight")]
        public static IEnumerator Fly(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string craftPath = Nav.LoadedCraft ?? throw new InvalidOperationException("load a craft first");
            if (!Aa.Present) throw new InvalidOperationException("AtmosphereAutopilot is not installed");
            var points = new List<(string phase, double alt, double mach)>();
            string[] pt = (q["point"] ?? "3000:0.8").Split(':');
            points.Add(("cruise", double.Parse(pt[0], Inv), double.Parse(pt[1], Inv)));
            string climbTo = q["climbTo"] ?? "7000:0.8";
            if (!climbTo.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                string[] p2 = climbTo.Split(':');
                points.Add(("cruise2", double.Parse(p2[0], Inv), double.Parse(p2[1], Inv)));
            }
            double D(string k, double d) => q[k] != null ? double.Parse(q[k], Inv) : d;
            double steadySec = D("steadySec", 8), windowSec = D("windowSec", 10), timeout = D("settleTimeoutSec", 120);
            double climbTimeout = D("climbTimeoutSec", 300), hyper = D("hyperWarp", 4);
            answer["craft"] = craftPath;
            answer["farBuild"] = FarSim.FarVersion();

            ctx.Step("Serve fly: launch");
            yield return Nav.EnsureState(ctx, StartState.Flight, craftPath, "Runway");
            yield return Wait.Seconds(5);   // FAR voxelizes the craft after load
            Vessel v = FlightGlobals.ActiveVessel;
            Flight.IgnoreMaxTemperature(true);
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleReactionWheel rw)
                    {
                        rw.wheelState = ModuleReactionWheel.WheelState.Disabled;
                        rw.authorityLimiter = 0;
                    }
            Ctrl.Sas(false);
            Ctrl.Stage();
            CelestialBody body = v.mainBody;
            double SpeedAt(double alt, double m)
            {
                double pr = body.GetPressure(alt);
                return m * body.GetSpeedOfSound(pr, body.GetDensity(pr, body.GetTemperature(alt)));
            }
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            var phases = new List<object>();
            answer["phases"] = phases;
            int parts = v.parts.Count;
            bool broken = false;
            // which parts a break took, and the part nearest its temperature limit (skin or internal) before it
            List<string> ids = null;
            string hottest = null;
            double hotFrac = 0, brokeMach = double.NaN, brokeAlt = double.NaN;
            // a sample every sampleS (game s) while settling: [t, alt m, Mach, AoA, pitch input, vertical speed, thrust kN]
            double sampleS = q["sampleS"] != null ? double.Parse(q["sampleS"], CultureInfo.InvariantCulture) : 0, nextSample = 0;
            var samples = new List<object>();
            void Sample(Vessel w, double ut0_)
            {
                double t = Planetarium.GetUniversalTime();
                if (sampleS <= 0 || t < nextSample) return;
                nextSample = t + sampleS;
                double thrust = 0;
                foreach (Part p in w.parts)
                    foreach (PartModule m in p.Modules)
                        if (m is ModuleEngines e) thrust += e.finalThrust;
                samples.Add(new List<object> { Math.Round(t - ut0_, 1), Math.Round(w.altitude), Math.Round(w.mach, 3), Math.Round(Flight.AoA, 2),
                                               Math.Round(w.ctrlState.pitch, 3), Math.Round(w.verticalSpeed, 1), Math.Round(thrust, 1) });
            }
            void Heat(Vessel w)
            {
                foreach (Part p in w.parts)
                {
                    double f = Math.Max(p.temperature / p.maxTemp, p.skinTemperature / p.skinMaxTemp);
                    if (f > hotFrac) { hotFrac = f; hottest = ServeSession.Id(p); }
                }
                brokeMach = w.mach; brokeAlt = w.altitude;
            }
            try
            {
                var start = new FarFlightProbe.Condition { Name = "air start", AltM = points[0].alt, Mach = points[0].mach, AoADeg = 3, Lat = 0, Lon = LevelDash.DaylightLon(body, points[0].alt) };
                yield return FarFlightProbe.Place(start);
                Flight.PointAtAoA(start.AoADeg);
                Ctrl.RetractGear();
                Ctrl.SetThrottle(1f);
                parts = FlightGlobals.ActiveVessel.parts.Count;
                ids = FlightGlobals.ActiveVessel.parts.Select(ServeSession.Id).ToList();
                if (hyper > 1 && !Flight.HyperWarp(hyper, 1.0)) answer["hyperWarp"] = "TimeControl hyper-warp unavailable: real time";
                double ut0 = Planetarium.GetUniversalTime(), wall0 = Time.realtimeSinceStartup;

                for (int ph = 0; ph < points.Count && !broken; ph++)
                {
                    var (phase, altM, mach) = points[ph];
                    double speed = SpeedAt(altM, mach);
                    Aa.Cruise(FlightGlobals.ActiveVessel, altM, speed, 90);
                    if (ph > 0)
                    {
                        ctx.Step($"Serve fly: climb to {altM / 1000:0.#} km");
                        double t0 = Planetarium.GetUniversalTime(), climbEnd = t0 + climbTimeout, inputMax = double.MinValue;
                        bool reached = false;
                        var wait = new WaitForFixedUpdate();
                        while (Planetarium.GetUniversalTime() < climbEnd)
                        {
                            yield return wait;
                            Vessel w = FlightGlobals.ActiveVessel;
                            if (w == null || w.parts.Count < parts) { broken = true; break; }
                            Heat(w);
                            if (Math.Abs(w.altitude - altM) < 60) { reached = true; break; }
                            inputMax = Math.Max(inputMax, w.ctrlState.pitch);
                        }
                        double dur = Planetarium.GetUniversalTime() - t0;
                        phases.Add(new Dictionary<string, object>
                        {
                            ["phase"] = "climb", ["reached"] = reached, ["durationS"] = Math.Round(dur, 1),
                            ["verticalSpeed"] = Math.Round((altM - points[ph - 1].alt) / Math.Max(dur, 1e-3), 2),
                            ["pitchInputMax"] = Math.Round(inputMax, 3),
                        });
                        if (broken) break;
                    }

                    ctx.Step($"Serve fly: {phase} at {altM / 1000:0.#} km, Mach {mach:0.##}");
                    double since = double.NaN, tSettle = Planetarium.GetUniversalTime(), deadline = tSettle + timeout;
                    bool steady = false;
                    while (Planetarium.GetUniversalTime() < deadline)
                    {
                        Vessel w = FlightGlobals.ActiveVessel;
                        if (w == null || w.parts.Count < parts) { broken = true; break; }
                        Heat(w);
                        Sample(w, ut0);
                        bool now = Math.Abs(w.altitude - altM) < 60 && Math.Abs(w.srfSpeed - speed) < 4 && Math.Abs(w.verticalSpeed) < 3;
                        double ut = Planetarium.GetUniversalTime();
                        if (!now) since = double.NaN;
                        else if (double.IsNaN(since)) since = ut;
                        else if (ut - since >= steadySec) { steady = true; break; }
                        yield return null;
                    }
                    if (broken) break;
                    double settle = Planetarium.GetUniversalTime() - tSettle;
                    var cols = new Dictionary<string, List<double>>();
                    void Add(string k, double x) { if (!cols.TryGetValue(k, out var l)) cols[k] = l = new List<double>(); l.Add(x); }
                    var fixedWait = new WaitForFixedUpdate();
                    double end = Planetarium.GetUniversalTime() + windowSec;
                    while (Planetarium.GetUniversalTime() < end)
                    {
                        yield return fixedWait;
                        Vessel w = FlightGlobals.ActiveVessel;
                        if (w == null || w.parts.Count < parts) { broken = true; break; }
                        Add("aoa", Flight.AoA); Add("input", w.ctrlState.pitch); Add("vs", w.verticalSpeed);
                        Add("speed", w.srfSpeed); Add("alt", w.altitude); Add("g", w.geeForce); Add("mass", w.totalMass);
                    }
                    double M(string k) => cols.TryGetValue(k, out var l) && l.Count > 0 ? Math.Round(l.Average(), 4) : double.NaN;
                    FarFlightProbe.Stats(cols.TryGetValue("aoa", out var al) ? al : new List<double>(), out double aoa, out double aoaSd);
                    phases.Add(new Dictionary<string, object>
                    {
                        ["phase"] = phase, ["steady"] = steady, ["settleS"] = Math.Round(settle, 1),
                        ["aoaDeg"] = Math.Round(aoa, 3), ["aoaSd"] = Math.Round(aoaSd, 3), ["pitchInput"] = M("input"),
                        ["verticalSpeed"] = M("vs"), ["speed"] = M("speed"), ["altM"] = M("alt"), ["g"] = M("g"), ["massT"] = M("mass"),
                    });
                }
                if (hyper > 1)
                    answer["speedup"] = Math.Round((Planetarium.GetUniversalTime() - ut0) / Math.Max(Time.realtimeSinceStartup - wall0, 1e-3), 2);
            }
            finally
            {
                Flight.HyperWarpOff();
                Vessel w = FlightGlobals.ActiveVessel;
                if (w != null) Aa.Off(w);
                FarFlightProbe.ResumeOtherPredictors(trajectories);
            }
            v = FlightGlobals.ActiveVessel;
            answer["broken"] = broken ? (v == null ? "no vessel" : $"{parts - v.parts.Count} parts lost") : null;
            if (broken && ids != null)
            {
                var left = v == null ? new HashSet<string>() : new HashSet<string>(v.parts.Select(ServeSession.Id));
                answer["lost"] = ids.Where(i => !left.Contains(i)).Cast<object>().ToList();
                answer["lastMach"] = Math.Round(brokeMach, 2);
                answer["lastAltM"] = Math.Round(brokeAlt);
            }
            if (sampleS > 0) answer["samples [t, alt, Mach, AoA, pitch input, vs, thrust kN]"] = samples;
            answer["hottest"] = hottest == null ? null : $"{hottest} at {hotFrac:P0} of its limit";
            if (!string.Equals(q["back"], "off", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Step("Serve fly: back to the SPH");
                yield return Nav.EnsureState(ctx, StartState.Sph, craftPath);
                Ugui.DismissPopups();
                answer["scene"] = HighLogic.LoadedScene.ToString();
            }
        }
    }
}
