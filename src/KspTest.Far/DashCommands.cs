using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;

namespace KspTest
{
    /// <summary>A top-speed run of the editor's craft (<see cref="LevelDash" />), from a Serve session, and back to the editor.</summary>
    public static class DashCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [ServeCommand("dash", "the editor's craft launched (a KCT sim in RP-1), air-started at point=ALT:MACH (15000:2) and AoA aoa=4, full throttle, " +
                              "held level (pitch through a PD on the attitude, trimmed toward zero vertical speed; wings level; heading held) for " +
                              "holdSec=120: a sample every sampleS=5 s [t, alt m, Mach, speed m/s, AoA, pitch input, vertical speed, thrust kN, " +
                              "acceleration along the path m/s2], the speed's peak and its last 20 s trend. hyperWarp=4. heat=on keeps temperature limits. back=off stays in flight")]
        public static IEnumerator Dash(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string craftPath = Nav.LoadedCraft ?? throw new InvalidOperationException("load a craft first");
            double D(string k, double d) => q[k] != null ? double.Parse(q[k], Inv) : d;
            string[] pt = (q["point"] ?? "15000:2").Split(':');
            var o = new LevelDash.Options
            {
                AltM = double.Parse(pt[0], Inv), Mach = double.Parse(pt[1], Inv), AoADeg = D("aoa", 4), HoldSec = D("holdSec", 120),
                SampleSec = D("sampleS", 5), HyperWarp = D("hyperWarp", 4), Heat = string.Equals(q["heat"], "on", StringComparison.OrdinalIgnoreCase),
            };
            answer["craft"] = craftPath;
            answer["farBuild"] = FarSim.FarVersion();

            ctx.Step("Serve dash: launch");
            yield return Nav.EnsureState(ctx, StartState.Flight, craftPath, "Runway");
            yield return Wait.Seconds(5);
            yield return LevelDash.Run(ctx, o, answer);
            if (!string.Equals(q["back"], "off", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Step("Serve dash: back to the SPH");
                yield return Nav.EnsureState(ctx, StartState.Sph, craftPath);
                Ugui.DismissPopups();
                answer["scene"] = HighLogic.LoadedScene.ToString();
            }
        }
    }
}
