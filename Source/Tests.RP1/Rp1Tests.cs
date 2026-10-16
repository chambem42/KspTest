using System;
using System.Collections;
using System.Globalization;
using KSPUITest;
using KSPUITest.RP1;
using RP0;

namespace KSPUITest.Tests.RP1
{
    /// <summary>RP-1 / KCT tests. Params: craft, peKm, apKm, altKm, inc, ut, entrySpeed, entryAltKm.</summary>
    public static class Rp1Tests
    {
        [UITest("Kct_SimOnPad", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "UITest_Sounding")]
        public static IEnumerator Kct_SimOnPad(TestContext ctx)
        {
            Ugui.DismissPopups();   // first-run / reminder dialogs
            ctx.Step("Simulate on the pad via Launch()");
            yield return Nav.Launch(ctx);
            Assert.True(Rp1.IsSimulatedFlight, "IsSimulatedFlight");
            ctx.Record("situation", FlightGlobals.ActiveVessel.situation.ToString());
            yield return Snap.Shot(ctx, "sim-pad");
            ctx.Step("Revert to editor via the Simulation window");
            yield return Rp1.RevertToEditorViaWindow();
            Assert.True(HighLogic.LoadedSceneIsEditor, "back in the editor");
        }

        [UITest("Kct_SimConfig_Circular", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "UITest_Sounding")]
        public static IEnumerator Kct_SimConfig_Circular(TestContext ctx)
        {
            double altKm = ctx.ParamDouble("altKm", 300);
            double inc = ctx.ParamDouble("inc", 28.5);
            Ugui.DismissPopups();   // first-run / reminder dialogs

            ctx.Step("Open the simulation dialog");
            yield return Rp1.OpenSimConfig();
            yield return Snap.Shot(ctx, "sim-config");

            ctx.Step("Configure a circular orbit");
            var spec = SimSpec.Circular(altKm, inc);
            if (ctx.Param("ut") != null) spec.UT = ctx.ParamDouble("ut", 0);   // absolute sim start UT
            yield return Rp1.ConfigureSim(spec);
            yield return Snap.Shot(ctx, "sim-config-filled");

            ctx.Step("Simulate");
            yield return Rp1.StartSim();
            var o = FlightGlobals.ActiveVessel.orbit;
            ctx.Record("apKm", Math.Round(o.ApA / 1000, 1));
            ctx.Record("peKm", Math.Round(o.PeA / 1000, 1));
            ctx.Record("inc", Math.Round(o.inclination, 2));
            Assert.Equal("Circular", Rp1.SimMode, "SimOrbitMode");
            Assert.Near(altKm, o.ApA / 1000, altKm * 0.02 + 1, "apoapsis km");
            Assert.Near(altKm, o.PeA / 1000, altKm * 0.02 + 1, "periapsis km");
            Assert.Near(inc, o.inclination, 0.5, "inclination");
            yield return Snap.Shot(ctx, "sim-orbit");

            ctx.Step("Revert to editor");
            yield return Rp1.RevertToEditorViaWindow();
            Assert.True(HighLogic.LoadedSceneIsEditor, "back in the editor");
            Assert.False(Rp1.IsSimulatedFlight, "IsSimulatedFlight cleared");
        }

        [UITest("Kct_SimConfig_Reentry", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "UITest_Capsule", TimeoutSec = 900)]
        public static IEnumerator Kct_SimConfig_Reentry(TestContext ctx)
        {
            double peKm = ctx.ParamDouble("peKm", 40);
            double entrySpeed = ctx.ParamDouble("entrySpeed", 8000);   // Earth, 40 km Pe: the dialog rejects anything under ~7830
            double? entryAltKm = ctx.Param("entryAltKm") != null ? ctx.ParamDouble("entryAltKm", 140) : (double?)null;
            double settleSec = ctx.ParamDouble("settleSec", 20);
            Ugui.DismissPopups();   // first-run / reminder dialogs

            ctx.Step("Configure a reentry simulation");
            yield return Rp1.ConfigureSim(SimSpec.Reentry(peKm, entrySpeed, entryAltKm, ctx.Param("attitude", "Retrograde")));
            Assert.True(Imgui.IsVisible("Entry:", Rp1.SimConfigWindow), "dialog shows the entry preview line");
            yield return Snap.Shot(ctx, "reentry-config");

            ctx.Step("Simulate");
            yield return Rp1.StartSim();
            var p = Rp1.SimParams;
            Assert.Equal("Reentry", Rp1.SimMode, "SimOrbitMode");
            double simEntrySpeed = Convert.ToDouble(Reflect.Get(p, "SimEntrySpeed"), CultureInfo.InvariantCulture);
            ctx.Record("simEntrySpeed", simEntrySpeed);
            Assert.Near(entrySpeed, simEntrySpeed, 1, "SimEntrySpeed parsed from the dialog");
            var v = FlightGlobals.ActiveVessel;
            ctx.Record("spawnAltKm", Math.Round(v.altitude / 1000, 1));
            ctx.Record("peKm", Math.Round(v.orbit.PeA / 1000, 1));
            Assert.Near(peKm, v.orbit.PeA / 1000, 2, "trajectory periapsis km");
            Assert.True(v.orbit.PeA < v.altitude, "vessel is above periapsis (inbound)");
            yield return Snap.Shot(ctx, "reentry-spawn");

            ctx.Step("Fly the entry and check the telemetry log");
            yield return Wait.GameSeconds(settleSec);
            Flight.AssertAlive();
            string log = Rp1.NewestReentryLog();
            Assert.NotNull(log, "RP-1_ReentryLogs csv written");
            var lines = Rp1.ReadLines(log);
            ctx.Record("telemetryRows", lines.Length - 1);
            Assert.True(lines.Length > 5, $"telemetry has rows ({lines.Length - 1})");
            Assert.True(lines[0].StartsWith("ut,missionTime"), "telemetry header, got: " + lines[0]);
            ctx.Record("altAfterSettle", Math.Round(v.altitude / 1000, 1));
            yield return Snap.Shot(ctx, "reentry-flying");
        }

        // Defaults are a user-reported CTD: Earth->Mars from a KAC alarm, Inc/LAN/Time to Pe blank.
        [UITest("Kct_SimConfig_Hyperbolic", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "UITest_Capsule", TimeoutSec = 900)]
        public static IEnumerator Kct_SimConfig_Hyperbolic(TestContext ctx)
        {
            string target = ctx.Param("target", "Mars");
            double departureUT = ctx.ParamDouble("departureUT", 1184066160);
            double settleSec = ctx.ParamDouble("settleSec", 30);
            Ugui.DismissPopups();   // first-run / reminder dialogs

            ctx.Step("Configure a hyperbolic approach");
            yield return Rp1.ConfigureSim(SimSpec.Hyperbolic(target, ctx.Param("origin", "Earth"), ctx.Param("transferTime", "17528214"),
                                                             ctx.Param("insertionDv", "2071"), ctx.Param("peKm", "200"), departureUT));
            yield return Snap.Shot(ctx, "hyperbolic-config");

            ctx.Step("Simulate");
            yield return Rp1.StartSim();
            Assert.Equal("Hyperbolic", Rp1.SimMode, "SimOrbitMode");
            var v = FlightGlobals.ActiveVessel;
            ctx.Record("body", v.mainBody.bodyName);
            ctx.Record("ecc", Math.Round(v.orbit.eccentricity, 4));
            ctx.Record("peKm", Math.Round(v.orbit.PeA / 1000, 1));
            ctx.Record("inc", Math.Round(v.orbit.inclination, 2));
            ctx.Record("altKm", Math.Round(v.altitude / 1000, 0));
            Assert.Equal(target, v.mainBody.name, "sim body");
            yield return Snap.Shot(ctx, "hyperbolic-spawn");

            ctx.Step("Coast");
            yield return Wait.GameSeconds(settleSec);
            Flight.AssertAlive();
            ctx.Record("altAfterSettleKm", Math.Round(v.altitude / 1000, 0));
            yield return Snap.Shot(ctx, "hyperbolic-coast");
        }

        [UITest("Rp1_TopWindowTabs", Requires = "RP-1", StartsAt = StartState.SpaceCenter)]
        public static IEnumerator Rp1_TopWindowTabs(TestContext ctx)
        {
            Ugui.DismissPopups();

            ctx.Step("Open the RP-1 window");
            yield return Rp1.ShowTopWindow();
            yield return Snap.Shot(ctx, "top-window");

            foreach (var tab in (ctx.Param("tabs") ?? "Budget,Tooling,Astronauts,Training,Career Log").Split(','))
            {
                ctx.Step("Tab " + tab);
                yield return Rp1.ClickTopWindowTab(tab.Trim());
                yield return Wait.Frames(3);
                yield return Snap.Shot(ctx, "tab-" + tab.Trim());
            }
            Log.Info(Imgui.Dump());
        }
    }
}
