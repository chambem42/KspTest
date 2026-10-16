using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KSPUITest
{
    /// <summary>
    /// The measuring half of the Trajectories accuracy tests, shared by the RP-1 entry (KCT simulation) and the
    /// stock one (set-orbit cheat): with the vessel already holding its angle and on the way down, sample
    /// Trajectories' prediction, the cached and direct aerodynamic force at the flown state, and how often a full
    /// prediction completes, until the entry ends. Writes &lt;prefix&gt;.csv and records under the prefix.
    ///
    /// Two accuracy measures:
    ///  - impact drift: each predicted impact against the last one (the original measure). The last prediction
    ///    is itself a prediction, and it runs on to the ground at the profile's angle whether or not the vehicle
    ///    can hold it down there, so this mixes the predictor's error with the vehicle's.
    ///  - crossing error: where each prediction says the vessel will descend through CrossAltKm, against where it
    ///    actually did. Pick an altitude the angle is still held at and everything compared is flown.
    /// </summary>
    public static class TrajEntry
    {
        public sealed class EntryOptions
        {
            /// <summary>The entry ends below EndAltKm, or below SlowBelowAltKm at under SlowSpeed m/s.</summary>
            public double EndAltKm = 25, SlowBelowAltKm = 90, SlowSpeed = 2000;

            /// <summary>Altitude whose predicted and actual crossing are compared; must be above EndAltKm.</summary>
            public double CrossAltKm = double.NaN;

            /// <summary>A sample counts for the summary statistics while the AoA is within this of the target.</summary>
            public double HeldBandDeg = 2;

            /// <summary>The angle of attack (deg) the vessel should be holding at a moment - the descent profile's for a
            /// varying profile; null means the constant angle passed to Sample.</summary>
            public Func<Vessel, double> TargetAoADeg;

            /// <summary>Fly to the ground: the entry ends at touchdown (below TouchdownAglM above the terrain, landed,
            /// splashed or destroyed), and every predicted impact is compared with where the vessel really came down.</summary>
            public bool ToGround;
            public double TouchdownAglM = 20;

            /// <summary>Altitudes (km) at which the whole predicted path is written to prediction_&lt;km&gt;.csv, and with any,
            /// the flown path every game second to flown_path.csv, in the same columns, to see where the two part.</summary>
            public double[] DumpAtKm;
        }

        /// <summary>Great-circle distance in metres between two lat/lon on a body.</summary>
        public static double GreatCircleM(CelestialBody body, double lat1, double lon1, double lat2, double lon2)
        {
            double d2r = Math.PI / 180.0;
            double p1 = lat1 * d2r, p2 = lat2 * d2r, dp = (lat2 - lat1) * d2r, dl = (lon2 - lon1) * d2r;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return body.Radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
        }

        /// <summary>
        /// All four descent profile nodes at one angle in AoA mode (so the profile's altitude blending is a no-op),
        /// the cache on or off, and AlwaysUpdate on - without it Trajectories only recomputes when its trajectory is
        /// drawn or a target is set, and the API would hand back a stale prediction.
        /// </summary>
        public static void ConfigureProfile(TestContext ctx, string k, bool useCache, double aoa)
        {
            TrajApi.AlwaysUpdate = true;
            TrajApi.UseCache = useCache;
            TrajApi.ResetProfile(aoa);
            ctx.Record(k + "useCache", TrajApi.UseCache);
            ctx.Record(k + "profileAnglesDeg", string.Join("/", Array.ConvertAll(TrajApi.ProfileAnglesDeg, d => Math.Round(d, 1).ToString())));
            ctx.Record(k + "profileModesAoA", string.Join("/", Array.ConvertAll(TrajApi.ProfileModesAoA, b => b ? "AoA" : "Horiz")));
            ctx.Record(k + "integrationStepSize", TrajApi.IntegrationStepSize);
            ctx.Record(k + "maxFramesPerPatch", TrajApi.MaxFramesPerPatch);
        }

        // CSV columns, by index, for the ones filled in after the flight.
        private const int ColImpactFromFinal = 10, ColCrossErr = 20, ColCrossTimeErr = 21;
        private const string Header =
            "missionTime,altitude,srfSpeed,mach,dynPreskPa,aoaActual," +
            "impactLat,impactLon,timeTillImpact,impactMoveM,impactFromFinalM," +
            "cachedDragKN,directDragKN,dragErrPct,cachedLiftKN,directLiftKN,liftErrPct," +
            "predCrossLat,predCrossLon,predCrossUT,crossErrM,crossTimeErrS," +
            "path0ErrM,physWarpRate,ctrlDeflectionDeg,ut," +
            "rhoActual,rhoAltOnly,rhoPositional,densityScale," +
            "trimInputPred,pitchInputActual,otherPitchTorqueKNm," +
            "farApiPitchKNm,trueAeroPitchKNm,farApiLiftKN,trueLiftKN,farApiDragKN,trueDragKN," +
            "aoaTarget,impactVsTouchdownM,farPredictionRho,seedPitchTorqueKNm,torqueLearned," +
            "aoaBiasDeg,alongMissM,crossMissM,bankCmdDeg,bankMeasuredDeg,predAirspeedFinalTop,predImpactAirspeed," +
            "maxFrameMs,fillsEmpty,fillsExpired,fillMs,invalidations,torqueInvalidations,reversalAltKm,peKm,apKm,rcsThrustKN,rhoAtStartLatLon," +
            "unholdableNodes,holdableClamps,sideForceRatio,yawTrimInputPred,yawInputActual,yawTrimSaturations,rollTrimInputPred,rollInputActual,vesselLat,vesselLon," +
            "farLiveMach,farPredMach,farLiveRe,farPredRe,farLiveRho,farPredRho,farPredTempK,farReTempK," +
            "apiWingDragKN,apiBodyDragKN,trueWingDragKN,trueBodyDragKN,extTempLiveK,extTempPredK,stiffSubsteps\n";

        // The RCS thrust firing at this moment, all thrusters summed (kN).
        private static double RcsThrustKN(Vessel v)
        {
            double sum = 0;
            foreach (Part p in v.parts)
                foreach (ModuleRCS m in p.FindModulesImplementing<ModuleRCS>())
                    if (m.thrustForces != null)
                        foreach (float f in m.thrustForces)
                            sum += f;
            return sum;
        }

        // Cumulative cache counters at the previous sample, for per-interval differences.
        private static double lastFillsEmpty, lastFillsExpired, lastFillMs, lastInvalidations, lastTorqueInvalidations;

        private static string Delta(string type, string property, ref double last, string format)
        {
            double now = TrajApi.StaticDouble(type, property);
            double d = now - last;
            last = now;
            return double.IsNaN(d) ? "" : d.ToString(format);
        }
        private const int ColAoATarget = 39, ColImpactVsTouchdown = 40;

        /// <summary>The vessel's bank about its surface velocity (deg, positive right wing down).</summary>
        public static double MeasuredBankDeg(Vessel v)
        {
            if (v == null || v.ReferenceTransform == null || v.srf_velocity.sqrMagnitude < 1) return double.NaN;
            Vector3d position = v.GetWorldPos3D() - v.mainBody.position;
            Vector3d forward = v.srf_velocity.normalized;
            // Unity is left-handed: Cross(up, forward) is right and Cross(forward, right) is up.
            Vector3d right = Vector3d.Cross(position, forward).normalized;
            Vector3d up = Vector3d.Cross(forward, right).normalized;
            Vector3d roof = -(Vector3d)v.ReferenceTransform.forward;
            // Heat shield first the prediction's level attitude is roof down, so there it is the belly's tilt, as
            // Trajectories measures it.
            if (Vector3d.Dot(v.ReferenceTransform.up, forward) < 0.0) roof = -roof;
            return Math.Atan2(Vector3d.Dot(roof, right), Vector3d.Dot(roof, up)) * 180.0 / Math.PI;
        }

        public static IEnumerator Sample(TestContext ctx, string k, double aoa, double sampleSec, EntryOptions opt,
                                         double flightTimeout, int partsStart, double physWarp)
        {
            string tag = k.TrimEnd('.');
            ctx.Record(k + "aeroModel", TrajApi.ModelName);
            ctx.Record(k + "comOffsetParts", FarApi.ComOffsetSummary(FlightGlobals.ActiveVessel));
            double crossAlt = opt.CrossAltKm * 1000;
            bool measureCross = !double.IsNaN(crossAlt);
            if (measureCross) ctx.Record(k + "crossAltKm", opt.CrossAltKm);

            var rows = new List<string[]>();
            var impacts = new List<(int row, double lat, double lon)>();
            var crossRows = new List<(int row, double lat, double lon, double ut)>();
            double nextSample = 0, prevLat = double.NaN, prevLon = double.NaN;
            double wanderM = 0, maxDragErr = 0, sumDragErr = 0;
            int errSamples = 0, noPrediction = 0;
            int partsEnd = partsStart;
            // Prediction refresh: a finished prediction swaps Trajectory.Patches, so count swaps against real
            // time. That, not per-frame cost (capped at 2 ms by Trajectories), is what extra force work costs.
            object lastPatches = TrajApi.PatchesRef;
            int completions = 0, frames = 0;
            double compTimeSum = 0;
            float realStart = UnityEngine.Time.realtimeSinceStartup;
            // The actual crossing, interpolated between the frames either side of it.
            bool crossed = false;
            double actLat = double.NaN, actLon = double.NaN, actUT = double.NaN;
            double fAlt = double.NaN, fLat = 0, fLon = 0, fUT = 0;
            // Where the vessel came down (ToGround): the last position seen above TouchdownAglM, updated each frame
            // so a vessel destroyed on impact still leaves one.
            bool touchedDown = false;
            double tdLat = double.NaN, tdLon = double.NaN;
            // where the first sample was taken: the density there at each later altitude is the air a prediction made
            // then assumed (FAR predicts in the atmosphere at the vessel's latitude and longitude)
            double startLat = double.NaN, startLon = double.NaN;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var flown = opt.DumpAtKm != null && opt.DumpAtKm.Length > 0 ? new StringBuilder("kind,patch,ut,altKm,airspeed,inertialSpeed,fpaDeg,groundAltM\n") : null;
            var dumped = new HashSet<double>();
            double lastFlownUT = double.NaN;

            yield return Wait.Until(() =>
            {
                var v = FlightGlobals.ActiveVessel;
                if (v == null || Flight.Dead) { touchedDown |= opt.ToGround && !double.IsNaN(tdLat); return true; }
                partsEnd = v.parts.Count;
                double ut = Planetarium.GetUniversalTime();
                if (flown != null)
                {
                    if (double.IsNaN(lastFlownUT) || ut - lastFlownUT >= 1.0)
                    {
                        lastFlownUT = ut;
                        Vector3d pos = v.GetWorldPos3D() - v.mainBody.position;
                        double fpa = 90.0 - Vector3d.Angle(pos, v.obt_velocity);   // inertial, as the prediction dump has it
                        flown.Append("flown,0,").Append(ut.ToString("F2", inv)).Append(",").Append((v.altitude / 1000).ToString("F3", inv)).Append(",")
                             .Append(v.srfSpeed.ToString("F2", inv)).Append(",").Append(v.obt_speed.ToString("F2", inv)).Append(",")
                             .Append(fpa.ToString("F4", inv)).Append(",").Append(v.terrainAltitude.ToString("F0", inv)).Append("\n");
                    }
                    foreach (double km in opt.DumpAtKm)
                        if (!dumped.Contains(km) && v.altitude < km * 1000 && TrajApi.DumpPrediction(Path.Combine(ctx.ResultsDir, $"prediction_{km:F0}.csv")))
                            dumped.Add(km);
                }
                double target = opt.TargetAoADeg != null ? opt.TargetAoADeg(v) : aoa;
                if (double.IsNaN(target)) target = aoa;
                if (opt.ToGround)
                {
                    tdLat = v.latitude; tdLon = v.longitude;
                    double agl = v.altitude - Math.Max(0.0, v.terrainAltitude);
                    if (v.Landed || v.Splashed || agl < opt.TouchdownAglM)
                    {
                        touchedDown = true;
                        // a time lapse pauses here for its landing shots (no-op without one)
                        VideoCapture.Freeze();
                        return true;
                    }
                }

                if (measureCross && !crossed && !double.IsNaN(fAlt) && fAlt >= crossAlt && v.altitude < crossAlt)
                {
                    double f = (fAlt - crossAlt) / Math.Max(1e-9, fAlt - v.altitude);
                    double dLon = v.longitude - fLon;
                    if (dLon > 180) dLon -= 360; else if (dLon < -180) dLon += 360;
                    actLat = fLat + (v.latitude - fLat) * f;
                    actLon = fLon + dLon * f;
                    actUT = fUT + (ut - fUT) * f;
                    crossed = true;
                }
                fAlt = v.altitude; fLat = v.latitude; fLon = v.longitude; fUT = ut;

                object patches = TrajApi.PatchesRef;
                if (patches != null && !ReferenceEquals(patches, lastPatches)) { completions++; lastPatches = patches; }
                double ct = TrajApi.ComputationTimeMs;
                if (!double.IsNaN(ct)) { compTimeSum += ct; frames++; }

                if (v.missionTime >= nextSample)
                {
                    nextSample = v.missionTime + sampleSec;
                    bool got = TrajApi.ImpactLatLon(out double iLat, out double iLon, out double iAlt);
                    if (!got) noPrediction++;

                    double moveM = double.NaN;
                    if (got && !double.IsNaN(prevLat))
                    {
                        moveM = GreatCircleM(v.mainBody, prevLat, prevLon, iLat, iLon);
                        wanderM += moveM;
                    }
                    if (got) { prevLat = iLat; prevLon = iLon; impacts.Add((rows.Count, iLat, iLon)); }

                    // FAR's predicted pitching moment at the real state and deflection (what Trajectories' trim
                    // is fed) against the moment FAR is actually applying in flight - taken before the force
                    // sampling below, which runs trim solves at this very airflow. NaN without FAR.
                    double apiPitch = double.NaN, truePitch = double.NaN;
                    double apiLift = double.NaN, apiDrag = double.NaN, trueLift = double.NaN, trueDrag = double.NaN;
                    try
                    {
                        FarApi.ClearMemo(v);
                        apiPitch = FarApi.PredictedPitchTorqueKNm(v, out apiLift, out apiDrag);
                        truePitch = FarApi.TrueAeroPitchMomentKNm(v, out _, out _);
                        FarApi.TrueAeroForceKN(v, out trueLift, out trueDrag);
                    }
                    catch (Exception) { }
                    bool flowOk = FarApi.FlowInputs(v, out double liveMach, out double liveRe, out double liveRho,
                                                    out double predMach, out double predRe, out double predRho,
                                                    out double predTempK, out double reTempK);
                    bool splitOk = false;
                    double apiWingDrag = double.NaN, apiBodyDrag = double.NaN, trueWingDrag = double.NaN, trueBodyDrag = double.NaN;
                    try { splitOk = FarApi.WingBodyDrag(v, out apiWingDrag, out apiBodyDrag, out trueWingDrag, out trueBodyDrag); }
                    catch (Exception) { }
                    FarApi.ExternalTemperatures(v, out double extLive, out double extPred);

                    double dragC = double.NaN, dragD = double.NaN, liftC = double.NaN, liftD = double.NaN,
                           dragErr = double.NaN, liftErr = double.NaN;
                    if (TrajApi.SampleForces(v, target, out Vector3d cached, out Vector3d direct))
                    {
                        Vector3d velN = ((Vector3d)v.srf_velocity).normalized;
                        dragC = -Vector3d.Dot(cached, velN);
                        dragD = -Vector3d.Dot(direct, velN);
                        liftC = (cached - Vector3d.Dot(cached, velN) * velN).magnitude;
                        liftD = (direct - Vector3d.Dot(direct, velN) * velN).magnitude;
                        if (Math.Abs(dragD) > 0.01)
                        {
                            dragErr = 100.0 * (dragC - dragD) / Math.Abs(dragD);
                            if (Math.Abs(Flight.AoA - target) <= opt.HeldBandDeg)
                            {
                                maxDragErr = Math.Max(maxDragErr, Math.Abs(dragErr));
                                sumDragErr += Math.Abs(dragErr);
                                errSamples++;
                            }
                        }
                        if (Math.Abs(liftD) > 0.01) liftErr = 100.0 * (liftC - liftD) / Math.Abs(liftD);
                    }

                    // The predicted path: where it crosses the comparison altitude, and a sanity check that its
                    // first point is where the vessel is (it should be within the distance flown since the
                    // prediction started).
                    string pcLat = "", pcLon = "", pcUT = "", path0 = "";
                    if (measureCross && !crossed && v.altitude > crossAlt)
                    {
                        var path = TrajApi.PredictedPath();
                        if (path.Count > 0)
                            path0 = GreatCircleM(v.mainBody, path[0].Lat, path[0].Lon, v.latitude, v.longitude).ToString("F0");
                        if (TrajApi.PredictedCrossing(path, crossAlt, out TrajApi.PathPoint pc))
                        {
                            crossRows.Add((rows.Count, pc.Lat, pc.Lon, pc.UT));
                            pcLat = pc.Lat.ToString("F5"); pcLon = pc.Lon.ToString("F5"); pcUT = pc.UT.ToString("F1");
                        }
                    }

                    TrajApi.StockDensities(v, out double rhoAlt, out double rhoPos);
                    if (double.IsNaN(startLat)) { startLat = v.latitude; startLon = v.longitude; }
                    rows.Add(new[]
                    {
                        v.missionTime.ToString("F1"), v.altitude.ToString("F0"), v.srfSpeed.ToString("F1"),
                        v.mach.ToString("F2"), v.dynamicPressurekPa.ToString("F4"), Flight.AoA.ToString("F2"),
                        got ? iLat.ToString("F5") : "", got ? iLon.ToString("F5") : "",
                        TrajApi.TimeTillImpact.ToString("F1"),
                        double.IsNaN(moveM) ? "" : moveM.ToString("F0"), "",
                        dragC.ToString("F3"), dragD.ToString("F3"), dragErr.ToString("F2"),
                        liftC.ToString("F3"), liftD.ToString("F3"), liftErr.ToString("F2"),
                        pcLat, pcLon, pcUT, "", "",
                        path0, TimeWarp.CurrentRate.ToString("F1"), FarApi.MeanAbsDeflection(v).ToString("F2"),
                        ut.ToString("F1"),
                        // The density the vessel really flies through against Trajectories' two stock estimates, and
                        // the scale the model is applying: how much the altitude-only atmosphere is off, and whether
                        // the position correction closes it.
                        v.atmDensity.ToString("E4"), rhoAlt.ToString("E4"), rhoPos.ToString("E4"),
                        TrajApi.DensityScaleAt(v).ToString("F4"),
                        // The trim model's prediction of the pitch input the vessel needs right now, against the
                        // input the autopilot is actually giving it - a direct check of the trim before any
                        // trajectory comparison.
                        (TrajApi.TrimControlSurfaces == true ? TrajApi.LastSampleTrimInput : double.NaN).ToString("F3"),
                        v.ctrlState.pitch.ToString("F3"),
                        TrajApi.OtherPitchTorque.ToString("F2"),
                        apiPitch.ToString("F2"), truePitch.ToString("F2"),
                        apiLift.ToString("F3"), trueLift.ToString("F3"), apiDrag.ToString("F3"), trueDrag.ToString("F3"),
                        target.ToString("F2"), "",
                        FarApi.FarPredictionDensity(v).ToString("E4"),
                        TrajApi.ModelDouble("SeedPitchTorque").ToString("F2"),
                        TrajApi.ModelBool("OtherPitchTorqueLearned") == true ? "1" : "0",
                        (TrajApi.StaticDouble("Trajectories.DescentProfile", "AoABiasRad") * 180.0 / Math.PI).ToString("F2"),
                        TrajApi.StaticDouble("Trajectories.TargetSteer", "AlongTrackMissM").ToString("F0"),
                        TrajApi.StaticDouble("Trajectories.TargetSteer", "CrossTrackMissM").ToString("F0"),
                        (TrajApi.StaticDouble("Trajectories.DescentProfile", "BankRad") * 180.0 / Math.PI).ToString("F2"),
                        MeasuredBankDeg(v).ToString("F2"),
                        TrajApi.PredictedAirspeedAt(0.05 * v.mainBody.atmosphereDepth).ToString("F1"),
                        TrajApi.StaticDouble("Trajectories.Trajectory", "PredictedImpactAirspeed").ToString("F1"),
                        TrajApi.StaticDouble("Trajectories.Trajectory", "MaxFrameMsSinceRead").ToString("F1"),
                        Delta("Trajectories.AeroForceCache", "FillsEmpty", ref lastFillsEmpty, "F0"),
                        Delta("Trajectories.AeroForceCache", "FillsExpired", ref lastFillsExpired, "F0"),
                        Delta("Trajectories.AeroForceCache", "FillMs", ref lastFillMs, "F0"),
                        Delta("Trajectories.AeroForceCache", "Invalidations", ref lastInvalidations, "F0"),
                        Delta("Trajectories.AeroDynamicModel", "TorqueInvalidations", ref lastTorqueInvalidations, "F0"),
                        (TrajApi.StaticDouble("Trajectories.DescentProfile", "ReversalAltM") / 1000.0).ToString("F1"),
                        (v.orbit.PeA / 1000.0).ToString("F4"),
                        (v.orbit.ApA / 1000.0).ToString("F4"),
                        RcsThrustKN(v).ToString("F3"),
                        TrajApi.StockDensityAt(v.mainBody, v.mainBody.GetWorldSurfacePosition(startLat, startLon, v.altitude)).ToString("E4"),
                        // profile nodes the latest prediction held short of (bits: 1 entry .. 8 final), and clamps since load
                        TrajApi.StaticDouble("Trajectories.DescentProfile", "UnholdableNodes").ToString("F0"),
                        TrajApi.StaticDouble("Trajectories.AeroDynamicModel", "HoldableClamps").ToString("F0"),
                        // the side force the model learned from the flight, over the aerodynamic force
                        TrajApi.ModelDouble("SideForceRatio").ToString("F5"),
                        // the yaw input the last yaw-trimmed evaluation settled on, the flown one, and saturated yaw trims
                        TrajApi.ModelDouble("LastYawTrimInput").ToString("F4"),
                        v.ctrlState.yaw.ToString("F4"),
                        TrajApi.StaticDouble("Trajectories.AeroDynamicModel", "YawTrimSaturations").ToString("F0"),
                        TrajApi.ModelDouble("LastRollTrimInput").ToString("F4"),
                        v.ctrlState.roll.ToString("F4"),
                        // where the vessel is: the flown ground track, to measure a predicted impact across it
                        v.latitude.ToString("F6"),
                        v.longitude.ToString("F6"),
                        // the flow inputs FAR's flight took against the ones its prediction takes here
                        flowOk ? liveMach.ToString("F5") : "", flowOk ? predMach.ToString("F5") : "",
                        flowOk ? liveRe.ToString("E5") : "", flowOk ? predRe.ToString("E5") : "",
                        flowOk ? liveRho.ToString("E6") : "", flowOk ? predRho.ToString("E6") : "",
                        flowOk ? predTempK.ToString("F3") : "", flowOk ? reTempK.ToString("F3") : "",
                        // drag split into FAR's wing models and voxel body, predicted against applied
                        splitOk ? apiWingDrag.ToString("F4") : "", splitOk ? apiBodyDrag.ToString("F4") : "",
                        splitOk ? trueWingDrag.ToString("F4") : "", splitOk ? trueBodyDrag.ToString("F4") : "",
                        // the external temperature the wings' skin friction takes, flown against predicted
                        extLive.ToString("F2"), extPred.ToString("F2"),
                        // extra RK4 steps the prediction split stiff horizon-held steps into, since load
                        TrajApi.StaticDouble("Trajectories.Trajectory", "StiffSubstepsTaken").ToString("F0"),
                    });

                    Log.Info($"traj[{tag}]: alt={v.altitude / 1000:F1} km srf={v.srfSpeed:F0} " +
                             $"aoa={Flight.AoA:F1} impact={(got ? iLat.ToString("F3") + "/" + iLon.ToString("F3") : "none")} " +
                             $"move={(double.IsNaN(moveM) ? 0 : moveM) / 1000:F1} km dragErr={dragErr:F1}%" +
                             (pcLat.Length > 0 ? $" cross={pcLat}/{pcLon}@{pcUT}" : ""));
                }

                if (partsEnd < partsStart * 0.7) return true;             // broke up
                if (v.altitude < opt.EndAltKm * 1000) return true;
                if (v.altitude < opt.SlowBelowAltKm * 1000 && v.srfSpeed < opt.SlowSpeed) return true;
                return false;
            }, flightTimeout, "entry to end (breakup, slow, or below the end altitude)");

            if (physWarp > 1) Flight.WarpOff();
            if (flown != null) File.WriteAllText(Path.Combine(ctx.ResultsDir, "flown_path.csv"), flown.ToString());
            var vEnd = FlightGlobals.ActiveVessel;
            CelestialBody body = vEnd != null ? vEnd.mainBody : FlightGlobals.GetHomeBody();
            Func<int, bool> held = i => double.TryParse(rows[i][5], out double a) && double.TryParse(rows[i][ColAoATarget], out double t) &&
                                        Math.Abs(a - t) <= opt.HeldBandDeg;

            // Predicted impact against where the vessel really came down - ground truth, not a later prediction.
            var touchErrs = new List<double>();
            if (opt.ToGround)
            {
                ctx.Record(k + "touchedDown", touchedDown);
                if (touchedDown)
                {
                    ctx.Record(k + "touchdownLat", Math.Round(tdLat, 5));
                    ctx.Record(k + "touchdownLon", Math.Round(tdLon, 5));
                    foreach (var im in impacts)
                    {
                        double d = GreatCircleM(body, im.lat, im.lon, tdLat, tdLon);
                        rows[im.row][ColImpactVsTouchdown] = d.ToString("F0");
                        if (held(im.row)) touchErrs.Add(d);
                    }
                }
                if (touchErrs.Count > 0)
                {
                    var s = touchErrs.OrderBy(x => x).ToList();
                    ctx.Record(k + "touchSamplesHeld", touchErrs.Count);
                    ctx.Record(k + "touchMeanKm", Math.Round(touchErrs.Average() / 1000, 2));
                    ctx.Record(k + "touchMedianKm", Math.Round(s[s.Count / 2] / 1000, 2));
                    ctx.Record(k + "touchMaxKm", Math.Round(s[s.Count - 1] / 1000, 2));
                }
            }

            // Impact drift: each prediction against the one the predictor settled on.
            double spreadM = 0;
            if (impacts.Count > 0)
            {
                var last = impacts[impacts.Count - 1];
                foreach (var im in impacts)
                {
                    double d = GreatCircleM(body, im.lat, im.lon, last.lat, last.lon);
                    spreadM = Math.Max(spreadM, d);
                    rows[im.row][ColImpactFromFinal] = d.ToString("F0");
                }
            }

            // Crossing error: each prediction against where the vessel really went through the altitude.
            var crossErrs = new List<double>();
            var crossTimeErrs = new List<double>();
            if (crossed)
            {
                ctx.Record(k + "actualCrossLat", Math.Round(actLat, 5));
                ctx.Record(k + "actualCrossLon", Math.Round(actLon, 5));
                ctx.Record(k + "actualCrossUT", Math.Round(actUT, 1));
                foreach (var cr in crossRows)
                {
                    double d = GreatCircleM(body, cr.lat, cr.lon, actLat, actLon);
                    double dt = cr.ut - actUT;
                    rows[cr.row][ColCrossErr] = d.ToString("F0");
                    rows[cr.row][ColCrossTimeErr] = dt.ToString("F1");
                    if (held(cr.row)) { crossErrs.Add(d); crossTimeErrs.Add(dt); }
                }
            }
            ctx.Record(k + "crossed", crossed);
            if (crossErrs.Count > 0)
            {
                var sorted = crossErrs.OrderBy(x => x).ToList();
                ctx.Record(k + "crossSamplesHeld", crossErrs.Count);
                ctx.Record(k + "crossMeanKm", Math.Round(crossErrs.Average() / 1000, 2));
                ctx.Record(k + "crossMedianKm", Math.Round(sorted[sorted.Count / 2] / 1000, 2));
                ctx.Record(k + "crossMaxKm", Math.Round(sorted[sorted.Count - 1] / 1000, 2));
                ctx.Record(k + "crossMeanAbsTimeErrS", Math.Round(crossTimeErrs.Select(Math.Abs).Average(), 1));
                ctx.Record(k + "crossMeanTimeErrS", Math.Round(crossTimeErrs.Average(), 1));
            }

            var csv = new StringBuilder(Header);
            foreach (var r in rows) csv.Append(string.Join(",", r)).Append('\n');
            File.WriteAllText(Path.Combine(ctx.ResultsDir, tag + ".csv"), csv.ToString());

            double realSec = UnityEngine.Time.realtimeSinceStartup - realStart;
            ctx.Record(k + "predictionsCompleted", completions);
            ctx.Record(k + "realSeconds", Math.Round(realSec, 1));
            ctx.Record(k + "secondsPerPrediction", completions > 0 ? Math.Round(realSec / completions, 3) : double.NaN);
            ctx.Record(k + "meanComputationMsPerFrame", frames > 0 ? Math.Round(compTimeSum / frames, 3) : double.NaN);
            ctx.Record(k + "samples", rows.Count);
            ctx.Record(k + "noPredictionSamples", noPrediction);
            ctx.Record(k + "impactWanderKm", Math.Round(wanderM / 1000, 2));
            ctx.Record(k + "impactSpreadKm", Math.Round(spreadM / 1000, 2));
            ctx.Record(k + "maxDragErrPct", Math.Round(maxDragErr, 2));
            ctx.Record(k + "meanDragErrPct", errSamples > 0 ? Math.Round(sumDragErr / errSamples, 2) : 0);
            ctx.Record(k + "partsLost", partsStart - partsEnd);
            if (vEnd != null)
            {
                ctx.Record(k + "endAltKm", Math.Round(vEnd.altitude / 1000, 1));
                ctx.Record(k + "endSrfSpeed", Math.Round(vEnd.srfSpeed));
                ctx.Record(k + "endLat", Math.Round(vEnd.latitude, 5));
                ctx.Record(k + "endLon", Math.Round(vEnd.longitude, 5));
            }
            Assert.True(rows.Count > 5, $"the entry produced samples ({rows.Count})");
            Assert.True(noPrediction < rows.Count, "Trajectories produced a prediction during the entry");
            if (measureCross) Assert.True(crossed, $"the vessel descended through the comparison altitude {opt.CrossAltKm} km");
        }
    }
}
