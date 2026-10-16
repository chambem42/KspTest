using System;
using MuMech;
using UnityEngine;

namespace KspTest.MechJeb
{
    /// <summary>
    /// Flies Trajectories' descent profile through MechJeb's SmartASS from outside, for builds without their own
    /// hold (upstream 2.4.5.4): every frame the profile's angle of attack at the vessel's state (TrajApi.ProfileAoADeg,
    /// Trajectories' own DescentProfile.GetAngleOfAttack) goes to SmartASS as surface prograde plus that pitch, wings
    /// level - what a player would have set by hand.
    /// </summary>
    public class ProfileHold : MonoBehaviour
    {
        private static GameObject holder;
        private double lastPitch = double.NaN;

        public static void Begin()
        {
            End();
            holder = new GameObject("KspTest.ProfileHold");
            DontDestroyOnLoad(holder);
            holder.AddComponent<ProfileHold>();
        }

        public static void End()
        {
            if (holder != null)
                Destroy(holder);
            holder = null;
        }

        private void Update()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed || !Mj.Present)
                return;
            double aoa = TrajApi.ProfileAoADeg(v);
            if (double.IsNaN(aoa) || Math.Abs(aoa) > 90 || (!double.IsNaN(lastPitch) && Math.Abs(aoa - lastPitch) < 0.1))
                return;
            if (double.IsNaN(lastPitch))
                Mj.SmartAssSurfaceProgradeOffset(aoa);   // mode, target and level roll, once
            else
            {
                // a new angle, not a new maneuver: keep the attitude controller's integrators, as Trajectories' hold does
                var sa = Mj.Core.GetComputerModule<MechJebModuleSmartASS>();
                sa.srfVelPit.Val = aoa;
                sa.Engage(false);
            }
            lastPitch = aoa;
        }
    }
}
