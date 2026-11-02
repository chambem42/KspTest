using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using ModuleWheels;

namespace KspTest
{
    /// <summary>Flight-scene helpers: telemetry, warp, pause, locks.</summary>
    public static class Flight
    {
        public static Vessel ActiveVessel => FlightGlobals.ActiveVessel;

        /// <summary>Nose-up pitch axis: the rotation taking the nose toward the roof.</summary>
        public static Vector3 PitchUpAxis(Vessel v)
        {
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            return Vector3.Cross(nose, roof).normalized;
        }

        public static double Altitude => ActiveVessel?.altitude ?? double.NaN;
        public static double RadarAltitude => ActiveVessel?.heightFromTerrain ?? double.NaN;
        public static double SurfaceSpeed => ActiveVessel?.srfSpeed ?? double.NaN;
        public static double OrbitalSpeed => ActiveVessel?.obt_speed ?? double.NaN;
        public static double Apoapsis => ActiveVessel?.orbit?.ApA ?? double.NaN;
        public static double Periapsis => ActiveVessel?.orbit?.PeA ?? double.NaN;
        public static double Inclination => ActiveVessel?.orbit?.inclination ?? double.NaN;
        public static bool Landed => ActiveVessel != null && ActiveVessel.LandedOrSplashed;
        public static bool InOrbit => ActiveVessel != null && ActiveVessel.situation == Vessel.Situations.ORBITING;
        public static bool Dead => ActiveVessel == null || ActiveVessel.state == Vessel.State.DEAD || ActiveVessel.rootPart == null;

        /// <summary>Angle of attack in degrees (velocity vs the reference transform forward axis).</summary>
        public static double AoA
        {
            get
            {
                var v = ActiveVessel;
                if (v == null || v.srfSpeed < 1) return double.NaN;
                var fwd = v.ReferenceTransform.up;
                var vel = v.srf_velocity.normalized;
                var up = v.ReferenceTransform.forward * -1f;   // vessel "top"
                double a = Vector3.Angle(fwd, vel);
                return Vector3.Dot(vel, up) < 0 ? a : -a;
            }
        }

        public static string Telemetry()
        {
            var v = ActiveVessel;
            if (v == null) return "no active vessel";
            return $"'{v.vesselName}' {v.situation} alt={v.altitude:F0}m srf={v.srfSpeed:F1}m/s obt={v.obt_speed:F1}m/s Ap={v.orbit?.ApA:F0} Pe={v.orbit?.PeA:F0} inc={v.orbit?.inclination:F2} mass={v.totalMass:F2}t";
        }

        /// <summary>
        /// Stock's "Ignore Max Temperature" cheat: parts over their limit stay on the vessel instead of exploding,
        /// so a telemetry log shows how far past 100 % they go (the editor analysis keeps them too). The runner's
        /// cleanup switches it back off after every test.
        /// </summary>
        public static void IgnoreMaxTemperature(bool on)
        {
            if (CheatOptions.IgnoreMaxTemperature == on) return;
            CheatOptions.IgnoreMaxTemperature = on;
            Log.Info("cheat IgnoreMaxTemperature = " + on);
        }

        /// <summary>
        /// The vehicle's angle in the frame stock's solar occlusion judges its rectangles in: OcclusionData.Update
        /// projects through Quaternion.FromToRotation(sunDir, Vector3.up) and reads x/z, so this is the lateral axis's
        /// in-plane angle there, folded to 0..90 (the axis-aligned rectangles repeat every quarter turn; 0 = tight).
        /// It turns with the planet's azimuth in world coordinates, not with the vessel or the sun.
        /// </summary>
        public static double SolarFrameAngle(Vessel v)
        {
            if (v?.ReferenceTransform == null || Planetarium.fetch?.Sun == null) return double.NaN;
            Vector3 sun = (Vector3)(Planetarium.fetch.Sun.position - v.CoMD).normalized;
            Vector3 lateral = Vector3.ProjectOnPlane(v.ReferenceTransform.right, sun);
            if (lateral.sqrMagnitude < 1e-6f) return double.NaN;
            Vector3 f = Quaternion.FromToRotation(sun, Vector3.up) * lateral.normalized;
            double deg = Math.Atan2(f.z, f.x) * 180.0 / Math.PI;
            deg = Math.Abs(deg) % 180.0;
            return deg > 90.0 ? 180.0 - deg : deg;
        }

        /// <summary>Stock's "Infinite Propellant" cheat (engines and RCS draw nothing); the runner's cleanup switches it back off.</summary>
        public static void InfinitePropellant(bool on)
        {
            if (CheatOptions.InfinitePropellant == on) return;
            CheatOptions.InfinitePropellant = on;
            Log.Info("cheat InfinitePropellant = " + on);
        }

        /// <summary>
        /// Puts the active vessel on an orbit around its body (the stock "Set Orbit" cheat, FlightGlobals.SetShipOrbit):
        /// periapsis and apoapsis altitudes in m, starting at the apoapsis. Works from the ground; the vessel goes on
        /// rails there and comes off them as usual.
        /// </summary>
        public static void SetOrbit(double peAltM, double apAltM, double inclinationDeg = 0, double lanDeg = 0, double argPeDeg = 0)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            Log.Info($"set orbit {body.bodyName} Pe {peAltM / 1000:F1} km Ap {apAltM / 1000:F1} km inc {inclinationDeg} (sma {sma:F0} ecc {ecc:F4}), at apoapsis");
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, Math.PI, argPeDeg, period * 0.5);
        }

        /// <summary>
        /// Puts the active vessel on an orbit (periapsis/apoapsis altitudes, inclination, LAN, argument of periapsis,
        /// degrees) at the point where it descends through altM - the way HyperEdit places a vessel - so an entry can
        /// be aimed.
        /// </summary>
        public static void SetOrbitDescendingAt(double peAltM, double apAltM, double inclinationDeg, double lanDeg, double argPeDeg, double altM)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM, r = body.Radius + altM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            double cosNu = Math.Max(-1, Math.Min(1, (sma * (1 - ecc * ecc) / r - 1) / ecc));
            double nu = 2 * Math.PI - Math.Acos(cosNu);   // the descending half, heading for periapsis
            double E = 2 * Math.Atan(Math.Sqrt((1 - ecc) / (1 + ecc)) * Math.Tan(nu / 2));
            double M = E - ecc * Math.Sin(E);
            if (M < 0) M += 2 * Math.PI;
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, M, argPeDeg, M / (2 * Math.PI) * period);
        }

        /// <summary>
        /// Puts the active vessel on an orbit (apsis altitudes, inclination, LAN, argument of periapsis in degrees) at a
        /// mean anomaly (radians) now - HyperEdit's orbit editor.
        /// </summary>
        public static void SetOrbitAtMeanAnomaly(double peAltM, double apAltM, double inclinationDeg, double lanDeg, double argPeDeg, double meanAnomalyRad)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            double M = meanAnomalyRad % (2 * Math.PI);
            if (M < 0) M += 2 * Math.PI;
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, M, argPeDeg, M / (2 * Math.PI) * period);
        }

        /// <summary>
        /// Turns the active vessel so its control point's nose sits aoaDeg above its surface velocity with the wings
        /// level - the attitude a descent profile holds. Placing a vessel on a new orbit keeps its old orientation,
        /// which against the new velocity can be anything (an SSTO came out at -127 deg AoA and spent 20 s slewing).
        /// </summary>
        public static void PointAtAoA(double aoaDeg)
        {
            var v = ActiveVessel;
            v.SetRotation(AoARotation(v, aoaDeg));
        }

        /// <summary>
        /// <see cref="PointAtAoA"/> against a given surface velocity direction and local up (world) instead of the
        /// vessel's own - for a vessel being placed, whose own velocity is not yet the one it will fly with.
        /// </summary>
        public static void PointAtAoA(double aoaDeg, Vector3d velDir, Vector3d up)
        {
            var v = ActiveVessel;
            v.SetRotation(AoARotation(v, aoaDeg, velDir, up));
        }

        /// <summary>
        /// Holds the attitude of <see cref="PointAtAoA"/> without teleporting: every part's angular velocity is set to
        /// turn the vessel by `gain` of its remaining error over the next physics frame. Vessel.SetRotation puts every
        /// part back at its as-built pose relative to the root, undoing the joints' flex, so a light wing under a large
        /// load accelerates away from the fuselage again within every frame and flies at another angle than the vessel;
        /// steering leaves the flex to settle. Returns the remaining error (deg).
        /// </summary>
        public static double SteerToAoA(double aoaDeg, double gain = 0.5)
        {
            var v = ActiveVessel;
            Quaternion error = AoARotation(v, aoaDeg) * Quaternion.Inverse(v.transform.rotation);
            error.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-6f)
                axis = Vector3.zero;
            Vector3 omega = axis * (angle * Mathf.Deg2Rad * (float)gain / TimeWarp.fixedDeltaTime);
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                    p.Rigidbody.angularVelocity = omega;
            return Mathf.Abs(angle);
        }

        /// <summary>
        /// The torque (kN m, world, about the rigidbodies' mass center) the last <see cref="TorqueHoldAoA"/> applied:
        /// held steady, minus the vessel's own net torque - what an equilibrium leaves at zero.
        /// </summary>
        public static Vector3 LastHoldTorque;

        /// <summary>
        /// Holds the attitude of <see cref="PointAtAoA"/> the way a rigid body would be turned: a damped angular
        /// acceleration toward the target (natural frequency wn rad/s, critically damped against the vessel's mass-
        /// weighted angular velocity), given to every part as the force m (alpha x r) about the vessel's CoM plus the
        /// same angular acceleration on its own rotation. Those forces sum to no net force and load no joint, so the
        /// flex is left to the air. Returns the remaining error (deg).
        /// </summary>
        public static double TorqueHoldAoA(double aoaDeg, double wn = 15)
        {
            var v = ActiveVessel;
            Quaternion error = AoARotation(v, aoaDeg) * Quaternion.Inverse(v.transform.rotation);
            error.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-6f)
                axis = Vector3.zero;
            Vector3 theta = axis * (angle * Mathf.Deg2Rad);
            Vector3 omega = Vector3.zero;
            // The rigidbodies' own mass center: the forces below sum to zero only about it (KSP's vessel.CoM is
            // computed differently and is off by enough to push the whole vessel).
            Vector3 com = Vector3.zero;
            float mass = 0;
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                {
                    omega += p.Rigidbody.angularVelocity * p.Rigidbody.mass;
                    com += p.Rigidbody.worldCenterOfMass * p.Rigidbody.mass;
                    mass += p.Rigidbody.mass;
                }
            if (mass <= 0) return Mathf.Abs(angle);
            omega /= mass;
            com /= mass;
            Vector3 alpha = (float)(wn * wn) * theta - (float)(2 * wn) * omega;
            Vector3 torque = Vector3.zero;
            foreach (Part p in v.parts)
            {
                Rigidbody rb = p.Rigidbody;
                if (rb == null) continue;
                Vector3 r = rb.worldCenterOfMass - com, f = Vector3.Cross(alpha, r) * rb.mass;
                rb.AddForce(f, ForceMode.Force);
                rb.AddTorque(alpha, ForceMode.Acceleration);
                Quaternion q = rb.rotation * rb.inertiaTensorRotation;
                torque += Vector3.Cross(r, f) + q * Vector3.Scale(rb.inertiaTensor, Quaternion.Inverse(q) * alpha);
            }
            LastHoldTorque = torque;
            return Mathf.Abs(angle);
        }

        /// <summary>How far (deg) the vessel is from the attitude of <see cref="PointAtAoA"/>.</summary>
        public static double AttitudeErrorDeg(double aoaDeg)
        {
            var v = ActiveVessel;
            return Quaternion.Angle(AoARotation(v, aoaDeg), v.transform.rotation);
        }

        // The vessel rotation that puts its control point's nose aoaDeg above the surface velocity, wings level.
        private static Quaternion AoARotation(Vessel v, double aoaDeg) =>
            AoARotation(v, aoaDeg, v.srf_velocity.sqrMagnitude > 1 ? v.srf_velocity.normalized : v.obt_velocity.normalized,
                        (v.GetWorldPos3D() - v.mainBody.position).normalized);

        private static Quaternion AoARotation(Vessel v, double aoaDeg, Vector3d vel, Vector3d up)
        {
            vel = vel.normalized;
            Vector3d liftUp = Vector3d.Exclude(vel, up).normalized;
            double a = aoaDeg * Math.PI / 180;
            Vector3 nose = (Vector3)(vel * Math.Cos(a) + liftUp * Math.Sin(a));
            Vector3 belly = (Vector3)(-(liftUp * Math.Cos(a) - vel * Math.Sin(a)));
            // The control point: its up is the nose and its forward the belly.
            Quaternion refRot = Quaternion.LookRotation(belly, nose);
            Quaternion localRef = Quaternion.Inverse(v.transform.rotation) * v.ReferenceTransform.rotation;
            return refRot * Quaternion.Inverse(localRef);
        }

        /// <summary>Mean anomaly (radians) of a true anomaly (degrees) on an orbit of eccentricity ecc.</summary>
        public static double MeanAnomalyOf(double trueAnomalyDeg, double ecc)
        {
            double nu = trueAnomalyDeg * Math.PI / 180;
            double E = 2 * Math.Atan(Math.Sqrt((1 - ecc) / (1 + ecc)) * Math.Tan(nu / 2));
            double M = E - ecc * Math.Sin(E);
            return M < 0 ? M + 2 * Math.PI : M;
        }

        /// <summary>True anomaly (deg) at which an orbit with these apsides descends through altM.</summary>
        public static double DescendingTrueAnomalyDeg(CelestialBody body, double peAltM, double apAltM, double altM)
        {
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM, r = body.Radius + altM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double cosNu = Math.Max(-1, Math.Min(1, (sma * (1 - ecc * ecc) / r - 1) / ecc));
            return 360.0 - Math.Acos(cosNu) * 180.0 / Math.PI;
        }

        public static void Unpause()
        {
            if (FlightDriver.Pause) FlightDriver.SetPause(false);
        }

        public static void ClearLocks()
        {
            InputLockManager.ClearControlLocks();
        }

        /// <summary>
        /// On-rails warp by index into TimeWarp.fetch.warpRates (0 = 1x). Note KSP allows this on the
        /// pad, which packs the vessel and locks staging/throttle (TimeWarpLock) - use PhysicsWarp there.
        /// </summary>
        public static void Warp(int rateIndex)
        {
            Log.Info("warp index " + rateIndex);
            if (rateIndex > 0) TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(rateIndex, true, false);
        }

        /// <summary>Physics warp (1-4x, index into physicsWarpRates); the vessel keeps simulating and controls stay live.</summary>
        public static void PhysicsWarp(int rateIndex)
        {
            Log.Info("physics warp index " + rateIndex);
            TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
            TimeWarp.SetRate(rateIndex, true, false);
        }

        /// <summary>Physics warp by rate (e.g. 4 for 4x): picks the physicsWarpRates step nearest the rate, whatever a warp mod put in the table.</summary>
        public static void PhysicsWarpRate(double rate)
        {
            float[] rates = TimeWarp.fetch.physicsWarpRates;
            // Stock's table stops at 4x. BetterTimeWarp only swaps this same array for one of its presets, so
            // do the same: a table that ends at the rate asked for (BetterTimeWarp puts its own back next flight).
            if (rate > rates[rates.Length - 1] * 1.05)
            {
                rates = new float[rates.Length];
                for (int i = 0; i < rates.Length; ++i) rates[i] = i == 0 ? 1f : (float)(rate * Math.Pow(0.5, rates.Length - 1 - i));
                TimeWarp.fetch.physicsWarpRates = rates;
            }
            int best = 0;
            for (int i = 1; i < rates.Length; ++i)
                if (System.Math.Abs(rates[i] - rate) < System.Math.Abs(rates[best] - rate)) best = i;
            Log.Info($"physics warp {rate}x -> index {best} ({rates[best]}x) of [{string.Join(",", rates)}]");
            PhysicsWarp(best);
        }

        public static void WarpOff()
        {
            TimeWarp.SetRate(0, true, false);
        }

        /// <summary>
        /// Custom waypoints (shown by stock and Waypoint Manager) on the KSC runway and on its extended centerline
        /// before it, for an approach in the runway's direction: "RWY" at the runway spawn point (its start, facing
        /// down the runway), and "RWY -n km" n km before it, at the height a glideslope of glideDeg gives. Returns the
        /// runway heading (deg), NaN without a runway. Flight scene only.
        /// </summary>
        public static double AddRunwayWaypoints(double[] distancesKm, double glideDeg, System.Collections.Generic.List<string> added)
        {
            var facility = PSystemSetup.Instance?.GetSpaceCenterFacility("Runway");
            var spawn = facility?.GetSpawnPoint("Runway");
            if (spawn == null || ScenarioCustomWaypoints.Instance == null) return double.NaN;
            spawn.GetSpawnPointLatLonAlt(out double lat, out double lon, out double alt);
            CelestialBody body = FlightGlobals.GetHomeBody();
            Transform t = spawn.GetSpawnPointTransform();
            // down the runway: whichever of the spawn point's axes lies flattest along the ground
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d north = Vector3d.Exclude(up, body.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            Vector3d dir = Math.Abs(Vector3d.Dot(t.forward, up)) < Math.Abs(Vector3d.Dot(t.up, up)) ? (Vector3d)t.forward : (Vector3d)t.up;
            dir = Vector3d.Exclude(up, dir).normalized;
            double heading = (Math.Atan2(Vector3d.Dot(dir, east), Vector3d.Dot(dir, north)) * 180 / Math.PI + 360) % 360;
            double r = body.Radius, d2r = Math.PI / 180;
            Action<string, double, double, double> add = (name, la, lo, al) =>
            {
                var wp = new FinePrint.Waypoint { name = name, celestialName = body.name, latitude = la, longitude = lo, altitude = al };
                ScenarioCustomWaypoints.AddWaypoint(wp);
                added?.Add($"{name}: {la:F5}, {lo:F5}, {al:F0} m");
            };
            add("RWY", lat, lon, alt);
            foreach (double km in distancesKm)
            {
                double d = km * 1000;
                double la = lat - d * Math.Cos(heading * d2r) / r / d2r;
                double lo = lon - d * Math.Sin(heading * d2r) / (r * Math.Cos(lat * d2r)) / d2r;
                add($"RWY -{km:0.#} km", la, lo, alt + d * Math.Tan(glideDeg * d2r));
            }
            Log.Info($"runway waypoints: heading {heading:F1} deg; " + string.Join("; ", added ?? new System.Collections.Generic.List<string>()));
            return heading;
        }

        // TimeControl's HyperWarpController (by reflection: TimeControl is optional), or null without it.
        private static object HyperWarpController() =>
            Reflect.TryFindType("TimeControl.HyperWarpController")?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);

        /// <summary>
        /// TimeControl's hyper-warp at a rate, with the physics step at physicsAccuracy x the default (1 = the 1x
        /// step, more physics frames per rendered frame instead): what it attempts, the machine sets what it gets.
        /// False without TimeControl or when it cannot hyper-warp now.
        /// </summary>
        public static bool HyperWarp(double rate, double physicsAccuracy = 1.0)
        {
            object hw = HyperWarpController();
            if (hw == null) return false;
            var t = hw.GetType();
            t.GetProperty("MaxAttemptedRate").SetValue(hw, (float)rate, null);
            t.GetProperty("PhysicsAccuracy").SetValue(hw, (float)physicsAccuracy, null);
            t.GetMethod("ActivateHyper", Type.EmptyTypes).Invoke(hw, null);
            bool on = (bool)t.GetProperty("IsHyperWarping").GetValue(hw, null);
            Log.Info($"hyper-warp {rate}x at physics accuracy {physicsAccuracy}: {(on ? "on" : "refused")}, time scale {Time.timeScale}, step {Time.fixedDeltaTime:F3} s");
            return on;
        }

        /// <summary> TimeControl's attempted hyper-warp rate, changed while hyper-warping (physics accuracy as it is). </summary>
        public static bool SetHyperWarpRate(double rate)
        {
            object hw = HyperWarpController();
            if (hw == null) return false;
            hw.GetType().GetProperty("MaxAttemptedRate").SetValue(hw, (float)rate, null);
            Log.Info($"hyper-warp rate {rate}x: time scale {Time.timeScale}, step {Time.fixedDeltaTime:F3} s");
            return true;
        }

        public static void HyperWarpOff()
        {
            object hw = HyperWarpController();
            hw?.GetType().GetMethod("DeactivateHyper", Type.EmptyTypes).Invoke(hw, null);
        }

        public static IEnumerator WarpTo(double ut, double timeoutSec = 600)
        {
            Log.Info($"warp to UT {ut:F0} (now {Planetarium.GetUniversalTime():F0})");
            TimeWarp.fetch.WarpTo(ut);
            yield return Wait.Until(() => Planetarium.GetUniversalTime() >= ut - 1 && TimeWarp.CurrentRateIndex == 0, timeoutSec, "warp to UT");
        }

        public static IEnumerator WarpSeconds(double gameSeconds, double timeoutSec = 600)
        {
            yield return WarpTo(Planetarium.GetUniversalTime() + gameSeconds, timeoutSec);
        }

        /// <summary>Fails the test if the vessel is gone/destroyed; use inside long waits.</summary>
        public static void AssertAlive()
        {
            if (Dead) throw new UITestFailure("vessel is dead or gone");
        }
    }
}
