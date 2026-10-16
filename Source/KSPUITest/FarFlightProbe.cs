using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// FAR's prediction API against FAR in flight, at the same state. The vessel is put flying at a condition (Mach,
    /// altitude, heading, angle of attack), held there with no rotation, and every physics frame the force and torque
    /// FAR applied (the gold standard) are compared with what FARAPI.CalculateVesselAeroForcesAt predicts for the state
    /// the vessel measured in that frame - its own velocity, position and attitude, not the ones asked for - so
    /// whatever the vessel really did, the prediction is judged against exactly that.
    /// </summary>
    public static class FarFlightProbe
    {
        public sealed class Condition
        {
            public string Name;
            public double Mach, AltM, AoADeg;
            public double Lat, Lon, HeadingDeg = 90;
            /// <summary>Airspeed (m/s) to place at instead of the Mach, where there is no speed of sound (vacuum).</summary>
            public double SpeedMs;

            public override string ToString() =>
                string.Format(CultureInfo.InvariantCulture, "{0}: Mach {1:0.##}, {2:0.#} km, AoA {3:0.#}", Name, Mach, AltM / 1000, AoADeg);
        }

        private static readonly string[] TrajectoriesSwitches = { "DisplayTrajectories", "AlwaysUpdate" };

        /// <summary>
        /// Stops Trajectories computing (it asks FAR's prediction API for a whole grid of conditions, and a prediction
        /// leaves state on the wings that the next physics frame can read, so the flight measured would depend on
        /// it). Returns the switches' old values for <see cref="ResumeOtherPredictors"/>, null without Trajectories.
        /// </summary>
        public static bool[] QuietOtherPredictors()
        {
            if (TrajApi.StaticBool("Trajectories.Settings", "DisplayTrajectories") == null)
                return null;
            var old = new bool[TrajectoriesSwitches.Length];
            for (int i = 0; i < old.Length; ++i)
            {
                old[i] = TrajApi.StaticBool("Trajectories.Settings", TrajectoriesSwitches[i]) ?? false;
                TrajApi.SetStaticBool("Trajectories.Settings", TrajectoriesSwitches[i], false);
            }
            return old;
        }

        public static void ResumeOtherPredictors(bool[] old)
        {
            if (old == null) return;
            for (int i = 0; i < old.Length; ++i)
                TrajApi.SetStaticBool("Trajectories.Settings", TrajectoriesSwitches[i], old[i]);
        }

        /// <summary>
        /// How a hold keeps the attitude. "torque" (the default): a rigid-body angular acceleration given to every
        /// part as force and torque (Flight.TorqueHoldAoA), loading no joint. "steer": every part's angular velocity
        /// set toward the attitude (Flight.SteerToAoA). "teleport": the attitude set every frame (Flight.PointAtAoA),
        /// as the probe first did - which puts every part back at its as-built pose each frame, so flexible wings fly
        /// mid-spring at another angle than the vessel. The first two teleport only when more than 1 deg off, just
        /// after a placement.
        /// </summary>
        public static string HoldMode = "torque";

        private static void Pin(double aoaDeg)
        {
            if (HoldMode == "teleport" || Flight.AttitudeErrorDeg(aoaDeg) > 1)
            {
                Flight.PointAtAoA(aoaDeg);
                foreach (Part part in FlightGlobals.ActiveVessel.parts)
                    if (part.Rigidbody != null)
                        part.Rigidbody.angularVelocity = Vector3.zero;
            }
            else if (HoldMode == "steer")
            {
                Flight.SteerToAoA(aoaDeg);
            }
            else
            {
                Flight.TorqueHoldAoA(aoaDeg);
            }
        }

        /// <summary>Force components (kN) and moments (kN m), in the order of <see cref="ComponentNames"/>.</summary>
        public static readonly string[] ComponentNames = { "drag", "lift", "side", "pitch", "yaw", "roll" };

        public sealed class Sample
        {
            public double UT, AltM, Airspeed, Mach, AoADeg, QkPa, ForceKN;
            /// <summary>The game's air density at the vessel (what FAR flies on) and FAR's prediction density there.</summary>
            public double RhoFlight, RhoPredicted;
            public double Lat, Lon;
            /// <summary>The control state the vessel flew on in that frame (after any autopilot).</summary>
            public double CtrlPitch, CtrlYaw, CtrlRoll, Throttle;
            /// <summary>Vertical speed (m/s), and the pitch torque (kN m) the hold applied that frame (NaN unpinned).</summary>
            public double VerticalSpeed, HoldPitchKNm;
            /// <summary>
            /// The running engines' mean actual throttle (they lag the command): what to hold to fly on the same thrust
            /// - an autopilot's command averages to something else when the engines cannot follow it - in the units of the
            /// throttle command (a thrust limiter taken out). NaN without any.
            /// </summary>
            public double EngineThrottle;
            /// <summary>Surface velocity in the control point's frame.</summary>
            public Vector3 VelLocal;
            public readonly double[] Flight = new double[6], Predicted = new double[6];
            /// <summary>
            /// Drag, lift and side force (kN) from the vessel's own acceleration over the frame: mass x (acceleration -
            /// gravity - the rotating frame's Coriolis and centrifugal terms). A check on Flight that does not depend on
            /// which FAR modules are summed. NaN on the first frame.
            /// </summary>
            public readonly double[] Kinematic = { double.NaN, double.NaN, double.NaN };
        }

        /// <summary>
        /// A condition flown and measured (the gold standard), reduced to one state - the mean velocity in the vessel's
        /// own frame, place and time - that a prediction can be asked for from anywhere else.
        /// </summary>
        public sealed class Target
        {
            public Condition C;
            public readonly List<Sample> Samples = new List<Sample>();
            public Vector3 VelLocal;
            public double Lat, Lon, AltM, UT, ForceKN, RhoFlight, RhoFar;
            public readonly double[] Flight = new double[6], FlightSd = new double[6];
            public readonly double[] Kinematic = new double[3];

            public void Reduce(CelestialBody body)
            {
                int n = Samples.Count;
                VelLocal = Vector3.zero;
                foreach (Sample s in Samples) VelLocal += s.VelLocal / n;
                Lat = Samples.Average(s => s.Lat);
                Lon = Samples.Average(s => s.Lon);
                AltM = Samples.Average(s => s.AltM);
                UT = Samples.Average(s => s.UT);
                ForceKN = Samples.Average(s => s.ForceKN);
                RhoFlight = Samples.Average(s => s.RhoFlight);
                RhoFar = FarApi.HasPredictAt ? FarApi.FarDensityAt(body, Lat, Lon, AltM, UT) : double.NaN;
                for (int k = 0; k < 6; ++k)
                {
                    int kk = k;
                    Stats(Samples.Select(s => s.Flight[kk]), out Flight[k], out FlightSd[k]);
                }
                for (int k = 0; k < 3; ++k)
                {
                    int kk = k;
                    Stats(Samples.Where(s => !double.IsNaN(s.Kinematic[kk])).Select(s => s.Kinematic[kk]), out Kinematic[k], out _);
                }
            }
        }

        /// <summary>
        /// Puts the active vessel at a latitude, longitude and altitude with its surface velocity the condition's
        /// Mach along its heading, level, the way HyperEdit's orbit editor would: an orbit from those state vectors.
        /// The attitude is set once it is off rails (see Hold).
        /// </summary>
        public static IEnumerator Place(Condition c)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            double ut = Planetarium.GetUniversalTime();
            double terrain = Math.Max(0, body.TerrainAltitude(c.Lat, c.Lon));
            if (c.AltM - terrain < 500)
                throw new UITestFailure($"probe place {c.Name}: {c.AltM:F0} m is {c.AltM - terrain:F0} m above the terrain at {c.Lat:F1}, {c.Lon:F1}");
            double p = body.GetPressure(c.AltM);
            double rho = body.GetDensity(p, body.GetTemperature(c.AltM));
            double speed = c.SpeedMs > 0 ? c.SpeedMs : c.Mach * body.GetSpeedOfSound(p, rho);
            // On rails the vessel coasts on the orbit - a ballistic arc - for CoastSec (as the last placement took)
            // before it unpacks, and nothing may touch it after (a velocity change on the first frames off rails
            // tore the vessel apart). So aim the arc: start below the point, rising, so that the apex - level - is the
            // point. The vertical acceleration in the rotating frame, flying horizontally: gravity less the
            // centrifugal terms of the speed and of the body's spin, plus the Coriolis term of the eastward speed.
            double h = c.HeadingDeg * Math.PI / 180;
            double r0 = body.Radius + c.AltM;
            double spin = body.rotationPeriod != 0 ? 2 * Math.PI / body.rotationPeriod : 0, cosLat = Math.Cos(c.Lat * Math.PI / 180);
            double up = -body.gravParameter / (r0 * r0) + speed * speed / r0 + spin * spin * r0 * cosLat * cosLat
                        + 2 * spin * speed * Math.Sin(h) * cosLat;
            double climb = -up * CoastSec, below = -0.5 * up * CoastSec * CoastSec;
            double startAlt = c.AltM - below;
            Vector3d pos = body.GetWorldSurfacePosition(c.Lat, c.Lon, startAlt);
            // (north and east from points at the same altitude: 1e-4 deg is only about a meter)
            Vector3d north = (body.GetWorldSurfacePosition(c.Lat + 1e-4, c.Lon, startAlt) - pos).normalized;
            Vector3d east = (body.GetWorldSurfacePosition(c.Lat, c.Lon + 1e-4, startAlt) - pos).normalized;
            Vector3d upDir = (pos - body.position).normalized;
            Vector3d surfaceVel = (north * Math.Cos(h) + east * Math.Sin(h)) * speed + upDir * climb;
            Vector3d inertialVel = surfaceVel + body.getRFrmVel(pos);
            var o = new Orbit();
            o.UpdateFromStateVectors((pos - body.position).xzy, inertialVel.xzy, body, ut);
            Log.Info($"probe place {c}: {speed:F0} m/s, ecc {o.eccentricity:F4}, sma {o.semiMajorAxis:F0}");
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, o.eccentricity, o.semiMajorAxis, o.inclination, o.LAN,
                                             o.meanAnomaly, o.argumentOfPeriapsis, o.ObT);

            // The vessel keeps whatever attitude it had, which against the new velocity can be anything - a vessel
            // that was climbing came off rails 35 deg nose-high at 260 m/s and lost its wings. Face it at the
            // condition's angle of attack against the level velocity it will arrive with, on rails and again on each
            // of its first frames off (while its own velocity still reads stale), not rotating.
            Vector3d level = (north * Math.Cos(h) + east * Math.Sin(h)).normalized;
            Flight.PointAtAoA(c.AoADeg, level, upDir);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed, 30, "vessel off rails", shot: false);
            CoastSec = Math.Max(0, Math.Min(10, Planetarium.GetUniversalTime() - ut));
            for (int i = 0; i < 5; ++i)
            {
                Flight.PointAtAoA(c.AoADeg, level, upDir);
                foreach (Part part in FlightGlobals.ActiveVessel.parts)
                    if (part.Rigidbody != null)
                        part.Rigidbody.angularVelocity = Vector3.zero;
                yield return new WaitForFixedUpdate();
            }
            v = FlightGlobals.ActiveVessel;
            Log.Info(string.Format(CultureInfo.InvariantCulture, "probe place {0}: coast {1:F2} s, {2:+0.0;-0.0} m off the altitude, vertical {3:+0.00;-0.00} m/s, {4:F1} m/s",
                                   c.Name, CoastSec, v.altitude - c.AltM, v.verticalSpeed, v.srfSpeed));
        }

        /// <summary>How long (s) the last placement coasted on rails before unpacking; the next aims for it.</summary>
        public static double CoastSec = 2.2;

        /// <summary>
        /// Holds the vessel at the condition's angle of attack, wings level and not rotating, for settleFrames physics
        /// frames, then samples frames more: each the force FAR applied in that frame and the prediction for the
        /// state the vessel measured. With pin off the vessel is left to fly as it is (an autopilot, say) and only
        /// sampled. With predict off the prediction API is not called at all (Predicted stays NaN): a prediction can
        /// leave state on the wings that the next physics frame reads, so the flight alone is measured that way.
        /// </summary>
        public static IEnumerator Hold(Condition c, int settleFrames, int frames, List<Sample> into, bool pin = true, bool predict = true)
        {
            var fixedWait = new WaitForFixedUpdate();
            Vector3d prevVel = Vector3d.zero;
            double prevUT = double.NaN;
            for (int i = 0; i < settleFrames + frames; ++i)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (pin)
                    Pin(c.AoADeg);
                yield return fixedWait;
                v = FlightGlobals.ActiveVessel;
                Vector3d kinematic = NonGravitationalForce(v, ref prevVel, ref prevUT);
                if (i < settleFrames)
                    continue;
                if (!FarApi.FlightForceAndTorque(v, out Vector3 fFlight, out Vector3 tFlight))
                    continue;
                Vector3 vel = v.srf_velocity;
                Vector3 fPred = new Vector3(float.NaN, float.NaN, float.NaN), tPred = fPred;
                if (predict)
                {
                    FarApi.ClearMemo(v);
                    if (!FarApi.PredictAtFull(v, vel, v.latitude, v.longitude, v.altitude, Planetarium.GetUniversalTime(), out fPred, out tPred))
                        continue;
                }
                var s = new Sample
                {
                    UT = Planetarium.GetUniversalTime(), AltM = v.altitude, Airspeed = vel.magnitude, Mach = v.mach,
                    AoADeg = Flight.AoA, QkPa = v.dynamicPressurekPa, ForceKN = fFlight.magnitude,
                    RhoFlight = v.atmDensity, Lat = v.latitude, Lon = v.longitude,
                    CtrlPitch = v.ctrlState.pitch, CtrlYaw = v.ctrlState.yaw, CtrlRoll = v.ctrlState.roll, Throttle = v.ctrlState.mainThrottle,
                    VerticalSpeed = v.verticalSpeed,
                    HoldPitchKNm = pin && HoldMode == "torque" ? Vector3.Dot(Flight.LastHoldTorque, FarApi.PitchUpAxis(v)) : double.NaN,
                    EngineThrottle = Engines(v).Select(e => e.currentThrottle / Math.Max(0.01, e.thrustPercentage / 100.0)).DefaultIfEmpty(double.NaN).Average(),
                    VelLocal = v.ReferenceTransform.InverseTransformDirection(vel),
                    RhoPredicted = !predict ? double.NaN : FarApi.HasPredictAt
                        ? FarApi.FarDensityAt(v.mainBody, v.latitude, v.longitude, v.altitude, Planetarium.GetUniversalTime())
                        : FarApi.FarPredictionDensity(v),
                };
                Components(v, vel, fFlight, tFlight, s.Flight);
                Components(v, vel, fPred, tPred, s.Predicted);
                if (!double.IsNaN(kinematic.x))
                {
                    var kin = new double[6];
                    Components(v, vel, (Vector3)kinematic, Vector3.zero, kin);
                    Array.Copy(kin, s.Kinematic, 3);
                }
                into.Add(s);
            }
        }

        /// <summary>
        /// Holds the vessel at hold as Hold does and every frame after settleFrames asks the prediction API for each
        /// target's state - its velocity turned into the vessel's present frame, its place and its time - adding the
        /// components to into[i]. Targets go in turn each frame, so each starts from wherever the last left FAR.
        /// </summary>
        public static IEnumerator PredictFrom(Condition hold, int settleFrames, int frames, IList<Target> targets, List<double[]>[] into)
        {
            var fixedWait = new WaitForFixedUpdate();
            for (int i = 0; i < settleFrames + frames; ++i)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                Pin(hold.AoADeg);
                yield return fixedWait;
                if (i < settleFrames)
                    continue;
                v = FlightGlobals.ActiveVessel;
                for (int t = 0; t < targets.Count; ++t)
                {
                    Target tg = targets[t];
                    Vector3 vel = v.ReferenceTransform.TransformDirection(tg.VelLocal);
                    FarApi.ClearMemo(v);
                    if (!FarApi.PredictAtFull(v, vel, tg.Lat, tg.Lon, tg.AltM, tg.UT, out Vector3 f, out Vector3 tq))
                        continue;
                    var comp = new double[6];
                    Components(v, vel, f, tq, comp);
                    into[t].Add(comp);
                }
            }
        }

        /// <summary>
        /// Holds the attitude (at.AoADeg) until every running engine has caught up with its throttle, 15 s at most.
        /// Waiting flies the vessel wherever it is, which can be anywhere (the last condition, falling), so it waits at
        /// the condition - placed there if the engines are not there within 10 frames, and again whenever it drifts
        /// 150 m off the altitude or comes within 300 m of the ground. The caller places it for what follows.
        /// </summary>
        public static IEnumerator SpoolAt(Condition at, double engineThrottle = double.NaN)
        {
            var fixedWait = new WaitForFixedUpdate();
            // Until it is placed at the condition the vessel is still flying the last one: hold the attitude it has
            // there (at.AoADeg can be 20 deg, which at the last condition's speed takes the wings off).
            double aoaNow = Flight.AoA;
            if (double.IsNaN(aoaNow)) aoaNow = 0;
            // One frame for the engines to take the new throttle as their request, then put them where steady flight
            // on it has them rather than spooling there: at engineThrottle (command units) when given, else the request.
            Pin(aoaNow);
            yield return fixedWait;
            bool placed = false;
            // Engines flamed out at the last condition (no air, no speed) light again only where there is air: go
            // there first. Their state is set after every placement - a relight starts them from nothing.
            if (FlamedOut(FlightGlobals.ActiveVessel) && at.AltM < FlightGlobals.ActiveVessel.mainBody.atmosphereDepth)
            {
                yield return Place(at);
                placed = true;
                for (int i = 0; i < 5; ++i) { Pin(at.AoADeg); yield return fixedWait; }
            }
            SetEngineThrottle(FlightGlobals.ActiveVessel, engineThrottle);
            bool wasOut = FlamedOut(FlightGlobals.ActiveVessel);
            for (int i = 0; i < 750; ++i)
            {
                Vessel w = FlightGlobals.ActiveVessel;
                bool isOut = FlamedOut(w);
                if (wasOut && !isOut)
                    SetEngineThrottle(w, engineThrottle);   // lit again just now, from nothing
                wasOut = isOut;
                if (i > 10 && EnginesSpooled(w) && !(FlamedOut(w) && at.AltM < w.mainBody.atmosphereDepth && i < 100)) break;
                if (i == 10 && !placed || placed && (Math.Abs(w.altitude - at.AltM) > 150 || w.heightFromTerrain > 0 && w.heightFromTerrain < 300))
                {
                    yield return Place(at);
                    placed = true;
                    SetEngineThrottle(FlightGlobals.ActiveVessel, engineThrottle);
                }
                Pin(placed ? at.AoADeg : aoaNow);
                yield return fixedWait;
            }
        }

        /// <summary>
        /// Puts every running engine's actual throttle (ModuleEngines.currentThrottle, which spools toward the request)
        /// at throttle in command units - times the engine's thrust limiter - or, with NaN, at what it is asking for:
        /// the engine state of steady flight on that throttle, without the spool-up to it.
        /// </summary>
        public static void SetEngineThrottle(Vessel v, double throttle)
        {
            foreach (ModuleEngines e in Engines(v))
                e.currentThrottle = double.IsNaN(throttle) ? e.requestedThrottle : (float)(throttle * e.thrustPercentage / 100);
        }

        /// <summary>The running engines' mean actual throttle in command units (thrust limiter taken out), NaN without any.</summary>
        public static double EngineThrottleOf(Vessel v) =>
            Engines(v).Select(e => e.currentThrottle / Math.Max(0.01, e.thrustPercentage / 100.0)).DefaultIfEmpty(double.NaN).Average();

        /// <summary>
        /// Holds the attitude (at.AoADeg) until the control surfaces have stopped moving - FAR swings them toward the
        /// inputs over a fraction of a second, and a placement does not cut that short - with the mean deflection
        /// steady to 0.01 deg for 10 frames, 250 frames at most. Yields the frames it took in into[0].
        /// </summary>
        public static IEnumerator SettleSurfaces(Condition at, int[] into = null)
        {
            var fixedWait = new WaitForFixedUpdate();
            double last = double.NaN;
            int steady = 0, i = 0;
            for (; i < 250 && steady < 10; ++i)
            {
                Pin(at.AoADeg);
                yield return fixedWait;
                double now = FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel);
                steady = double.IsNaN(now) || !double.IsNaN(last) && Math.Abs(now - last) < 0.01 ? steady + 1 : 0;
                last = now;
            }
            if (into != null) into[0] = i;
        }

        /// <summary>Where <see cref="Trim"/> ended up and how far from equilibrium, with one line per iteration.</summary>
        public sealed class TrimResult
        {
            public double AoADeg, AoACommanded, Pitch, Throttle;
            /// <summary>
            /// The engines' actual throttle (command units) at the end - the throttle once they caught up, or NaN with
            /// none running; flying the trim puts them there instead of spooling.
            /// </summary>
            public double EngineThrottle = double.NaN;
            /// <summary>Residuals at the end: vertical and along-track acceleration (m/s^2), pitch torque the hold still supplied (kN m).</summary>
            public double VerticalAccel, AlongAccel, HoldPitchKNm;
            public int Iterations;
            /// <summary>Every quantity still being trimmed is within tolerance.</summary>
            public bool Converged;
            /// <summary>
            /// What was trimmed: "level" (angle of attack, pitch input and throttle), or what had to be given up and why
            /// (no thrust, throttle at its stop, no level flight within the angle-of-attack range, pitch input at its
            /// stop, no air).
            /// </summary>
            public string Mode = "level";
            public readonly List<string> Steps = new List<string>();

            /// <summary>Tab-separated, after a key: the trim, what it gave up, and how far from equilibrium it ended.</summary>
            public string ToLine(string key)
            {
                var ic = CultureInfo.InvariantCulture;
                return string.Join("\t", new[]
                {
                    key, AoACommanded.ToString("0.###", ic), AoADeg.ToString("0.###", ic), Pitch.ToString("0.####", ic), Throttle.ToString("0.####", ic),
                    EngineThrottle.ToString("0.####", ic),
                    VerticalAccel.ToString("0.###", ic), AlongAccel.ToString("0.###", ic), HoldPitchKNm.ToString("0.#", ic),
                    Iterations.ToString(ic), Converged ? "true" : "false", Mode,
                });
            }

            public const string LineHeader = "key\taoaCommanded\taoa\tpitch\tthrottle\tengineThrottle\tverticalAccel\talongAccel\tholdPitchKNm\titerations\tconverged\tmode";

            /// <summary>As a C# initializer entry for a table of trims keyed like the line.</summary>
            public string ToCode(string key)
            {
                var ic = CultureInfo.InvariantCulture;
                return string.Format(ic, "{{ \"{0}\", new FarFlightProbe.TrimResult {{ AoACommanded = {1:0.###}, AoADeg = {2:0.###}, Pitch = {3:0.####}, Throttle = {4:0.####}, EngineThrottle = {5}, Converged = {6}, Mode = \"{7}\" }} }},",
                                     key, AoACommanded, AoADeg, Pitch, Throttle, double.IsNaN(EngineThrottle) ? "double.NaN" : EngineThrottle.ToString("0.####", ic),
                                     Converged ? "true" : "false", Mode.Replace("\"", "'"));
            }
        }

        /// <summary>
        /// Trims the vessel at the condition's place, speed and heading the way a pilot would: the angle of attack at
        /// which it neither climbs nor sinks, the pitch input at which it needs no help to hold that attitude, and the
        /// throttle at which it neither speeds up nor slows down - starting from c.AoADeg, pitch0 and throttle0. Each
        /// iteration sets the inputs, lets the engines spool, places the vessel, holds the attitude (torque hold) and
        /// measures the vertical and along-track acceleration and the pitch torque the hold had to apply; then a Newton
        /// step: the angle of attack and pitch input together from the lift and moment slopes (angle of attack from
        /// FAR's prediction, pitch input from the last two iterations, the first step a nudge), the throttle from the
        /// engines' thrust per unit of throttle. Yaw and roll inputs stay at zero. Needs Ctrl attached, no autopilot,
        /// and the torque hold.
        ///
        /// Where the craft cannot fly the condition steadily it gives up only what it must, and says so in Mode: with
        /// no thrust (flamed out, none) or the throttle at a stop the speed is left to change; with no level flight
        /// between aoaMinDeg and aoaMaxDeg the angle of attack goes back to c.AoADeg and the vessel is left to climb or
        /// sink - still trimmed in pitch, the attitude it would hold passing through; with the pitch input at a stop
        /// the input stays there, the hold carries the rest of the moment, and the level angle of attack is still
        /// found. With no air there is nothing to trim: inputs centered, as FAR centers the surfaces.
        /// </summary>
        public static IEnumerator Trim(Condition c, double pitch0, double throttle0, TrimResult r,
                                       int settleFrames = 25, int frames = 50, int maxIterations = 12,
                                       double aoaMinDeg = -10, double aoaMaxDeg = 25)
        {
            var inv = CultureInfo.InvariantCulture;
            var fixedWait = new WaitForFixedUpdate();
            const double tolA = 0.05;
            var modes = new List<string>();
            r.AoADeg = r.AoACommanded = c.AoADeg; r.Pitch = pitch0; r.Throttle = throttle0;

            CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
            if (!body.atmosphere || c.AltM >= body.atmosphereDepth)
            {
                r.Pitch = 0; r.Throttle = 0; r.Converged = true; r.Mode = "no air: inputs centered, throttle closed";
                Ctrl.Set(pitch: 0f, yaw: 0f, roll: 0f);
                Ctrl.SetThrottle(0f);
                yield break;
            }

            double aoa = c.AoADeg, e = pitch0, thr = throttle0;
            bool fitAoA = true, fitThrottle = true, fitPitch = true;
            int throttleStops = 0, pitchStops = 0, outOfRange = 0, budget = maxIterations;
            double prevE = double.NaN, prevAoA = double.NaN, prevM = double.NaN, prevFv = double.NaN;
            double me = double.NaN, le = 0;
            int parts = FlightGlobals.ActiveVessel.parts.Count;
            for (int it = 1; it <= budget; ++it)
            {
                Ctrl.Set(pitch: (float)e, yaw: 0f, roll: 0f);
                Ctrl.SetThrottle((float)thr);
                var at = new Condition { Name = c.Name + " trim", AltM = c.AltM, Mach = c.Mach, SpeedMs = c.SpeedMs, Lat = c.Lat, Lon = c.Lon, HeadingDeg = c.HeadingDeg, AoADeg = aoa };
                yield return SpoolAt(at);
                yield return SettleSurfaces(at);
                yield return Place(at);
                var t = new List<double>(); var vs = new List<double>(); var sp = new List<double>(); var mp = new List<double>(); var aoas = new List<double>();
                for (int i = 0; i < settleFrames + frames; ++i)
                {
                    Pin(aoa);
                    yield return fixedWait;
                    if (i < settleFrames) continue;
                    Vessel w = FlightGlobals.ActiveVessel;
                    t.Add(Planetarium.GetUniversalTime());
                    vs.Add(w.verticalSpeed);
                    sp.Add(w.srfSpeed);
                    mp.Add(Vector3.Dot(Flight.LastHoldTorque, FarApi.PitchUpAxis(w)));
                    aoas.Add(Flight.AoA);
                }
                Vessel v = FlightGlobals.ActiveVessel;
                if (v.parts.Count != parts)
                    throw new UITestFailure($"trim {c.Name}: vessel broke up in iteration {it} ({v.parts.Count} of {parts} parts)");
                double m = v.GetTotalMass();
                double av = Slope(t, vs), ah = Slope(t, sp), holdM = mp.Average(), aoaMeas = aoas.Average();
                double fv = m * av, ft = m * ah, mNat = -holdM;
                LiftAndMomentSlopes(v, aoaMeas, out double la, out double ma);
                double tolM = 1e-3 * m * 9.81 * VesselLengthM(v);
                double perThrottle = ThrustPerThrottle(v);
                r.AoADeg = aoaMeas; r.AoACommanded = aoa; r.Pitch = e; r.Throttle = thr; r.Iterations = it;
                r.EngineThrottle = EngineThrottleOf(v);
                r.VerticalAccel = av; r.AlongAccel = ah; r.HoldPitchKNm = holdM;
                r.Steps.Add(string.Format(inv, "{0}: AoA {1:F2} (held {2:F2}), pitch {3:+0.000;-0.000}, throttle {4:F3} -> vertical {5:+0.000;-0.000} m/s2, along {6:+0.000;-0.000} m/s2, hold pitch {7:+0;-0} kN m",
                                          it, aoa, aoaMeas, e, thr, av, ah, holdM));

                // What cannot be trimmed here.
                if (fitThrottle && perThrottle <= 0)
                {
                    // flamed out or none: the throttle does nothing here, so leave it closed
                    fitThrottle = false;
                    thr = r.Throttle = 0;
                    Ctrl.SetThrottle(0f);
                    modes.Add(string.Format(inv, "no thrust: speed changing {0:+0.00;-0.00} m/s2, throttle closed", ah));
                }
                // The throttle at a stop and still wanting past it: the speed is not held (for as long as that lasts -
                // the need can turn back as the angle of attack moves).
                bool throttleStopped = fitThrottle && (thr >= 1 && ah < -tolA || thr <= 0 && ah > tolA);
                throttleStops = throttleStopped ? throttleStops + 1 : 0;
                // The pitch input at a stop and still wanting past it: the craft cannot hold this attitude on its own.
                // Leave the input there and the rest of the moment to the hold, and go on finding the level angle of
                // attack (and throttle) - the state it is in on its way to pitching away, as near to steady as it gets.
                bool pitchStopped = fitPitch && Math.Abs(e) >= 1 && Math.Sign(mNat) == -Math.Sign(e);
                pitchStops = pitchStopped ? pitchStops + 1 : 0;
                if (pitchStops >= 2)
                {
                    fitPitch = false;
                    budget = Math.Max(budget, it + 6);
                }
                if ((!fitAoA || Math.Abs(av) < tolA) && (!fitThrottle || throttleStops >= 2 || Math.Abs(ah) < tolA) && (!fitPitch || Math.Abs(mNat) < tolM))
                {
                    r.Converged = true;
                    break;
                }

                // Pitch-input slopes from the last two iterations, with what the angle of attack change explains taken out.
                if (!double.IsNaN(prevE) && Math.Abs(e - prevE) > 1e-3)
                {
                    double meNew = (mNat - prevM - ma * (aoaMeas - prevAoA)) / (e - prevE);
                    if (meNew > 0) me = meNew;
                    le = (fv - prevFv - la * (aoaMeas - prevAoA)) / (e - prevE);
                }
                double da = 0, de;
                if (!fitPitch)
                {
                    if (fitAoA) da = -fv / la;
                    de = 0;
                }
                else if (double.IsNaN(me))
                {
                    if (fitAoA) da = -fv / la;
                    de = -0.15 * Math.Sign(mNat);   // pitch input up for a nose-down moment
                }
                else if (fitAoA)
                {
                    // [la le; ma me] [da; de] = -[fv; mNat]
                    double det = la * me - le * ma;
                    da = (-fv * me + le * mNat) / det;
                    de = (-la * mNat + ma * fv) / det;
                }
                else
                    de = -mNat / me;
                prevE = e; prevAoA = aoaMeas; prevM = mNat; prevFv = fv;
                if (fitAoA)
                {
                    // The full Newton step says where level flight is; outside the range twice running (or the capped
                    // step already out), there is none in range.
                    double aim = aoa + da, next = aoa + Math.Max(-3, Math.Min(3, da));
                    outOfRange = aim < aoaMinDeg || aim > aoaMaxDeg ? outOfRange + 1 : 0;
                    if (next < aoaMinDeg || next > aoaMaxDeg || outOfRange >= 2)
                    {
                        // No level flight in range: hold the condition's own angle of attack, trimmed in pitch there.
                        fitAoA = false;
                        modes.Add(string.Format(inv, "no level flight within AoA {0:0} to {1:0} (at about {2:F1}): held at AoA {3:F1}, {4:+0.00;-0.00} m/s2 vertical",
                                                aoaMinDeg, aoaMaxDeg, aim, c.AoADeg, av));
                        next = c.AoADeg;
                        // the pitch input the joint step chose was for another angle of attack (it can have used the
                        // elevator to shed lift): start the trim in pitch over from where it began
                        e = pitch0; de = 0;
                        me = double.NaN; prevE = double.NaN;
                        budget = Math.Max(budget, it + 8);
                    }
                    aoa = next;
                }
                // steps in pitch input capped - wider once only the pitch is left and its slope is known
                double capE = !fitAoA && !double.IsNaN(me) ? 0.6 : 0.3;
                e = Math.Max(-1, Math.Min(1, e + Math.Max(-capE, Math.Min(capE, de))));
                if (fitThrottle)
                    thr = Math.Max(0, Math.Min(1, thr - ft / perThrottle));
            }
            if (!fitPitch)
                modes.Add(string.Format(inv, "pitch input at {0:+0;-0}: {1:+0;-0} kN m left to the hold", r.Pitch, r.HoldPitchKNm));
            if (throttleStops >= 2)
                modes.Add(string.Format(inv, "throttle at {0:0}: speed changing {1:+0.00;-0.00} m/s2", r.Throttle, r.AlongAccel));
            if (modes.Count > 0)
                r.Mode = string.Join("; ", modes);
        }

        // The running engines' thrust (kN, world): along each thrust transform's back, split as the engine splits it.
        private static Vector3 EngineThrust(Vessel v)
        {
            Vector3 sum = Vector3.zero;
            foreach (ModuleEngines e in Engines(v))
            {
                int n = e.thrustTransforms.Count;
                for (int i = 0; i < n; ++i)
                {
                    float share = e.thrustTransformMultipliers != null && e.thrustTransformMultipliers.Count == n ? e.thrustTransformMultipliers[i] : 1f / n;
                    sum += -e.thrustTransforms[i].forward * e.finalThrust * share;
                }
            }
            return sum;
        }

        /// <summary>
        /// How far a held flight was from equilibrium over its samples: vertical and along-track acceleration (m/s^2,
        /// fitted) and the mean pitch torque the hold supplied (kN m) - all near zero for a vessel flying a trim.
        /// </summary>
        public static void Equilibrium(List<Sample> samples, out double verticalAccel, out double alongAccel, out double holdPitchKNm)
        {
            var t = samples.Select(s => s.UT).ToList();
            verticalAccel = samples.Count > 2 ? Slope(t, samples.Select(s => s.VerticalSpeed).ToList()) : double.NaN;
            alongAccel = samples.Count > 2 ? Slope(t, samples.Select(s => s.Airspeed).ToList()) : double.NaN;
            var holds = samples.Where(s => !double.IsNaN(s.HoldPitchKNm)).Select(s => s.HoldPitchKNm).ToList();
            holdPitchKNm = holds.Count > 0 ? holds.Average() : double.NaN;
        }

        // Least-squares slope of y against x.
        private static double Slope(List<double> x, List<double> y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < x.Count; ++i) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            return sxx > 0 ? sxy / sxx : 0;
        }

        // Lift (kN/deg) and pitch moment (kN m/deg) slopes against angle of attack from FAR's prediction at the vessel's
        // present state, surfaces as they are.
        private static void LiftAndMomentSlopes(Vessel v, double aoaDeg, out double liftPerDeg, out double momentPerDeg)
        {
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            Vector3 axis = FarApi.PitchUpAxis(v);
            double speed = v.srfSpeed, ut = Planetarium.GetUniversalTime();
            double[] lift = new double[2], moment = new double[2];
            for (int k = 0; k < 2; ++k)
            {
                double a = (aoaDeg + (k == 0 ? -0.5 : 0.5)) * Math.PI / 180;
                Vector3 vel = (float)speed * ((float)Math.Cos(a) * nose - (float)Math.Sin(a) * roof);
                FarApi.ClearMemo(v);
                FarApi.PredictAtFull(v, vel, v.latitude, v.longitude, v.altitude, ut, out Vector3 f, out Vector3 tq);
                lift[k] = Vector3.Dot(f, Vector3.ProjectOnPlane(roof, vel.normalized).normalized);
                moment[k] = Vector3.Dot(tq, axis);
            }
            liftPerDeg = lift[1] - lift[0];
            momentPerDeg = moment[1] - moment[0];
        }

        private static IEnumerable<ModuleEngines> Engines(Vessel v) =>
            v.parts.SelectMany(p => p.Modules.OfType<ModuleEngines>()).Where(e => e.EngineIgnited && !e.flameout);

        // Ignited engines that have flamed out (no air, too slow, too high).
        private static bool FlamedOut(Vessel v) =>
            v.parts.SelectMany(p => p.Modules.OfType<ModuleEngines>()).Any(e => e.EngineIgnited && e.flameout);

        private static bool EnginesSpooled(Vessel v) =>
            Engines(v).All(e => Math.Abs(e.currentThrottle - e.requestedThrottle) < 0.01);

        // Thrust (kN) per unit of throttle at the present condition: what the engines make now over their throttle,
        // or their rated thrust when idle.
        private static double ThrustPerThrottle(Vessel v)
        {
            double thrust = 0, throttle = 0, rated = 0;
            int n = 0;
            foreach (ModuleEngines e in Engines(v))
            {
                thrust += e.finalThrust;
                throttle += e.currentThrottle;
                rated += e.maxThrust * e.thrustPercentage / 100;
                n++;
            }
            if (n == 0) return 0;
            return throttle / n > 0.05 ? thrust / (throttle / n) : rated;
        }

        private static double VesselLengthM(Vessel v)
        {
            Vector3 nose = v.ReferenceTransform.up;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (Part p in v.parts)
            {
                double x = Vector3.Dot(p.transform.position, nose);
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
            return Math.Max(1, hi - lo);
        }

        // Mass x (acceleration - gravity - fictitious) over the last frame (kN, world), NaN on the first call. Below
        // the body's rotating-frame altitude the world axes turn with it, so the surface velocity is differenced and
        // the Coriolis and centrifugal terms taken out; above it the orbital velocity, in a fixed frame.
        private static Vector3d NonGravitationalForce(Vessel v, ref Vector3d prevVel, ref double prevUT)
        {
            CelestialBody body = v.mainBody;
            bool rotating = body.inverseRotation;
            Vector3d vel = rotating ? v.srf_velocity : v.obt_velocity;
            double ut = Planetarium.GetUniversalTime();
            Vector3d result = new Vector3d(double.NaN, double.NaN, double.NaN);
            if (!double.IsNaN(prevUT) && ut > prevUT)
            {
                Vector3d a = (vel - prevVel) / (ut - prevUT);
                Vector3d com = v.CoM;
                Vector3d g = FlightGlobals.getGeeForceAtPosition(com);
                Vector3d fictitious = Vector3d.zero;
                if (rotating && body.rotationPeriod != 0)
                {
                    Vector3d r = com - body.position;
                    Vector3d w = body.RotationAxis.normalized * (2 * Math.PI / body.rotationPeriod);
                    Vector3d east = body.GetWorldSurfacePosition(v.latitude, v.longitude + 1e-4, v.altitude) -
                                    body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
                    if (Vector3d.Dot(Vector3d.Cross(w, r), east) < 0) w = -w;   // the body turns east
                    fictitious = -2 * Vector3d.Cross(w, vel) - Vector3d.Cross(w, Vector3d.Cross(w, r));
                }
                // less the engines' thrust, so what is left is the aerodynamic force
                result = v.GetTotalMass() * (a - g - fictitious) - (Vector3d)EngineThrust(v);
            }
            prevVel = vel;
            prevUT = ut;
            return result;
        }

        // Drag against the airflow, lift toward the roof across it, side force to the right; moments about the
        // vessel's own pitch (nose up), yaw and roll axes.
        private static void Components(Vessel v, Vector3 vel, Vector3 force, Vector3 torque, double[] into)
        {
            Vector3 velN = vel.normalized;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            Vector3 liftDir = Vector3.ProjectOnPlane(roof, velN).normalized;
            Vector3 sideDir = Vector3.Cross(liftDir, velN).normalized;
            into[0] = -Vector3.Dot(force, velN);
            into[1] = Vector3.Dot(force, liftDir);
            into[2] = Vector3.Dot(force, sideDir);
            into[3] = Vector3.Dot(torque, FarApi.PitchUpAxis(v));
            into[4] = Vector3.Dot(torque, roof);
            into[5] = Vector3.Dot(torque, nose);
        }

        /// <summary>Every sample as CSV: the state, then each component flight and predicted.</summary>
        public static string Csv(Condition c, List<Sample> samples)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("condition,ut,altM,airspeed,mach,aoaDeg,qkPa,forceKN,rhoFlight,rhoPredicted");
            foreach (string n in ComponentNames)
                sb.Append(',').Append(n).Append("Flight,").Append(n).Append("Pred");
            sb.Append('\n');
            foreach (Sample s in samples)
            {
                sb.Append(c.Name).Append(',').Append(s.UT.ToString("F3", inv)).Append(',').Append(s.AltM.ToString("F1", inv)).Append(',')
                  .Append(s.Airspeed.ToString("F2", inv)).Append(',').Append(s.Mach.ToString("F4", inv)).Append(',')
                  .Append(s.AoADeg.ToString("F3", inv)).Append(',').Append(s.QkPa.ToString("F4", inv)).Append(',').Append(s.ForceKN.ToString("F4", inv))
                  .Append(',').Append(s.RhoFlight.ToString("G6", inv)).Append(',').Append(s.RhoPredicted.ToString("G6", inv));
                for (int k = 0; k < 6; ++k)
                    sb.Append(',').Append(s.Flight[k].ToString("G6", inv)).Append(',').Append(s.Predicted[k].ToString("G6", inv));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Mean and standard deviation.</summary>
        public static void Stats(IEnumerable<double> values, out double mean, out double sd)
        {
            double[] a = values.ToArray();
            mean = a.Length == 0 ? double.NaN : a.Average();
            double m = mean;
            sd = a.Length < 2 ? double.NaN : Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / (a.Length - 1));
        }
    }
}
