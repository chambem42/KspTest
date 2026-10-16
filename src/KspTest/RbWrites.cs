using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Everything written to the active vessel's rigidbodies directly, by whoever writes it: Rigidbody.AddForce,
    /// AddForceAtPosition and AddTorque, and the velocity and angular velocity setters. For a window of physics steps
    /// each write's pitching moment about the vessel's CoM (a velocity write as the angular momentum it changes, per
    /// second) is summed by the calling type and method. Where the vessel loses torque the integrator applied, this
    /// names what takes it. Main thread.
    /// </summary>
    public static class RbWrites
    {
        private static Harmony harmony;
        private static double windowEndUT = double.NegativeInfinity;
        private static int steps;
        private static double lastStepTime = double.NaN;
        private static readonly Dictionary<string, double> sums = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private static Vector3d com, axis;
        private static Vessel vessel;

        public static bool Arm()
        {
            if (harmony != null) return true;
            harmony = new Harmony("KspTest.RbWrites");
            Type rb = typeof(Rigidbody);
            void Patch(string name, Type[] args, string prefix)
            {
                MethodInfo m = AccessTools.Method(rb, name, args);
                if (m != null)
                    harmony.Patch(m, new HarmonyMethod(typeof(RbWrites), prefix));
                else
                    Log.Warn("RbWrites: no Rigidbody." + name);
            }
            Patch("AddForce", new[] { typeof(Vector3), typeof(ForceMode) }, nameof(AddForce));
            Patch("AddForceAtPosition", new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) }, nameof(AddForceAtPosition));
            Patch("AddTorque", new[] { typeof(Vector3), typeof(ForceMode) }, nameof(AddTorque));
            Patch("set_velocity", new[] { typeof(Vector3) }, nameof(SetVelocity));
            Patch("set_angularVelocity", new[] { typeof(Vector3) }, nameof(SetAngularVelocity));
            return true;
        }

        /// <summary>Starts summing for the next seconds of game time.</summary>
        public static void Window(double seconds)
        {
            sums.Clear();
            counts.Clear();
            steps = 0;
            lastStepTime = double.NaN;
            vessel = FlightGlobals.ActiveVessel;
            windowEndUT = Planetarium.GetUniversalTime() + seconds;
        }

        public static bool WindowDone => Planetarium.GetUniversalTime() > windowEndUT;

        // the vessel's CoM and pitch axis, once per physics step; false outside the window or for another vessel's body
        private static bool Ours(Rigidbody rb, out Part part)
        {
            part = null;
            if (rb == null || vessel == null || Planetarium.GetUniversalTime() > windowEndUT) return false;
            part = rb.GetComponent<Part>();
            if (part == null || part.vessel != vessel) return false;
            if (Time.fixedTime != lastStepTime)
            {
                lastStepTime = Time.fixedTime;
                ++steps;
                com = vessel.CoMD;
                axis = (Vector3d)FarApi.PitchUpAxis(vessel);
            }
            return true;
        }

        private static string Caller(string what)
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            for (int i = 0; i < st.FrameCount && i < 8; ++i)
            {
                MethodBase m = st.GetFrame(i)?.GetMethod();
                Type t = m?.DeclaringType;
                if (t == null || t == typeof(RbWrites) || t == typeof(Rigidbody) || t.Name.StartsWith("<")) continue;
                return what + " by " + t.Name + "." + m.Name;
            }
            return what + " by ?";
        }

        private static void Add(string key, double moment)
        {
            if (double.IsNaN(moment) || double.IsInfinity(moment)) return;
            sums.TryGetValue(key, out double s);
            sums[key] = s + moment;
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }

        private static double ForceOf(Rigidbody rb, Vector3 f, ForceMode mode)
        {
            return mode == ForceMode.Acceleration ? rb.mass : mode == ForceMode.Impulse ? 1.0 / Time.fixedDeltaTime
                 : mode == ForceMode.VelocityChange ? rb.mass / Time.fixedDeltaTime : 1.0;
        }

        private static void AddForce(Rigidbody __instance, Vector3 force, ForceMode mode)
        {
            if (!Ours(__instance, out _)) return;
            Vector3d f = (Vector3d)force * ForceOf(__instance, force, mode);
            Add(Caller("AddForce"), Vector3d.Dot(Vector3d.Cross((Vector3d)__instance.worldCenterOfMass - com, f), axis));
        }

        private static void AddForceAtPosition(Rigidbody __instance, Vector3 force, Vector3 position, ForceMode mode)
        {
            if (!Ours(__instance, out _)) return;
            Vector3d f = (Vector3d)force * ForceOf(__instance, force, mode);
            Add(Caller("AddForceAtPosition"), Vector3d.Dot(Vector3d.Cross((Vector3d)position - com, f), axis));
        }

        private static void AddTorque(Rigidbody __instance, Vector3 torque, ForceMode mode)
        {
            if (!Ours(__instance, out _)) return;
            // an acceleration or velocity change is taken about the body's own axes as given (roughly: its inertia on the axis)
            double scale = mode == ForceMode.Force ? 1.0 : mode == ForceMode.Impulse ? 1.0 / Time.fixedDeltaTime : double.NaN;
            Add(Caller("AddTorque" + (mode == ForceMode.Force || mode == ForceMode.Impulse ? "" : "(" + mode + ")")),
                Vector3d.Dot((Vector3d)torque, axis) * scale);
        }

        private static void SetVelocity(Rigidbody __instance, Vector3 value)
        {
            if (!Ours(__instance, out _)) return;
            Vector3d dv = (Vector3d)value - (Vector3d)__instance.velocity;
            // the angular momentum about the CoM it changes, per second
            Add(Caller("set_velocity"), Vector3d.Dot(Vector3d.Cross((Vector3d)__instance.worldCenterOfMass - com, dv * __instance.mass), axis) / Time.fixedDeltaTime);
        }

        private static void SetAngularVelocity(Rigidbody __instance, Vector3 value)
        {
            if (!Ours(__instance, out _)) return;
            Quaternion q = __instance.rotation * __instance.inertiaTensorRotation;
            Vector3 dw = value - __instance.angularVelocity;
            Vector3 dL = q * Vector3.Scale(__instance.inertiaTensor, Quaternion.Inverse(q) * dw);
            Add(Caller("set_angularVelocity"), Vector3d.Dot((Vector3d)dL, axis) / Time.fixedDeltaTime);
        }

        /// <summary>The window's sums by writer, as a mean per physics step (kN m), largest first, and how many writes.</summary>
        public static string Report()
        {
            if (harmony == null) return "not armed";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendFormat(inv, "{0} steps, total {1:F0} kN m |", steps, steps > 0 ? sums.Values.Sum() / steps : double.NaN);
            foreach (var kv in sums.OrderByDescending(x => Math.Abs(x.Value)).Take(15))
                sb.AppendFormat(inv, " {0}: {1:F0} kN m ({2}/step) |", kv.Key, steps > 0 ? kv.Value / steps : double.NaN,
                    steps > 0 ? counts[kv.Key] / steps : 0);
            return sb.ToString();
        }
    }
}
