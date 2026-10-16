using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// The net force the active vessel's RCS applies, every physics step: each thruster pushes along minus its
    /// transform's up (forward with useZaxis) with the thrustForces ModuleRCS fired it at (kN). Summed by altitude
    /// band: the mean force along the surface velocity and its normal in the vertical plane, and the speed it added
    /// along the velocity (integral of F/m dt). Also, by band: the pitch torque the thrusters apply about the CoM, the
    /// pitch, yaw and roll inputs, and the torque and normal force of a replica of ModuleRCS's firing run twice, on the
    /// whole rotation input and on the pitch input alone - how much the yaw and roll inputs add to what the RCS gives
    /// for a pitch input.
    /// </summary>
    public class RcsForceProbe : MonoBehaviour
    {
        private static RcsForceProbe instance;
        private double[] bandTopsKm;
        private double[] alongSum, normalSum, dv, seconds, firing;
        private double[] torqueSum, pitchSum, pitchSqSum, absYawSum, absRollSum, yawSqSum, rollSqSum;
        private double[] repAllTorque, repAllNormal, repPitchTorque, repPitchNormal;

        public static void Begin(double[] bandTopsKm)
        {
            End();
            var go = new GameObject("KSPUITest.RcsForceProbe");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<RcsForceProbe>();
            instance.bandTopsKm = bandTopsKm;
            int n = bandTopsKm.Length;
            instance.alongSum = new double[n];
            instance.normalSum = new double[n];
            instance.dv = new double[n];
            instance.seconds = new double[n];
            instance.firing = new double[n];
            instance.torqueSum = new double[n];
            instance.pitchSum = new double[n];
            instance.pitchSqSum = new double[n];
            instance.absYawSum = new double[n];
            instance.absRollSum = new double[n];
            instance.yawSqSum = new double[n];
            instance.rollSqSum = new double[n];
            instance.repAllTorque = new double[n];
            instance.repAllNormal = new double[n];
            instance.repPitchTorque = new double[n];
            instance.repPitchNormal = new double[n];
        }

        /// <summary>Stops and returns band -> "along kN, normal kN, dv m/s, firing %" (bands top-down).</summary>
        public static List<KeyValuePair<string, string>> End()
        {
            var r = new List<KeyValuePair<string, string>>();
            if (instance == null)
                return r;
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < instance.bandTopsKm.Length; i++)
            {
                double s = instance.seconds[i];
                if (s <= 0)
                    continue;
                string band = instance.bandTopsKm[i].ToString(inv) + "-" + (i + 1 < instance.bandTopsKm.Length ? instance.bandTopsKm[i + 1].ToString(inv) : "0");
                r.Add(new KeyValuePair<string, string>(band, string.Format(inv, "along {0:F2} kN, normal {1:F2} kN, dv {2:F1} m/s, firing {3:F0} % over {4:F0} s",
                    instance.alongSum[i] / s, instance.normalSum[i] / s, instance.dv[i], 100 * instance.firing[i] / s, s)));
                r.Add(new KeyValuePair<string, string>(band + "_inputs", string.Format(inv,
                     "pitch {0:F3} (sd {10:F3}), |yaw| {1:F3} (rms {2:F3}), |roll| {3:F3} (rms {4:F3}); pitch torque {5:F2} kN m; " +
                    "replica all axes {6:F2} kN m / normal {7:F3} kN, pitch only {8:F2} kN m / normal {9:F3} kN",
                    instance.pitchSum[i] / s, instance.absYawSum[i] / s, Math.Sqrt(instance.yawSqSum[i] / s),
                    instance.absRollSum[i] / s, Math.Sqrt(instance.rollSqSum[i] / s), instance.torqueSum[i] / s,
                    instance.repAllTorque[i] / s, instance.repAllNormal[i] / s, instance.repPitchTorque[i] / s, instance.repPitchNormal[i] / s,
                    Math.Sqrt(Math.Max(0, instance.pitchSqSum[i] / s - (instance.pitchSum[i] / s) * (instance.pitchSum[i] / s))))));
            }
            Destroy(instance.gameObject);
            instance = null;
            return r;
        }

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed)
                return;
            double km = v.altitude / 1000.0;
            int band = -1;
            for (int i = 0; i < bandTopsKm.Length; i++)
                if (km <= bandTopsKm[i])
                    band = i;
            if (band < 0)
                return;
            Vector3d net = Vector3d.zero;
            Vector3 com = v.CurrentCoM;
            Vector3 pitchAxis = v.ReferenceTransform.right;
            double torque = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleRCS rcs) || rcs.thrustForces == null || rcs.thrusterTransforms == null)
                        continue;
                    for (int i = 0; i < rcs.thrusterTransforms.Count && i < rcs.thrustForces.Length; i++)
                    {
                        Transform t = rcs.thrusterTransforms[i];
                        if (t == null || rcs.thrustForces[i] <= 0f)
                            continue;
                        Vector3 dir = rcs.useZaxis ? t.forward : t.up;
                        Vector3 f = -dir * rcs.thrustForces[i];
                        net += (Vector3d)f;
                        torque += Vector3.Dot(Vector3.Cross(t.position - com, f), pitchAxis);
                    }
                }
            double dt = TimeWarp.fixedDeltaTime;
            Vector3d vel = v.srf_velocity.normalized;
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            Vector3d normal = (up - Vector3d.Dot(up, vel) * vel).normalized;

            FlightCtrlState c = v.ctrlState;
            torqueSum[band] += torque * dt;
            pitchSum[band] += c.pitch * dt;
            pitchSqSum[band] += c.pitch * c.pitch * dt;
            absYawSum[band] += Math.Abs(c.yaw) * dt;
            absRollSum[band] += Math.Abs(c.roll) * dt;
            yawSqSum[band] += c.yaw * c.yaw * dt;
            rollSqSum[band] += c.roll * c.roll * dt;
            if (v.ActionGroups[KSPActionGroup.RCS])
            {
                Replica(v, new Vector3(c.pitch, c.roll, c.yaw), com, pitchAxis, normal, out double tAll, out double nAll);
                Replica(v, new Vector3(c.pitch, 0f, 0f), com, pitchAxis, normal, out double tPitch, out double nPitch);
                repAllTorque[band] += tAll * dt;
                repAllNormal[band] += nAll * dt;
                repPitchTorque[band] += tPitch * dt;
                repPitchNormal[band] += nPitch * dt;
            }
            double along = Vector3d.Dot(net, vel), norm = Vector3d.Dot(net, normal);
            alongSum[band] += along * dt;
            normalSum[band] += norm * dt;
            dv[band] += along / v.totalMass * dt;   // kN / t = m/s^2
            seconds[band] += dt;
            if (net.sqrMagnitude > 0)
                firing[band] += dt;
        }

        // ModuleRCS's firing for a rotation input (pitch, roll, yaw in the reference transform's x, y, z): axis inputs
        // under EPSILON zeroed, the input turned into world space, each thruster fired by how well its nozzle lines up
        // with input x arm (clipped at 0, capped at 1), dropped under EPSILON, at maxFuelFlow x Isp(p) x g x ispMult x
        // limiter. No translation input, no full-thrust or precision mode (none on this vessel).
        private static void Replica(Vessel v, Vector3 inputLocal, Vector3 com, Vector3 pitchAxis, Vector3d normal,
                                    out double torque, out double normalForce)
        {
            torque = normalForce = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleRCS rcs) || !rcs.moduleIsEnabled || !rcs.rcsEnabled || rcs.isJustForShow || rcs.flameout
                        || rcs.thrusterTransforms == null)
                        continue;
                    if (rcs.part.ShieldedFromAirstream && !rcs.shieldedCanThrust)
                        continue;
                    float eps2 = rcs.EPSILON * rcs.EPSILON;
                    Vector3 local = new Vector3(rcs.enablePitch ? inputLocal.x : 0f, rcs.enableRoll ? inputLocal.y : 0f, rcs.enableYaw ? inputLocal.z : 0f);
                    if (local.x * local.x < eps2) local.x = 0f;
                    if (local.y * local.y < eps2) local.y = 0f;
                    if (local.z * local.z < eps2) local.z = 0f;
                    Vector3 inputRot = v.ReferenceTransform.rotation * local;
                    if (inputRot == Vector3.zero)
                        continue;
                    double unit = rcs.maxFuelFlow * rcs.atmosphereCurve.Evaluate((float)rcs.part.staticPressureAtm) * rcs.G * rcs.ispMult
                                  * rcs.thrustPercentage * 0.01;
                    foreach (Transform t in rcs.thrusterTransforms)
                    {
                        if (t == null || t.position == Vector3.zero || !t.gameObject.activeInHierarchy)
                            continue;
                        Vector3 vector = rcs.useZaxis ? t.forward : t.up;
                        Vector3 rot = Vector3.Cross(inputRot, Vector3.ProjectOnPlane(t.position - com, inputRot).normalized);
                        float fire = Mathf.Min(Mathf.Max(Vector3.Dot(vector, rot), 0f), 1f);
                        if (fire <= rcs.EPSILON)
                            continue;
                        Vector3 f = -vector * (float)(fire * unit);
                        torque += Vector3.Dot(Vector3.Cross(t.position - com, f), pitchAxis);
                        normalForce += Vector3d.Dot((Vector3d)f, normal);
                    }
                }
        }
    }
}
