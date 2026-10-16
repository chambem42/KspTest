using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// The Trajectories mod by reflection: its public API (predicted impact point, descent profile) plus the
    /// two internals an accuracy test needs - the UseCache setting, and the aerodynamic model itself so the
    /// cached force and the force the model computes directly at the same state can be read side by side.
    /// Trajectories only recomputes when its trajectory is displayed, AlwaysUpdate is set, or a target is set
    /// (Trajectory.Update), so a test that samples the prediction must set AlwaysUpdate first.
    /// Every member is inert without Trajectories installed; check Available.
    /// </summary>
    public static class TrajApi
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static bool _resolved;
        private static Type _api, _settings, _trajectory;
        private static MethodInfo _computeForces, _getForces;

        /// <summary>A Trajectories type by full name (null without it), for tests reaching its internals.</summary>
        public static Type TryFindTypePublic(string fullName) => TryFindType(fullName);

        private static Type TryFindType(string fullName)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                var t = a.assembly.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _api = TryFindType("Trajectories.API");
            _settings = TryFindType("Trajectories.Settings");
            _trajectory = TryFindType("Trajectories.Trajectory");
            var model = TryFindType("Trajectories.AeroDynamicModel");
            if (model != null)
            {
                _computeForces = model.GetMethod("ComputeForces", All);
                _getForces = model.GetMethod("GetForces", All);
            }
        }

        /// <summary>True when Trajectories is installed and its API resolved.</summary>
        public static bool Available { get { Resolve(); return _api != null; } }

        /// <summary>Trajectories' version string, or "" without it.</summary>
        public static string Version
        {
            get
            {
                Resolve();
                if (_api == null) return "";
                try { return (string)Reflect.GetStatic(_api, "GetVersion"); }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>Which aerodynamic model Trajectories picked for the attached vessel ("FAR", "Stock", ...).</summary>
        public static string ModelName
        {
            get
            {
                Resolve();
                if (_trajectory == null) return "";
                try { return (string)Reflect.GetStatic(_trajectory, "AerodynamicModelName"); }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>
        /// Settings.AlwaysUpdate. Without it (and without the trajectory displayed or a target set) Trajectories
        /// never recomputes, and the API returns a stale prediction or none at all.
        /// </summary>
        public static bool AlwaysUpdate
        {
            get { Resolve(); return _api != null && (bool)Reflect.GetStatic(_api, "AlwaysUpdate"); }
            set { Resolve(); if (_api != null) Reflect.SetStatic(_api, "AlwaysUpdate", value); }
        }

        /// <summary>
        /// Settings.UseCache: on, forces come from the 32 x 33 x 32 velocity/AoA/altitude table interpolated
        /// trilinearly; off, every step calls the aerodynamic model directly. Not on the public API.
        /// </summary>
        public static bool UseCache
        {
            get { Resolve(); return _settings != null && (bool)Reflect.GetStatic(_settings, "UseCache"); }
            set { Resolve(); if (_settings != null) Reflect.SetStatic(_settings, "UseCache", value); }
        }

        /// <summary>
        /// The list object holding the last finished prediction. Trajectories computes a prediction a slice at a
        /// time (at most 2 ms per frame) into a back buffer and swaps it in when done, so this reference changing
        /// marks a completed prediction; the real time between changes is how stale the displayed one gets.
        /// </summary>
        public static object PatchesRef
        {
            get
            {
                Resolve();
                if (_trajectory == null) return null;
                try { return Reflect.GetStatic(_trajectory, "Patches"); }
                catch (Exception) { return null; }
            }
        }

        /// <summary>A point of the predicted path as a ground position: where the vessel is predicted to be over the
        /// surface at that time, the planet's rotation up to then removed the way Trajectories places its impact.</summary>
        public struct PathPoint { public double Lat, Lon, AltM, UT; }

        /// <summary>
        /// The predicted path of the last finished prediction, in order: every stored point of its atmospheric
        /// patches, each converted exactly as Trajectories converts its impact point (CalculateRotatedPosition at
        /// the point's time, then GetLatLonAlt against the body's current position), so it lines up with the impact
        /// marker. Empty when there is no prediction.
        /// </summary>
        public static List<PathPoint> PredictedPath()
        {
            var res = new List<PathPoint>();
            Resolve();
            if (_trajectory == null) return res;
            var v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return res;
            CelestialBody body = v.mainBody;
            var patches = PatchesRef as System.Collections.IEnumerable;
            if (patches == null) return res;
            var rotate = _trajectory.GetMethod("CalculateRotatedPosition", All);
            if (rotate == null) return res;
            foreach (object patch in patches)
            {
                if (patch == null || !(bool)Reflect.Get(patch, "IsAtmospheric")) continue;
                var points = Reflect.Get(patch, "AtmosphericTrajectory") as Array;
                if (points == null) continue;
                FieldInfo posF = null, timeF = null;
                int count = PointCount(patch, points);
                for (int i = 0; i < count; ++i)
                {
                    object pt = points.GetValue(i);
                    if (pt == null) continue;
                    if (posF == null) { posF = pt.GetType().GetField("pos", All); timeF = pt.GetType().GetField("time", All); }
                    var pos = (Vector3d)posF.GetValue(pt);
                    double t = (double)timeF.GetValue(pt);
                    if (t <= 0 || pos.sqrMagnitude < 1) continue;   // unused tail of a chunk
                    var rotated = (Vector3d)rotate.Invoke(null, new object[] { body, pos, t });
                    body.GetLatLonAlt(rotated + body.position, out double lat, out double lon, out double alt);
                    res.Add(new PathPoint { Lat = lat, Lon = lon, AltM = pos.magnitude - body.Radius, UT = t });
                }
            }
            return res;
        }

        /// <summary>
        /// Where the predicted path first descends through an altitude, interpolated between the two path points
        /// either side of it. False when the path never gets that low (or there is none).
        /// </summary>
        public static bool PredictedCrossing(List<PathPoint> path, double altitudeM, out PathPoint at)
        {
            at = default(PathPoint);
            for (int i = 1; i < path.Count; i++)
            {
                PathPoint a = path[i - 1], b = path[i];
                if (a.AltM >= altitudeM && b.AltM < altitudeM)
                {
                    double f = (a.AltM - altitudeM) / Math.Max(1e-9, a.AltM - b.AltM);
                    double dLon = b.Lon - a.Lon;
                    if (dLon > 180) dLon -= 360; else if (dLon < -180) dLon += 360;
                    at = new PathPoint { Lat = a.Lat + (b.Lat - a.Lat) * f, Lon = a.Lon + dLon * f, AltM = altitudeM, UT = a.UT + (b.UT - a.UT) * f };
                    return true;
                }
            }
            return false;
        }

        /// <summary>Trajectory.ComputationTime: smoothed milliseconds of prediction work per frame.</summary>
        public static double ComputationTimeMs
        {
            get
            {
                Resolve();
                if (_trajectory == null) return double.NaN;
                try { return Convert.ToDouble(Reflect.GetStatic(_trajectory, "ComputationTime")); }
                catch (Exception) { return double.NaN; }
            }
        }

        /// <summary>
        /// StockModel.UsePositionDensity on builds that have it (the restored stock density correction); null on
        /// builds without it. Setting it on such a build does nothing.
        /// </summary>
        public static bool? StockPositionDensity
        {
            get
            {
                var t = TryFindType("Trajectories.StockModel");
                var p = t?.GetProperty("UsePositionDensity", All);
                return p == null ? (bool?)null : (bool)p.GetValue(null, null);
            }
            set
            {
                var t = TryFindType("Trajectories.StockModel");
                var p = t?.GetProperty("UsePositionDensity", All);
                if (p != null && value.HasValue) p.SetValue(null, value.Value, null);
            }
        }

        /// <summary>
        /// Trajectories' two stock density estimates at the vessel: StockAeroUtil.GetDensity(altitude) - the
        /// altitude-only atmosphere its stock model computes forces with - and GetDensity(position), which adds
        /// latitude and sun angle the way the game does. NaN when not available.
        /// </summary>
        /// <summary>
        /// The game's air density at a world position by Trajectories' StockAeroUtil (FlightIntegrator's latitude and
        /// sun terms, within ~0.1% of vessel.atmDensity); NaN without Trajectories.
        /// </summary>
        public static double StockDensityAt(CelestialBody body, Vector3d worldPos)
        {
            var t = TryFindType("Trajectories.StockAeroUtil");
            var m = t?.GetMethod("GetDensity", All, null, new[] { typeof(Vector3d), typeof(CelestialBody) }, null);
            if (m == null) return double.NaN;
            try { return (double)m.Invoke(null, new object[] { worldPos, body }); }
            catch (Exception) { return double.NaN; }
        }

        public static void StockDensities(Vessel v, out double altitudeOnly, out double positional)
        {
            altitudeOnly = positional = double.NaN;
            if (v == null || v.mainBody == null) return;
            var t = TryFindType("Trajectories.StockAeroUtil");
            if (t == null) return;
            try
            {
                var byAlt = t.GetMethod("GetDensity", All, null, new[] { typeof(double), typeof(CelestialBody) }, null);
                var byPos = t.GetMethod("GetDensity", All, null, new[] { typeof(Vector3d), typeof(CelestialBody) }, null);
                if (byAlt != null) altitudeOnly = (double)byAlt.Invoke(null, new object[] { v.altitude, v.mainBody });
                if (byPos != null) positional = (double)byPos.Invoke(null, new object[] { v.GetWorldPos3D(), v.mainBody });
            }
            catch (Exception) { }
        }

        /// <summary>AeroDynamicModel.TrimControlSurfaces on builds that have it; null otherwise.</summary>
        public static bool? TrimControlSurfaces
        {
            get
            {
                var p = TryFindType("Trajectories.AeroDynamicModel")?.GetProperty("TrimControlSurfaces", All);
                return p == null ? (bool?)null : (bool)p.GetValue(null, null);
            }
            set
            {
                var p = TryFindType("Trajectories.AeroDynamicModel")?.GetProperty("TrimControlSurfaces", All);
                if (p != null && value.HasValue) p.SetValue(null, value.Value, null);
            }
        }

        /// <summary>The pitch input the model's trim solve gave for the direct evaluation of the last SampleForces
        /// call - its prediction of the input the vessel needs right now (NaN without trim).</summary>
        public static double LastSampleTrimInput { get; private set; } = double.NaN;

        /// <summary>A static property of a Trajectories type as a double (NaN when missing).</summary>
        public static double StaticDouble(string type, string property)
        {
            try
            {
                var p = TryFindType(type)?.GetProperty(property, All);
                return p == null ? double.NaN : Convert.ToDouble(p.GetValue(null, null));
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>Sets a static double property of a Trajectories type; false when missing.</summary>
        public static bool SetStaticDouble(string type, string property, double value)
        {
            try
            {
                var p = TryFindType(type)?.GetProperty(property, All);
                if (p == null) return false;
                p.SetValue(null, value, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Sets a static int property of a Trajectories type; false when missing.</summary>
        public static bool SetStaticInt(string type, string property, int value)
        {
            try
            {
                var p = TryFindType(type)?.GetProperty(property, All);
                if (p == null) return false;
                p.SetValue(null, value, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Sets a static string property of a Trajectories type; false when missing.</summary>
        public static bool SetStaticString(string type, string property, string value)
        {
            try
            {
                var p = TryFindType(type)?.GetProperty(property, All);
                if (p == null) return false;
                p.SetValue(null, value, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Sets a static bool property of a Trajectories type; false when missing.</summary>
        public static bool SetStaticBool(string type, string property, bool value)
        {
            try
            {
                var p = TryFindType(type)?.GetProperty(property, All);
                if (p == null) return false;
                p.SetValue(null, value, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A static string property of a Trajectories type (null when missing).</summary>
        /// <summary>A static bool property's value, or null when the type or property is not there.</summary>
        public static bool? StaticBool(string type, string property)
        {
            try { return TryFindType(type)?.GetProperty(property, All)?.GetValue(null, null) as bool?; }
            catch (Exception) { return null; }
        }

        public static string StaticString(string type, string property)
        {
            try { return TryFindType(type)?.GetProperty(property, All)?.GetValue(null, null) as string; }
            catch (Exception) { return null; }
        }

        private static bool CallGui(string method, params object[] args)
        {
            var m = TryFindType("Trajectories.MainGUI")?.GetMethod(method, All);
            if (m == null) return false;
            m.Invoke(null, args);
            return true;
        }

        /// <summary>Trajectory.PredictedAirspeedAt: predicted airspeed (m/s) descending through an altitude; NaN on builds without it.</summary>
        public static double PredictedAirspeedAt(double altitude)
        {
            var m = TryFindType("Trajectories.Trajectory")?.GetMethod("PredictedAirspeedAt", All);
            try { return m == null ? double.NaN : (double)m.Invoke(null, new object[] { altitude }); }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>TargetProfile.SetFromLatLonAlt: target a latitude/longitude on a body at its surface height.</summary>
        public static bool SetTargetLatLon(CelestialBody body, double lat, double lon)
        {
            var m = TryFindType("Trajectories.TargetProfile")?.GetMethod("SetFromLatLonAlt", All);
            if (m == null) return false;
            m.Invoke(null, new object[] { body, lat, lon, null });
            return true;
        }

        /// <summary>
        /// Runs Trajectory.ComputeTrajectory to completion within this frame, twice (the first finishes whatever was in
        /// progress, the second starts from the vessel as it is now), so a prediction can be taken at a frozen state.
        /// False when it did not complete.
        /// </summary>
        public static bool PredictNow()
        {
            var m = TryFindType("Trajectories.Trajectory")?.GetMethod("ComputeTrajectory", All);
            if (m == null) return false;
            for (int round = 0; round < 2; ++round)
            {
                int start = CompletedPredictions;
                for (int i = 0; i < 20000 && CompletedPredictions == start; ++i)
                    m.Invoke(null, null);
                if (CompletedPredictions == start) return false;
            }
            return true;
        }

        /// <summary>
        /// Writes the last prediction's patches as CSV: one "patch" row per patch (type, start UT, end UT, start
        /// altitude and speed), then for atmospheric patches every stored point (UT, altitude, airspeed, inertial speed,
        /// flight path angle, ground altitude).
        /// </summary>
        // How many of a patch's points are its path: AtmosphericPointCount where the build has it (the array is reused
        // and may be longer), else the whole array.
        private static int PointCount(object patch, Array points)
        {
            var p = patch.GetType().GetProperty("AtmosphericPointCount", All);
            return p == null ? points.Length : Math.Min(points.Length, (int)p.GetValue(patch, null));
        }

        public static bool DumpPrediction(string file)
        {
            if (!(PatchesRef is System.Collections.IList list) || list.Count == 0) return false;
            var v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("kind,patch,ut,altKm,airspeed,inertialSpeed,fpaDeg,groundAltM\n");
            for (int pi = 0; pi < list.Count; ++pi)
            {
                object p = list[pi];
                Type pt = p.GetType();
                bool atmo = (bool)pt.GetProperty("IsAtmospheric", All).GetValue(p, null);
                double end = Convert.ToDouble(pt.GetProperty("EndTime", All).GetValue(p, null));
                object st = pt.GetProperty("StartingState", All).GetValue(p, null);
                Type stt = st.GetType();
                double t0 = Convert.ToDouble(stt.GetProperty("Time", All).GetValue(st, null));
                var pos0 = (Vector3d)stt.GetProperty("Position", All).GetValue(st, null);
                var vel0 = (Vector3d)stt.GetProperty("Velocity", All).GetValue(st, null);
                sb.Append(atmo ? "atmoPatch," : "spacePatch,").Append(pi).Append(",").Append(t0.ToString("F2", inv)).Append(",")
                  .Append(((pos0.magnitude - body.Radius) / 1000).ToString("F3", inv)).Append(",")
                  .Append((vel0 - body.getRFrmVel(body.position + pos0)).magnitude.ToString("F2", inv)).Append(",")
                  .Append(vel0.magnitude.ToString("F2", inv)).Append(",").Append(Fpa(pos0, vel0).ToString("F4", inv)).Append(",end ")
                  .Append(end.ToString("F2", inv)).Append("\n");
                if (!atmo) continue;
                var pts = pt.GetProperty("AtmosphericTrajectory", All).GetValue(p, null) as Array;
                if (pts == null) continue;
                int count = PointCount(p, pts);
                for (int i = 0; i < count; ++i)
                {
                    object q = pts.GetValue(i);
                    Type qt = q.GetType();
                    var pos = (Vector3d)qt.GetField("pos", All).GetValue(q);
                    var vel = (Vector3d)qt.GetField("orbitalVelocity", All).GetValue(q);
                    double t = Convert.ToDouble(qt.GetField("time", All).GetValue(q));
                    float ga = (float)qt.GetField("groundAltitude", All).GetValue(q);
                    sb.Append("pt,").Append(pi).Append(",").Append(t.ToString("F2", inv)).Append(",")
                      .Append(((pos.magnitude - body.Radius) / 1000).ToString("F3", inv)).Append(",")
                      .Append((vel - body.getRFrmVel(body.position + pos)).magnitude.ToString("F2", inv)).Append(",")
                      .Append(vel.magnitude.ToString("F2", inv)).Append(",").Append(Fpa(pos, vel).ToString("F4", inv)).Append(",")
                      .Append(ga.ToString("F0", inv)).Append("\n");
                }
            }
            System.IO.File.WriteAllText(file, sb.ToString());
            return true;
        }

        private static double Fpa(Vector3d pos, Vector3d vel) =>
            90.0 - Vector3d.Angle(pos, vel);

        /// <summary>Whether the first patch of the last prediction is atmospheric, and how many patches it has.</summary>
        public static void FirstPatch(out bool atmospheric, out int count)
        {
            atmospheric = false;
            count = 0;
            if (!(PatchesRef is System.Collections.IList list) || list.Count == 0) return;
            count = list.Count;
            var p = list[0].GetType().GetProperty("IsAtmospheric", All);
            atmospheric = p != null && (bool)p.GetValue(list[0], null);
        }

        /// <summary>
        /// The model's ComputeForcesAndMoment_Model at a fixed pitch input (the surfaces set to their flap position plus
        /// that pitch deflection, no trim solve): force (kN, world) and pitching moment (kN m) at an AoA, speed and
        /// altitude in the vessel's frame, as ComputeForces builds it.
        /// </summary>
        public static bool ForceAtPitchInput(Vessel v, double aoaDeg, double speed, double altitude, double pitchInput,
                                             out Vector3d force, out double moment)
        {
            force = Vector3d.zero;
            moment = double.NaN;
            Resolve();
            if (_trajectory == null || v == null || v.ReferenceTransform == null) return false;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return false; }
            var m = model?.GetType().GetMethod("ComputeForcesAndMoment_Model", All);
            if (m == null) return false;
            Transform t = v.ReferenceTransform;
            Vector3d forward = t.up, up = -t.forward;
            Vector3d right = Vector3d.Cross(up, -forward).normalized;
            double a = aoaDeg * Math.PI / 180;
            Vector3d airVel = (forward * Math.Cos(-a) + up * Math.Sin(-a)) * speed;
            object[] args = { airVel, altitude, a, pitchInput, right, 0.0 };
            try { force = (Vector3d)m.Invoke(model, args); moment = (double)args[5]; return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Trajectory.InvalidateAerodynamicModel: the model and its force cache are rebuilt on the next prediction.</summary>
        public static bool InvalidateAerodynamicModel()
        {
            var m = TryFindType("Trajectories.Trajectory")?.GetMethod("InvalidateAerodynamicModel", All);
            if (m == null) return false;
            m.Invoke(null, null);
            return true;
        }

        /// <summary>
        /// Predictions Trajectories has completed and published (Trajectory.CompletedCount). Builds without the counter
        /// (upstream 2.4.5.4) are counted from here on by the published patch list changing (PatchesRef), which they
        /// swap in whole when a prediction completes.
        /// </summary>
        public static int CompletedPredictions
        {
            get
            {
                double c = StaticDouble("Trajectories.Trajectory", "CompletedCount");
                if (!double.IsNaN(c))
                    return (int)c;
                if (_swapCounter == null)
                {
                    _swapCounter = new GameObject("KspTest.PatchSwapCounter");
                    UnityEngine.Object.DontDestroyOnLoad(_swapCounter);
                    _swapCounter.AddComponent<PatchSwapCounter>();
                }
                return _swaps;
            }
        }

        private static GameObject _swapCounter;
        private static int _swaps;
        private static object _lastPatches;

        private sealed class PatchSwapCounter : MonoBehaviour
        {
            private void Update()
            {
                object p = PatchesRef;
                if (p != null && !ReferenceEquals(p, _lastPatches))
                {
                    _lastPatches = p;
                    ++_swaps;
                }
            }
        }

        /// <summary>The Target page's "current impact" button: target where the last prediction lands.</summary>
        public static bool SetTargetFromImpact() => CallGui("OnButtonClick_TargetImpact");

        /// <summary>The Descent page's "Adjust AoA to stay on target" checkbox, through its GUI handler.</summary>
        public static bool SetSteerToTarget(bool on) => CallGui("OnButtonClick_SteerToTarget", on);

        /// <summary>DescentProfile.SteerToTarget; null on builds without it.</summary>
        public static bool? SteerToTarget
        {
            get
            {
                var p = TryFindType("Trajectories.DescentProfile")?.GetProperty("SteerToTarget", All);
                return p == null ? (bool?)null : (bool)p.GetValue(null, null);
            }
        }

        /// <summary>The target as latitude, longitude, altitude, or null without one.</summary>
        public static Vector3d? TargetLatLonAlt
        {
            get
            {
                var m = TryFindType("Trajectories.TargetProfile")?.GetMethod("GetLatLonAlt", All);
                try { return m?.Invoke(null, null) as Vector3d?; }
                catch (Exception) { return null; }
            }
        }

        /// <summary>A property of the aerodynamic model as a double (NaN when missing).</summary>
        /// <summary>
        /// Sets the current model's non-aerodynamic pitch torque (kN m) as its own learning would (UseOtherTorque, which
        /// also marks the force cache stale). False when the build or the model has none. Learning or seeding later
        /// overwrites it.
        /// </summary>
        public static bool ForceOtherTorque(double torqueKNm)
        {
            Resolve();
            object model = null;
            try { model = _trajectory == null ? null : Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { }
            // private on the base AeroDynamicModel: walk up from the model's own type
            MethodInfo m = null;
            for (Type t = model?.GetType(); t != null && m == null; t = t.BaseType)
                m = t.GetMethod("UseOtherTorque", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (m == null) return false;
            m.Invoke(model, new object[] { torqueKNm });
            return true;
        }

        public static double ModelDouble(string property)
        {
            Resolve();
            try
            {
                object model = _trajectory == null ? null : Reflect.GetStatic(_trajectory, "aerodynamicModel_");
                var p = model?.GetType().GetProperty(property, All);
                return p == null ? double.NaN : Convert.ToDouble(p.GetValue(model, null));
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>A bool property of the aerodynamic model (null when missing).</summary>
        public static bool? ModelBool(string property)
        {
            Resolve();
            try
            {
                object model = _trajectory == null ? null : Reflect.GetStatic(_trajectory, "aerodynamicModel_");
                var p = model?.GetType().GetProperty(property, All);
                return p == null ? (bool?)null : (bool)p.GetValue(model, null);
            }
            catch (Exception) { return null; }
        }

        /// <summary>The model's seeding of the non-aerodynamic pitch torque from ITorqueProvider (null on builds without it).</summary>
        public static bool? SeedOtherTorque
        {
            get
            {
                Resolve();
                var p = TryFindType("Trajectories.AeroDynamicModel")?.GetProperty("SeedOtherTorque", All | BindingFlags.Static);
                return p == null ? (bool?)null : (bool)p.GetValue(null, null);
            }
            set
            {
                Resolve();
                var p = TryFindType("Trajectories.AeroDynamicModel")?.GetProperty("SeedOtherTorque", All | BindingFlags.Static);
                if (p != null && value.HasValue) p.SetValue(null, value.Value, null);
            }
        }

        /// <summary>The model's learned non-aerodynamic pitch torque, kN m (NaN before it has one or without trim).</summary>
        public static double OtherPitchTorque
        {
            get
            {
                Resolve();
                try
                {
                    object model = _trajectory == null ? null : Reflect.GetStatic(_trajectory, "aerodynamicModel_");
                    var p = model?.GetType().GetProperty("OtherPitchTorque", All);
                    return p == null ? double.NaN : Convert.ToDouble(p.GetValue(model, null));
                }
                catch (Exception) { return double.NaN; }
            }
        }

        /// <summary>The model's DensityScale at the vessel (1 on builds or models without one).</summary>
        public static double DensityScaleAt(Vessel v)
        {
            Resolve();
            if (_trajectory == null || v == null) return 1.0;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return 1.0; }
            var m = model?.GetType().GetMethod("DensityScale", All);
            if (m == null) return 1.0;
            try
            {
                // The CoM for both, as v.altitude is: GetWorldPos3D is the root part, metres off it (the Mk2
                // spaceplane's read 0.18 % denser than its altitude's air).
                Vector3d bodySpace = v.CoMD - v.mainBody.position;
                return (double)m.Invoke(model, new object[] { v.mainBody, bodySpace, bodySpace.magnitude - v.mainBody.Radius });
            }
            catch (Exception) { return 1.0; }
        }

        /// <summary>Settings.IntegrationStepSize, seconds per prediction step.</summary>
        public static double IntegrationStepSize
        {
            get { Resolve(); return _settings == null ? double.NaN : (double)Reflect.GetStatic(_settings, "IntegrationStepSize"); }
            set { Resolve(); if (_settings != null) Reflect.SetStatic(_settings, "IntegrationStepSize", value); }
        }

        /// <summary>Settings.MaxFramesPerPatch, how many frames one patch's computation is spread over.</summary>
        public static int MaxFramesPerPatch
        {
            get { Resolve(); return _settings == null ? -1 : (int)Reflect.GetStatic(_settings, "MaxFramesPerPatch"); }
        }

        /// <summary>
        /// Sets all four descent profile nodes to the same angle of attack in AoA mode (not Horizon), so the
        /// altitude blending between nodes (DescentProfile.GetAngleOfAttack) is a no-op and the prediction
        /// follows one constant angle - what a test that holds one angle through the entry needs.
        /// </summary>
        public static void ResetProfile(double aoaDeg)
        {
            Resolve();
            if (_api == null) return;
            Reflect.CallStatic(_api, "ResetDescentProfile", aoaDeg * Math.PI / 180.0);
        }

        /// <summary>Sets the four node angles (entry, high, low, final; degrees), all in AoA mode.</summary>
        public static void SetProfileDeg(double entry, double high, double low, double final) =>
            SetProfileDeg(entry, high, low, final, new[] { false, false, false, false });

        /// <summary>The four node angles (deg); horizon[i] makes node i relative to the horizon instead of an AoA.</summary>
        public static void SetProfileDeg(double entry, double high, double low, double final, bool[] horizon)
        {
            Resolve();
            if (_api == null) return;
            double d2r = Math.PI / 180.0;
            // Trajectories' API takes "is AoA" flags: false is relative to the horizon.
            Reflect.SetStatic(_api, "DescentProfileModes", new List<bool> { !horizon[0], !horizon[1], !horizon[2], !horizon[3] });
            Reflect.SetStatic(_api, "DescentProfileAngles", new List<double> { entry * d2r, high * d2r, low * d2r, final * d2r });
        }

        /// <summary>
        /// Speed mode on the nodes with an airspeed (m/s; NaN leaves a node as SetProfileDeg set it): each holds its
        /// airspeed by pitch (DescentProfile.Node.Speed). False on builds without speed mode.
        /// </summary>
        public static bool SetProfileSpeeds(double[] speedMs)
        {
            var t = TryFindType("Trajectories.DescentProfile");
            if (t == null) return false;
            string[] nodes = { "AtmosEntry", "HighAltitude", "LowAltitude", "FinalApproach" };
            for (int i = 0; i < 4; ++i)
            {
                if (double.IsNaN(speedMs[i]))
                    continue;
                object node = t.GetProperty(nodes[i], All)?.GetValue(null, null);
                var speedProp = node?.GetType().GetProperty("Speed", All);
                var msProp = node?.GetType().GetProperty("SpeedMs", All);
                if (speedProp == null || msProp == null)
                    return false;
                msProp.SetValue(node, speedMs[i], null);
                speedProp.SetValue(node, true, null);
            }
            return true;
        }

        /// <summary>DescentProfile.HoldWithSmartASS - Trajectories flying its own profile through SmartASS - on builds
        /// that have it; null otherwise.</summary>
        public static bool? HoldWithSmartASS
        {
            get
            {
                var p = TryFindType("Trajectories.DescentProfile")?.GetProperty("HoldWithSmartASS", All);
                return p == null ? (bool?)null : (bool)p.GetValue(null, null);
            }
            set
            {
                var p = TryFindType("Trajectories.DescentProfile")?.GetProperty("HoldWithSmartASS", All);
                if (p != null && value.HasValue) p.SetValue(null, value.Value, null);
            }
        }

        /// <summary>The angle of attack (deg) the descent profile gives at the vessel's altitude; NaN without one.</summary>
        public static double ProfileAoADeg(Vessel v)
        {
            if (v == null || v.mainBody == null) return double.NaN;
            var t = TryFindType("Trajectories.DescentProfile");
            // (body, position, velocity), with an optional AoA bias on later builds (null = the steering's own)
            var m = t?.GetMethod("GetAngleOfAttack", All, null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(Vector3d) }, null)
                ?? t?.GetMethod("GetAngleOfAttack", All, null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(Vector3d), typeof(double?) }, null);
            if (m == null) return double.NaN;
            try
            {
                object[] args = m.GetParameters().Length == 4
                    ? new object[] { v.mainBody, v.GetWorldPos3D() - v.mainBody.position, (Vector3d)v.srf_velocity, null }
                    : new object[] { v.mainBody, v.GetWorldPos3D() - v.mainBody.position, (Vector3d)v.srf_velocity };
                var r = m.Invoke(null, args) as double?;
                return r.HasValue ? r.Value * 180.0 / Math.PI : double.NaN;
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>The four node angles in degrees (entry, high, low, final), to prove what the profile holds.</summary>
        public static double[] ProfileAnglesDeg
        {
            get
            {
                Resolve();
                if (_api == null) return new double[0];
                var list = Reflect.GetStatic(_api, "DescentProfileAngles") as List<double>;
                if (list == null) return new double[0];
                var res = new double[list.Count];
                for (int i = 0; i < list.Count; i++) res[i] = list[i] * 180.0 / Math.PI;
                return res;
            }
        }

        /// <summary>Per node, true = the angle is an AoA, false = it is measured from the horizon.</summary>
        public static bool[] ProfileModesAoA
        {
            get
            {
                Resolve();
                if (_api == null) return new bool[0];
                var list = Reflect.GetStatic(_api, "DescentProfileModes") as List<bool>;
                return list == null ? new bool[0] : list.ToArray();
            }
        }

        /// <summary>Seconds until the predicted impact, or NaN when there is no prediction.</summary>
        public static double TimeTillImpact
        {
            get
            {
                Resolve();
                if (_api == null) return double.NaN;
                var t = Reflect.CallStatic(_api, "GetTimeTillImpact") as double?;
                return t ?? double.NaN;
            }
        }

        /// <summary>
        /// The predicted impact point as latitude/longitude on the vessel's body, converted the way
        /// Trajectories' own GUI does it (body-relative position + body position -> GetLatLonAlt).
        /// False when there is no prediction yet.
        /// </summary>
        public static bool ImpactLatLon(out double lat, out double lon, out double alt)
        {
            lat = lon = alt = double.NaN;
            Resolve();
            if (_api == null) return false;
            var p = Reflect.CallStatic(_api, "GetImpactPosition") as Vector3?;
            if (p == null) return false;
            var v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return false;
            v.mainBody.GetLatLonAlt((Vector3d)p.Value + v.mainBody.position, out lat, out lon, out alt);
            return true;
        }

        /// <summary>
        /// The aerodynamic force Trajectories would use at the vessel's current state, both ways: through the
        /// cache (GetForces, what the prediction actually integrates) and straight from the model
        /// (ComputeForces, which calls FAR for this exact state). Their difference is the cache's interpolation
        /// error at a state the vehicle really flew. Forces are in the body-relative world frame, kN.
        /// False without Trajectories or before it has built a model for the vessel.
        /// </summary>
        public static bool SampleForces(Vessel v, double aoaDeg, out Vector3d cached, out Vector3d direct)
        {
            cached = direct = Vector3d.zero;
            Resolve();
            if (_trajectory == null || _computeForces == null || _getForces == null) return false;
            if (v == null || v.mainBody == null) return false;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return false; }
            if (model == null) return false;

            CelestialBody body = v.mainBody;
            // the CoM, where v.altitude is taken (GetWorldPos3D is the root part, metres off it)
            Vector3d bodySpacePosition = v.CoMD - body.position;
            Vector3d airVelocity = v.srf_velocity;
            Vector3d vup = (v.CoMD - body.position).normalized;
            double aoaRad = aoaDeg * Math.PI / 180.0;
            try
            {
                // GetForces applies the model's DensityScale (the stock position correction) on top of what
                // ComputeForces returns, uncached as well as cached; scale the direct side the same way so the
                // comparison stays one of cache against no cache.
                direct = (Vector3d)_computeForces.Invoke(model, new object[] { v.altitude, airVelocity, vup, aoaRad }) * DensityScaleAt(v);
                // Read now: the cached call below can fill cells and move LastTrimInput on.
                var trimProp = model.GetType().GetProperty("LastTrimInput", All);
                LastSampleTrimInput = trimProp == null ? double.NaN : Convert.ToDouble(trimProp.GetValue(model, null));
                cached = (Vector3d)_getForces.Invoke(model, new object[] { body, bodySpacePosition, airVelocity, aoaRad });
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("Trajectories force sample failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The same two force paths as SampleForces, but at an arbitrary point of the cache's (speed, AoA,
        /// altitude) space rather than the state the vessel happens to be in. Both calls take the velocity and
        /// position as arguments, so a cell can be probed without flying to it: the position is built along the
        /// vessel's current up at the requested altitude and the velocity along its current airspeed direction.
        /// The vessel's orientation still supplies the frame ComputeForces builds its fixed-AoA velocity in, so
        /// the ship should be holding a steady attitude while a sweep runs.
        /// </summary>
        public static bool SampleForcesAt(Vessel v, double aoaDeg, double speedMS, double altitudeM,
                                          out Vector3d cached, out Vector3d direct)
        {
            cached = direct = Vector3d.zero;
            Resolve();
            if (_trajectory == null || _computeForces == null || _getForces == null) return false;
            if (v == null || v.mainBody == null) return false;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return false; }
            if (model == null) return false;

            CelestialBody body = v.mainBody;
            if (altitudeM >= body.atmosphereDepth) return false;   // both paths return zero above it
            Vector3d up = (v.CoMD - body.position).normalized;
            Vector3d bodySpacePosition = up * (body.Radius + altitudeM);
            Vector3d airVelocity = ((Vector3d)v.srf_velocity).normalized * speedMS;
            double aoaRad = aoaDeg * Math.PI / 180.0;
            try
            {
                direct = (Vector3d)_computeForces.Invoke(model, new object[] { altitudeM, airVelocity, up, aoaRad });
                cached = (Vector3d)_getForces.Invoke(model, new object[] { body, bodySpacePosition, airVelocity, aoaRad });
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("Trajectories force sample failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Drag (x, along the airflow) and lift (y) in the cache's own synthetic frame.</summary>
        public struct XY { public double X, Y; public XY(double x, double y) { X = x; Y = y; } }

        /// <summary>One point of the cache's error, split into its parts. All in the synthetic frame the cache
        /// fills in (velocity along +x, up along +y), so no frame reconstruction is involved.</summary>
        public sealed class Decomposition
        {
            public double Mach, SoundSpeed;
            public XY Truth, Cached, Fresh, AoAOnly, MachOnly, AltOnly, RoundTrip;
            public int MachNode, AoANode, AltNode;
            public double MachFrac, AoAFrac, AltFrac;
            public string Geometry;
        }

        /// <summary>
        /// Splits the cache's error at one state. Needs the reworked cache (Mach axis, SpeedOfSoundAt); returns
        /// null on the stock build. Fresh corners are computed now, so Cached - Fresh is staleness and
        /// Fresh - Truth is interpolation; the single-axis blends say which axis the interpolation error is on.
        /// </summary>
        public static Decomposition Decompose(Vessel v, double aoaDeg, double speedMS, double altitudeM)
        {
            Resolve();
            if (_trajectory == null || v == null) return null;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return null; }
            if (model == null) return null;
            Type mt = model.GetType();
            var compute = mt.GetMethod("ComputeForces", All);
            var pack = mt.GetMethod("PackForces", All);
            var unpack = mt.GetMethod("UnpackForces", All);
            var sound = mt.GetMethod("SpeedOfSoundAt", All);
            object cache = null;
            for (Type t = mt; t != null && cache == null; t = t.BaseType)
            {
                var f = t.GetField("cachedForces", All);
                if (f != null) cache = f.GetValue(model);
            }
            if (compute == null || pack == null || unpack == null || sound == null || cache == null) return null;
            Type ct = cache.GetType();
            var getForce = ct.GetMethod("GetForce", All);
            double maxMach = (double)Reflect.Get(cache, "MaxMach"), maxAoA = (double)Reflect.Get(cache, "MaxAoA"), maxAlt = (double)Reflect.Get(cache, "MaxAltitude");
            int nM = (int)Reflect.Get(cache, "MachResolution"), nA = (int)Reflect.Get(cache, "AoAResolution"), nH = (int)Reflect.Get(cache, "AltitudeResolution");

            Vector3d up = new Vector3d(0, 1, 0);
            Func<double, double> a = h => (double)sound.Invoke(model, new object[] { h });
            Func<double, double, double, Vector3d> force = (h, vel, aoaRad) =>
                (Vector3d)compute.Invoke(model, new object[] { h, new Vector3d(vel, 0, 0), up, aoaRad });
            // The coefficient a cache node stores: the force at that Mach/AoA/altitude over rho*v^2.
            Func<double, double, double, Vector2d> coeff = (mach, aoaRad, h) =>
            {
                double vel = mach * a(h);
                return (Vector2d)pack.Invoke(model, new object[] { force(h, vel, aoaRad), h, vel });
            };
            Func<Vector2d, XY> toXY = c => { var f = (Vector3d)unpack.Invoke(model, new object[] { c, altitudeM, speedMS }); return new XY(f.x, f.y); };

            double aoaRad0 = aoaDeg * Math.PI / 180.0;
            double aq = a(altitudeM), mq = speedMS / aq;
            var d = new Decomposition { Mach = mq, SoundSpeed = aq,
                Geometry = $"Mach 0-{maxMach} x{nM}, AoA +-{maxAoA * 180 / Math.PI:F0} x{nA}, alt 0-{maxAlt:F0} x{nH}" };

            var tf = force(altitudeM, speedMS, aoaRad0);
            d.Truth = new XY(tf.x, tf.y);
            var cf = (Vector3d)getForce.Invoke(cache, new object[] { mq, speedMS, aoaRad0, altitudeM });
            d.Cached = new XY(cf.x, cf.y);
            d.RoundTrip = toXY(coeff(mq, aoaRad0, altitudeM));

            // Same index arithmetic as AeroForceCache.GetForce.
            Func<double, int, (int, double)> idx = (frac, n) =>
            {
                int fl = Math.Max(0, Math.Min(n - 2, (int)frac));
                return (fl, Math.Max(0.0, Math.Min(1.0, frac - fl)));
            };
            (int mi, double mf) = idx(mq / maxMach * (nM - 1), nM);
            (int ai, double af) = idx((aoaRad0 / maxAoA * 0.5 + 0.5) * (nA - 1), nA);
            (int hi, double hf) = idx(altitudeM / maxAlt * (nH - 1), nH);
            d.MachNode = mi; d.MachFrac = mf; d.AoANode = ai; d.AoAFrac = af; d.AltNode = hi; d.AltFrac = hf;
            Func<int, double> machAt = i => maxMach * i / (nM - 1);
            Func<int, double> aoaAt = i => maxAoA * ((double)i / (nA - 1) * 2.0 - 1.0);
            Func<int, double> altAt = i => maxAlt * i / (nH - 1);

            Vector2d fresh = Vector2d.zero;
            for (int di = 0; di < 2; di++)
                for (int dj = 0; dj < 2; dj++)
                    for (int dk = 0; dk < 2; dk++)
                    {
                        double w = (di == 0 ? 1 - mf : mf) * (dj == 0 ? 1 - af : af) * (dk == 0 ? 1 - hf : hf);
                        if (w == 0) continue;
                        fresh += coeff(machAt(mi + di), aoaAt(ai + dj), altAt(hi + dk)) * w;
                    }
            d.Fresh = toXY(fresh);
            d.AoAOnly = toXY(coeff(mq, aoaAt(ai), altitudeM) * (1 - af) + coeff(mq, aoaAt(ai + 1), altitudeM) * af);
            d.MachOnly = toXY(coeff(machAt(mi), aoaRad0, altitudeM) * (1 - mf) + coeff(machAt(mi + 1), aoaRad0, altitudeM) * mf);
            d.AltOnly = toXY(coeff(mq, aoaRad0, altAt(hi)) * (1 - hf) + coeff(mq, aoaRad0, altAt(hi + 1)) * hf);
            return d;
        }

        /// <summary>
        /// One AeroDynamicModel.ComputeForces at an arbitrary point, with no cache call alongside it, so a
        /// sequence of these exercises only the model. Used to test whether the model is a pure function of
        /// (speed, AoA, altitude) or carries state from one call to the next.
        /// </summary>
        public static bool ComputeDirectAt(Vessel v, double aoaDeg, double speedMS, double altitudeM, out Vector3d force)
        {
            force = Vector3d.zero;
            Resolve();
            if (_trajectory == null || _computeForces == null) return false;
            if (v == null || v.mainBody == null) return false;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return false; }
            if (model == null || altitudeM >= v.mainBody.atmosphereDepth) return false;

            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            Vector3d airVelocity = ((Vector3d)v.srf_velocity).normalized * speedMS;
            try
            {
                force = (Vector3d)_computeForces.Invoke(model, new object[] { altitudeM, airVelocity, up, aoaDeg * Math.PI / 180.0 });
                return true;
            }
            catch (Exception ex) { Log.Info("Trajectories direct compute failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Microseconds per uncached force evaluation - one AeroDynamicModel.ComputeForces, which is one
        /// FARAPI.CalculateVesselAeroForces through a reflection Invoke. This is the call the predictor makes
        /// four times per RK4 step, so it sets the floor on an uncached prediction's cost.
        /// Returns NaN if the model is not available.
        /// </summary>
        public static double TimeDirectCallUs(Vessel v, double aoaDeg, int calls, bool varyState = true)
        {
            Resolve();
            if (_trajectory == null || _computeForces == null) return double.NaN;
            if (v == null || v.mainBody == null) return double.NaN;
            object model;
            try { model = Reflect.GetStatic(_trajectory, "aerodynamicModel_"); }
            catch (Exception) { return double.NaN; }
            if (model == null) return double.NaN;

            CelestialBody body = v.mainBody;
            Vector3d up = (v.CoMD - body.position).normalized;
            Vector3d airVelocity = v.srf_velocity;
            double aoaRad = aoaDeg * Math.PI / 180.0;
            var args = new object[] { v.altitude, airVelocity, up, aoaRad };
            try
            {
                _computeForces.Invoke(model, args);   // once to warm up, outside the timing
                // Every call must be at a different state. FARVesselAero.SimulateAeroProperties short-circuits
                // on lastSimResults when the velocity and altitude match the previous call, so a loop repeating
                // one state times that memo rather than a solve - which is not what the predictor ever does.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < calls; i++)
                {
                    Vector3d vel = varyState ? airVelocity * (1.0 + i * 1e-4) : airVelocity;
                    _computeForces.Invoke(model, new object[] { v.altitude + (varyState ? i * 1.0 : 0.0), vel, up, aoaRad });
                }
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds * 1000.0 / calls;
            }
            catch (Exception ex)
            {
                Log.Info("Trajectories force timing failed: " + ex.Message);
                return double.NaN;
            }
        }
    }
}
