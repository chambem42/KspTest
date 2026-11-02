using System;
using System.Collections;
using System.Linq;
using KspTest;
using KspTest.RP1;

namespace KspTest.Tests.WaypointManager
{
    public static class WaypointTests
    {
        /// <summary>
        /// How often WaypointManager rebuilds its waypoint groups in flight (WaypointProbe), and what FinePrint's
        /// waypoint list holds, over holdSec of game time in a 300 km orbit.
        /// </summary>
        [UITest(Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 900)]
        public static IEnumerator Wpm_RebuildCount(TestContext ctx)
        {
            Ugui.DismissPopups();
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double holdSec = ctx.ParamDouble("holdSec", 30);

            ctx.Step($"Open {craft} and simulate a 300 km orbit");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            yield return Rp1.ConfigureSim(SimSpec.Circular(300, 28.6));
            yield return Rp1.StartSim();
            double settle = Planetarium.GetUniversalTime() + 5;
            while (Planetarium.GetUniversalTime() < settle)
                yield return null;

            ctx.Step("Count rebuilds");
            Assert.True(WaypointProbe.Start(), "WaypointManager loaded");
            int frames = 0;
            double end = Planetarium.GetUniversalTime() + holdSec;
            while (Planetarium.GetUniversalTime() < end)
            {
                ++frames;
                yield return null;
            }
            WaypointProbe.Stop();
            ctx.Record("frames", frames);
            ctx.Record("calls", WaypointProbe.Calls);
            ctx.Record("worked", WaypointProbe.Worked);
            ctx.Record("rebuilt", WaypointProbe.Rebuilt);
            ctx.Record("rebuiltPct", WaypointProbe.Worked > 0 ? Math.Round(100.0 * WaypointProbe.Rebuilt / WaypointProbe.Worked, 1) : double.NaN);
            ctx.Record("contents", WaypointProbe.Contents());
            var lines = WaypointProbe.Describe();
            for (int i = 0; i < lines.Count; i++)
                ctx.Record($"waypoint{i:D2}", lines[i]);
            ctx.Record("bodies", string.Join(", ", FlightGlobals.Bodies.Select(b => b.GetName())));
        }
    }
}
