using System;
using System.Globalization;
using UnityEngine;

namespace KspTest.AtmosphereAutopilot
{
    /// <summary>Fills FlightTrace's aa* columns with AtmosphereAutopilot's pitch chain.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class AaIntegration : MonoBehaviour
    {
        private void Awake()
        {
            FlightTrace.AutopilotColumns = Columns;
            Destroy(this);
        }

        private static double[] Columns(Vessel v, Vector3d up) => new[]
        {
            Aa.Active(v) ? 1 : 0,
            Num(Aa.Read(v, "DirectorController", "desired_aoa"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "DirectorController", "desired_pitch_v"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "DirectorController", "desired_pitch_acc"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "PitchAoAController", "desired_aoa"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "PitchAngularVelocityController", "desired_v"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "PitchAngularAccController", "desired_acc"), up) * Mathf.Rad2Deg,
            Num(Aa.Read(v, "PitchAngularAccController", "output"), up),
            Num(Aa.Read(v, "CruiseController", "desired_vert_acc"), up),
        };

        private static double Num(object o, Vector3d up)
        {
            switch (o)
            {
                case null: return double.NaN;
                case Vector3d d: return Vector3d.Dot(d, up);
                case Vector3 f: return Vector3d.Dot((Vector3d)f, up);
                case IConvertible c: return c.ToDouble(CultureInfo.InvariantCulture);
                default: return double.NaN;
            }
        }
    }
}
