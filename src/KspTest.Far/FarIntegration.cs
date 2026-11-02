using System;
using System.Collections.Generic;
using UnityEngine;

namespace KspTest
{
    /// <summary>Fills FlightTrace's lift, drag, moment and stall columns from FAR, and has a Serve session's loads wait for FAR's editor sim.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class FarIntegration : MonoBehaviour
    {
        private void Awake()
        {
            FlightTrace.Aero = Aero;
            if (Env.HasFAR)
                ServeSession.EditorReady.Add(new KeyValuePair<string, Func<bool>>("FAR editor sim", FarSim.Ready));
            Destroy(this);
        }

        private static bool Aero(Vessel v, out Vector3 force, out Vector3 torque, out double stall)
        {
            stall = FarApi.WingStallMean(v);
            return FarApi.FlightForceAndTorque(v, out force, out torque);
        }
    }
}
