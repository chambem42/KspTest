using System.Collections;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Manual flight controls. A single FlightInputCallback is attached to the active vessel
    /// (OnFlyByWire by default; OnPostAutopilotUpdate when overriding MechJeb/kOS/SAS output) and
    /// writes the harness stick state into the FlightCtrlState every physics frame, exactly like a
    /// joystick would. Axes are -1..1, throttle 0..1; null means "leave whatever is there".
    /// Angular velocity note: vessel.angularVelocity is in the ReferenceTransform frame, so
    /// .y is roll rate, .x pitch rate, .z yaw rate (rad/s).
    /// </summary>
    public static class Ctrl
    {
        public static float? Pitch, Yaw, Roll, Throttle;
        public static Vessel Attached { get; private set; }
        private static bool _post;
        private static bool _hooked;
        // KSP's EventData.Add dereferences the handler's Target, so the listener must be an instance method
        private class Listener { public void OnVesselChange(Vessel v) => Ctrl.OnVesselChange(v); }
        private static readonly Listener _listener = new Listener();

        public static void Attach(bool overrideAutopilots = false)
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null) throw new UITestFailure("Ctrl.Attach: no active vessel");
            if (Attached == v && _post == overrideAutopilots) return;
            Detach();
            Attached = v;
            _post = overrideAutopilots;
            if (_post) v.OnPostAutopilotUpdate += Apply; else v.OnFlyByWire += Apply;
            if (!_hooked) { GameEvents.onVesselChange.Add(_listener.OnVesselChange); _hooked = true; }
            Log.Info($"Ctrl attached to '{v.vesselName}' ({(overrideAutopilots ? "post-autopilot" : "fly-by-wire")})");
        }

        public static void Detach()
        {
            if (Attached != null)
            {
                if (_post) Attached.OnPostAutopilotUpdate -= Apply; else Attached.OnFlyByWire -= Apply;
                Log.Info($"Ctrl detached from '{Attached.vesselName}'");
            }
            Attached = null;
            Pitch = Yaw = Roll = Throttle = null;
        }

        private static void OnVesselChange(Vessel v)
        {
            if (Attached != null && v != Attached) Attach(_post);
        }

        private static void Apply(FlightCtrlState s)
        {
            if (Pitch.HasValue) s.pitch = Mathf.Clamp(Pitch.Value, -1f, 1f);
            if (Yaw.HasValue) s.yaw = Mathf.Clamp(Yaw.Value, -1f, 1f);
            if (Roll.HasValue) s.roll = Mathf.Clamp(Roll.Value, -1f, 1f);
            if (Throttle.HasValue) s.mainThrottle = Mathf.Clamp01(Throttle.Value);
        }

        /// <summary>Sets the stick; stays applied until changed, Neutral() or Detach().</summary>
        public static void Set(float? pitch = null, float? yaw = null, float? roll = null, float? throttle = null)
        {
            Attach(_post);
            if (pitch.HasValue) Pitch = pitch;
            if (yaw.HasValue) Yaw = yaw;
            if (roll.HasValue) Roll = roll;
            if (throttle.HasValue) SetThrottle(throttle.Value);
            Log.Info($"Ctrl set pitch={Pitch} yaw={Yaw} roll={Roll} throttle={Throttle}");
        }

        /// <summary>Holds a stick input for a number of game seconds, then centres the axes (throttle kept).</summary>
        public static IEnumerator Hold(float pitch = 0, float yaw = 0, float roll = 0, float? throttle = null, double seconds = 1)
        {
            Set(pitch, yaw, roll, throttle);
            yield return Wait.GameSeconds(seconds);
            Neutral();
        }

        public static void Neutral()
        {
            Pitch = Yaw = Roll = 0f;
            Log.Info("Ctrl neutral");
        }

        /// <summary>Throttle that persists even after Detach (also written to the stock input state).</summary>
        public static void SetThrottle(float t)
        {
            Throttle = Mathf.Clamp01(t);
            if (FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = Throttle.Value;
        }

        public static void Sas(bool on)
        {
            var v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, on);
            Log.Info("SAS " + (on ? "on" : "off"));
        }

        public static void SasMode(VesselAutopilot.AutopilotMode mode)
        {
            var v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (!v.Autopilot.CanSetMode(mode)) throw new UITestFailure($"SAS mode {mode} not available on '{v.vesselName}'");
            v.Autopilot.SetMode(mode);
            Log.Info("SAS mode " + mode);
        }

        public static void Rcs(bool on) => Group(KSPActionGroup.RCS, on);
        public static void Gear(bool down) => Group(KSPActionGroup.Gear, down);
        public static void Brakes(bool on) => Group(KSPActionGroup.Brakes, on);
        public static void Lights(bool on) => Group(KSPActionGroup.Light, on);
        public static void Abort() => Group(KSPActionGroup.Abort, true);

        public static void Group(KSPActionGroup g, bool on)
        {
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(g, on);
            Log.Info($"action group {g} = {on}");
        }

        public static void Custom(int n, bool? on = null)
        {
            var g = (KSPActionGroup)System.Enum.Parse(typeof(KSPActionGroup), "Custom" + n.ToString("D2"));
            var ag = FlightGlobals.ActiveVessel.ActionGroups;
            if (on.HasValue) ag.SetGroup(g, on.Value); else ag.ToggleGroup(g);
            Log.Info($"action group {g} " + (on.HasValue ? "= " + on : "toggled"));
        }

        public static void Stage()
        {
            Log.Info("stage");
            KSP.UI.Screens.StageManager.ActivateNextStage();
        }

        /// <summary>
        /// Switches every RCS thruster on and enables the RCS action group, without staging: thrusters that sit in
        /// a stage start with rcsEnabled off and staging only flips it (ModuleRCS.OnActive), so flip it directly and
        /// leave the rest of the staging list (engines, decouplers) alone. Returns the number of thrusters enabled.
        /// </summary>
        public static int EnableRcs()
        {
            var v = FlightGlobals.ActiveVessel;
            int enabled = 0, total = 0;
            foreach (var p in v.parts)
                foreach (var m in p.FindModulesImplementing<ModuleRCS>())
                {
                    if (!m.moduleIsEnabled) continue;
                    ++total;
                    if (m.rcsEnabled) continue;
                    m.rcsEnabled = true;
                    ++enabled;
                }
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            Log.Info($"RCS on: {enabled} of {total} thrusters were off");
            return total;
        }

        /// <summary>
        /// Retracts every landing gear and clears the Gear group. A spawned vessel can carry its gear deployed with
        /// the group already false (KSPWheel only syncs the group from the wheel state on deploy), and
        /// ActionGroups.SetGroup does nothing when the group already holds the value, so the group alone may not
        /// move anything. This fires what a group change would: every module action bound to the Gear group, with
        /// Deactivate (stock ModuleWheelDeployment, KSPWheel's Deployment and AdjustableGear all retract on it).
        /// Returns the number of actions fired.
        /// </summary>
        public static int RetractGear()
        {
            var v = FlightGlobals.ActiveVessel;
            var param = new KSPActionParam(KSPActionGroup.Gear, KSPActionType.Deactivate);
            int count = 0;
            foreach (var p in v.parts)
                foreach (PartModule m in p.Modules)
                    foreach (BaseAction a in m.Actions)
                    {
                        if ((a.actionGroup & KSPActionGroup.Gear) == 0 || !a.active) continue;
                        a.Invoke(param);
                        ++count;
                    }
            v.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
            Log.Info($"gear up: {count} Gear-group actions fired; wheels: {GearStates()}");
            return count;
        }

        /// <summary>Each wheel's state, stock (ModuleWheelDeployment.stateString) or KSPWheel (KSPWheelBase.wheelState), for the log.</summary>
        public static string GearStates()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in FlightGlobals.ActiveVessel.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleWheels.ModuleWheelDeployment d) sb.Append($"{p.name}={d.stateString} ");
                    else if (m.moduleName == "KSPWheelBase") sb.Append($"{p.name}={Reflect.Get(m, "wheelState")} ");
                }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "(none)";
        }

        /// <summary>Integrates roll (degrees, signed) over game seconds from vessel.angularVelocity.y.</summary>
        public static IEnumerator IntegrateRoll(double seconds, System.Action<double> result)
        {
            double total = 0;
            double last = Planetarium.GetUniversalTime();
            double end = last + seconds;
            while (true)
            {
                yield return new WaitForFixedUpdate();
                double now = Planetarium.GetUniversalTime();
                var v = FlightGlobals.ActiveVessel;
                if (v != null) total += v.angularVelocity.y * (now - last) * Mathf.Rad2Deg;
                last = now;
                if (now >= end) break;
            }
            result(total);
        }
    }
}
