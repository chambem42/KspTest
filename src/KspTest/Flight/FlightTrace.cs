using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Records the active vessel's longitudinal flight every physics frame to a CSV, flushed every second so a crash or
    /// a killed run keeps it: attitude and flight path, the pitch command the vessel actually received (after any
    /// autopilot), the force and pitch moment FAR applied, and AtmosphereAutopilot's pitch chain (director target AoA
    /// and pitch rate, AoA controller target, angular velocity and acceleration targets, output). Tells an airframe
    /// that cannot hold its attitude (command pinned, nose still going) from a controller that does not ask it to.
    /// Signs: pitch, AoA, pitch rate and pitch moment nose-up positive; flight path up positive.
    /// </summary>
    public class FlightTrace : MonoBehaviour
    {
        /// <summary>A label the test sets for the rows that follow (takeoff, SmartASS, AA, gear up, ...).</summary>
        public static string Phase = "";

        /// <summary>
        /// The aa* columns for a vessel (the vertical given): AtmosphereAutopilot's state in the header's order, from
        /// KspTest.AtmosphereAutopilot when it is installed; without it the columns are 0 (not active) and empty.
        /// </summary>
        public static Func<Vessel, Vector3d, double[]> AutopilotColumns;

        /// <summary>The aerodynamic force and torque the flight applied, and the wings' mean stall.</summary>
        public delegate bool AeroReader(Vessel v, out Vector3 force, out Vector3 torque, out double stall);

        /// <summary>FAR's readings for the lift, drag, moment and stall columns, from KspTest.Far; without it they are empty.</summary>
        public static AeroReader Aero;

        private static readonly double[] NoAutopilot =
            { 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN };

        private static FlightTrace _current;
        private string _path;
        private readonly StringBuilder _rows = new StringBuilder();
        private double _t0 = double.NaN;
        private float _lastFlush;

        private const string Header =
            "t,phase,alt,radarAlt,srfSpeed,vSpeed,pitchDeg,aoaDeg,gammaDeg,qDegS,ctrlPitch,throttle,liftKN,dragKN,weightKN,farPitchKNm,stallMean," +
            "aaActive,aaDirAoA,aaDirPitchV,aaDirPitchAcc,aaAoADes,aaVelDes,aaAccDes,aaAccOut,aaVertAcc," +
            "mach,qkPa,refArea,farCl,farCd,farStallFrac,Cl,Cd,Cm";

        // FARAPI's per-vessel statics (VesselLiftCoeff etc.), looked up once
        private static readonly System.Collections.Generic.Dictionary<string, System.Reflection.MethodInfo> FarStats =
            new System.Collections.Generic.Dictionary<string, System.Reflection.MethodInfo>();

        private static double FarStat(Vessel v, string name)
        {
            if (!FarStats.TryGetValue(name, out var m))
            {
                m = Reflect.TryFindType("FerramAerospaceResearch.FARAPI")?.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                                                                                   null, new[] { typeof(Vessel) }, null);
                FarStats[name] = m;
            }
            try { return m == null ? double.NaN : Convert.ToDouble(m.Invoke(null, new object[] { v }), CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }

        public static void Begin(string path)
        {
            End();
            var go = new GameObject("KspTest.FlightTrace");
            DontDestroyOnLoad(go);
            _current = go.AddComponent<FlightTrace>();
            _current._path = path;
            File.WriteAllText(path, Header + "\n");
            Log.Info($"flight trace -> {path}");
        }

        public static void End()
        {
            if (_current == null) return;
            _current.Flush();
            Destroy(_current.gameObject);
            _current = null;
        }

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.ReferenceTransform == null || v.rootPart == null) return;
            try { Sample(v); }
            catch (Exception ex) { Log.Info("flight trace sample failed: " + ex.Message); }
            if (Time.realtimeSinceStartup - _lastFlush > 1f) Flush();
        }

        private void Flush()
        {
            _lastFlush = Time.realtimeSinceStartup;
            if (_rows.Length == 0 || _path == null) return;
            File.AppendAllText(_path, _rows.ToString());
            _rows.Length = 0;
        }

        private void Sample(Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            if (double.IsNaN(_t0)) _t0 = ut;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            Vector3 pitchAxis = Flight.PitchUpAxis(v);
            Vector3 vel = v.srf_velocity;
            Vector3 velN = vel.normalized;
            double pitch = 90 - Vector3d.Angle((Vector3d)nose, up);
            double gamma = vel.sqrMagnitude > 1 ? 90 - Vector3d.Angle((Vector3d)velN, up) : double.NaN;
            double q = v.rootPart.Rigidbody != null ? Vector3.Dot(v.rootPart.Rigidbody.angularVelocity, pitchAxis) * Mathf.Rad2Deg : double.NaN;
            double lift = double.NaN, drag = double.NaN, moment = double.NaN, stall = double.NaN;
            if (vel.sqrMagnitude > 1 && Aero != null && Aero(v, out Vector3 force, out Vector3 torque, out stall))
            {
                drag = -Vector3.Dot(force, velN);
                lift = Vector3.Dot(force, Vector3.ProjectOnPlane(roof, velN).normalized);
                moment = Vector3.Dot(torque, pitchAxis);
            }
            double g = FlightGlobals.getGeeForceAtPosition(v.CoMD).magnitude;
            var ic = CultureInfo.InvariantCulture;
            // Coefficients from the force and torque FAR applied (lift, drag and pitch moment as above) on FAR's
            // reference area; the moment on the square root of that area as the reference length (FAR's flight
            // data has no chord).
            double qkPa = v.dynamicPressurekPa, refArea = FarStat(v, "VesselRefArea");
            double qs = qkPa * refArea;
            double cl = qs > 1e-6 ? lift / qs : double.NaN, cd = qs > 1e-6 ? drag / qs : double.NaN;
            double cm = qs > 1e-6 && refArea > 0 ? moment / (qs * Math.Sqrt(refArea)) : double.NaN;
            double[] aa = AutopilotColumns?.Invoke(v, up) ?? NoAutopilot;
            double[] nums =
            {
                ut - _t0, double.NaN, v.altitude, v.heightFromTerrain, v.srfSpeed, v.verticalSpeed, pitch, Flight.AoA, gamma, q,
                v.ctrlState.pitch, v.ctrlState.mainThrottle, lift, drag, v.totalMass * g, moment, stall,
                aa[0], aa[1], aa[2], aa[3], aa[4], aa[5], aa[6], aa[7], aa[8],
                v.mach, qkPa, refArea, FarStat(v, "VesselLiftCoeff"), FarStat(v, "VesselDragCoeff"), FarStat(v, "VesselStallFrac"),
                cl, cd, cm,
            };
            for (int i = 0; i < nums.Length; ++i)
            {
                if (i > 0) _rows.Append(',');
                if (i == 1) _rows.Append(Phase);
                else if (!double.IsNaN(nums[i])) _rows.Append(nums[i].ToString("0.####", ic));
            }
            _rows.Append('\n');
        }
    }
}
