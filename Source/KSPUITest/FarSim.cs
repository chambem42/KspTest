using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// FAR's editor simulation and flight prediction API, reached by reflection so the same test binary runs
    /// against any FAR build (the A/B comparisons need the exact same probe on both). Where a signature changed
    /// between builds (stability derivatives, the sweeps) both forms are handled.
    /// </summary>
    public static class FarSim
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static string FarVersion()
        {
            try
            {
                foreach (var a in AssemblyLoader.loadedAssemblies)
                    if (a.name == "FerramAerospaceResearch")
                        return a.assembly.GetName().Version + " " + System.IO.File.GetLastWriteTime(a.path).ToString("yyyy-MM-dd HH:mm") + " " + new System.IO.FileInfo(a.path).Length + "B";
            }
            catch { }
            return "?";
        }

        private static object Gui()
        {
            Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            return guiType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
        }

        private static object Sim() => Reflect.Get(Gui(), "_instantSim");
        private static object SimManager() => Reflect.Get(Gui(), "_simManager");

        /// <summary>
        /// The branch's opt-in viscous toggle (EditorSimManager.UseAccurateViscousCalc): returns the persisted value, or
        /// null on a build without it. With set, the in-memory value is changed without writing the config.
        /// </summary>
        public static bool? ViscousToggle(bool? set = null)
        {
            try
            {
                Type t = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.EditorSimManager");
                var p = t.GetProperty("UseAccurateViscousCalc", BindingFlags.Public | BindingFlags.Static);
                if (p == null) return null;
                bool v = (bool)p.GetValue(null, null);   // loads from the config
                if (set.HasValue)
                {
                    t.GetField("_useAccurateViscous", Any).SetValue(null, set.Value);
                    t.GetField("_viscousLoaded", Any).SetValue(null, true);
                }
                return v;
            }
            catch (Exception ex) { Log.Info("viscous toggle: " + ex.Message); return null; }
        }

        public static bool Ready()
        {
            try { return (bool)Reflect.Get(Sim(), "Ready"); } catch { return false; }
        }

        /// <summary>The instant sim at one input; every coefficient the output carries (Cl, Cd, Cm, Cy, Cn, C_roll and any split the build adds).</summary>
        public static Dictionary<string, double> Instant(double alpha, double beta, double phi, double mach, double pitch, int flaps, bool spoilers)
        {
            object sim = Sim();
            Type inType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSimInput");
            object input = Activator.CreateInstance(inType);
            Reflect.Set(input, "alpha", alpha); Reflect.Set(input, "beta", beta); Reflect.Set(input, "phi", phi);
            Reflect.Set(input, "alphaDot", 0.0); Reflect.Set(input, "betaDot", 0.0); Reflect.Set(input, "phiDot", 0.0);
            Reflect.Set(input, "machNumber", mach); Reflect.Set(input, "pitchValue", pitch);
            Reflect.Set(input, "flaps", flaps); Reflect.Set(input, "spoilers", spoilers);
            var method = sim.GetType().GetMethod("GetClCdCmSteady", Any);
            object[] args = { input, null, true, false };
            method.Invoke(sim, args);
            var d = new Dictionary<string, double>();
            foreach (var f in args[1].GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(double)) d[f.Name] = (double)f.GetValue(args[1]);
            return d;
        }

        /// <summary>
        /// The stability derivatives at a flight point, through whichever CalculateStabilityDerivs the build has: the
        /// original (body, alt, mach, flaps, spoilers, a, b, phi) or the branch's (props, body, alt, mach, density, sspeed, ...).
        /// Keys: stableAoA, stableCl, stableCd, area, MAC, b, nominalVelocity, d00..d26.
        /// </summary>
        public static Dictionary<string, double> StabilityDerivs(CelestialBody body, double altM, double mach, int flaps, bool spoilers)
        {
            object mgr = SimManager();
            object calc = Reflect.Get(mgr, "StabDerivCalculator");
            Type atmType = Reflect.FindType("FerramAerospaceResearch.FARAtmosphere");
            Reflect.FindType("FerramAerospaceResearch.FARAeroUtil")
                   .GetMethod("UpdateCurrentActiveBody", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(CelestialBody) }, null)
                   .Invoke(null, new object[] { body });
            MethodInfo m = calc.GetType().GetMethod("CalculateStabilityDerivs", Any);
            object result;
            if (m.GetParameters().Length == 8)
            {
                result = m.Invoke(calc, new object[] { body, altM, mach, flaps, spoilers, 0.0, 0.0, 0.0 });
            }
            else
            {
                object props = Reflect.Call(calc, "ComputeVehicleProperties", flaps, spoilers);
                object gas = atmType.GetMethod("GetGasProperties", BindingFlags.Public | BindingFlags.Static, null,
                                               new[] { typeof(CelestialBody), typeof(Vector3d), typeof(double) }, null)
                                    .Invoke(null, new object[] { body, new Vector3d(0, 0, altM), Planetarium.GetUniversalTime() });
                double density = Convert.ToDouble(Reflect.Get(gas, "Density")), sspeed = Convert.ToDouble(Reflect.Get(gas, "SpeedOfSound"));
                result = m.Invoke(calc, new object[] { props, body, altM, mach, density, sspeed, flaps, spoilers, 0.0, 0.0, 0.0 });
            }
            var d = new Dictionary<string, double>();
            foreach (string k in new[] { "stableAoA", "stableCl", "stableCd", "area", "MAC", "b", "nominalVelocity" })
                d[k] = Convert.ToDouble(Reflect.Get(result, k));
            var derivs = (double[])Reflect.Get(result, "stabDerivs");
            for (int i = 0; i < derivs.Length; ++i) d["d" + i.ToString("00")] = derivs[i];
            return d;
        }

        /// <summary>
        /// A Mach or AoA sweep (SweepSim.MachNumberSweep / AngleOfAttackSweep, with or without the branch's altitude
        /// argument): x values and the Cl, Cd, Cm, L/D series.
        /// </summary>
        public static bool Sweep(bool machSweep, double fixedValue, double pitch, double lower, double upper, int n, int flaps, bool spoilers, CelestialBody body, double altM,
                                 out double[] x, out List<double[]> y, out List<string> names)
        {
            x = null; y = null; names = null;
            object sweep = Reflect.Get(SimManager(), "SweepSim");
            MethodInfo m = sweep.GetType().GetMethod(machSweep ? "MachNumberSweep" : "AngleOfAttackSweep", Any);
            var args = new List<object> { fixedValue, pitch, lower, upper, n, flaps, spoilers, body };
            if (m.GetParameters().Length == 9) args.Add(altM);
            object data = m.Invoke(sweep, args.ToArray());
            if (data == null) return false;
            x = (double[])Reflect.Get(data, "xValues");
            y = (List<double[]>)Reflect.Get(data, "yValues");
            names = (List<string>)Reflect.Get(data, "lineNames");
            return true;
        }

        /// <summary>The editor's reference numbers behind the sim: the body length and max cross-section it derived, and the wing count.</summary>
        public static void EditorRefs(out double bodyLength, out double maxCrossSection, out int wings)
        {
            object sim = Sim();
            bodyLength = Convert.ToDouble(Reflect.Get(sim, "_bodyLength"));
            maxCrossSection = Convert.ToDouble(Reflect.Get(sim, "_maxCrossSectionFromBody"));
            var w = Reflect.Get(sim, "_wingAerodynamicModel") as System.Collections.ICollection;
            wings = w?.Count ?? -1;
        }

        // ---- flight ----

        private static MethodInfo _calc;

        /// <summary>
        /// FARAPI.CalculateVesselAeroForces at a synthetic state: the force and torque FAR predicts for the vessel at a
        /// world velocity and altitude, resolved in the vessel frame (drag along -velocity, lift toward the roof, side
        /// to the right; torque about pitch-up, yaw-right, roll-right axes). The flight-scene prediction path:
        /// FARAeroSection.PredictionCalculateAeroForces + the wing model's PrecomputeCenterOfLift.
        /// </summary>
        public static bool Predict(Vessel v, Vector3 velWorld, double altitude, out double drag, out double lift, out double side, out double pitchM, out double yawM, out double rollM)
        {
            drag = lift = side = pitchM = yawM = rollM = double.NaN;
            if (_calc == null)
                _calc = Reflect.FindType("FerramAerospaceResearch.FARAPI").GetMethod("CalculateVesselAeroForces", BindingFlags.Public | BindingFlags.Static);
            if (v == null || v.ReferenceTransform == null) return false;
            object[] args = { v, null, null, velWorld, altitude };
            _calc.Invoke(null, args);
            var force = (Vector3)args[1];
            var torque = (Vector3)args[2];
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward, right = v.ReferenceTransform.right;
            Vector3 velN = velWorld.normalized;
            drag = -Vector3.Dot(force, velN);
            Vector3 liftDir = Vector3.ProjectOnPlane(roof, velN).normalized;
            lift = Vector3.Dot(force, liftDir);
            side = Vector3.Dot(force, Vector3.Cross(velN, liftDir));
            pitchM = Vector3.Dot(torque, Vector3.Cross(nose, roof).normalized);
            yawM = Vector3.Dot(torque, -roof);
            rollM = Vector3.Dot(torque, nose);
            return true;
        }

        /// <summary>A world velocity of the given speed at an angle of attack and sideslip relative to the vessel's nose.</summary>
        public static Vector3 BodyVelocity(Vessel v, double speed, double alphaDeg, double betaDeg)
        {
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward, right = v.ReferenceTransform.right;
            Vector3 dir = Quaternion.AngleAxis((float)alphaDeg, right) * nose;   // positive alpha: velocity from below (nose above the velocity), lift positive
            dir = Quaternion.AngleAxis((float)betaDeg, roof) * dir;                // beta: the velocity swung about the roof axis (sign as constructed)
            return dir.normalized * (float)speed;
        }

        private static bool IsFarWing(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "FARWingAerodynamicModel") return true;
            return false;
        }

        /// <summary>
        /// The aerodynamic force and moment FAR is applying this frame (the flight path, not the prediction API): the
        /// wing modules' worldSpaceForce at their AerodynamicCenter and the voxel parts' worldSpaceAeroForce at the part
        /// CoM plus worldSpaceTorque, summed and resolved like Predict. Also the total control-surface deflection.
        /// </summary>
        public static void Applied(Vessel v, out double drag, out double lift, out double side, out double pitchM, out double wingPitchM, out double bodyPitchM, out double meanDeflectDeg)
        {
            drag = lift = side = pitchM = wingPitchM = bodyPitchM = meanDeflectDeg = double.NaN;
            if (v == null || v.ReferenceTransform == null) return;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            Vector3 axis = Vector3.Cross(nose, roof).normalized;
            Vector3 com = v.CoM;
            Vector3 force = Vector3.zero;
            double wk = 0, bk = 0, defl = 0; int nd = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        if (IsFarWing(m))
                        {
                            var f = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                            var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                            force += f;
                            wk += Vector3.Dot(Vector3.Cross((Vector3)(ac - (Vector3d)com), f), axis);
                            if (t.Name == "FARControllableSurface") { defl += Math.Abs(Convert.ToDouble(Reflect.Get(m, "AoAoffset"))); ++nd; }
                        }
                        else if (t.Name == "FARAeroPartModule")
                        {
                            var f = (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                            var tq = (Vector3)t.GetField("worldSpaceTorque").GetValue(m);
                            Vector3 at = p.Rigidbody != null ? p.Rigidbody.worldCenterOfMass : p.transform.TransformPoint(p.CoMOffset);
                            force += f;
                            bk += Vector3.Dot(Vector3.Cross(at - com, f) + tq, axis);
                        }
                    }
                    catch { }
                }
            Vector3 velN = v.srf_velocity.normalized;
            drag = -Vector3.Dot(force, velN);
            Vector3 liftDir = Vector3.ProjectOnPlane(roof, velN).normalized;
            lift = Vector3.Dot(force, liftDir);
            side = Vector3.Dot(force, Vector3.Cross(velN, liftDir));
            wingPitchM = wk; bodyPitchM = bk; pitchM = wk + bk;
            meanDeflectDeg = nd > 0 ? defl / nd : 0;
        }

        public static bool Voxelized(Vessel v)
        {
            try
            {
                var m = Reflect.FindType("FerramAerospaceResearch.FARAPI").GetMethod("VesselVoxelizationCompletedAndValid", BindingFlags.Public | BindingFlags.Static);
                return m != null && (bool)m.Invoke(null, new object[] { v });
            }
            catch { return false; }
        }
    }
}
