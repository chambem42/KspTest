using System;
using System.Reflection;
using MuMech;

namespace KspTest.MechJeb
{
    /// <summary>
    /// MechJeb members that differ between the versions KspTest runs with, by reflection so it builds against any of
    /// them: releases (2.15.4: VesselState.time, thrustAvailable; no SmartASS smooth control), dev builds before the
    /// October 2026 API cleanup (Time, ThrustAvailable, attitudeError) and after it (AttitudeError). NaN when a version
    /// has neither name.
    /// </summary>
    public static class MjCompat
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
        private static readonly Func<object, double> StateTime = Getter(typeof(VesselState), "Time", "time");
        private static readonly Func<object, double> StateThrustAvailable = Getter(typeof(VesselState), "ThrustAvailable", "thrustAvailable");
        private static readonly Func<object, double> AttError = Getter(typeof(MechJebModuleAttitudeController), "AttitudeError", "attitudeError");
        private static readonly FieldInfo SaSmooth = typeof(MechJebModuleSmartASS).GetField("smoothControl", Flags);
        private static readonly FieldInfo SaDps = typeof(MechJebModuleSmartASS).GetField("degreesPerSecond", Flags);

        public static double Time(VesselState s) => StateTime(s);
        public static double ThrustAvailable(VesselState s) => StateThrustAvailable(s);
        public static double AttitudeError(MechJebModuleAttitudeController a) => AttError(a);

        /// <summary>SmartASS's "Smooth Control" (its target attitude slews at most DegreesPerSecond): dev builds only.</summary>
        public static bool HasSmoothControl => SaSmooth?.FieldType == typeof(bool) && SaDps?.FieldType == typeof(EditableDouble);

        public static bool SmoothControl(MechJebModuleSmartASS sa) => (bool)SaSmooth.GetValue(sa);
        public static void SetSmoothControl(MechJebModuleSmartASS sa, bool on) => SaSmooth.SetValue(sa, on);
        public static double SmoothDegreesPerSecond(MechJebModuleSmartASS sa) => ((EditableDouble)SaDps.GetValue(sa)).Val;
        public static void SetSmoothDegreesPerSecond(MechJebModuleSmartASS sa, double dps) => ((EditableDouble)SaDps.GetValue(sa)).Val = dps;

        private static Func<object, double> Getter(Type t, params string[] names)
        {
            foreach (string n in names)
            {
                FieldInfo f = t.GetField(n, Flags);
                if (f != null && f.FieldType == typeof(double))
                    return o => (double)f.GetValue(o);
                PropertyInfo p = t.GetProperty(n, Flags);
                if (p != null && p.PropertyType == typeof(double) && p.GetGetMethod() != null)
                    return o => (double)p.GetValue(o, null);
            }

            Log.Warn($"MechJeb's {t.Name} has none of {string.Join(", ", names)}");
            return _ => double.NaN;
        }
    }
}
