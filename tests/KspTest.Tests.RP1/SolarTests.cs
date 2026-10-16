using System;
using System.Collections;
using KspTest;
using KspTest.RP1;
using UnityEngine;

namespace KspTest.Tests.RP1
{
    /// <summary>
    /// Stock's solar occlusion between parts (FlightIntegrator.UpdateOcclusionSolar) reduces each part to an
    /// axis-aligned rectangle in a frame taken from Quaternion.FromToRotation(sunDir, Vector3.up), so how much of
    /// a part the parts in front of it shade from the sun depends on the vessel's angle in that frame - which
    /// turns with the planet's azimuth in world coordinates, not with anything about the vessel or the sun.
    /// This holds a vessel at a fixed attitude to the sun in a circular sim orbit and samples every part's
    /// sunAreaMultiplier; run it from saves that differ only in their clock (the world azimuth is a function of
    /// the clock at scene entry) and the edge parts' shading moves while the geometry does not.
    /// Params: craft, orbitAltKm (300), sunOffsetDeg (40: the sun this far off the nose toward the belly, the
    /// airflow's place at 40 deg AoA), holdSec (60), simUT (fixed so the sun's direction is the same every run).
    /// </summary>
    public static class SolarTests
    {
        [UITest("Solar_Occlusion", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big NoShroud", TimeoutSec = 900)]
        public static IEnumerator Solar_Occlusion(TestContext ctx)
        {
            double orbitAltKm = ctx.ParamDouble("orbitAltKm", 300);
            double offsetDeg = ctx.ParamDouble("sunOffsetDeg", 40);
            double holdSec = ctx.ParamDouble("holdSec", 60);
            double simUT = ctx.ParamDouble("simUT", 2019800000);
            Ugui.DismissPopups();

            ctx.Step("Simulate a circular orbit");
            ctx.Record("padUT", Math.Round(Planetarium.GetUniversalTime(), 1));
            var spec = SimSpec.Circular(orbitAltKm, 0);
            spec.UT = simUT > 0 ? simUT : (double?)null;
            yield return Rp1.ConfigureSim(spec);
            yield return Rp1.StartSim();
            var v = FlightGlobals.ActiveVessel;
            ctx.Record("frame.directRotAngle", Math.Round(v.mainBody.directRotAngle, 3));
            ctx.Record("frame.inverseRotAngle", Math.Round(Planetarium.InverseRotAngle, 3));
            ctx.Record("frame.ut", Math.Round(Planetarium.GetUniversalTime(), 1));
            ctx.Record("orbitAltKm", Math.Round(v.altitude / 1000, 1));
            yield return Wait.Seconds(3);   // FAR's first voxelization of the fresh vessel, with it still on RP-1's hold

            ctx.Step("Point the nose " + offsetDeg + " deg off the sun and hold with SAS");
            if (!v.directSunlight)
            {
                Log.Info("in shadow at placement; warping ahead");
                yield return Wait.Until(() => FlightGlobals.ActiveVessel.directSunlight, 900, "the vessel in sunlight", shot: false);
            }
            Flight.InfinitePropellant(true);
            SetSunAttitude(v, offsetDeg);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            yield return Wait.Seconds(5);
            ctx.Record("sunOffsetDegHeld", Math.Round(SunOffset(v), 2));
            ctx.Record("directSunlight", v.directSunlight);

            ctx.Step("Sample each part's sunAreaMultiplier for " + holdSec + " s");
            yield return Solar.Sample(ctx, "", "solar_" + ctx.SaveFolder + ".csv", holdSec, null);
            ctx.Record("sunOffsetDegEnd", Math.Round(SunOffset(v), 2));
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
        }

        /// <summary>
        /// Nose offsetDeg off the sun toward the belly, lateral axis along the orbit normal, set in one go and
        /// left for SAS to hold (an inertial hold in orbit). ReferenceTransform: up = nose, forward = -roof.
        /// </summary>
        private static void SetSunAttitude(Vessel v, double offsetDeg)
        {
            Vector3 s = (Vector3)(Planetarium.fetch.Sun.position - v.CoMD).normalized;
            Vector3 normal = Vector3.Cross((Vector3)(v.CoMD - v.mainBody.position), (Vector3)v.obt_velocity).normalized;   // world frame throughout
            Vector3 lateral = (normal - Vector3.Dot(normal, s) * s).normalized;
            Vector3 r = Vector3.Cross(lateral, s).normalized;   // in the plane of s, perpendicular to it
            double a = offsetDeg * Math.PI / 180.0;
            Vector3 nose = (s * (float)Math.Cos(a) + r * (float)Math.Sin(a)).normalized;
            Vector3 roof = Vector3.Cross(lateral, nose).normalized;    // sun on the belly side: roof away from it
            if (Vector3.Dot(roof, s) > 0) { roof = -roof; lateral = -lateral; }
            Quaternion targetRef = Quaternion.LookRotation(-roof, nose);
            Transform refT = v.ReferenceTransform;
            Quaternion offset = Quaternion.Inverse(v.transform.rotation) * refT.rotation;
            v.SetRotation(targetRef * Quaternion.Inverse(offset), true);
            Log.Info($"attitude set: sun offset {SunOffset(v):F2} deg, lateral.normal {Vector3.Dot(v.ReferenceTransform.right, normal):F3}");
        }

        /// <summary>Angle between the nose and the sun, degrees.</summary>
        private static double SunOffset(Vessel v)
        {
            Vector3 s = (Vector3)(Planetarium.fetch.Sun.position - v.CoMD).normalized;
            return Vector3.Angle(v.ReferenceTransform.up, s);
        }
    }
}
