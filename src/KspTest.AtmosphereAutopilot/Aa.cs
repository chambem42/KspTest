using System;
using System.Collections;
using System.Reflection;

namespace KspTest.AtmosphereAutopilot
{
    /// <summary>
    /// AtmosphereAutopilot's cruise flight, reached by reflection so the harness does not depend on AA: course hold at
    /// an altitude and a speed (m/s).
    /// </summary>
    public static class Aa
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static Type TypeNamed(string name)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (a.assembly.GetName().Name != "AtmosphereAutopilot") continue;
                foreach (Type t in a.assembly.GetTypes())
                    if (t.Name == name) return t;
            }
            return null;
        }

        public static bool Present => TypeNamed("AtmosphereAutopilot") != null;

        // The vessel's AA module map (AtmosphereAutopilot.Instance.autopilot_module_lists[v]), or null.
        private static IDictionary Modules(Vessel v)
        {
            Type aaType = TypeNamed("AtmosphereAutopilot");
            object aa = aaType?.GetProperty("Instance", Any)?.GetValue(null, null);
            var lists = aa == null ? null : aaType.GetField("autopilot_module_lists", Any)?.GetValue(aa) as IDictionary;
            return lists != null && lists.Contains(v) ? lists[v] as IDictionary : null;
        }

        private static object Module(Vessel v, string typeName)
        {
            IDictionary mods = Modules(v);
            Type t = TypeNamed(typeName);
            return mods != null && t != null && mods.Contains(t) ? mods[t] : null;
        }

        private static void SetField(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name, Any);
            if (f == null) throw new UITestFailure($"AtmosphereAutopilot: no field {name} on {o.GetType().Name}");
            f.SetValue(o, f.FieldType.IsEnum && value is string s ? Enum.Parse(f.FieldType, s) : value);
        }

        // DelayedFieldFloat.Value
        private static void SetDelayed(object o, string field, float value)
        {
            object df = o.GetType().GetField(field, Any)?.GetValue(o);
            if (df == null) throw new UITestFailure($"AtmosphereAutopilot: no field {field} on {o.GetType().Name}");
            df.GetType().GetProperty("Value", Any).SetValue(df, value, null);
        }

        /// <summary>
        /// Cruise flight: course hold on courseDeg at altitudeM, with speed control at speedMs; maxClimbAngleDeg (when
        /// given) caps the climb and descent angle (AA's max_climb_angle, 30 by default). With speedControl off AA
        /// leaves the throttle alone and flies the altitude without regard to speed (speedMs unused).
        /// </summary>
        public static void Cruise(Vessel v, double altitudeM, double speedMs, double courseDeg, double maxClimbAngleDeg = double.NaN, bool speedControl = true)
        {
            object top = Module(v, "TopModuleManager") ?? throw new UITestFailure("AtmosphereAutopilot: no TopModuleManager for the vessel");
            object cruise = top.GetType().GetMethod("activateAutopilot", Any).Invoke(top, new object[] { TypeNamed("CruiseController") })
                            ?? throw new UITestFailure("AtmosphereAutopilot: cruise flight did not activate");
            SetField(cruise, "current_mode", "CourseHold");
            SetDelayed(cruise, "desired_course", (float)courseDeg);
            SetField(cruise, "vertical_control", true);
            SetField(cruise, "height_mode", "Altitude");
            SetDelayed(cruise, "desired_altitude", (float)altitudeM);
            if (!double.IsNaN(maxClimbAngleDeg))
                SetField(cruise, "max_climb_angle", maxClimbAngleDeg);
            object thrust = cruise.GetType().GetField("thrust_c", Any)?.GetValue(cruise) ?? Module(v, "ProgradeThrustController")
                            ?? throw new UITestFailure("AtmosphereAutopilot: no ProgradeThrustController");
            SetField(thrust, "spd_control_enabled", speedControl);
            if (speedControl)
            {
                SetDelayed(thrust, "setpoint_field", (float)speedMs);
                FieldInfo sp = thrust.GetType().GetField("setpoint", Any);
                object setpoint = sp.GetValue(thrust);   // a struct: set on the box, then write the box back
                setpoint.GetType().GetField("value", Any).SetValue(setpoint, (float)speedMs);
                sp.SetValue(thrust, setpoint);
            }
            Log.Info($"AA cruise: course {courseDeg:F0}, {altitudeM:F0} m, " + (speedControl ? $"{speedMs:F0} m/s" : "no speed control")
                     + (double.IsNaN(maxClimbAngleDeg) ? "" : $", climb angle at most {maxClimbAngleDeg:F0}"));
        }

        /// <summary>
        /// A field or property of one of the vessel's AA modules (by type name), or null when the module or member is
        /// missing - for telemetry of AA's internal targets.
        /// </summary>
        public static object Read(Vessel v, string moduleType, string member)
        {
            object m = Module(v, moduleType);
            if (m == null) return null;
            FieldInfo f = m.GetType().GetField(member, Any);
            if (f != null) return f.GetValue(m);
            PropertyInfo p = m.GetType().GetProperty(member, Any);
            return p?.GetValue(m, null);
        }

        /// <summary>AtmosphereAutopilot's master switch for the vessel, false when it has none.</summary>
        public static bool Active(Vessel v)
        {
            object top = Module(v, "TopModuleManager");
            return top?.GetType().GetProperty("Active", Any)?.GetValue(top, null) is bool b && b;
        }

        /// <summary>AtmosphereAutopilot's master switch off for the vessel.</summary>
        public static void Off(Vessel v)
        {
            object top = Module(v, "TopModuleManager");
            top?.GetType().GetProperty("Active", Any)?.SetValue(top, false, null);
            Log.Info("AA off");
        }
    }
}
