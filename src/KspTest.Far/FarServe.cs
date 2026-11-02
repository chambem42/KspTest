using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using UnityEngine;

namespace KspTest
{
    /// <summary>FAR's Serve requests (<see cref="ServeSession" />).</summary>
    public static class FarServe
    {
        [ServeCommand("far", "mach=0.3 [aoas=0,2,4] [pitches=0] [alt=3000]: FAR's editor sim per point; slopes (per rad, zero input), static margin (of FAR's MAC), FAR's area and MAC, mass, CoM")]
        public static IEnumerator Far(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return Wait.Until(FarSim.Ready, 60, "FAR editor sim ready", shot: false);
            double mach = ServeSession.Doubles(q["mach"], "0.3")[0];
            double[] aoas = ServeSession.Doubles(q["aoas"], "0,2,4");
            double[] pitches = ServeSession.Doubles(q["pitches"], "0");
            FarSim.Instant(5, 0, 0, mach, 0, 0, false);   // the sim carries state between calls: settle it first
            var rows = new List<object>();
            var a0 = new List<double>();
            var cl0 = new List<double>();
            var cm0 = new List<double>();
            foreach (double pitch in pitches)
                foreach (double aoa in aoas)
                {
                    var o = FarSim.Instant(aoa, 0, 0, mach, pitch, 0, false);
                    rows.Add(new Dictionary<string, object> { ["aoaDeg"] = aoa, ["pitchInput"] = pitch, ["Cl"] = o["Cl"], ["Cd"] = o["Cd"], ["Cm"] = o["Cm"] });
                    if (pitch == 0) { a0.Add(aoa * Math.PI / 180); cl0.Add(o["Cl"]); cm0.Add(o["Cm"]); }
                }
            answer["rows"] = rows;
            if (a0.Count >= 2)
            {
                double cla = ServeSession.Slope(a0, cl0), cma = ServeSession.Slope(a0, cm0);
                answer["CLa"] = cla;
                answer["Cma"] = cma;
                answer["margin"] = -cma / cla;
            }
            try
            {
                var d = FarSim.StabilityDerivs(FlightGlobals.GetHomeBody(), double.Parse(q["alt"] ?? "3000", CultureInfo.InvariantCulture), mach, 0, false);
                answer["area"] = d["area"];
                answer["MAC"] = d["MAC"];
            }
            catch (Exception e) { answer["refError"] = e.Message; }
            ServeSession.MassAndCom(ServeSession.ShipParts(), out double mass, out Vector3 com);
            answer["massT"] = mass;
            answer["com"] = ServeSession.Vec(com);
        }
    }
}
