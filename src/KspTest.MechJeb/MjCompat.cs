using System;
using System.Reflection;
using MuMech;

namespace KspTest.MechJeb
{
    /// <summary>
    /// MechJeb members whose names differ between the versions KspTest runs with, read by reflection so it builds against
    /// any of them: releases (2.15.4: VesselState.time, thrustAvailable), dev builds before the October 2026 API cleanup
    /// (Time, ThrustAvailable, attitudeError) and after it (AttitudeError). NaN when a version has neither name.
    /// </summary>
    internal static class MjCompat
    {
        private static readonly Func<object, double> StateTime = Getter(typeof(VesselState), "Time", "time");
        private static readonly Func<object, double> StateThrustAvailable = Getter(typeof(VesselState), "ThrustAvailable", "thrustAvailable");
        private static readonly Func<object, double> AttError = Getter(typeof(MechJebModuleAttitudeController), "AttitudeError", "attitudeError");

        public static double Time(VesselState s) => StateTime(s);
        public static double ThrustAvailable(VesselState s) => StateThrustAvailable(s);
        public static double AttitudeError(MechJebModuleAttitudeController a) => AttError(a);

        private static Func<object, double> Getter(Type t, params string[] names)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            foreach (string n in names)
            {
                FieldInfo f = t.GetField(n, flags);
                if (f != null && f.FieldType == typeof(double))
                    return o => (double)f.GetValue(o);
                PropertyInfo p = t.GetProperty(n, flags);
                if (p != null && p.PropertyType == typeof(double) && p.GetGetMethod() != null)
                    return o => (double)p.GetValue(o, null);
            }

            Log.Warn($"MechJeb's {t.Name} has none of {string.Join(", ", names)}");
            return _ => double.NaN;
        }
    }
}
