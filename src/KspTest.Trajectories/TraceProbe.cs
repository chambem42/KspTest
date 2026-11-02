using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Asks Trajectories (builds with Trajectory.TraceFile) to trace one prediction as the vessel passes each altitude:
    /// trace_<km>.csv under the results folder, a row every few integration steps with the profile's AoA, the trim input
    /// and the cached and directly evaluated lift and drag.
    /// </summary>
    public class TraceProbe : MonoBehaviour
    {
        public string Dir;
        public Queue<double> Alts;
        public readonly List<string> Requested = new List<string>();

        private void Update()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || Alts == null || Alts.Count == 0)
                return;
            if (v.altitude >= Alts.Peek() * 1000)
                return;
            double km = Alts.Dequeue();
            string file = Path.Combine(Dir, $"trace_{km:F0}.csv");
            bool set = TrajApi.SetStaticString("Trajectories.Trajectory", "TraceFile", file);
            Requested.Add($"{km:F0}:{(set ? "set" : "no TraceFile")}");
            Log.Info($"TraceProbe: trace at {v.altitude / 1000:F1} km -> {file} ({(set ? "set" : "no TraceFile")})");
        }
    }
}
