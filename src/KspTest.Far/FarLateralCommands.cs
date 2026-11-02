using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

namespace KspTest
{
    /// <summary>FAR's editor sim over sideslip: side force, yawing and rolling moment, for directional and roll stability.</summary>
    public static class FarLateralCommands
    {
        [ServeCommand("farlat", "mach=0.1 [betas=-4,-2,0,2,4] [aoa=2]: FAR's editor sim per sideslip (deg), controls neutral: Cy, Cn, " +
                                "C_roll and every other coefficient the build reports; the slopes per rad at zero input (Cn_beta > 0 weathervanes " +
                                "into the wind, C_roll_beta < 0 is dihedral effect)")]
        public static IEnumerator FarLat(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            double D(string k, double d) => q[k] == null ? d : double.Parse(q[k], CultureInfo.InvariantCulture);
            double[] L(string k, string d) => (q[k] ?? d).Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            double mach = D("mach", 0.1), aoa = D("aoa", 2);
            double[] betas = L("betas", "-4,-2,0,2,4");
            if (!FarSim.Ready())
            {
                answer["error"] = "FAR's editor sim isn't ready (no craft, or its voxels still building)";
                yield break;
            }
            var rows = new List<object>();
            var zero = new List<(double b, Dictionary<string, double> r)>();
            foreach (double b in betas)
            {
                var r = FarSim.Instant(aoa, b, 0, mach, 0, 0, false);
                var row = new Dictionary<string, object> { ["betaDeg"] = b };
                foreach (var kv in r) row[kv.Key] = Math.Round(kv.Value, 6);
                rows.Add(row);
                zero.Add((b, r));
            }
            answer["rows"] = rows;
            if (zero.Count >= 2)
            {
                double Slope(string k)
                {
                    if (!zero[0].r.ContainsKey(k)) return double.NaN;
                    double mx = zero.Average(z => z.b), my = zero.Average(z => z.r[k]);
                    double sxy = zero.Sum(z => (z.b - mx) * (z.r[k] - my)), sxx = zero.Sum(z => (z.b - mx) * (z.b - mx));
                    return sxx > 0 ? sxy / sxx * 180 / Math.PI : double.NaN;
                }
                var slopes = new Dictionary<string, object>();
                foreach (string k in zero[0].r.Keys)
                    slopes[k] = Math.Round(Slope(k), 5);
                answer["slopesPerRad"] = slopes;
            }
        }
    }
}
