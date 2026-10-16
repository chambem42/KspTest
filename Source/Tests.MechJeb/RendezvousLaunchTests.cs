using System;
using System.Collections;
using KSPUITest;
using KSPUITest.MechJeb;
using MuMech;
using UnityEngine;

namespace KSPUITest.Tests.MechJeb
{
    /// <summary>
    /// MechJeb's PSG "Launch to rendezvous": spawns a probe in orbit, targets it, plans the launch from the Ascent Guidance
    /// window, lets MJ warp to the window and fly the ascent, and compares the phase angle to the target at insertion with
    /// the planner's prediction. Passes if the cheapest two-burn rendezvous from insertion needs under maxRdvDv (m/s, 20);
    /// rdv=rcs or rendezvous=... go on to fly one. Params: target (an existing vessel to fly to instead of the probe),
    /// moveTarget=on (put that vessel into the targetAltKm/Inc/Lan/ArgLat orbit first), orbitAltKm (insertion, default the
    /// target's altitude), targetAltKm (150), targetInc (20), targetLan (60), targetArgLat (where the target is at spawn,
    /// 200), hohmann=on (insert below for a Hohmann transfer; else directly, phaseAngle (deg, 0.05) behind the target),
    /// lastStage (the last stage PSG flies, the craft's setting by default), phaseTol (deg, 1), maxPlaneOffset (deg, 1),
    /// tolDeg (allowed prediction error, 2), warp (physics warp index during the ascent), hyper (TimeControl hyper-warp
    /// rate during the ascent, 4; 0 = off), hyperAccuracy (its physics step multiplier, 1). Records the distance to the
    /// target at insertion and the closest approach of a Hohmann transfer to the target's orbit burned right at insertion.
    /// </summary>
    public static class RendezvousLaunchTests
    {
        private static readonly object User = new object();

        // timelapse=on: a captioned time lapse of the whole flight (see Timelapse)
        private static double liftoffUT = double.NaN;

        private static void Caption(string title, string detail = "", float? intervalSec = null)
        {
            if (!Timelapse.Active) return;
            Timelapse.Caption(title, detail);
            if (intervalSec.HasValue) Timelapse.IntervalSec = intervalSec.Value;
        }

        private static string LiveLine(Vessel sat)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || sat == null) return null;
            double now = Planetarium.GetUniversalTime();
            string clock = double.IsNaN(liftoffUT) ? "on the pad" : "T+" + KSPUtil.PrintTimeCompact(now - liftoffUT, false);
            double d = (v.GetWorldPos3D() - sat.GetWorldPos3D()).magnitude;
            string dist = d > 10000 ? $"{d / 1000:F0} km" : d > 1000 ? $"{d / 1000:F2} km" : $"{d:F0} m";
            string rel = v.situation != Vessel.Situations.ORBITING ? "" : $"   relative speed {(v.obt_velocity - sat.obt_velocity).magnitude:F2} m/s";
            return $"{clock}   target {dist}{rel}   warp x{TimeWarp.CurrentRate:F0}";
        }

        // closes MechJeb's windows but the Ascent Guidance one (keep = true) or all of them
        private static void CloseMjWindows(MechJebCore core, bool keepAscent)
        {
            foreach (var m in core.GetComputerModules<DisplayModule>())
                if (!(keepAscent && m is MechJebModuleAscentMenu))
                    m.Enabled = false;
        }

        [UITest("Mj_LaunchToRendezvous", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket", TimeoutSec = 3600)]
        public static IEnumerator Mj_LaunchToRendezvous(TestContext ctx)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;

            Vessel sat = null;
            string targetName = ctx.Param("target");
            if (targetName != null)
            {
                ctx.Step($"Target {targetName}");
                sat = FlightGlobals.Vessels.Find(x => x.vesselName == targetName);
                Assert.True(sat != null, $"a vessel named '{targetName}' in the save");
                if (ctx.Param("moveTarget") == "on")
                {
                    // into the orbit asked for (unset elements keep the vessel's own)
                    Orbit o = sat.orbit;
                    MoveToOrbit(sat, ctx.ParamDouble("targetAltKm", (o.semiMajorAxis - body.Radius) / 1000) * 1000,
                        ctx.ParamDouble("targetInc", o.inclination), ctx.ParamDouble("targetLan", o.LAN), ctx.ParamDouble("targetArgLat", 200));
                }
            }
            else
            {
                ctx.Step("Spawn the target satellite and target it");
                string satName = "UITest Satellite";
                SpawnSatellite(satName, body, ctx.ParamDouble("targetAltKm", 150) * 1000, ctx.ParamDouble("targetInc", 20),
                    ctx.ParamDouble("targetLan", 60), ctx.ParamDouble("targetArgLat", 200));
                yield return Wait.Until(() => (sat = FlightGlobals.Vessels.Find(x => x.vesselName == satName)) != null, 30, "satellite in FlightGlobals");
            }
            // insertion at the target's altitude unless asked otherwise
            double altKm = ctx.ParamDouble("orbitAltKm", Math.Round((sat.orbit.semiMajorAxis - body.Radius) / 1000, 1));
            FlightGlobals.fetch.SetVesselTarget(sat);
            yield return Wait.Seconds(1);
            Log.Info($"satellite: {Describe(sat.orbit)}");
            ctx.Record("targetOrbit", Describe(sat.orbit));

            liftoffUT = double.NaN;
            if (ctx.Param("timelapse") == "on")
            {
                Timelapse.Begin(ctx.ResultsDir, 0.5f);
                Vessel liveSat = sat;
                Timelapse.Live = () => LiveLine(liveSat);
                Caption($"Target: {sat.vesselName} in a {sat.orbit.PeA / 1000:F0} km, {sat.orbit.inclination:F1}° orbit",
                    "Goal: launch straight to it and rendezvous on the payload's own small thruster");
                yield return Wait.Seconds(4);
            }

            ctx.Step("Plan the launch from the Ascent Guidance window");
            MechJebCore core = Mj.Core;
            MechJebModuleAscentSettings s = core.AscentSettings;
            s.AscentType = AscentType.PSG;
            s.DesiredOrbitAltitude.Val = altKm * 1000;
            s.DesiredApoapsis.Val = altKm * 1000;
            // MJ only sets this while its PSG settings window is drawn (true unless every stage is fixed burn time); with
            // that window closed it stays false and PSG flies to the fixed attach altitude instead of the target orbit
            s.OptimizeStageFlag = true;
            s.Autostage = true;
            // the last stage PSG flies (the craft's own setting otherwise; 0 has it plan with the payload's stages too)
            if (ctx.Param("lastStage") != null) s.LastStage.Val = (int)ctx.ParamDouble("lastStage", -1);
            ctx.Record("lastStage", s.LastStage.Val);
            // a direct insertion just behind the target (phaseAngle: deg the target is ahead), or hohmann=on for the phase a
            // Hohmann transfer from insertion needs
            s.RendezvousHohmannPhase = ctx.Param("hohmann") == "on";
            s.RendezvousPhaseAngle.Val = ctx.ParamDouble("phaseAngle", 0.05);
            if (ctx.Param("liftoffDelay") != null) s.RendezvousLiftoffDelay.Val = ctx.ParamDouble("liftoffDelay", 3);
            ctx.Record("liftoffDelay", s.RendezvousLiftoffDelay.Val);
            s.RendezvousPhaseTolerance.Val = ctx.ParamDouble("phaseTol", 1);
            s.RendezvousMaxLanError.Val = ctx.ParamDouble("maxPlaneOffset", 1);
            core.Node.Autowarp = true;
            core.GetComputerModule<MechJebModuleAscentMenu>().Enabled = true;
            yield return Imgui.WaitWindow("Ascent Guidance");
            if (Timelapse.Active)
            {
                yield return Wait.Frames(2);
                CloseMjWindows(core, true);
                Caption("MechJeb: Launch to rendezvous",
                    "Solve the PSG ascent once, turn it with the Earth to every launch time, pick the one that inserts just behind the target");
                yield return Wait.Seconds(3);
            }
            yield return Imgui.Toggle("Launch to rendezvous", true, window: "Ascent Guidance");
            yield return Wait.Frames(2);
            yield return Snap.Shot(ctx, "before-plan");
            // with the toggle on, Engage autopilot plans the launch and engages the autopilot once the countdown is set
            double planUT = Planetarium.GetUniversalTime();
            yield return Imgui.Click("Engage autopilot", window: "Ascent Guidance");
            Assert.False(core.Ascent.Enabled, "autopilot not engaged while planning");

            var planner = core.GetComputerModule<MechJebModuleRendezvousLaunchPlanner>();

            // cancel=planning: Disengage stays up while the launch is planned and cancels the plan
            if (ctx.Param("cancel") == "planning")
            {
                ctx.Step("Disengage while planning");
                yield return Wait.Frames(2);
                Assert.True(planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.PLANNING, "planning: " + planner.State);
                yield return Imgui.Click("Disengage autopilot", window: "Ascent Guidance");
                yield return Wait.Seconds(1);
                Assert.True(planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.IDLE, "planner idle: " + planner.State);
                Assert.True(!core.Ascent.Enabled && !core.Ascent.TimedLaunch && !planner.Launching, "no autopilot or countdown");
                yield return Wait.Seconds(5);
                Assert.True(!core.Ascent.Enabled && planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.IDLE,
                    "the dropped optimizer job didn't engage anything");
                yield break;
            }
            float lastPlanLog = 0;
            yield return Wait.Until(() =>
            {
                if (Time.realtimeSinceStartup - lastPlanLog > 10)
                {
                    lastPlanLog = Time.realtimeSinceStartup;
                    ITargetable t = FlightGlobals.fetch.VesselTarget;
                    Log.Info($"planning: {planner.State} '{planner.Status}' | target {(t == null ? "none" : t.GetName())} | " +
                        $"UT {Planetarium.GetUniversalTime():F1} warp x{TimeWarp.CurrentRate}");
                }
                return planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.DONE ||
                    planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.FAILED;
            }, 1800, "rendezvous launch plan (and its check shortly before the launch)");
            Assert.True(planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.DONE, "planner finished: " + planner.Status);
            Assert.True(core.Ascent.Enabled && core.Ascent.TimedLaunch, "autopilot engaged with the countdown running");

            double now = Planetarium.GetUniversalTime();
            ctx.Record("planeWindow", planner.PlaneReachable);
            ctx.Record("timedToPlane", planner.TimedToPlane);
            ctx.Record("waitSec", Math.Round(planner.LaunchTime - planUT, 1));
            ctx.Record("waitDays", Math.Round((planner.LaunchTime - planUT) / Math.Abs(body.rotationPeriod), 3));
            ctx.Record("ascentInclination", planner.Inclination);
            ctx.Record("plannedPhaseAngle", Math.Round(planner.PhaseAngle, 3));
            ctx.Record("plannedPhaseError", Math.Round(planner.PhaseError, 4));
            ctx.Record("plannedPlaneOffset", Math.Round(planner.PlaneOffset, 3));
            ctx.Record("plannedTgo", Math.Round(planner.InsertionTime - planner.LaunchTime, 2));
            ctx.Record("checkShiftSec", Math.Round(planner.CheckShift, 2));
            ctx.Record("checkTgoChangeSec", Math.Round(planner.CheckTgoChange, 2));

            // cancel=disengage: the Ascent Guidance window's Disengage during the countdown cancels the planned launch
            if (ctx.Param("cancel") == "disengage")
            {
                ctx.Step("Disengage during the countdown");
                yield return Imgui.Click("Disengage autopilot", window: "Ascent Guidance");
                yield return Wait.Seconds(1);
                Assert.True(!core.Ascent.Enabled, "autopilot disengaged");
                Assert.True(!core.Ascent.TimedLaunch && !planner.Launching && !s.LaunchingToPlane, "countdown and rendezvous launch cancelled");
                Assert.True(planner.State == MechJebModuleRendezvousLaunchPlanner.PlannerState.IDLE, "planner idle: " + planner.State);
                // the warp to the countdown stops with it (the warp controller would go on to the last time it was given)
                yield return Wait.Until(() => TimeWarp.CurrentRateIndex == 0, 10, "warp to the cancelled countdown stopped");
                // then on past the old T-0 (rails warp, on the pad): nothing may launch there
                double oldT0 = planner.LaunchTime;
                TimeWarp.fetch.WarpTo(oldT0 + 5);
                yield return Wait.Until(() => Planetarium.GetUniversalTime() > oldT0 + 5, 300, "past the old T-0");
                Flight.WarpOff();
                Assert.True(FlightGlobals.ActiveVessel.LandedOrSplashed, "no launch at the old T-0");
                yield break;
            }

            yield return Snap.Shot(ctx, "planned");
            Caption($"Launch window: {(planner.LaunchTime - planUT) / 86400:F1} days from now",
                $"Inserts {planner.PhaseAngle:F2}° behind the target · {planner.PlaneOffset:F2}° off its plane · " +
                $"{planner.InsertionTime - planner.LiftoffTime:F0} s to orbit · MechJeb warps there", 0.5f);
            if (Timelapse.Active) yield return Wait.Seconds(3);

            ctx.Step("Wait for MechJeb to warp to the window and launch");
            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 10)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"countdown: T-{core.Ascent.TMinus:F1}s warp x{TimeWarp.CurrentRate} | {core.Ascent.Status}");
                }
                return !FlightGlobals.ActiveVessel.LandedOrSplashed && Flight.Altitude > 200;
            }, 900, "liftoff");
            liftoffUT = planner.LiftoffTime;
            Caption("Liftoff, on time", "PSG guidance flies the planned ascent into the target's plane", 0.25f);
            ctx.Record("liftoffLateSec", Math.Round(Planetarium.GetUniversalTime() - planner.LaunchTime, 2));
            ctx.Record("liftoffAfterPlannedAscentStartSec", Math.Round(Planetarium.GetUniversalTime() - planner.LiftoffTime, 2));
            yield return Wait.Until(() => Flight.Altitude > 1000, 120, "clear of the pad");
            if (ctx.Param("warp") != null) Flight.PhysicsWarp(int.Parse(ctx.Param("warp")));
            // TimeControl hyper-warp for the ascent: more physics steps per rendered frame, the same step (accuracy 1)
            double hyper = ctx.ParamDouble("hyper", 4);
            if (hyper > 1 && !Flight.HyperWarp(hyper, ctx.ParamDouble("hyperAccuracy", 1)))
                Log.Warn("hyper-warp refused (TimeControl missing?), flying the ascent at 1x");

            ctx.Step("Fly the ascent");
            double insertionUT = double.NaN;
            lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"ascent: {core.Ascent.Status} guidance {core.Guidance.Status} tgo {core.Guidance.Tgo:F1} | {Flight.Telemetry()} | engines {Engines()}");
                }
                if (core.Guidance.Status == PSGStatus.FINISHED || !core.Ascent.Enabled)
                {
                    insertionUT = Planetarium.GetUniversalTime();
                    return true;
                }
                // the guidance never finished: a last stage that ran dry short of the target (terminal mode with nothing
                // burning), or a stale solution that misses its cutoff and keeps burning
                if (core.Guidance.Tgo < -30)
                    throw new UITestFailure($"guidance 30 s past its cutoff without finishing ({core.Guidance.Status}, engines {Engines()}): {Describe(FlightGlobals.ActiveVessel.orbit)}");
                return false;
            }, 1200, "insertion (PSG guidance finished)");
            Flight.HyperWarpOff();
            Flight.WarpOff();
            yield return Wait.Seconds(1);

            Vessel av = FlightGlobals.ActiveVessel;
            double expected = planner.PhaseAngle + planner.PhaseError;
            double atInsertion = PhaseDeg(av.orbit, sat.orbit, insertionUT);
            double atPlanned = PhaseDeg(av.orbit, sat.orbit, planner.InsertionTime);
            double planeAngle = Vector3d.Angle(Normal(av.orbit, insertionUT), Normal(sat.orbit, insertionUT));
            double distanceKm = (R(av.orbit, insertionUT) - R(sat.orbit, insertionUT)).magnitude / 1000;
            double approachKm = HohmannClosestApproachKm(av.orbit, sat.orbit, insertionUT, out double approachUT);
            Log.Info($"distance to the target at insertion {distanceKm:F1} km; Hohmann transfer from there: closest approach " +
                $"{approachKm:F2} km, {approachUT - insertionUT:F0} s after insertion");
            // what the ascent left: MJ's vacuum stage stats for the whole remaining vessel, fresh from after insertion
            core.StageStats.RequestUpdate();
            yield return Wait.Until(() => { core.StageStats.RequestUpdate(); return core.StageStats.VacT > insertionUT; }, 30, "stage stats after insertion", shot: false);
            double remainingDv = 0;
            foreach (var stage in core.StageStats.VacStats) remainingDv += stage.DeltaV;
            Log.Info($"remaining vacuum delta-v after insertion {remainingDv:F1} m/s");
            ctx.Record("remainingDvAfterInsertion", Math.Round(remainingDv, 1));
            ctx.Record("distanceAtInsertionKm", Math.Round(distanceKm, 2));
            ctx.Record("hohmannClosestApproachKm", Math.Round(approachKm, 3));
            ctx.Record("hohmannClosestApproachAfterSec", Math.Round(approachUT - insertionUT, 1));

            Log.Info($"insertion: UT {insertionUT:F1} (planned {planner.InsertionTime:F1}) phase {atInsertion:F3} at insertion, " +
                $"{atPlanned:F3} at the planned time, expected {expected:F3}; relative inclination {planeAngle:F3}; {Describe(av.orbit)}");
            ctx.Record("insertionLateSec", Math.Round(insertionUT - planner.InsertionTime, 2));
            ctx.Record("expectedPhase", Math.Round(expected, 3));
            ctx.Record("phaseAtInsertion", Math.Round(atInsertion, 3));
            ctx.Record("phaseAtPlannedInsertion", Math.Round(atPlanned, 3));
            ctx.Record("relativeInclination", Math.Round(planeAngle, 3));
            ctx.Record("orbit", Describe(av.orbit));
            yield return Snap.Shot(ctx, "orbit");
            if (Timelapse.Active)
            {
                CloseMjWindows(core, false);
                Timelapse.AimAt = sat; // from here on the camera looks at the target
                Caption($"In orbit, {distanceKm:F1} km from the target",
                    $"Same plane, {atInsertion:F2}° behind it, {insertionUT - planner.LiftoffTime:F0} s after liftoff", 0.5f);
                yield return Wait.Seconds(4);
            }

            // the budget for closing in on the target with RCS: the cheapest two-burn rendezvous from here, and what the
            // vessel's RCS can do
            var rdv = CheapestRendezvous(av.orbit, sat.orbit, Planetarium.GetUniversalTime(), ctx.ParamDouble("rdvMaxRevs", 3));
            double rcsDv = RcsDeltaV(av);
            Log.Info($"cheapest two-burn rendezvous from insertion: {rdv.dv:F2} m/s ({rdv.dv1:F2} + {rdv.dv2:F2}) arriving after " +
                $"{rdv.tof:F0} s ({rdv.tof / sat.orbit.period:F2} target orbits, nrev {rdv.nrev}); RCS on board {rcsDv:F1} m/s");
            ctx.Record("rendezvousDv", Math.Round(rdv.dv, 2));
            ctx.Record("rendezvousDv1", Math.Round(rdv.dv1, 2));
            ctx.Record("rendezvousDv2", Math.Round(rdv.dv2, 2));
            ctx.Record("rendezvousTofSec", Math.Round(rdv.tof, 0));
            ctx.Record("rcsDvOnBoard", Math.Round(rcsDv, 1));

            core.Ascent.Users.Remove(core.GetComputerModule<MechJebModuleAscentMenu>());
            Assert.True(av.orbit.PeA > 0, "periapsis above the surface");
            double tol = ctx.ParamDouble("tolDeg", 2);
            Assert.Near(expected, atPlanned, tol, "phase angle at the planned insertion time");
            Assert.Near(0, planeAngle, ctx.ParamDouble("planeTolDeg", 0.5), "angle between the vessel's and the target's orbital planes");
            double maxRdvDv = ctx.ParamDouble("maxRdvDv", 20);
            Assert.True(rdv.dv < maxRdvDv, $"the cheapest rendezvous from insertion needs under {maxRdvDv:F0} m/s (got {rdv.dv:F1})");

            // by default that is the test: the launch put the vessel where a cheap rendezvous starts
            if (ctx.Param("rdv") != "rcs" && ctx.Param("rendezvous") == null)
                yield break;

            ctx.Step("MechJeb Hohmann transfer to the target");
            yield return HohmannRendezvous(ctx, core, sat, ctx.Param("rendezvous") == "execute");

            if (ctx.Param("rdv") == "rcs")
            {
                ctx.Step("Stage to the payload");
                Caption("The payload separates", "From here on, only its small maneuvering thruster");
                yield return StageToPayload(ctx, (int)ctx.ParamDouble("payloadStage", 0));
                ctx.Step(ctx.Param("rdvThrust") == "rcs" ? "Rendezvous on RCS alone" : "Rendezvous on the payload's thruster");
                yield return RcsRendezvous(ctx, Mj.Core, sat);
            }
        }

        /// <summary>
        /// Stages until the vessel's current stage is payloadStage (default 0: Wayfarer's LR87 stage and its tank ring gone,
        /// and the probe's own stage activated, which its RCS needs, at zero throttle so its kick motor doesn't fire).
        /// Records what is left.
        /// </summary>
        private static IEnumerator StageToPayload(TestContext ctx, int payloadStage)
        {
            Flight.WarpOff();
            Ctrl.SetThrottle(0);
            while (FlightGlobals.ActiveVessel.currentStage > payloadStage)
            {
                int before = FlightGlobals.ActiveVessel.currentStage;
                Ctrl.Stage();
                yield return Wait.Until(() => FlightGlobals.ActiveVessel.currentStage < before, 10, "stage " + (before - 1), shot: false);
                yield return Wait.Seconds(2); // let the decoupled parts drift off
            }

            Vessel v = FlightGlobals.ActiveVessel;
            double thrust = 0;
            foreach (var e in v.FindPartModulesImplementing<ModuleEngines>()) thrust += e.finalThrust;
            var rcs = v.FindPartModulesImplementing<ModuleRCS>();
            int rcsActive = rcs.FindAll(r => r.rcsEnabled && r.isEnabled).Count;
            Log.Info($"payload: '{v.vesselName}' {v.parts.Count} parts {v.totalMass:F3} t, stage {v.currentStage}, " +
                $"RCS {rcsActive}/{rcs.Count} enabled, {RcsDeltaV(v):F2} m/s, engine thrust {thrust:F1} kN, engines: {Engines()}");
            ctx.Record("payloadParts", v.parts.Count);
            ctx.Record("payloadMassT", Math.Round(v.totalMass, 3));
            ctx.Record("payloadRcsDv", Math.Round(RcsDeltaV(v), 2));
            ctx.Record("payloadRcsEnabled", $"{rcsActive}/{rcs.Count}");
            Assert.True(thrust == 0, "no engine thrusting on the payload");
            Assert.True(rcsActive > 0, "payload RCS enabled");
            yield return Snap.Shot(ctx, "payload");
        }

        /// <summary>
        /// Closes in on the target on RCS: the cheapest Lambert arc (up to rdvMaxRevs target orbits) to a point holdBackM
        /// behind the target, flown with MJ's node executor in RCS-only mode, a mid-course correction halfway, the arrival
        /// burn matching the target's velocity, then MJ's rendezvous autopilot (RCS only, closing speed capped) to finish
        /// within holdBackM. Records the RCS delta-v spent, the final distance and relative speed, and the time it took.
        /// </summary>
        private static IEnumerator RcsRendezvous(TestContext ctx, MechJebCore core, Vessel sat)
        {
            double holdBack = ctx.ParamDouble("holdBackM", 100);
            double maxRevs = ctx.ParamDouble("rdvMaxRevs", 3);
            double start = Planetarium.GetUniversalTime();
            double rcsStart = RcsDeltaV(FlightGlobals.ActiveVessel);

            Ctrl.Sas(false);
            Ctrl.Rcs(ctx.Param("rdvThrust") == "rcs"); // the payload's reaction wheel holds attitude; MJ turns RCS on for ullage if needed
            core.Node.RCSOnly = ctx.Param("rdvThrust") == "rcs"; // else the payload's own thruster, RCS for attitude and ullage
            core.Node.Autowarp = true;

            // burn 1 a minute from now (time to turn), arrival on the cheapest arc
            double ut0 = start + 60;
            var arc = RendezvousArc(FlightGlobals.ActiveVessel.orbit, sat.orbit, ut0, maxRevs, holdBack, double.NaN);
            double arrival = ut0 + arc.tof;
            Log.Info($"RCS rendezvous: burn 1 {arc.dv1:F2} m/s, arrive {arc.tof:F0} s later ({arc.tof / sat.orbit.period:F2} orbits) " +
                $"{holdBack:F0} m behind the target, arrival burn {arc.dv2:F2} m/s");
            ctx.Record("rcsPlanDv1", Math.Round(arc.dv1, 3));
            ctx.Record("rcsPlanDv2", Math.Round(arc.dv2, 3));
            ctx.Record("rcsPlanTofSec", Math.Round(arc.tof, 0));
            double massStart = FlightGlobals.ActiveVessel.totalMass;
            Caption($"Burn 1: {arc.dv1:F2} m/s",
                $"Onto an arc that arrives {holdBack:F0} m behind the target {arc.tof / sat.orbit.period:F1} orbits from now", 0.25f);
            yield return PlaceAndFly(ctx, core, sat, new[] { (arc.burn1, ut0) }, "burn 1");
            Caption("Coasting", "Camera on the target: the gap closes over one orbit", 0.5f);

            // mid-course: re-aim at the same arrival point and time from where the first burn actually left us
            double mid = (Planetarium.GetUniversalTime() + arrival) / 2;
            if (arrival - mid > 600)
            {
                yield return CoastTo(core, mid - 120);
                double utc = Planetarium.GetUniversalTime() + 60;
                var corr = RendezvousArc(FlightGlobals.ActiveVessel.orbit, sat.orbit, utc, maxRevs, holdBack, arrival);
                Log.Info($"RCS rendezvous: mid-course correction {corr.dv1:F3} m/s, arrival burn now {corr.dv2:F2} m/s");
                ctx.Record("rcsMidCourseDv", Math.Round(corr.dv1, 3));
                Caption($"Mid-course correction: {corr.dv1:F2} m/s", "Re-aimed at the same point and time, from where the first burn actually left us");
                yield return PlaceAndFly(ctx, core, sat, new[] { (corr.burn1, utc) }, "mid-course correction");
                Caption("Coasting", "Waiting for the arrival point");
            }

            // arrival: match the target's velocity, recomputed from the vessel's actual state
            {
                double uta = arrival;
                Orbit o = FlightGlobals.ActiveVessel.orbit;
                Vector3d dv = (sat.orbit.getOrbitalVelocityAtUT(uta) - o.getOrbitalVelocityAtUT(uta)).xzy;
                yield return PlaceAndFly(ctx, core, sat, new[] { (dv, uta) }, "arrival burn");
            }

            // finish with MJ's rendezvous autopilot: stop within holdBack at a closing speed RCS can afford
            var rz = core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
            rz.desiredDistance.Val = holdBack;
            rz.maxClosingSpeed.Val = ctx.ParamDouble("maxClosingSpeed", 1);
            rz.Users.Add(User);
            Caption("Final approach: MechJeb's rendezvous autopilot", $"Closing at under {rz.maxClosingSpeed.Val:F0} m/s to {holdBack:F0} m, then matching velocity", 0.25f);
            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"rendezvous autopilot: {rz.status} | target {core.Target.Distance:F0} m, rel {core.Target.RelativeVelocity.magnitude:F2} m/s, " +
                        $"RCS left {RcsDeltaV(FlightGlobals.ActiveVessel):F2} m/s");
                }
                return !rz.Enabled;
            }, ctx.ParamDouble("finalTimeoutSec", 900), "MJ rendezvous autopilot done");
            core.Node.RCSOnly = false;

            Vessel v = FlightGlobals.ActiveVessel;
            double dist = core.Target.Distance, rel = core.Target.RelativeVelocity.magnitude;
            double used = rcsStart - RcsDeltaV(v);
            Log.Info($"RCS rendezvous done: {dist:F1} m from the target at {rel:F3} m/s, {used:F2} m/s of RCS used, " +
                $"{(Planetarium.GetUniversalTime() - start) / 60:F1} min after insertion; autopilot '{rz.status}'");
            ctx.Record("finalDistanceM", Math.Round(dist, 1));
            ctx.Record("finalRelSpeed", Math.Round(rel, 3));
            ctx.Record("rcsDvUsed", Math.Round(used, 2));
            ctx.Record("rcsDvLeft", Math.Round(RcsDeltaV(v), 2));
            ctx.Record("rendezvousMinutes", Math.Round((Planetarium.GetUniversalTime() - start) / 60, 1));
            double propKg = (massStart - v.totalMass) * 1000;
            ctx.Record("rendezvousPropellantKg", Math.Round(propKg, 2));
            if (Timelapse.Active)
            {
                Caption($"Rendezvous: {dist:F0} m from the target at {rel:F2} m/s",
                    $"{(Planetarium.GetUniversalTime() - liftoffUT) / 60:F0} min after liftoff, {propKg:F1} kg of the payload's propellant");
                yield return Wait.Seconds(6);
                Timelapse.End();
            }
            yield return Snap.Shot(ctx, "rendezvous");
            Assert.True(dist < holdBack * 2 && rel < 0.5, $"within {holdBack * 2:F0} m at under 0.5 m/s (got {dist:F0} m, {rel:F2} m/s)");
        }

        // warps to ut with the throttle at zero (a lit engine with the throttle up keeps KSP from rails warp), logging the
        // rate KSP actually gives
        private static IEnumerator CoastTo(MechJebCore core, double ut)
        {
            core.Thrust.ThrustOff();
            Ctrl.SetThrottle(0);
            yield return Wait.Seconds(1);
            TimeWarp.fetch.WarpTo(ut);
            yield return Wait.Seconds(3);
            Vessel v = FlightGlobals.ActiveVessel;
            Log.Info($"coasting to UT {ut:F0}: warp x{TimeWarp.CurrentRate} (index {TimeWarp.CurrentRateIndex}), throttle {v.ctrlState.mainThrottle:F2}, " +
                $"g {v.geeForce:F3}, engines {Engines()}, packed {v.packed}");
            yield return Wait.Until(() => Planetarium.GetUniversalTime() >= ut - 1 && TimeWarp.CurrentRateIndex == 0, 1800, "coast to UT " + ut.ToString("F0"));
        }

        // places the nodes (world-frame delta-v, UT) and has MJ's node executor fly them
        private static IEnumerator PlaceAndFly(TestContext ctx, MechJebCore core, Vessel sat, (Vector3d dv, double ut)[] nodes, string what)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            double rcsBefore = RcsDeltaV(v), massBefore = v.totalMass;
            v.RemoveAllManeuverNodes();
            foreach (var n in nodes) v.PlaceManeuverNode(v.orbit, n.dv, n.ut);
            core.Node.ExecuteAllNodes(User);
            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"{what}: T-{nodes[0].ut - Planetarium.GetUniversalTime():F0}s, target {core.Target.Distance / 1000:F2} km, " +
                        $"RCS left {RcsDeltaV(FlightGlobals.ActiveVessel):F2} m/s");
                }
                // time lapse: denser frames once the target is close enough to see
                if (Timelapse.Active && Timelapse.Title == "Coasting" && core.Target.Distance < 2000)
                {
                    Timelapse.IntervalSec = 0.25f;
                    Caption("Arriving", "The arrival burn will match the target's velocity");
                }
                return !core.Node.Enabled;
            }, 900, what + " flown by MJ");
            core.Thrust.ThrustOff();
            Ctrl.SetThrottle(0);
            Flight.WarpOff();
            double used = rcsBefore - RcsDeltaV(FlightGlobals.ActiveVessel), propKg = (massBefore - FlightGlobals.ActiveVessel.totalMass) * 1000;
            Log.Info($"{what} done: {propKg:F2} kg of propellant ({used:F3} m/s of RCS reserve) for {nodes[0].dv.magnitude:F3} m/s planned; target {core.Target.Distance / 1000:F3} km, " +
                $"rel {core.Target.RelativeVelocity.magnitude:F3} m/s");
            if (nodes[0].dv.magnitude > 0.05)
                Assert.True(propKg > 0.001, $"{what} burned propellant ({propKg:F3} kg for {nodes[0].dv.magnitude:F3} m/s)");
        }

        /// <summary>
        /// Lambert arc from the vessel at ut0 to a point holdBackM behind the target (against its velocity) on arrival: the
        /// cheapest over flight times up to maxRevs target orbits, or for the given arrival time if not NaN. burn1 is in
        /// KSP's world frame, for a maneuver node.
        /// </summary>
        private static (Vector3d burn1, double dv1, double dv2, double tof) RendezvousArc(Orbit vessel, Orbit target, double ut0, double maxRevs,
            double holdBackM, double arrivalUT)
        {
            double mu = vessel.referenceBody.gravParameter;
            var r1 = V3Of(vessel.getRelativePositionAtUT(ut0));
            var v1 = V3Of(vessel.getOrbitalVelocityAtUT(ut0));
            var h = MechJebLib.Primitives.V3.Cross(r1, v1);
            double period = target.period;
            var best = (dv: double.MaxValue, burn1: Vector3d.zero, dv1: double.NaN, dv2: double.NaN, tof: double.NaN);

            double from = double.IsNaN(arrivalUT) ? 0.25 * period : arrivalUT - ut0;
            double to = double.IsNaN(arrivalUT) ? maxRevs * period : arrivalUT - ut0;
            for (double tof = from; tof <= to + 1e-6; tof += 20)
            {
                Vector3d vt = target.getOrbitalVelocityAtUT(ut0 + tof);
                var r2 = V3Of(target.getRelativePositionAtUT(ut0 + tof) - vt.normalized * holdBackM);
                var v2 = V3Of(vt);
                int maxRev = (int)(tof / period) + 1;
                for (int n = -maxRev; n <= maxRev; n++)
                {
                    try
                    {
                        (var a, var b) = MechJebLib.Maths.Izzo.Solve(mu, r1, r2, tof, MechJebLib.Lambert.TransferGeometry.Prograde, n, h);
                        double d1 = (a - v1).magnitude, d2 = (v2 - b).magnitude;
                        if (!double.IsNaN(d1 + d2) && d1 + d2 < best.dv)
                        {
                            var burn = a - v1;
                            best = (d1 + d2, new Vector3d(burn.x, burn.y, burn.z).xzy, d1, d2, tof);
                        }
                    }
                    catch (Exception) { } // no arc with that many revolutions for this flight time
                }
            }
            if (best.dv == double.MaxValue) throw new UITestFailure("no Lambert arc to the target");
            return (best.burn1, best.dv1, best.dv2, best.tof);
        }

        /// <summary>
        /// The maneuver planner's bi-impulsive (Hohmann) transfer to the target with its defaults (rendezvous, capture, not
        /// coplanar, no lag): records both burns and the wait for the first. With execute, MJ's node executor flies them and
        /// the distance and relative speed to the target after the second are recorded.
        /// </summary>
        private static IEnumerator HohmannRendezvous(TestContext ctx, MechJebCore core, Vessel sat, bool execute)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            double now = Planetarium.GetUniversalTime();
            Vector3d dV1, dV2;
            double ut1, ut2;
            try
            {
                (dV1, ut1, dV2, ut2) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(v.orbit, sat.orbit, now, 0, false, false, true, true);
            }
            catch (Exception e)
            {
                // e.g. inserted into the target's own orbit: nothing for a Hohmann transfer to do
                Log.Warn("MJ Hohmann transfer failed: " + e.Message);
                ctx.Record("hohmannError", e.Message);
                yield break;
            }

            Log.Info($"MJ Hohmann transfer: first burn {dV1.magnitude:F2} m/s in {ut1 - now:F0} s, second {dV2.magnitude:F2} m/s " +
                $"{ut2 - ut1:F0} s later, total {dV1.magnitude + dV2.magnitude:F2} m/s");
            ctx.Record("hohmannWaitSec", Math.Round(ut1 - now, 1));
            ctx.Record("hohmannDv1", Math.Round(dV1.magnitude, 2));
            ctx.Record("hohmannDv2", Math.Round(dV2.magnitude, 2));
            ctx.Record("hohmannDvTotal", Math.Round(dV1.magnitude + dV2.magnitude, 2));
            ctx.Record("hohmannTransferSec", Math.Round(ut2 - ut1, 1));
            if (!execute) yield break;

            v.RemoveAllManeuverNodes();
            v.PlaceManeuverNode(v.orbit, dV1, ut1);
            v.PlaceManeuverNode(v.orbit, dV2, ut2);
            core.Node.Autowarp = true;
            core.Node.ExecuteAllNodes(User);
            yield return Snap.Shot(ctx, "hohmann-nodes");

            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    double d = (FlightGlobals.ActiveVessel.GetWorldPos3D() - sat.GetWorldPos3D()).magnitude;
                    Log.Info($"rendezvous: {FlightGlobals.ActiveVessel.patchedConicSolver.maneuverNodes.Count} nodes left, " +
                        $"T-{ut1 - Planetarium.GetUniversalTime():F0}s to burn 1, target {d / 1000:F2} km | engines {Engines()}");
                }
                return !core.Node.Enabled;
            }, 900, "MJ node executor done with both burns");
            Flight.WarpOff();
            yield return Wait.Seconds(1);

            Vessel av = FlightGlobals.ActiveVessel;
            double distKm = (av.GetWorldPos3D() - sat.GetWorldPos3D()).magnitude / 1000;
            double relSpeed = (av.obt_velocity - sat.obt_velocity).magnitude;
            Log.Info($"after the transfer: target {distKm:F3} km away, relative speed {relSpeed:F2} m/s, " +
                $"{av.patchedConicSolver.maneuverNodes.Count} nodes left; {Describe(av.orbit)}");
            ctx.Record("afterTransferDistanceKm", Math.Round(distKm, 3));
            ctx.Record("afterTransferRelSpeed", Math.Round(relSpeed, 2));
            ctx.Record("afterTransferOrbit", Describe(av.orbit));
            yield return Snap.Shot(ctx, "after-transfer");
        }

        private static void SpawnSatellite(string name, CelestialBody body, double altM, double incDeg, double lanDeg, double argLatDeg)
        {
            string partName = null;
            foreach (string candidate in new[] { "probeCoreOcto.v2", "probeCoreOcto", "probeCoreSphere.v2", "probeCoreSphere" })
                if (PartLoader.getPartInfoByName(candidate) != null) { partName = candidate; break; }
            if (partName == null) throw new UITestFailure("no stock probe core part found to spawn");

            var orbit = new Orbit(incDeg, 0, body.Radius + altM, lanDeg, 0, argLatDeg * Math.PI / 180, Planetarium.GetUniversalTime(), body);
            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode[] parts = { ProtoVessel.CreatePartNode(partName, flightId) };
            ConfigNode[] extra = { ProtoVessel.CreateDiscoveryNode(DiscoveryLevels.Owned, UntrackedObjectClass.A, double.PositiveInfinity, double.PositiveInfinity) };
            ConfigNode node = ProtoVessel.CreateVesselNode(name, VesselType.Probe, orbit, 0, parts, extra);
            HighLogic.CurrentGame.AddVessel(node);
            Log.Info($"spawned '{name}' ({partName}) in a {altM / 1000:F0} km orbit, inc {incDeg} LAN {lanDeg} arg of latitude {argLatDeg}");
        }

        // puts a vessel on rails (an existing target, say a station) into a circular orbit
        private static void MoveToOrbit(Vessel v, double altM, double incDeg, double lanDeg, double argLatDeg)
        {
            if (!v.packed) throw new UITestFailure($"'{v.vesselName}' is loaded; only a vessel on rails can be moved");
            CelestialBody body = v.mainBody;
            var o = new Orbit(incDeg, 0, body.Radius + altM, lanDeg, 0, argLatDeg * Math.PI / 180, Planetarium.GetUniversalTime(), body);
            v.orbitDriver.orbit.SetOrbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis,
                o.meanAnomalyAtEpoch, o.epoch, body);
            v.orbitDriver.updateFromParameters();
            Log.Info($"moved '{v.vesselName}' to a {altM / 1000:F0} km orbit, inc {incDeg} LAN {lanDeg} arg of latitude {argLatDeg}");
        }

        // body-relative state in a right-handed frame (KSP's world axes with y and z swapped)
        private static Vector3d R(Orbit o, double ut) => o.getRelativePositionAtUT(ut).xzy;
        private static Vector3d V(Orbit o, double ut) => o.getOrbitalVelocityAtUT(ut).xzy;
        private static Vector3d Normal(Orbit o, double ut) => Vector3d.Cross(R(o, ut), V(o, ut)).normalized;

        /// <summary>Angle the target is ahead of the vessel, in the target's orbital plane, degrees in (-180, 180].</summary>
        private static double PhaseDeg(Orbit vessel, Orbit target, double ut)
        {
            Vector3d h = Normal(target, ut);
            Vector3d rv = Vector3d.Exclude(h, R(vessel, ut)).normalized;
            Vector3d rt = Vector3d.Exclude(h, R(target, ut)).normalized;
            double sin = Vector3d.Dot(Vector3d.Cross(rv, rt), h);
            double cos = Vector3d.Dot(rv, rt);
            return Math.Atan2(sin, cos) * 180 / Math.PI;
        }

        /// <summary>
        /// Closest approach (km) to the target of the transfer ellipse an impulsive Hohmann burn at ut puts the vessel on:
        /// horizontal, prograde, apoapsis at the target's semi-major axis. Sampled over 1.25x the transfer time.
        /// </summary>
        // KSP's orbit frame (y/z swapped from the world) is right-handed, as MechJebLib's V3 expects
        private static MechJebLib.Primitives.V3 V3Of(Vector3d v) => new MechJebLib.Primitives.V3(v.x, v.y, v.z);

        /// <summary>
        /// Cheapest two-impulse rendezvous with the target starting at ut: Lambert arcs (prograde, multi-revolution
        /// included) from the vessel to where the target will be, for flight times from a quarter of the target's period to
        /// maxRevs of them in 20 s steps. Burn 1 is the arc's departure minus the vessel's velocity, burn 2 the target's
        /// velocity minus the arc's arrival.
        /// </summary>
        private static (double dv, double dv1, double dv2, double tof, int nrev) CheapestRendezvous(Orbit vessel, Orbit target, double ut, double maxRevs)
        {
            double mu = vessel.referenceBody.gravParameter;
            var r1 = V3Of(vessel.getRelativePositionAtUT(ut));
            var v1 = V3Of(vessel.getOrbitalVelocityAtUT(ut));
            var h = MechJebLib.Primitives.V3.Cross(r1, v1);
            double period = target.period;
            var best = (dv: double.MaxValue, dv1: double.NaN, dv2: double.NaN, tof: double.NaN, nrev: 0);

            for (double tof = 0.25 * period; tof <= maxRevs * period; tof += 20)
            {
                var r2 = V3Of(target.getRelativePositionAtUT(ut + tof));
                var v2 = V3Of(target.getOrbitalVelocityAtUT(ut + tof));
                int maxRev = (int)(tof / period) + 1;
                for (int n = -maxRev; n <= maxRev; n++)
                {
                    try
                    {
                        (var a, var b) = MechJebLib.Maths.Izzo.Solve(mu, r1, r2, tof, MechJebLib.Lambert.TransferGeometry.Prograde, n, h);
                        double d1 = (a - v1).magnitude, d2 = (v2 - b).magnitude;
                        if (!double.IsNaN(d1 + d2) && d1 + d2 < best.dv) best = (d1 + d2, d1, d2, tof, n);
                    }
                    catch (Exception) { } // no arc with that many revolutions for this flight time
                }
            }
            return best;
        }

        /// <summary>
        /// Vacuum delta-v of the vessel's RCS on all the propellant it uses (an overestimate where RCS shares tanks with an
        /// engine).
        /// </summary>
        private static double RcsDeltaV(Vessel v)
        {
            double isp = 0, propMass = 0;
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var rcs in v.FindPartModulesImplementing<ModuleRCS>())
            {
                isp = Math.Max(isp, rcs.atmosphereCurve.Evaluate(0f));
                foreach (var p in rcs.propellants) names.Add(p.name);
            }
            foreach (var part in v.parts)
                foreach (PartResource r in part.Resources)
                    if (names.Contains(r.resourceName)) propMass += r.amount * r.info.density;
            double m = v.totalMass;
            return isp > 0 && propMass > 0 && propMass < m ? isp * 9.80665 * Math.Log(m / (m - propMass)) : 0;
        }

        private static double HohmannClosestApproachKm(Orbit vessel, Orbit target, double ut, out double minUT)
        {
            // Orbit's own (y/z swapped) frame both ways, which is what UpdateFromStateVectors takes
            Vector3d r = vessel.getRelativePositionAtUT(ut), v = vessel.getOrbitalVelocityAtUT(ut);
            double mu = vessel.referenceBody.gravParameter, r1 = r.magnitude, a = (r1 + target.semiMajorAxis) / 2;
            Vector3d horizontal = (v - r * (Vector3d.Dot(r, v) / r.sqrMagnitude)).normalized;
            var transfer = new Orbit();
            transfer.UpdateFromStateVectors(r, horizontal * Math.Sqrt(mu * (2 / r1 - 1 / a)), vessel.referenceBody, ut);

            double transferTime = Math.PI * Math.Sqrt(a * a * a / mu);
            double min = double.MaxValue;
            minUT = ut;
            for (double t = ut; t <= ut + 1.25 * transferTime; t += 1)
            {
                double d = (transfer.getRelativePositionAtUT(t) - target.getRelativePositionAtUT(t)).magnitude;
                if (d < min) { min = d; minUT = t; }
            }
            return min / 1000;
        }

        // the ignited engines and their thrust (kN), "FLAMEOUT" for a dead one: a stage that loses an engine shows up here
        private static string Engines()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var e in FlightGlobals.ActiveVessel.FindPartModulesImplementing<ModuleEngines>())
                if (e.EngineIgnited || e.flameout)
                    parts.Add($"{e.part.partInfo.name}:{(e.flameout ? "FLAMEOUT" : e.finalThrust.ToString("F0"))}");
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        private static string Describe(Orbit o) =>
            $"Pe {o.PeA / 1000:F1} km Ap {o.ApA / 1000:F1} km inc {o.inclination:F3} LAN {o.LAN:F3}";
    }
}
