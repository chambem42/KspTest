using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// A top-speed run: the vessel in flight air-started (<see cref="FarFlightProbe.Place" />), full throttle, held level by a
    /// simple attitude controller (no autopilot mod), its speed, thrust and acceleration sampled. The answer has the samples,
    /// the peak Mach, the Mach trend over the last 20 s, what broke off and the hottest part at the end.
    /// </summary>
    public static class LevelDash
    {
        public sealed class Options
        {
            public double AltM = 15000, Mach = 2, AoADeg = 4;
            /// <summary>How long the throttle stays open, game seconds.</summary>
            public double HoldSec = 120;
            /// <summary>A sample every SampleSec game seconds.</summary>
            public double SampleSec = 5;
            /// <summary>TimeControl hyper-warp at this rate (physics step unchanged); 1 or less for real time.</summary>
            public double HyperWarp = 4;
            /// <summary>Parts' temperature limits kept (a part that overheats explodes and ends the run); off, they are ignored.</summary>
            public bool Heat;
        }

        /// <summary>
        /// The run on the active vessel (just launched: staged here to light its engines); answer gets its results.
        /// Leaves the vessel in flight with the controls detached.
        /// </summary>
        public static IEnumerator Run(TestContext ctx, Options o, Dictionary<string, object> answer)
        {
            Vessel v = FlightGlobals.ActiveVessel ?? throw new InvalidOperationException("no vessel in flight");
            Flight.IgnoreMaxTemperature(!o.Heat);
            Ctrl.Sas(false);
            Ctrl.Stage();
            CelestialBody body = v.mainBody;
            double Sound(double alt)
            {
                double pr = body.GetPressure(alt);
                return body.GetSpeedOfSound(pr, body.GetDensity(pr, body.GetTemperature(alt)));
            }
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            int parts = v.parts.Count;
            bool broken = false;
            var samples = new List<object>();
            var speeds = new List<(double t, double s)>();
            try
            {
                var start = new FarFlightProbe.Condition { Name = "dash start", AltM = o.AltM, Mach = o.Mach, AoADeg = o.AoADeg, Lat = 0, Lon = DaylightLon(body, o.AltM) };
                yield return FarFlightProbe.Place(start);
                Flight.PointAtAoA(o.AoADeg);
                Ctrl.RetractGear();
                Ctrl.Attach();
                Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: 1);
                v = FlightGlobals.ActiveVessel;
                parts = v.parts.Count;
                if (o.HyperWarp > 1 && !Flight.HyperWarp(o.HyperWarp, 1.0)) answer["hyperWarp"] = "TimeControl hyper-warp unavailable: real time";

                Vector3d Up() => (v.CoMD - body.position).normalized;
                double Pitch() => 90 - Vector3.Angle(v.ReferenceTransform.up, Up());
                double Roll() => 90 - Vector3.Angle(v.ReferenceTransform.right, Up());
                Vector3d fwd0 = Vector3d.Exclude(Up(), (Vector3d)v.ReferenceTransform.up).normalized;
                Vector3d right0 = Vector3d.Cross(Up(), fwd0).normalized;     // Unity: up x forward = right
                double Heading()
                {
                    Vector3d f = Vector3d.Exclude(Up(), (Vector3d)v.ReferenceTransform.up).normalized;
                    return Math.Atan2(Vector3d.Dot(f, right0), Vector3d.Dot(f, fwd0)) * 180 / Math.PI;
                }
                double t0 = Planetarium.GetUniversalTime(), lastT = t0, nextSample = t0;
                double cmd = Pitch(), lastPitch = cmd, lastRoll = Roll(), lastHdg = Heading(), lastSpeed = v.srfSpeed, accel = 0;
                var wait = new WaitForFixedUpdate();
                ctx.Step($"Dash: full throttle at {o.AltM / 1000:0.#} km");
                while (Planetarium.GetUniversalTime() - t0 < o.HoldSec)
                {
                    yield return wait;
                    v = FlightGlobals.ActiveVessel;
                    if (v == null || v.parts.Count < parts) { broken = true; break; }
                    double t = Planetarium.GetUniversalTime(), dt = Math.Max(t - lastT, 1e-3);
                    double pitch = Pitch(), roll = Roll(), hdg = Heading();
                    double pr = (pitch - lastPitch) / dt, rr = (roll - lastRoll) / dt, hr = (hdg - lastHdg) / dt;
                    // the attitude commanded drifts to null the vertical speed (0.02 deg/s per m/s, at most 1 deg/s)
                    cmd += dt * Math.Max(-1, Math.Min(1, -0.02 * v.verticalSpeed));
                    // gains scaled by dynamic pressure: tuned at 12 kPa (a takeoff's), less authority asked for at Mach 2+
                    double kq = Math.Min(1, 12.0 / Math.Max(v.dynamicPressurekPa, 1));
                    float pIn = Mathf.Clamp((float)(kq * (0.3 * (cmd - pitch) - 0.6 * pr)), -1f, 1f);
                    Ctrl.Pitch = pIn;
                    Ctrl.Roll = Mathf.Clamp((float)(kq * (0.05 * roll + 0.1 * rr)), -1f, 1f);
                    Ctrl.Yaw = Mathf.Clamp((float)(kq * (-0.2 * hdg - 0.4 * hr)), -1f, 1f);
                    accel = 0.9 * accel + 0.1 * (v.srfSpeed - lastSpeed) / dt;
                    lastPitch = pitch; lastRoll = roll; lastHdg = hdg; lastT = t; lastSpeed = v.srfSpeed;
                    speeds.Add((t - t0, v.srfSpeed / Sound(v.altitude)));
                    if (t >= nextSample)
                    {
                        nextSample += o.SampleSec;
                        double thrust = 0;
                        foreach (Part p in v.parts)
                            foreach (PartModule m in p.Modules)
                                if (m is ModuleEngines e) thrust += e.finalThrust;
                        samples.Add(new List<object> { Math.Round(t - t0, 1), Math.Round(v.altitude), Math.Round(v.srfSpeed / Sound(v.altitude), 3),
                                                       Math.Round(v.srfSpeed, 1), Math.Round(Flight.AoA, 2), Math.Round(pIn, 2), Math.Round(v.verticalSpeed, 1),
                                                       Math.Round(thrust, 1), Math.Round(accel, 2) });
                    }
                }
            }
            finally
            {
                Ctrl.Detach();
                Flight.HyperWarpOff();
                FarFlightProbe.ResumeOtherPredictors(trajectories);
            }
            answer["samples [t, alt, Mach, speed, AoA, pitch input, vs, thrust kN, accel]"] = samples;
            if (speeds.Count > 0)
            {
                answer["peakMach"] = Math.Round(speeds.Max(s => s.s), 3);
                double tEnd = speeds[speeds.Count - 1].t;
                var tail = speeds.Where(s => s.t >= tEnd - 20).ToList();
                if (tail.Count > 2)
                    answer["machPerMinLast20s"] = Math.Round((tail[tail.Count - 1].s - tail[0].s) / Math.Max(tail[tail.Count - 1].t - tail[0].t, 1e-3) * 60, 3);
            }
            v = FlightGlobals.ActiveVessel;
            answer["broken"] = broken ? (v == null ? "no vessel" : $"{parts - v.parts.Count} parts lost") : null;
            if (v != null)
            {
                Part hot = null; double frac = 0;
                foreach (Part p in v.parts)
                {
                    double f = Math.Max(p.temperature / p.maxTemp, p.skinTemperature / p.skinMaxTemp);
                    if (f > frac) { frac = f; hot = p; }
                }
                if (hot != null) answer["hottestAtEnd"] = $"{hot.partInfo.name}_{hot.craftID} at {frac:P0} of its limit";
            }
        }

        /// <summary>A longitude in daylight over ground well below altM, on the equator (Far_ArCruise's air starts).</summary>
        public static double DaylightLon(CelestialBody body, double altM)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || sun == body) return -60;
            double lon = body.GetLongitude(sun.position) + 30;
            for (int i = 0; i < 36; ++i, lon += 10)
                if (body.TerrainAltitude(0, lon) < altM - 1500)
                    return ((lon + 540) % 360) - 180;
            return -60;
        }
    }
}
