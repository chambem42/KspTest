using System.IO;
using System.Text;
using MuMech;
using UnityEngine;

namespace KSPUITest.MechJeb
{
    /// <summary>
    /// Every physics step, what the attitude control loop sees and does: the rendered frame it falls in, the universal
    /// time and MechJeb's VesselState time (stale when they differ), the control inputs, angular velocity, RCS thrust
    /// and MechJeb's attitude error. For telling a per-frame fault from a per-step one under TimeControl's hyper-warp,
    /// where a rendered frame holds many physics steps.
    /// </summary>
    public sealed class StepLog : MonoBehaviour
    {
        private readonly StringBuilder csv = new StringBuilder(
            "step,frame,realSec,ut,mjStateUt,fixedDt,timeScale,pitch,yaw,roll,wx,wy,wz,rcsKN,attErrDeg,altKm,rcsOn\n");
        private int step;
        private float endReal = float.MaxValue;
        private float startReal;

        public bool Done => Time.realtimeSinceStartup >= endReal;

        public static StepLog Begin(double realSec)
        {
            var go = new GameObject("KSPUITest.StepLog");
            var log = go.AddComponent<StepLog>();
            log.startReal = Time.realtimeSinceStartup;
            log.endReal = log.startReal + (float)realSec;
            return log;
        }

        private void FixedUpdate()
        {
            if (Done)
                return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed)
                return;
            MechJebCore core = v.GetMasterMechJeb();
            double rcs = 0;
            foreach (Part p in v.parts)
                foreach (ModuleRCS m in p.FindModulesImplementing<ModuleRCS>())
                    if (m.thrustForces != null)
                        foreach (float f in m.thrustForces)
                            rcs += f;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            FlightCtrlState s = v.ctrlState;
            Vector3d w = v.angularVelocityD;
            csv.Append(string.Join(",", new[]
            {
                (step++).ToString(inv), Time.frameCount.ToString(inv), (Time.realtimeSinceStartup - startReal).ToString("F3", inv),
                Planetarium.GetUniversalTime().ToString("F3", inv), (core != null ? core.VesselState.Time : double.NaN).ToString("F3", inv),
                Time.fixedDeltaTime.ToString("F3", inv), Time.timeScale.ToString("F1", inv),
                s.pitch.ToString("F3", inv), s.yaw.ToString("F3", inv), s.roll.ToString("F3", inv),
                w.x.ToString("F4", inv), w.y.ToString("F4", inv), w.z.ToString("F4", inv),
                rcs.ToString("F3", inv), (core != null ? core.Attitude.attitudeError : double.NaN).ToString("F3", inv),
                (v.altitude / 1000).ToString("F3", inv), v.ActionGroups[KSPActionGroup.RCS] ? "1" : "0",
            })).Append('\n');
        }

        public void Write(string path)
        {
            File.WriteAllText(path, csv.ToString());
            Destroy(gameObject);
        }
    }
}
