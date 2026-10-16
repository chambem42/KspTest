using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using ModuleWheels;

namespace KSPUITest
{
    /// <summary>
    /// FAR's prediction API (FARAPI.CalculateVesselAeroForces): the force and torque FAR predicts for the vessel
    /// at a state, which is what its editor static analysis and other mods' predictions are built on. Not the
    /// physics; in a trimmed steady flight the real aerodynamic torque is zero, so what this reports at the
    /// current state is its own error.
    /// </summary>
    public static class FarApi
    {
        private static System.Reflection.MethodInfo _calc;
        private static bool _resolved;

        /// <summary>Pitch torque (kN m, nose-up positive) FAR predicts at the vessel's current velocity and altitude; NaN without FAR.</summary>
        public static double PredictedPitchTorqueKNm(Vessel v, out double liftKN, out double dragKN)
        {
            liftKN = dragKN = double.NaN;
            if (!_resolved)
            {
                _resolved = true;
                var t = Reflect.FindType("FerramAerospaceResearch.FARAPI");
                _calc = t?.GetMethod("CalculateVesselAeroForces", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            if (_calc == null || v == null || v.ReferenceTransform == null) return double.NaN;
            try
            {
                Vector3 vel = v.srf_velocity;
                object[] args = { v, null, null, vel, v.altitude };
                _calc.Invoke(null, args);
                var force = (Vector3)args[1];
                var torque = (Vector3)args[2];
                Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
                Vector3 pitchUp = Vector3.Cross(nose, roof).normalized;
                Vector3 velN = vel.normalized;
                dragKN = -Vector3.Dot(force, velN);
                Vector3 liftDir = Vector3.ProjectOnPlane(roof, velN).normalized;
                liftKN = Vector3.Dot(force, liftDir);
                return Vector3.Dot(torque, pitchUp);
            }
            catch (Exception ex) { Log.Info("FARAPI.CalculateVesselAeroForces failed: " + ex.Message); return double.NaN; }
        }

        private static System.Reflection.MethodInfo _calcAt;
        private static bool _calcAtResolved;

        /// <summary>True when FAR has FARAPI.CalculateVesselAeroForcesAt (a lat/lon/alt and UT).</summary>
        public static bool HasPredictAt
        {
            get
            {
                if (!_calcAtResolved)
                {
                    _calcAtResolved = true;
                    var t = Reflect.FindType("FerramAerospaceResearch.FARAPI");
                    _calcAt = t?.GetMethod("CalculateVesselAeroForcesAt", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                        new[] { typeof(Vessel), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Vector3), typeof(Vector3d), typeof(double) }, null);
                }
                return _calcAt != null;
            }
        }

        /// <summary>
        /// The flow inputs FAR's flight took this frame (FARVesselAero.MachNumber, ReynoldsNumber, vessel.atmDensity)
        /// and the ones its prediction API takes at the same place and airflow (FARAtmosphere.GetPredictionGasProperties:
        /// Mach from its speed of sound, the Reynolds number by FARAeroUtil.CalculateReynoldsNumber at the temperature the
        /// prediction uses for it - GetFlightReynoldsTemperature where the build has it, else the gas's own). False
        /// without FAR or when a member is missing.
        /// </summary>
        public static bool FlowInputs(Vessel v, out double liveMach, out double liveRe, out double liveRho,
                                      out double predMach, out double predRe, out double predRho, out double predTempK, out double reTempK)
        {
            liveMach = liveRe = liveRho = predMach = predRe = predRho = predTempK = reTempK = double.NaN;
            try
            {
                const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                const System.Reflection.BindingFlags I = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                Type aeroT = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
                Type atmT = Reflect.FindType("FerramAerospaceResearch.FARAtmosphere");
                Type utilT = Reflect.FindType("FerramAerospaceResearch.FARAeroUtil");
                if (aeroT == null || atmT == null || utilT == null || v == null) return false;
                Component aero = v.GetComponent(aeroT);
                if (aero == null) return false;
                liveMach = Convert.ToDouble(aeroT.GetProperty("MachNumber", I).GetValue(aero, null));
                liveRe = Convert.ToDouble(aeroT.GetProperty("ReynoldsNumber", I).GetValue(aero, null));
                double length = Convert.ToDouble(aeroT.GetProperty("Length", I).GetValue(aero, null));
                liveRho = v.atmDensity;
                var where = new Vector3d(v.latitude, v.longitude, v.altitude);
                double ut = Planetarium.GetUniversalTime();
                object gas = atmT.GetMethod("GetPredictionGasProperties", S, null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(double) }, null)
                    .Invoke(null, new object[] { v.mainBody, where, ut });
                Type gasT = gas.GetType();
                predRho = Convert.ToDouble(gasT.GetField("Density").GetValue(gas));
                predTempK = Convert.ToDouble(gasT.GetField("Temperature").GetValue(gas));
                double gamma = Convert.ToDouble(gasT.GetField("AdiabaticIndex").GetValue(gas));
                double sos = Convert.ToDouble(gasT.GetProperty("SpeedOfSound").GetValue(gas, null));
                double speed = v.srfSpeed;
                predMach = speed / sos;
                var reT = atmT.GetMethod("GetFlightReynoldsTemperature", S, null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(double) }, null);
                reTempK = reT != null ? Convert.ToDouble(reT.Invoke(null, new object[] { v.mainBody, where, ut })) : predTempK;
                predRe = Convert.ToDouble(utilT.GetMethod("CalculateReynoldsNumber", S, null,
                        new[] { typeof(double), typeof(double), typeof(double), typeof(double), typeof(double), typeof(double) }, null)
                    .Invoke(null, new object[] { predRho, length, speed, predMach, reTempK, gamma }));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>FARAtmosphere's gas density at a latitude, longitude, altitude and UT; NaN without FAR.</summary>
        public static double FarDensityAt(CelestialBody body, double lat, double lon, double alt, double ut)
        {
            try
            {
                var t = Reflect.FindType("FerramAerospaceResearch.FARAtmosphere");
                var m = t.GetMethod("GetGasProperties", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                                    null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(double) }, null);
                object gas = m.Invoke(null, new object[] { body, new Vector3d(lat, lon, alt), ut });
                return Convert.ToDouble(gas.GetType().GetField("Density").GetValue(gas));
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>
        /// FARAPI.CalculateVesselAeroForcesAt: the force FAR predicts for the vessel at a world velocity with the atmosphere
        /// at a latitude, longitude, altitude and UT, as lift (toward the roof, perpendicular to the velocity) and drag (kN).
        /// </summary>
        public static bool PredictAt(Vessel v, Vector3 vel, double lat, double lon, double alt, double ut, out double liftKN, out double dragKN)
        {
            liftKN = dragKN = double.NaN;
            if (!HasPredictAt || v == null || v.ReferenceTransform == null) return false;
            object[] args = { v, null, null, vel, new Vector3d(lat, lon, alt), ut };
            _calcAt.Invoke(null, args);
            var force = (Vector3)args[1];
            Vector3 velN = vel.normalized;
            dragKN = -Vector3.Dot(force, velN);
            liftKN = Vector3.Dot(force, Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, velN).normalized);
            return true;
        }

        /// <summary>
        /// The aerodynamic force FAR is actually applying (kN): wing modules' worldSpaceForce plus the voxel body
        /// parts' worldSpaceAeroForce, as lift (along the roof, perpendicular to the airflow) and drag (against it) -
        /// the same decomposition PredictedPitchTorqueKNm gives the prediction API's force. NaN without FAR.
        /// </summary>
        public static bool TrueAeroForceKN(Vessel v, out double liftKN, out double dragKN)
        {
            liftKN = dragKN = double.NaN;
            if (v == null || v.ReferenceTransform == null) return false;
            Vector3 total = Vector3.zero;
            int n = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        if (IsFarWing(m)) { total += (Vector3)t.GetField("worldSpaceForce").GetValue(m); n++; }
                        else if (t.Name == "FARAeroPartModule") { total += (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m); n++; }
                    }
                    catch { }
                }
            if (n == 0) return false;
            Vector3 velN = ((Vector3)v.srf_velocity).normalized;
            Vector3 roof = -v.ReferenceTransform.forward;
            dragKN = -Vector3.Dot(total, velN);
            liftKN = Vector3.Dot(total, Vector3.ProjectOnPlane(roof, velN).normalized);
            return true;
        }

        /// <summary>
        /// The aerodynamic force (kN, world) FAR applied in the last physics frame and its torque (kN m) about the
        /// vessel's CoM, the point FAR's prediction API takes its torque about: wing modules' worldSpaceForce at their
        /// AerodynamicCenter, and voxel parts' worldSpaceAeroForce at the part's rigidbody CoM (where Part.AddForce
        /// applies it) plus their worldSpaceTorque. Intake ram drag is inside the intake part's worldSpaceAeroForce.
        /// A shielded wing applies nothing but keeps the worldSpaceForce it last had, so it is left out.
        /// </summary>
        public static bool FlightForceAndTorque(Vessel v, out Vector3 force, out Vector3 torque)
        {
            force = torque = Vector3.zero;
            if (v == null) return false;
            Vector3 com = v.CoM;
            int n = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        if (IsFarWing(m))
                        {
                            if (IsShielded(m))
                                continue;
                            var f = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                            var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                            force += f;
                            torque += Vector3.Cross((Vector3)(ac - (Vector3d)com), f);
                            n++;
                        }
                        else if (t.Name == "FARAeroPartModule")
                        {
                            var f = (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                            var tq = (Vector3)t.GetField("worldSpaceTorque").GetValue(m);
                            Vector3 at = p.Rigidbody != null ? p.Rigidbody.worldCenterOfMass : p.transform.TransformPoint(p.CoMOffset);
                            force += f;
                            torque += Vector3.Cross(at - com, f) + tq;
                            n++;
                        }
                    }
                    catch { }
                }
            return n > 0;
        }

        /// <summary>
        /// FARAPI.CalculateVesselAeroForcesAt's force (kN) and torque (kN m about the vessel CoM), world frame; on a FAR
        /// without it (before the place-and-time fix), CalculateVesselAeroForces at the altitude, which is what that
        /// FAR offers.
        /// </summary>
        public static bool PredictAtFull(Vessel v, Vector3 vel, double lat, double lon, double alt, double ut, out Vector3 force, out Vector3 torque)
        {
            force = torque = Vector3.zero;
            if (v == null) return false;
            if (!HasPredictAt)
            {
                PredictedPitchTorqueKNm(v, out _, out _);   // resolves _calc
                if (_calc == null) return false;
                object[] old = { v, null, null, vel, alt };
                _calc.Invoke(null, old);
                force = (Vector3)old[1];
                torque = (Vector3)old[2];
                return true;
            }
            object[] args = { v, null, null, vel, new Vector3d(lat, lon, alt), ut };
            _calcAt.Invoke(null, args);
            force = (Vector3)args[1];
            torque = (Vector3)args[2];
            return true;
        }

        /// <summary>
        /// The density FAR's prediction path (SimulateAeroProperties) uses at the vessel: FARAtmosphere's gas
        /// properties at its current position. The flight path uses vessel/part.atmDensity instead, so the ratio of
        /// the two is how far a prediction's dynamic pressure is off before any aerodynamics. NaN without FAR.
        /// </summary>
        public static double FarPredictionDensity(Vessel v)
        {
            if (v == null) return double.NaN;
            try
            {
                var t = Reflect.FindType("FerramAerospaceResearch.FARAtmosphere");
                var m = t.GetMethod("GetGasProperties", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                                    null, new[] { typeof(Vessel), typeof(double), typeof(double) }, null);
                object gas = m.Invoke(null, new object[] { v, v.altitude, Planetarium.GetUniversalTime() });
                return Convert.ToDouble(gas.GetType().GetField("Density").GetValue(gas));
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>
        /// Clears FARVesselAero's one-entry memo, which returns the previous CalculateVesselAeroForces answer when the
        /// velocity and altitude match. Anything else evaluating the vessel at the same airflow - Trajectories' trim
        /// solve at the held angle, for one - can otherwise leave an answer for a different deflection in it.
        /// </summary>
        public static void ClearMemo(Vessel v)
        {
            if (v == null) return;
            try
            {
                var t = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
                var f = t.GetField("lastSimResults", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var c = v.GetComponent(t);
                if (f != null && c != null) f.SetValue(c, Activator.CreateInstance(f.FieldType));
            }
            catch (Exception) { }
        }

        /// <summary>Parts with a centre-of-mass offset (|CoMOffset| over 1 cm) and the largest offset, in metres - the
        /// parts whose moments FARAPI's prediction path took about the wrong point before the CoM-offset fix.</summary>
        public static string ComOffsetSummary(Vessel v)
        {
            if (v == null) return "";
            int n = 0;
            float max = 0f;
            foreach (Part p in v.parts)
            {
                float m = p.CoMOffset.magnitude;
                if (m > 0.01f) { n++; max = Math.Max(max, m); }
            }
            return $"{n} of {v.parts.Count} parts, max {max:F2} m";
        }

        /// <summary>Nose-up pitch axis: the rotation taking the nose toward the roof.</summary>
        public static Vector3 PitchUpAxis(Vessel v)
        {
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            return Vector3.Cross(nose, roof).normalized;
        }

        private static bool IsShielded(PartModule m) =>
            m.GetType().GetField("isShielded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)?.GetValue(m) is bool b && b;

        /// <summary>
        /// The shielded wings (FAR applies no force to them in flight) as "count: part (stale |worldSpaceForce| kN), ...".
        /// </summary>
        /// <summary>
        /// Puts FAR's shielding into the state its own rules give the vessel as it is now, whatever happened before:
        /// re-voxelizes (FARVesselAero.VesselUpdateEvent), waits for the new voxel sections (up to timeoutSec), then
        /// has every control surface re-apply its shield rule (CheckShielded: mostly buried and within 5 deg of
        /// neutral), which FAR otherwise only runs when a deflection changes. Yields true when the sections were
        /// replaced.
        /// </summary>
        public static System.Collections.IEnumerator ResettleShielding(Vessel v, bool[] done, float timeoutSec = 20)
        {
            done[0] = false;
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            VesselModule aero = null;
            foreach (VesselModule vm in v.GetComponents<VesselModule>())
                if (vm.GetType().Name == "FARVesselAero") aero = vm;
            if (aero == null) yield break;
            var sectionsField = aero.GetType().GetField("_currentAeroSections", Any);
            object before = sectionsField?.GetValue(aero);
            aero.GetType().GetMethod("VesselUpdateEvent", Any, null, new[] { typeof(Vessel) }, null)?.Invoke(aero, new object[] { v });
            float t0 = UnityEngine.Time.realtimeSinceStartup;
            while (UnityEngine.Time.realtimeSinceStartup - t0 < timeoutSec)
            {
                object now = sectionsField?.GetValue(aero);
                if (now != null && !ReferenceEquals(now, before)) { done[0] = true; break; }
                yield return new UnityEngine.WaitForFixedUpdate();
            }
            // A few more frames: wings take their exposure from the new sections as they are applied.
            for (int i = 0; i < 10; ++i)
                yield return new UnityEngine.WaitForFixedUpdate();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (IsFarControlSurface(m))
                        FindMethod(m.GetType(), "CheckShielded")?.Invoke(m, null);
        }

        /// <summary>
        /// Zeroes every FAR wing's stall - the instance field and, on builds with per-slot scratch, the flight's slot 0 -
        /// so the next frames converge to the steady stall from unstalled flight. FAR's flight stall ratchets up above
        /// the stall angle and follows the upstream wings below it, so without this a wing's stall depends on what the
        /// vessel flew before. Returns how many wings were reset.
        /// </summary>
        public static int ResetStall(Vessel v)
        {
            const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int n = 0;
            if (v == null) return 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarWing(m)) continue;
                    for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                    {
                        var stall = t.GetField("stall", Inst | System.Reflection.BindingFlags.DeclaredOnly);
                        stall?.SetValue(m, 0.0);
                        var slots = t.GetField("_scratchSlots", Inst | System.Reflection.BindingFlags.DeclaredOnly);
                        if (slots?.GetValue(m) is Array arr && arr.Length > 0 && arr.GetValue(0) is object s0)
                            s0.GetType().GetField("stall", Inst)?.SetValue(s0, 0.0);
                    }
                    ++n;
                }
            return n;
        }

        private static System.Reflection.MethodInfo FindMethod(Type t, string name)
        {
            for (; t != null && t != typeof(PartModule); t = t.BaseType)
            {
                var m = t.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                          System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        /// <summary>How many of the vessel's FAR wings are shielded (FAR applies no force to them in flight).</summary>
        public static int ShieldedCount(Vessel v)
        {
            int n = 0;
            if (v == null) return 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (IsFarWing(m) && IsShielded(m)) n++;
            return n;
        }

        public static string ShieldedWings(Vessel v)
        {
            if (v == null) return "";
            var names = new System.Collections.Generic.List<string>();
            int wings = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarWing(m)) continue;
                    wings++;
                    if (!IsShielded(m)) continue;
                    var f = (Vector3)m.GetType().GetField("worldSpaceForce").GetValue(m);
                    names.Add($"{p.partInfo.name} {p.flightID % 10000} ({f.magnitude:F1})");
                }
            return $"{names.Count} of {wings}: " + string.Join(", ", names);
        }

        /// <summary>
        /// The flying (unshielded) wings with a nonzero stall value - FAR's stall latches in flight, so a wing can carry
        /// it from an earlier condition - as "count of wings: part stall, ...".
        /// </summary>
        public static string StalledWings(Vessel v)
        {
            if (v == null) return "";
            var names = new System.Collections.Generic.List<string>();
            int wings = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarWing(m) || IsShielded(m)) continue;
                    wings++;
                    var f = m.GetType().GetField("stall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                                                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy);
                    for (Type t = m.GetType(); f == null && t != null; t = t.BaseType)
                        f = t.GetField("stall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    double s = f != null ? Convert.ToDouble(f.GetValue(m)) : double.NaN;
                    if (s != 0) names.Add($"{p.partInfo.name} {s:0.###}");
                }
            return $"{names.Count} of {wings}" + (names.Count > 0 ? ": " + string.Join(", ", names) : "");
        }

        private static bool IsFarWing(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "FARWingAerodynamicModel") return true;
            return false;
        }

        private static bool IsFarControlSurface(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "FARControllableSurface") return true;
            return false;
        }

        /// <summary>
        /// Sets maxdeflect on every FAR control surface (0 locks them flat). FAR evaluates a control surface at its
        /// current animated deflection in every force query, prediction APIs included, so this takes the
        /// deflection out of anything that caches those queries. Returns how many surfaces were changed.
        /// </summary>
        public static int SetMaxDeflect(Vessel v, double deg)
        {
            int n = 0;
            if (v == null) return 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarControlSurface(m)) continue;
                    var f = m.GetType().GetField("maxdeflect", System.Reflection.BindingFlags.Instance |
                                                               System.Reflection.BindingFlags.Public |
                                                               System.Reflection.BindingFlags.NonPublic);
                    if (f == null) continue;
                    f.SetValue(m, f.FieldType == typeof(float) ? (object)(float)deg : deg);
                    n++;
                }
            return n;
        }

        /// <summary>The part's FAR control surface module, or null.</summary>
        public static PartModule ControlSurface(Part p)
        {
            foreach (PartModule m in p.Modules)
                if (IsFarControlSurface(m)) return m;
            return null;
        }

        /// <summary>
        /// A FAR control surface's response to pitch, yaw and roll input (%, as its PAW sliders) and, when given, its
        /// max deflection (deg) - what a player sets in the editor. FAR reads these live every frame.
        /// </summary>
        public static void SetControlAxes(PartModule m, float pitch, float yaw, float roll, float? maxDeflect = null)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Type t = m.GetType();
            t.GetField("pitchaxis", any).SetValue(m, pitch);
            t.GetField("yawaxis", any).SetValue(m, yaw);
            t.GetField("rollaxis", any).SetValue(m, roll);
            if (maxDeflect.HasValue) t.GetField("maxdeflect", any).SetValue(m, maxDeflect.Value);
        }

        /// <summary>Every FAR control surface's maxdeflect, in part order, to put back with <see cref="RestoreMaxDeflects"/>.</summary>
        public static System.Collections.Generic.List<double> SaveMaxDeflects(Vessel v)
        {
            var saved = new System.Collections.Generic.List<double>();
            if (v == null) return saved;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarControlSurface(m)) continue;
                    var f = m.GetType().GetField("maxdeflect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f != null) saved.Add(Convert.ToDouble(f.GetValue(m)));
                }
            return saved;
        }

        public static void RestoreMaxDeflects(Vessel v, System.Collections.Generic.List<double> saved)
        {
            if (v == null || saved == null) return;
            int i = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarControlSurface(m)) continue;
                    var f = m.GetType().GetField("maxdeflect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f == null || i >= saved.Count) continue;
                    f.SetValue(m, f.FieldType == typeof(float) ? (object)(float)saved[i] : saved[i]);
                    i++;
                }
        }

        /// <summary>
        /// Mean absolute current deflection (deg) over the vessel's control surfaces; NaN without any. FAR surfaces
        /// report ControlDeflection (the fork) or AoAoffset (upstream); stock ModuleControlSurface its deflection.
        /// </summary>
        public static double MeanAbsDeflection(Vessel v)
        {
            if (v == null) return double.NaN;
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance |
                                                      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            double sum = 0;
            int n = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    object val = null;
                    Type t = m.GetType();
                    if (IsFarControlSurface(m))
                        val = t.GetProperty("ControlDeflection", any)?.GetValue(m, null)
                              ?? t.GetProperty("AoAoffset", any)?.GetValue(m, null)
                              ?? t.GetField("AoAoffset", any)?.GetValue(m);
                    else if (m is ModuleControlSurface)
                        val = t.GetField("deflection", any)?.GetValue(m);
                    if (val == null) continue;
                    sum += Math.Abs(Convert.ToDouble(val));
                    n++;
                }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>
        /// Mean of FAR's per-wing stall fraction (0..1) over the vessel's wing modules. DetermineStall computes
        /// it from its own previous value, so it is history-dependent state living on the module: a caller that
        /// evaluates the wings at a sequence of angles moves it. NaN without FAR or without wings.
        /// </summary>
        public static double WingStallMean(Vessel v)
        {
            if (v == null) return double.NaN;
            double sum = 0;
            int n = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarWing(m)) continue;
                    try
                    {
                        var f = m.GetType().GetField("stall", System.Reflection.BindingFlags.Instance |
                                                              System.Reflection.BindingFlags.NonPublic |
                                                              System.Reflection.BindingFlags.Public);
                        if (f == null) continue;
                        sum += Convert.ToDouble(f.GetValue(m));
                        n++;
                    }
                    catch (Exception) { }
                }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>
        /// The aerodynamic pitching moment FAR is actually applying about the vessel CoM (kN m, nose-up positive),
        /// from the wing modules (worldSpaceForce at AerodynamicCenter) and the voxel body parts
        /// (worldSpaceAeroForce at the part CoM plus worldSpaceTorque). In a steady trimmed flight with no other
        /// torque source (reaction wheels off, RCS off) this sums to about zero.
        /// </summary>
        public static double TrueAeroPitchMomentKNm(Vessel v, out double wingKNm, out double bodyKNm)
        {
            wingKNm = bodyKNm = 0.0;
            if (v == null || v.ReferenceTransform == null) return double.NaN;
            Vector3 axis = PitchUpAxis(v);
            Vector3 com = v.CoM;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        if (IsFarWing(m))
                        {
                            var force = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                            var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                            wingKNm += Vector3.Dot(Vector3.Cross((Vector3)(ac - (Vector3d)com), force), axis);
                        }
                        else if (t.Name == "FARAeroPartModule")
                        {
                            var force = (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                            var torque = (Vector3)t.GetField("worldSpaceTorque").GetValue(m);
                            Vector3 at = p.Rigidbody != null ? p.Rigidbody.worldCenterOfMass : p.transform.TransformPoint(p.CoMOffset);
                            bodyKNm += Vector3.Dot(Vector3.Cross(at - com, force) + torque, axis);
                        }
                    }
                    catch { }
                }
            return wingKNm + bodyKNm;
        }

        /// <summary>
        /// The whole aerodynamic moment FAR is applying about the vessel CoM (world vector, kN m), the same sums as
        /// TrueAeroPitchMomentKNm without the projection: wing modules and voxel body parts separately, and when perPart
        /// is given, each part's share.
        /// </summary>
        public static Vector3 TrueAeroMomentKNm(Vessel v, out Vector3 wing, out Vector3 body, Dictionary<Part, Vector3> perPart = null) =>
            TrueAeroMomentKNm(v, out wing, out body, perPart, out _, out _, null);

        /// <summary>
        /// TrueAeroMomentKNm, and the force sums beside it (kN, world): wing modules and voxel body parts, and each
        /// part's force when perPartForce is given.
        /// </summary>
        public static Vector3 TrueAeroMomentKNm(Vessel v, out Vector3 wing, out Vector3 body, Dictionary<Part, Vector3> perPart,
                                                out Vector3 wingForce, out Vector3 bodyForce, Dictionary<Part, Vector3> perPartForce)
        {
            wing = body = wingForce = bodyForce = Vector3.zero;
            if (v == null) return Vector3.zero;
            Vector3 com = v.CoM;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        Vector3 mo, force;
                        if (IsFarWing(m))
                        {
                            force = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                            var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                            mo = Vector3.Cross((Vector3)(ac - (Vector3d)com), force);
                            wing += mo;
                            wingForce += force;
                        }
                        else if (t.Name == "FARAeroPartModule")
                        {
                            force = (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                            var torque = (Vector3)t.GetField("worldSpaceTorque").GetValue(m);
                            Vector3 at = p.Rigidbody != null ? p.Rigidbody.worldCenterOfMass : p.transform.TransformPoint(p.CoMOffset);
                            mo = Vector3.Cross(at - com, force) + torque;
                            body += mo;
                            bodyForce += force;
                        }
                        else
                            continue;
                        if (perPart != null)
                            perPart[p] = (perPart.TryGetValue(p, out Vector3 had) ? had : Vector3.zero) + mo;
                        if (perPartForce != null)
                            perPartForce[p] = (perPartForce.TryGetValue(p, out Vector3 hadF) ? hadF : Vector3.zero) + force;
                    }
                    catch { }
                }
            return wing + body;
        }

        /// <summary>
        /// Every FAR control surface as flight has it now: deflection (AoAoffset, deg), shielded or not, its exposed
        /// area against its area (FAR treats one with NUFAR_areaExposedFactor under 0.1 S as shielded whenever it
        /// deflects 5 deg or less) and whether it has dynamic deflection. One line: "name:deflection:S|-:exposed/S:D|-".
        /// </summary>
        public static string SurfaceStates(Vessel v)
        {
            if (v == null) return "";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    if (t.Name != "FARControllableSurface") continue;
                    try
                    {
                        double aoa = Convert.ToDouble(ReadMember(m, "AoAoffset"));
                        bool shielded = Convert.ToBoolean(ReadMember(m, "isShielded"));
                        double exposed = Convert.ToDouble(ReadMember(m, "NUFAR_areaExposedFactor"));
                        double s = Convert.ToDouble(ReadMember(m, "S"));
                        bool dyn = Convert.ToBoolean(ReadMember(m, "isDynamicDeflection"));
                        parts.Add(string.Format(inv, "{0}:{1:F2}:{2}:{3:F2}:{4}", p.flightID % 10000, aoa, shielded ? "S" : "-",
                                                s > 0 ? exposed / s : double.NaN, dyn ? "D" : "-"));
                    }
                    catch (Exception ex) { parts.Add(p.flightID % 10000 + ":" + ex.GetType().Name); }
                }
            return string.Join(" ", parts.ToArray());
        }

        private static object ReadMember(object o, string name)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                       | System.Reflection.BindingFlags.NonPublic;
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                var f = c.GetField(name, any | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                var pr = c.GetProperty(name, any | System.Reflection.BindingFlags.DeclaredOnly);
                if (pr != null) return pr.GetValue(o, null);
            }
            throw new MissingMemberException(o.GetType().Name, name);
        }

        /// <summary>
        /// Where FARAPI.CalculateVesselAeroForces' pitching moment (about vessel.CoM) parts from the one FAR is applying:
        /// the prediction's voxel-section moment per part (FarSectionCapture: each section's force and torque change
        /// in the query, about the CoM) against the live FARAeroPartModule force at the part CoM plus its torque, and
        /// the wing remainder (prediction total minus sections) against the live wing forces at their aerodynamic
        /// centres. One line, kN m, with the five parts that differ most.
        /// </summary>
        /// <summary>FARAPI.CalculateVesselAeroForces at the vessel's surface velocity and altitude now (the wings' fields
        /// then hold the simulation's values). False without FAR.</summary>
        public static bool SimulateNow(Vessel v)
        {
            if (v == null) return false;
            PredictedPitchTorqueKNm(v, out _, out _);   // resolves _calc
            if (_calc == null) return false;
            ClearMemo(v);
            object[] args = { v, null, null, (Vector3)v.srf_velocity, v.altitude };
            _calc.Invoke(null, args);
            return true;
        }

        public static string MomentBreakdown(Vessel v)
        {
            if (v == null || !FarSectionCapture.EnsurePatched()) return "unavailable";
            PredictedPitchTorqueKNm(v, out _, out _);   // resolves _calc
            if (_calc == null) return "no FARAPI";
            Vector3 axis = PitchUpAxis(v);
            Vector3d com = v.CoM;
            ClearMemo(v);
            object[] args = { v, null, null, (Vector3)v.srf_velocity, v.altitude };
            FarSectionCapture.Arm();
            try { _calc.Invoke(null, args); }
            finally { FarSectionCapture.Disarm(); }
            double apiTotal = Vector3.Dot((Vector3)args[2], axis);
            var simBody = new Dictionary<Part, double>();
            double apiBody = 0;
            foreach (var kv in FarSectionCapture.Parts)
            {
                double m = Vector3d.Dot(kv.Value.Torque - Vector3d.Cross(com, kv.Value.Force), (Vector3d)axis);
                simBody[kv.Key] = m;
                apiBody += m;
            }
            // live, per part
            var liveBody = new Dictionary<Part, double>();
            double liveWing = 0, liveBodyTotal = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    Type t = m.GetType();
                    try
                    {
                        if (IsFarWing(m))
                        {
                            var force = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                            var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                            liveWing += Vector3.Dot(Vector3.Cross((Vector3)(ac - com), force), axis);
                        }
                        else if (t.Name == "FARAeroPartModule")
                        {
                            var force = (Vector3)t.GetField("worldSpaceAeroForce").GetValue(m);
                            var torque = (Vector3)t.GetField("worldSpaceTorque").GetValue(m);
                            Vector3 at = p.Rigidbody != null ? p.Rigidbody.worldCenterOfMass : p.transform.TransformPoint(p.CoMOffset);
                            double pm = Vector3.Dot(Vector3.Cross(at - (Vector3)com, force) + torque, axis);
                            liveBody[p] = pm;
                            liveBodyTotal += pm;
                        }
                    }
                    catch { }
                }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var diffs = new List<KeyValuePair<string, double>>();
            foreach (Part p in v.parts)
            {
                simBody.TryGetValue(p, out double a);
                liveBody.TryGetValue(p, out double b);
                if (a != 0 || b != 0)
                    diffs.Add(new KeyValuePair<string, double>(p.partInfo.name + $"({a.ToString("F2", inv)}/{b.ToString("F2", inv)})", a - b));
            }
            diffs.Sort((x, y) => Math.Abs(y.Value).CompareTo(Math.Abs(x.Value)));
            string top = string.Join("; ", diffs.GetRange(0, Math.Min(5, diffs.Count)).ConvertAll(d => d.Key));
            return string.Format(inv, "api {0:F2} = body {1:F2} + wing {2:F2}; live {3:F2} = body {4:F2} + wing {5:F2}; top body api/live: {6}; wings: {7}",
                apiTotal, apiBody, apiTotal - apiBody, liveBodyTotal + liveWing, liveBodyTotal, liveWing, top, WingStates(v, com, axis));
        }

        /// <summary>
        /// Every FAR wing as the flight has it now, by pitching moment (kN m about com on axis): its flightID, where its
        /// aerodynamic centre is ahead of com along the nose and above it (m), the force along the vessel's up axis (kN),
        /// Cl, Cd and stall, and for a control surface its deflection (deg). One line, largest moments first.
        /// </summary>
        public static string WingStates(Vessel v, Vector3d com, Vector3 axis)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            var rows = new List<KeyValuePair<double, string>>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarWing(m)) continue;
                    Type t = m.GetType();
                    try
                    {
                        var force = (Vector3)t.GetField("worldSpaceForce").GetValue(m);
                        var ac = (Vector3d)t.GetField("AerodynamicCenter").GetValue(m);
                        Vector3 r = (Vector3)(ac - com);
                        double mo = Vector3.Dot(Vector3.Cross(r, force), axis);
                        string defl = t.Name == "FARControllableSurface" ? string.Format(inv, " d{0:F1}", Convert.ToDouble(ReadMember(m, "AoAoffset"))) : "";
                        rows.Add(new KeyValuePair<double, string>(mo, string.Format(inv, "{0}@{1:F1}/{2:F1} M{3:F0} F{4:F0} Cl{5:F3} Cd{6:F3} st{7:F2}{8}",
                            p.flightID % 10000, Vector3.Dot(r, nose), Vector3.Dot(r, roof), mo, Vector3.Dot(force, roof),
                            Convert.ToDouble(ReadMember(m, "Cl")), Convert.ToDouble(ReadMember(m, "Cd")), Convert.ToDouble(ReadMember(m, "stall")), defl)));
                    }
                    catch (Exception ex) { rows.Add(new KeyValuePair<double, string>(0, p.flightID % 10000 + ":" + ex.GetType().Name)); }
                }
            rows.Sort((x, y) => Math.Abs(y.Key).CompareTo(Math.Abs(x.Key)));
            return string.Join(" | ", rows.ConvertAll(r => r.Value).ToArray());
        }

        /// <summary>
        /// FAR's prediction (FARAPI.CalculateVesselAeroForces) at an altitude, Mach, angle of attack and sideslip (deg,
        /// positive with the airflow's velocity to the right of the nose), the surfaces as they are: the side force
        /// (kN, toward the vessel's right) and the yaw moment about the CoM (kN m, about the roof axis), the voxel body's
        /// share from its sections (FarSectionCapture) and the wings' as the rest, and the body parts with the largest
        /// yaw moments. One line.
        /// </summary>
        /// <summary>
        /// Drag (kN, against the surface velocity) split into FAR's wing models and its voxel body, as the prediction API
        /// gives it at the flown state and deflection (the body by FarSectionCapture, the wings the rest) and as the
        /// flight applied it. False when either is unavailable.
        /// </summary>
        /// <summary>
        /// The external (shock) temperature the flight's wings take their skin friction at (vessel.externalTemperature)
        /// and the one FAR's prediction rebuilds for the same state (FARBaseAerodynamics.StockExternalTemperature from the
        /// prediction gas's temperature, the surface speed and FAR's Mach). NaN where unavailable.
        /// </summary>
        public static void ExternalTemperatures(Vessel v, out double live, out double predicted)
        {
            live = v != null ? v.externalTemperature : double.NaN;
            predicted = double.NaN;
            try
            {
                const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                Type baseT = Reflect.FindType("ferram4.FARBaseAerodynamics");
                if (baseT == null || !FlowInputs(v, out _, out _, out _, out double predMach, out _, out _, out double predTempK, out _)) return;
                predicted = Convert.ToDouble(baseT.GetMethod("StockExternalTemperature", S, null,
                        new[] { typeof(CelestialBody), typeof(double), typeof(double), typeof(double) }, null)
                    .Invoke(null, new object[] { v.mainBody, predTempK, v.srfSpeed, predMach }));
            }
            catch (Exception) { }
        }

        public static bool WingBodyDrag(Vessel v, out double apiWing, out double apiBody, out double trueWing, out double trueBody)
        {
            apiWing = apiBody = trueWing = trueBody = double.NaN;
            if (v == null || !FarSectionCapture.EnsurePatched()) return false;
            PredictedPitchTorqueKNm(v, out _, out _);   // resolves _calc
            if (_calc == null) return false;
            Vector3d velN = ((Vector3d)v.srf_velocity).normalized;
            ClearMemo(v);
            object[] args = { v, null, null, (Vector3)v.srf_velocity, v.altitude };
            FarSectionCapture.Arm();
            try { _calc.Invoke(null, args); }
            catch (Exception) { return false; }
            finally { FarSectionCapture.Disarm(); }
            Vector3d total = (Vector3)args[1], body = Vector3d.zero;
            foreach (var kv in FarSectionCapture.Parts)
                body += kv.Value.Force;
            apiBody = -Vector3d.Dot(body, velN);
            apiWing = -Vector3d.Dot(total - body, velN);
            TrueAeroMomentKNm(v, out _, out _, null, out Vector3 wingForce, out Vector3 bodyForce, null);
            trueWing = -Vector3d.Dot((Vector3d)wingForce, velN);
            trueBody = -Vector3d.Dot((Vector3d)bodyForce, velN);
            return true;
        }

        public static string YawBreakdown(Vessel v, double altitude, double mach, double aoaDeg, double betaDeg)
        {
            if (v == null || !FarSectionCapture.EnsurePatched()) return "unavailable";
            PredictedPitchTorqueKNm(v, out _, out _);   // resolves _calc
            if (_calc == null) return "no FARAPI";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Transform rt = v.ReferenceTransform;
            Vector3d nose = rt.up, right = rt.right, roof = -rt.forward;
            CelestialBody b = v.mainBody;
            double p = b.GetPressure(altitude), t = b.GetTemperature(altitude), rho = b.GetDensity(p, t);
            double speed = mach * Math.Sqrt(b.atmosphereAdiabaticIndex * p * 1000.0 / rho);
            double a = aoaDeg * Math.PI / 180.0, s = betaDeg * Math.PI / 180.0;
            Vector3d dir = nose * Math.Cos(a) * Math.Cos(s) + right * Math.Sin(s) - roof * Math.Sin(a) * Math.Cos(s);
            Vector3d com = v.CoM;
            ClearMemo(v);
            object[] args = { v, null, null, (Vector3)(dir * speed), altitude };
            FarSectionCapture.Arm();
            try { _calc.Invoke(null, args); }
            finally { FarSectionCapture.Disarm(); }
            Vector3d force = (Vector3)args[1], torque = (Vector3)args[2];
            double bodyYaw = 0, bodySide = 0;
            var parts = new List<KeyValuePair<string, double>>();
            foreach (var kv in FarSectionCapture.Parts)
            {
                Vector3d about = kv.Value.Torque - Vector3d.Cross(com, kv.Value.Force);
                double y = Vector3d.Dot(about, roof);
                bodyYaw += y;
                bodySide += Vector3d.Dot(kv.Value.Force, right);
                Vector3d at = kv.Key.transform.position - (Vector3)com;
                parts.Add(new KeyValuePair<string, double>(string.Format(inv, "{0}@{1:F1},{2:F1}", kv.Key.partInfo.name,
                    Vector3d.Dot(at, right), Vector3d.Dot(at, nose)), y));
            }
            parts.Sort((x, y) => Math.Abs(y.Value).CompareTo(Math.Abs(x.Value)));
            double yaw = Vector3d.Dot(torque, roof), side = Vector3d.Dot(force, right);
            return string.Format(inv, "side {0:F3} kN (body {1:F3}) yaw {2:F2} kN m (body {3:F2}, wings {4:F2}); body parts: {5}",
                side, bodySide, yaw, bodyYaw, yaw - bodyYaw,
                string.Join(" ", parts.GetRange(0, Math.Min(8, parts.Count)).ConvertAll(x => x.Key + "=" + x.Value.ToString("F2", inv)).ToArray()));
        }

        /// <summary>
        /// Every part's FAR geometry bounds (GeometryPartModule.overallMeshBounds, the vessel frame FAR voxelizes in) as
        /// csv rows: part, flightID, min x/y/z, max x/y/z, and the part's position in the same frame.
        /// </summary>
        public static string GeometryBoundsCsv(Vessel v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("part,id,minX,minY,minZ,maxX,maxY,maxZ,posX,posY,posZ\n");
            if (v == null) return sb.ToString();
            Matrix4x4 worldToVessel = v.vesselTransform.worldToLocalMatrix;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.GetType().Name != "GeometryPartModule") continue;
                    try
                    {
                        var b = (Bounds)m.GetType().GetField("overallMeshBounds").GetValue(m);
                        Vector3 pos = worldToVessel.MultiplyPoint3x4(p.transform.position);
                        sb.Append(string.Format(inv, "{0},{1},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8:F4},{9:F4},{10:F4}\n",
                            p.partInfo.name, p.flightID, b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z, pos.x, pos.y, pos.z));
                    }
                    catch (Exception ex) { sb.Append(p.partInfo.name + "," + ex.GetType().Name + "\n"); }
                }
            return sb.ToString();
        }

        /// <summary>
        /// FAR's voxel model of the flight vessel, written to dir: voxel_sections.csv (each cross-section's area and
        /// centroid, vessel frame, as FAR applies the section's force there) and voxel_parts.csv (each part's voxel
        /// volume - the sum of its voxels' fill - and the fill-weighted mean of its voxels' x index, from which a mirror
        /// twin's should differ only in sign about the grid's center). Returns a one-line summary.
        /// </summary>
        public static string VoxelDump(Vessel v, string dir)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Type vaType = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
            Component va = vaType == null ? null : v.GetComponent(vaType);
            object aero = va == null ? null : vaType.GetField("_vehicleAero", any)?.GetValue(va);
            if (aero == null) return "no vehicle aero";
            var sb = new System.Text.StringBuilder("m,area,cx,cy,cz\n");
            var sections = aero.GetType().GetField("_vehicleCrossSection", any)?.GetValue(aero) as Array;
            double moment = 0, total = 0;
            if (sections != null)
                for (int m = 0; m < sections.Length; ++m)
                {
                    object s = sections.GetValue(m);
                    double area = (double)s.GetType().GetField("area").GetValue(s);
                    var c = (Vector3d)s.GetType().GetField("centroid").GetValue(s);
                    if (area <= 0) continue;
                    sb.Append(string.Format(inv, "{0},{1:F5},{2:F4},{3:F4},{4:F4}\n", m, area, c.x, c.y, c.z));
                    moment += area * c.x;
                    total += area;
                }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "voxel_sections.csv"), sb.ToString());

            object voxel = aero.GetType().GetField("_voxel", any)?.GetValue(aero);
            var chunks = voxel?.GetType().GetField("voxelChunks", any)?.GetValue(voxel) as Array;
            var volume = new Dictionary<Part, double>();
            var xSum = new Dictionary<Part, double>();
            int xCells = 0;
            if (chunks != null)
            {
                xCells = (int)voxel.GetType().GetField("xCellLength", any).GetValue(voxel);
                for (int ci = 0; ci < chunks.GetLength(0); ++ci)
                    for (int cj = 0; cj < chunks.GetLength(1); ++cj)
                        for (int ck = 0; ck < chunks.GetLength(2); ++ck)
                        {
                            object chunk = chunks.GetValue(ci, cj, ck);
                            if (chunk == null) continue;
                            var points = chunk.GetType().GetField("voxelPoints", any)?.GetValue(chunk) as Array;
                            if (points == null) continue;
                            for (int idx = 0; idx < points.Length; ++idx)
                            {
                                object pair = points.GetValue(idx);
                                var part = pair.GetType().GetField("part", any)?.GetValue(pair) as Part;
                                if (part == null) continue;
                                double size = Convert.ToDouble(pair.GetType().GetMethod("GetSize", any).Invoke(pair, null));
                                if (size > 1.0) size = 1.0;
                                if (size <= 0) continue;
                                int i = ci * 8 + idx % 8;
                                volume[part] = (volume.TryGetValue(part, out double vv) ? vv : 0) + size;
                                xSum[part] = (xSum.TryGetValue(part, out double xs) ? xs : 0) + size * i;
                            }
                        }
            }
            var pb = new System.Text.StringBuilder("part,id,volumeVoxels,meanXIndex,mirroredXIndex\n");
            foreach (var kv in volume)
            {
                double mean = xSum[kv.Key] / kv.Value;
                pb.Append(string.Format(inv, "{0},{1},{2:F2},{3:F3},{4:F3}\n", kv.Key.partInfo.name, kv.Key.flightID, kv.Value, mean, (xCells - mean)));
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "voxel_parts.csv"), pb.ToString());
            return string.Format(inv, "sections {0}, area-weighted centroid x {1:F4} m, parts {2}, x cells {3}",
                sections?.Length ?? 0, total > 0 ? moment / total : double.NaN, volume.Count, xCells);
        }

        /// <summary>The VehicleAerodynamics behind the editor tab or the flight vessel (FARVesselAero._vehicleAero); null without FAR.</summary>
        private static object VehicleAero()
        {
            try
            {
                if (HighLogic.LoadedSceneIsEditor)
                {
                    Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
                    object gui = guiType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                    return gui == null ? null : Reflect.Get(gui, "_vehicleAero");
                }
                var v = FlightGlobals.ActiveVessel;
                if (v == null) return null;
                foreach (var c in v.GetComponents<MonoBehaviour>())
                    if (c != null && c.GetType().Name == "FARVesselAero") return Reflect.Get(c, "_vehicleAero");
            }
            catch (Exception ex) { Log.Info("FAR VehicleAerodynamics lookup failed: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// The voxel grid FAR last built for the editor craft or the active vessel: element (cube) size in m, the
        /// bounds it covers, and the vehicle length and max cross-section it derived. False without FAR or before
        /// the first voxelization.
        /// </summary>
        public static bool VoxelInfo(out double elementSize, out Vector3 boundsSize, out double length, out double maxCrossSection)
        {
            elementSize = length = maxCrossSection = double.NaN; boundsSize = Vector3.zero;
            object va = VehicleAero();
            if (va == null) return false;
            try
            {
                elementSize = Convert.ToDouble(Reflect.Get(va, "_voxelElementSize"));
                length = Convert.ToDouble(Reflect.Get(va, "Length"));
                maxCrossSection = Convert.ToDouble(Reflect.Get(va, "MaxCrossSectionArea"));
                boundsSize = ((Bounds)Reflect.Get(va, "VoxelBounds")).size;
                return elementSize > 0;
            }
            catch (Exception ex) { Log.Info("FAR voxel info failed: " + ex.Message); return false; }
        }

        /// <summary>True for the parts VehicleVoxel's sizing fix leaves out of the sizing bounds: control surfaces and wheels.</summary>
        public static bool IsControlSurfaceOrWheel(Part p)
        {
            foreach (PartModule m in p.Modules)
            {
                string n = m.GetType().Name;
                if (n == "FARControllableSurface" || n == "KSPWheelBase" || m is ModuleWheelBase) return true;
            }
            return false;
        }

        /// <summary>
        /// FAR's per-part voxel face areas (FARAeroPartModule.ProjectedAreas): the sum over every part, the sum over the
        /// parts that are neither control surfaces nor wheels, and one CSV row per part (name, persistentId, totalArea,
        /// the six faces, fixed flag) appended to rows with the given tag.
        /// </summary>
        public static void PartAreas(System.Collections.Generic.IList<Part> parts, string tag, System.Text.StringBuilder rows, out double totalAll, out double totalFixed)
        {
            totalAll = totalFixed = 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (Part p in parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (m.GetType().Name != "FARAeroPartModule") continue;
                    object pa = Reflect.Get(m, "ProjectedAreas");
                    double total = Convert.ToDouble(Reflect.Get(pa, "totalArea"));
                    bool fixedPart = !IsControlSurfaceOrWheel(p);
                    totalAll += total;
                    if (fixedPart) totalFixed += total;
                    if (rows != null)
                    {
                        rows.Append(tag).Append(',').Append(p.partInfo != null ? p.partInfo.name : p.name).Append(',').Append(p.persistentId).Append(',')
                            .Append(total.ToString("F3", inv));
                        foreach (string f in new[] { "iP", "iN", "jP", "jN", "kP", "kN" })
                            rows.Append(',').Append(Convert.ToDouble(Reflect.Get(pa, f)).ToString("F3", inv));
                        rows.Append(',').Append(fixedPart ? 1 : 0).Append('\n');
                    }
                    break;
                }
            }
        }

        public const string PartAreasHeader = "sample,part,persistentId,totalArea,iP,iN,jP,jN,kP,kN,fixedPart\n";

        /// <summary>
        /// Shows FAR's debug voxel mesh for the editor craft (the "Display Voxels" button on the debug tab), building it
        /// from the voxel as it is now: clears a stale display first so a re-voxelized craft is drawn afresh.
        /// </summary>
        public static bool ShowVoxels()
        {
            object va = VehicleAero();
            if (va == null || !HighLogic.LoadedSceneIsEditor) return false;
            try
            {
                var m = (Matrix4x4)EditorLogic.RootPart.partTransform.localToWorldMatrix;
                if ((bool)Reflect.Get(va, "visualizing")) Reflect.Call(va, "DebugVisualizeVoxels", m);
                Reflect.Call(va, "DebugVisualizeVoxels", m);
                return true;
            }
            catch (Exception ex) { Log.Info("FAR voxel display failed: " + ex.Message); return false; }
        }

        /// <summary>Hides the debug voxel mesh if it is shown.</summary>
        public static void HideVoxels()
        {
            object va = VehicleAero();
            try { if (va != null && (bool)Reflect.Get(va, "visualizing")) Reflect.Call(va, "DebugVisualizeVoxels", (Matrix4x4)EditorLogic.RootPart.partTransform.localToWorldMatrix); } catch { }
        }

        /// <summary>A part's FAR voxel face area total (FARAeroPartModule.ProjectedAreas.totalArea); NaN without one.</summary>
        public static double PartArea(Part p)
        {
            foreach (PartModule m in p.Modules)
                if (m.GetType().Name == "FARAeroPartModule")
                {
                    try { return Convert.ToDouble(Reflect.Get(Reflect.Get(m, "ProjectedAreas"), "totalArea")); } catch { return double.NaN; }
                }
            return double.NaN;
        }

        /// <summary>Asks FAR to voxelize the active vessel again (FARVesselAero.RequestUpdateVoxel); false without FAR.</summary>
        public static bool RequestRevoxel(Vessel v)
        {
            try
            {
                foreach (var c in v.GetComponents<MonoBehaviour>())
                    if (c != null && c.GetType().Name == "FARVesselAero") { Reflect.Call(c, "RequestUpdateVoxel", true); return true; }
            }
            catch (Exception ex) { Log.Info("FAR RequestUpdateVoxel failed: " + ex.Message); }
            return false;
        }

        /// <summary>A double-valued static on FARAPI taking the vessel (VesselDragCoeff, VesselLiftCoeff, VesselRefArea, VesselAoA...); NaN if absent.</summary>
        public static double VesselStat(Vessel v, string name)
        {
            try
            {
                var t = Reflect.FindType("FerramAerospaceResearch.FARAPI");
                var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Vessel) }, null);
                return m == null ? double.NaN : Convert.ToDouble(m.Invoke(null, new object[] { v }));
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// FAR's editor instant sim (the static analysis) evaluated at an angle of attack, Mach and pitch input:
        /// Cl, Cd and Cm as the editor reports them. Reflection into FAR's internal sim so it runs on any build.
        /// </summary>
        public static bool EditorClCdCm(double alphaDeg, double mach, double pitchInput, out double cl, out double cd, out double cm)
        {
            cl = cd = cm = double.NaN;
            try
            {
                Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
                object gui = guiType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                if (gui == null) { Log.Info("FAR EditorGUI.Instance is null"); return false; }
                object sim = Reflect.Get(gui, "_instantSim");
                if (sim == null) { Log.Info("FAR _instantSim is null"); return false; }
                Type inType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSimInput");
                object input = Activator.CreateInstance(inType);
                Reflect.Set(input, "alpha", alphaDeg); Reflect.Set(input, "beta", 0.0); Reflect.Set(input, "phi", 0.0);
                Reflect.Set(input, "machNumber", mach); Reflect.Set(input, "pitchValue", pitchInput);
                Reflect.Set(input, "flaps", 0); Reflect.Set(input, "spoilers", false);
                var method = sim.GetType().GetMethod("GetClCdCmSteady", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object[] args = { input, null, true, false };
                method.Invoke(sim, args);
                object o = args[1];
                cl = Convert.ToDouble(Reflect.Get(o, "Cl")); cd = Convert.ToDouble(Reflect.Get(o, "Cd")); cm = Convert.ToDouble(Reflect.Get(o, "Cm"));
                return true;
            }
            catch (Exception ex) { Log.Info("FAR editor sim call failed: " + ex.Message); return false; }
        }
    }

    /// <summary>Flight-scene helpers: telemetry, warp, pause, locks.</summary>
    public static class Flight
    {
        public static Vessel ActiveVessel => FlightGlobals.ActiveVessel;

        public static double Altitude => ActiveVessel?.altitude ?? double.NaN;
        public static double RadarAltitude => ActiveVessel?.heightFromTerrain ?? double.NaN;
        public static double SurfaceSpeed => ActiveVessel?.srfSpeed ?? double.NaN;
        public static double OrbitalSpeed => ActiveVessel?.obt_speed ?? double.NaN;
        public static double Apoapsis => ActiveVessel?.orbit?.ApA ?? double.NaN;
        public static double Periapsis => ActiveVessel?.orbit?.PeA ?? double.NaN;
        public static double Inclination => ActiveVessel?.orbit?.inclination ?? double.NaN;
        public static bool Landed => ActiveVessel != null && ActiveVessel.LandedOrSplashed;
        public static bool InOrbit => ActiveVessel != null && ActiveVessel.situation == Vessel.Situations.ORBITING;
        public static bool Dead => ActiveVessel == null || ActiveVessel.state == Vessel.State.DEAD || ActiveVessel.rootPart == null;

        /// <summary>Angle of attack in degrees (velocity vs the reference transform forward axis).</summary>
        public static double AoA
        {
            get
            {
                var v = ActiveVessel;
                if (v == null || v.srfSpeed < 1) return double.NaN;
                var fwd = v.ReferenceTransform.up;
                var vel = v.srf_velocity.normalized;
                var up = v.ReferenceTransform.forward * -1f;   // vessel "top"
                double a = Vector3.Angle(fwd, vel);
                return Vector3.Dot(vel, up) < 0 ? a : -a;
            }
        }

        public static string Telemetry()
        {
            var v = ActiveVessel;
            if (v == null) return "no active vessel";
            return $"'{v.vesselName}' {v.situation} alt={v.altitude:F0}m srf={v.srfSpeed:F1}m/s obt={v.obt_speed:F1}m/s Ap={v.orbit?.ApA:F0} Pe={v.orbit?.PeA:F0} inc={v.orbit?.inclination:F2} mass={v.totalMass:F2}t";
        }

        /// <summary>
        /// Stock's "Ignore Max Temperature" cheat: parts over their limit stay on the vessel instead of exploding,
        /// so a telemetry log shows how far past 100 % they go (the editor analysis keeps them too). The runner's
        /// cleanup switches it back off after every test.
        /// </summary>
        public static void IgnoreMaxTemperature(bool on)
        {
            if (CheatOptions.IgnoreMaxTemperature == on) return;
            CheatOptions.IgnoreMaxTemperature = on;
            Log.Info("cheat IgnoreMaxTemperature = " + on);
        }

        /// <summary>
        /// The vehicle's angle in the frame stock's solar occlusion judges its rectangles in: OcclusionData.Update
        /// projects through Quaternion.FromToRotation(sunDir, Vector3.up) and reads x/z, so this is the lateral axis's
        /// in-plane angle there, folded to 0..90 (the axis-aligned rectangles repeat every quarter turn; 0 = tight).
        /// It turns with the planet's azimuth in world coordinates, not with the vessel or the sun.
        /// </summary>
        public static double SolarFrameAngle(Vessel v)
        {
            if (v?.ReferenceTransform == null || Planetarium.fetch?.Sun == null) return double.NaN;
            Vector3 sun = (Vector3)(Planetarium.fetch.Sun.position - v.CoMD).normalized;
            Vector3 lateral = Vector3.ProjectOnPlane(v.ReferenceTransform.right, sun);
            if (lateral.sqrMagnitude < 1e-6f) return double.NaN;
            Vector3 f = Quaternion.FromToRotation(sun, Vector3.up) * lateral.normalized;
            double deg = Math.Atan2(f.z, f.x) * 180.0 / Math.PI;
            deg = Math.Abs(deg) % 180.0;
            return deg > 90.0 ? 180.0 - deg : deg;
        }

        /// <summary>Stock's "Infinite Propellant" cheat (engines and RCS draw nothing); the runner's cleanup switches it back off.</summary>
        public static void InfinitePropellant(bool on)
        {
            if (CheatOptions.InfinitePropellant == on) return;
            CheatOptions.InfinitePropellant = on;
            Log.Info("cheat InfinitePropellant = " + on);
        }

        /// <summary>
        /// Puts the active vessel on an orbit around its body (the stock "Set Orbit" cheat, FlightGlobals.SetShipOrbit):
        /// periapsis and apoapsis altitudes in m, starting at the apoapsis. Works from the ground; the vessel goes on
        /// rails there and comes off them as usual.
        /// </summary>
        public static void SetOrbit(double peAltM, double apAltM, double inclinationDeg = 0, double lanDeg = 0, double argPeDeg = 0)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            Log.Info($"set orbit {body.bodyName} Pe {peAltM / 1000:F1} km Ap {apAltM / 1000:F1} km inc {inclinationDeg} (sma {sma:F0} ecc {ecc:F4}), at apoapsis");
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, Math.PI, argPeDeg, period * 0.5);
        }

        /// <summary>
        /// Puts the active vessel on an orbit (periapsis/apoapsis altitudes, inclination, LAN, argument of periapsis,
        /// degrees) at the point where it descends through altM - the way HyperEdit places a vessel - so an entry can
        /// be aimed.
        /// </summary>
        public static void SetOrbitDescendingAt(double peAltM, double apAltM, double inclinationDeg, double lanDeg, double argPeDeg, double altM)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM, r = body.Radius + altM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            double cosNu = Math.Max(-1, Math.Min(1, (sma * (1 - ecc * ecc) / r - 1) / ecc));
            double nu = 2 * Math.PI - Math.Acos(cosNu);   // the descending half, heading for periapsis
            double E = 2 * Math.Atan(Math.Sqrt((1 - ecc) / (1 + ecc)) * Math.Tan(nu / 2));
            double M = E - ecc * Math.Sin(E);
            if (M < 0) M += 2 * Math.PI;
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, M, argPeDeg, M / (2 * Math.PI) * period);
        }

        /// <summary>
        /// Puts the active vessel on an orbit (apsis altitudes, inclination, LAN, argument of periapsis in degrees) at a
        /// mean anomaly (radians) now - HyperEdit's orbit editor.
        /// </summary>
        public static void SetOrbitAtMeanAnomaly(double peAltM, double apAltM, double inclinationDeg, double lanDeg, double argPeDeg, double meanAnomalyRad)
        {
            var v = ActiveVessel; var body = v.mainBody;
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double period = 2 * Math.PI * Math.Sqrt(sma * sma * sma / body.gravParameter);
            double M = meanAnomalyRad % (2 * Math.PI);
            if (M < 0) M += 2 * Math.PI;
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, ecc, sma, inclinationDeg, lanDeg, M, argPeDeg, M / (2 * Math.PI) * period);
        }

        /// <summary>
        /// Turns the active vessel so its control point's nose sits aoaDeg above its surface velocity with the wings
        /// level - the attitude a descent profile holds. Placing a vessel on a new orbit keeps its old orientation,
        /// which against the new velocity can be anything (an SSTO came out at -127 deg AoA and spent 20 s slewing).
        /// </summary>
        public static void PointAtAoA(double aoaDeg)
        {
            var v = ActiveVessel;
            v.SetRotation(AoARotation(v, aoaDeg));
        }

        /// <summary>
        /// <see cref="PointAtAoA"/> against a given surface velocity direction and local up (world) instead of the
        /// vessel's own - for a vessel being placed, whose own velocity is not yet the one it will fly with.
        /// </summary>
        public static void PointAtAoA(double aoaDeg, Vector3d velDir, Vector3d up)
        {
            var v = ActiveVessel;
            v.SetRotation(AoARotation(v, aoaDeg, velDir, up));
        }

        /// <summary>
        /// Holds the attitude of <see cref="PointAtAoA"/> without teleporting: every part's angular velocity is set to
        /// turn the vessel by `gain` of its remaining error over the next physics frame. Vessel.SetRotation puts every
        /// part back at its as-built pose relative to the root, undoing the joints' flex, so a light wing under a large
        /// load accelerates away from the fuselage again within every frame and flies at another angle than the vessel;
        /// steering leaves the flex to settle. Returns the remaining error (deg).
        /// </summary>
        public static double SteerToAoA(double aoaDeg, double gain = 0.5)
        {
            var v = ActiveVessel;
            Quaternion error = AoARotation(v, aoaDeg) * Quaternion.Inverse(v.transform.rotation);
            error.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-6f)
                axis = Vector3.zero;
            Vector3 omega = axis * (angle * Mathf.Deg2Rad * (float)gain / TimeWarp.fixedDeltaTime);
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                    p.Rigidbody.angularVelocity = omega;
            return Mathf.Abs(angle);
        }

        /// <summary>
        /// The torque (kN m, world, about the rigidbodies' mass center) the last <see cref="TorqueHoldAoA"/> applied:
        /// held steady, minus the vessel's own net torque - what an equilibrium leaves at zero.
        /// </summary>
        public static Vector3 LastHoldTorque;

        /// <summary>
        /// Holds the attitude of <see cref="PointAtAoA"/> the way a rigid body would be turned: a damped angular
        /// acceleration toward the target (natural frequency wn rad/s, critically damped against the vessel's mass-
        /// weighted angular velocity), given to every part as the force m (alpha x r) about the vessel's CoM plus the
        /// same angular acceleration on its own rotation. Those forces sum to no net force and load no joint, so the
        /// flex is left to the air. Returns the remaining error (deg).
        /// </summary>
        public static double TorqueHoldAoA(double aoaDeg, double wn = 15)
        {
            var v = ActiveVessel;
            Quaternion error = AoARotation(v, aoaDeg) * Quaternion.Inverse(v.transform.rotation);
            error.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-6f)
                axis = Vector3.zero;
            Vector3 theta = axis * (angle * Mathf.Deg2Rad);
            Vector3 omega = Vector3.zero;
            // The rigidbodies' own mass center: the forces below sum to zero only about it (KSP's vessel.CoM is
            // computed differently and is off by enough to push the whole vessel).
            Vector3 com = Vector3.zero;
            float mass = 0;
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                {
                    omega += p.Rigidbody.angularVelocity * p.Rigidbody.mass;
                    com += p.Rigidbody.worldCenterOfMass * p.Rigidbody.mass;
                    mass += p.Rigidbody.mass;
                }
            if (mass <= 0) return Mathf.Abs(angle);
            omega /= mass;
            com /= mass;
            Vector3 alpha = (float)(wn * wn) * theta - (float)(2 * wn) * omega;
            Vector3 torque = Vector3.zero;
            foreach (Part p in v.parts)
            {
                Rigidbody rb = p.Rigidbody;
                if (rb == null) continue;
                Vector3 r = rb.worldCenterOfMass - com, f = Vector3.Cross(alpha, r) * rb.mass;
                rb.AddForce(f, ForceMode.Force);
                rb.AddTorque(alpha, ForceMode.Acceleration);
                Quaternion q = rb.rotation * rb.inertiaTensorRotation;
                torque += Vector3.Cross(r, f) + q * Vector3.Scale(rb.inertiaTensor, Quaternion.Inverse(q) * alpha);
            }
            LastHoldTorque = torque;
            return Mathf.Abs(angle);
        }

        /// <summary>How far (deg) the vessel is from the attitude of <see cref="PointAtAoA"/>.</summary>
        public static double AttitudeErrorDeg(double aoaDeg)
        {
            var v = ActiveVessel;
            return Quaternion.Angle(AoARotation(v, aoaDeg), v.transform.rotation);
        }

        // The vessel rotation that puts its control point's nose aoaDeg above the surface velocity, wings level.
        private static Quaternion AoARotation(Vessel v, double aoaDeg) =>
            AoARotation(v, aoaDeg, v.srf_velocity.sqrMagnitude > 1 ? v.srf_velocity.normalized : v.obt_velocity.normalized,
                        (v.GetWorldPos3D() - v.mainBody.position).normalized);

        private static Quaternion AoARotation(Vessel v, double aoaDeg, Vector3d vel, Vector3d up)
        {
            vel = vel.normalized;
            Vector3d liftUp = Vector3d.Exclude(vel, up).normalized;
            double a = aoaDeg * Math.PI / 180;
            Vector3 nose = (Vector3)(vel * Math.Cos(a) + liftUp * Math.Sin(a));
            Vector3 belly = (Vector3)(-(liftUp * Math.Cos(a) - vel * Math.Sin(a)));
            // The control point: its up is the nose and its forward the belly.
            Quaternion refRot = Quaternion.LookRotation(belly, nose);
            Quaternion localRef = Quaternion.Inverse(v.transform.rotation) * v.ReferenceTransform.rotation;
            return refRot * Quaternion.Inverse(localRef);
        }

        /// <summary>Mean anomaly (radians) of a true anomaly (degrees) on an orbit of eccentricity ecc.</summary>
        public static double MeanAnomalyOf(double trueAnomalyDeg, double ecc)
        {
            double nu = trueAnomalyDeg * Math.PI / 180;
            double E = 2 * Math.Atan(Math.Sqrt((1 - ecc) / (1 + ecc)) * Math.Tan(nu / 2));
            double M = E - ecc * Math.Sin(E);
            return M < 0 ? M + 2 * Math.PI : M;
        }

        /// <summary>True anomaly (deg) at which an orbit with these apsides descends through altM.</summary>
        public static double DescendingTrueAnomalyDeg(CelestialBody body, double peAltM, double apAltM, double altM)
        {
            double rp = body.Radius + peAltM, ra = body.Radius + apAltM, r = body.Radius + altM;
            double sma = 0.5 * (rp + ra), ecc = (ra - rp) / (ra + rp);
            double cosNu = Math.Max(-1, Math.Min(1, (sma * (1 - ecc * ecc) / r - 1) / ecc));
            return 360.0 - Math.Acos(cosNu) * 180.0 / Math.PI;
        }

        public static void Unpause()
        {
            if (FlightDriver.Pause) FlightDriver.SetPause(false);
        }

        public static void ClearLocks()
        {
            InputLockManager.ClearControlLocks();
        }

        /// <summary>
        /// On-rails warp by index into TimeWarp.fetch.warpRates (0 = 1x). Note KSP allows this on the
        /// pad, which packs the vessel and locks staging/throttle (TimeWarpLock) - use PhysicsWarp there.
        /// </summary>
        public static void Warp(int rateIndex)
        {
            Log.Info("warp index " + rateIndex);
            if (rateIndex > 0) TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(rateIndex, true, false);
        }

        /// <summary>Physics warp (1-4x, index into physicsWarpRates); the vessel keeps simulating and controls stay live.</summary>
        public static void PhysicsWarp(int rateIndex)
        {
            Log.Info("physics warp index " + rateIndex);
            TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
            TimeWarp.SetRate(rateIndex, true, false);
        }

        /// <summary>Physics warp by rate (e.g. 4 for 4x): picks the physicsWarpRates step nearest the rate, whatever a warp mod put in the table.</summary>
        public static void PhysicsWarpRate(double rate)
        {
            float[] rates = TimeWarp.fetch.physicsWarpRates;
            // Stock's table stops at 4x. BetterTimeWarp only swaps this same array for one of its presets, so
            // do the same: a table that ends at the rate asked for (BetterTimeWarp puts its own back next flight).
            if (rate > rates[rates.Length - 1] * 1.05)
            {
                rates = new float[rates.Length];
                for (int i = 0; i < rates.Length; ++i) rates[i] = i == 0 ? 1f : (float)(rate * Math.Pow(0.5, rates.Length - 1 - i));
                TimeWarp.fetch.physicsWarpRates = rates;
            }
            int best = 0;
            for (int i = 1; i < rates.Length; ++i)
                if (System.Math.Abs(rates[i] - rate) < System.Math.Abs(rates[best] - rate)) best = i;
            Log.Info($"physics warp {rate}x -> index {best} ({rates[best]}x) of [{string.Join(",", rates)}]");
            PhysicsWarp(best);
        }

        public static void WarpOff()
        {
            TimeWarp.SetRate(0, true, false);
        }

        /// <summary>
        /// Custom waypoints (shown by stock and Waypoint Manager) on the KSC runway and on its extended centerline
        /// before it, for an approach in the runway's direction: "RWY" at the runway spawn point (its start, facing
        /// down the runway), and "RWY -n km" n km before it, at the height a glideslope of glideDeg gives. Returns the
        /// runway heading (deg), NaN without a runway. Flight scene only.
        /// </summary>
        public static double AddRunwayWaypoints(double[] distancesKm, double glideDeg, System.Collections.Generic.List<string> added)
        {
            var facility = PSystemSetup.Instance?.GetSpaceCenterFacility("Runway");
            var spawn = facility?.GetSpawnPoint("Runway");
            if (spawn == null || ScenarioCustomWaypoints.Instance == null) return double.NaN;
            spawn.GetSpawnPointLatLonAlt(out double lat, out double lon, out double alt);
            CelestialBody body = FlightGlobals.GetHomeBody();
            Transform t = spawn.GetSpawnPointTransform();
            // down the runway: whichever of the spawn point's axes lies flattest along the ground
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d north = Vector3d.Exclude(up, body.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            Vector3d dir = Math.Abs(Vector3d.Dot(t.forward, up)) < Math.Abs(Vector3d.Dot(t.up, up)) ? (Vector3d)t.forward : (Vector3d)t.up;
            dir = Vector3d.Exclude(up, dir).normalized;
            double heading = (Math.Atan2(Vector3d.Dot(dir, east), Vector3d.Dot(dir, north)) * 180 / Math.PI + 360) % 360;
            double r = body.Radius, d2r = Math.PI / 180;
            Action<string, double, double, double> add = (name, la, lo, al) =>
            {
                var wp = new FinePrint.Waypoint { name = name, celestialName = body.name, latitude = la, longitude = lo, altitude = al };
                ScenarioCustomWaypoints.AddWaypoint(wp);
                added?.Add($"{name}: {la:F5}, {lo:F5}, {al:F0} m");
            };
            add("RWY", lat, lon, alt);
            foreach (double km in distancesKm)
            {
                double d = km * 1000;
                double la = lat - d * Math.Cos(heading * d2r) / r / d2r;
                double lo = lon - d * Math.Sin(heading * d2r) / (r * Math.Cos(lat * d2r)) / d2r;
                add($"RWY -{km:0.#} km", la, lo, alt + d * Math.Tan(glideDeg * d2r));
            }
            Log.Info($"runway waypoints: heading {heading:F1} deg; " + string.Join("; ", added ?? new System.Collections.Generic.List<string>()));
            return heading;
        }

        // TimeControl's HyperWarpController (by reflection: TimeControl is optional), or null without it.
        private static object HyperWarpController()
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                var t = a.assembly.GetType("TimeControl.HyperWarpController", false);
                if (t != null)
                    return t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
            }
            return null;
        }

        /// <summary>
        /// TimeControl's hyper-warp at a rate, with the physics step at physicsAccuracy x the default (1 = the 1x
        /// step, more physics frames per rendered frame instead): what it attempts, the machine sets what it gets.
        /// False without TimeControl or when it cannot hyper-warp now.
        /// </summary>
        public static bool HyperWarp(double rate, double physicsAccuracy = 1.0)
        {
            object hw = HyperWarpController();
            if (hw == null) return false;
            var t = hw.GetType();
            t.GetProperty("MaxAttemptedRate").SetValue(hw, (float)rate, null);
            t.GetProperty("PhysicsAccuracy").SetValue(hw, (float)physicsAccuracy, null);
            t.GetMethod("ActivateHyper", Type.EmptyTypes).Invoke(hw, null);
            bool on = (bool)t.GetProperty("IsHyperWarping").GetValue(hw, null);
            Log.Info($"hyper-warp {rate}x at physics accuracy {physicsAccuracy}: {(on ? "on" : "refused")}, time scale {Time.timeScale}, step {Time.fixedDeltaTime:F3} s");
            return on;
        }

        /// <summary> TimeControl's attempted hyper-warp rate, changed while hyper-warping (physics accuracy as it is). </summary>
        public static bool SetHyperWarpRate(double rate)
        {
            object hw = HyperWarpController();
            if (hw == null) return false;
            hw.GetType().GetProperty("MaxAttemptedRate").SetValue(hw, (float)rate, null);
            Log.Info($"hyper-warp rate {rate}x: time scale {Time.timeScale}, step {Time.fixedDeltaTime:F3} s");
            return true;
        }

        public static void HyperWarpOff()
        {
            object hw = HyperWarpController();
            hw?.GetType().GetMethod("DeactivateHyper", Type.EmptyTypes).Invoke(hw, null);
        }

        public static IEnumerator WarpTo(double ut, double timeoutSec = 600)
        {
            Log.Info($"warp to UT {ut:F0} (now {Planetarium.GetUniversalTime():F0})");
            TimeWarp.fetch.WarpTo(ut);
            yield return Wait.Until(() => Planetarium.GetUniversalTime() >= ut - 1 && TimeWarp.CurrentRateIndex == 0, timeoutSec, "warp to UT");
        }

        public static IEnumerator WarpSeconds(double gameSeconds, double timeoutSec = 600)
        {
            yield return WarpTo(Planetarium.GetUniversalTime() + gameSeconds, timeoutSec);
        }

        /// <summary>Fails the test if the vessel is gone/destroyed; use inside long waits.</summary>
        public static void AssertAlive()
        {
            if (Dead) throw new UITestFailure("vessel is dead or gone");
        }
    }
}
