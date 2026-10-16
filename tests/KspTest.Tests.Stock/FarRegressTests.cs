using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>
    /// A/B regression probes of FAR's editor simulation, for running the same test against two FAR builds and diffing
    /// the recorded values (compare-results.ps1). Everything the editor tabs compute is sampled directly from the sim:
    /// the instant Cl/Cd/Cm/Cy/Cn/Croll grid, the stability derivatives, the Mach and AoA sweeps, and the voxel grid
    /// and per-part areas behind them.
    /// </summary>
    public static class FarRegressTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double R(double v) => double.IsNaN(v) || double.IsInfinity(v) ? v : Math.Round(v, 6);

        /// <summary>
        /// Params: crafts ("+"-separated, default "Aeris 4A+Stearwing A300+PoofySkirt"), machs (default
        /// "0.3,0.8,1.3,2.5"), alphas (default "-5,0,5,10,20"), viscousToo (default 1: on a build with the viscous
        /// toggle, sample the sweeps a second time with it on, under the "visc." prefix), sweepMachs (default "0.6": an AoA
        /// sweep at each), sweepAlphas (default "5": a Mach sweep at each), sweepsOnly (default 0: 1 skips the instant grid
        /// and the stability derivatives). The sweeps CSV's "at" column is the sweep's fixed Mach or AoA.
        /// </summary>
        [UITest("Far_Regress_Editor", StartsAt = StartState.Sph, Craft = "Aeris 4A", TimeoutSec = 1800)]
        public static IEnumerator Far_Regress_Editor(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            string[] crafts = (ctx.Param("crafts") ?? "Aeris 4A+Stearwing A300+PoofySkirt").Split('+');
            string[] machs = (ctx.Param("machs") ?? "0.3,0.8,1.3,2.5").Split(',');
            string[] alphas = (ctx.Param("alphas") ?? "-5,0,5,10,20").Split(',');
            bool viscousToo = ctx.ParamDouble("viscousToo", 1) != 0;
            double[] sweepMachs = (ctx.Param("sweepMachs") ?? "0.6").Split(',').Select(v => double.Parse(v.Trim(), Inv)).ToArray();
            double[] sweepAlphas = (ctx.Param("sweepAlphas") ?? "5").Split(',').Select(v => double.Parse(v.Trim(), Inv)).ToArray();
            bool sweepsOnly = ctx.ParamDouble("sweepsOnly", 0) != 0;
            var grid = new StringBuilder("craft,pass,alpha,beta,phi,mach,pitch,flaps,spoilers,Cl,Cd,Cm,Cy,Cn,C_roll\n");
            var sweeps = new StringBuilder("craft,sweep,viscous,at,x,series,value\n");
            var areas = new StringBuilder(FarApi.PartAreasHeader);
            CelestialBody body = FlightGlobals.GetHomeBody();

            foreach (string craftRaw in crafts)
            {
                string craft = craftRaw.Trim();
                ctx.Step("Craft " + craft);
                yield return Nav.EnsureState(ctx, StartState.Sph, ctx.CraftPath(craft, "SPH"));
                Ugui.DismissPopups();
                yield return Wait.Until(FarSim.Ready, 60, "FAR editor sim ready", shot: false);
                yield return Wait.Seconds(8);   // voxelization settles after the craft loads
                string k = craft + ".";

                // the persisted toggle (branch only); forced off for the like-for-like pass, without touching the config
                bool? visc = FarSim.ViscousToggle(false);
                ctx.Record(k + "viscousToggleInConfig", visc.HasValue ? visc.Value.ToString() : "n/a");

                ctx.Record(k + "parts", EditorLogic.fetch.ship.parts.Count);
                Assert.True(FarApi.VoxelInfo(out double el, out Vector3 bs, out double len, out double mcs), "FAR voxel info");
                FarApi.PartAreas(EditorLogic.fetch.ship.parts, craft, areas, out double areaAll, out double areaFixed);
                FarSim.EditorRefs(out double bodyLen, out double bodyMcs, out int wings);
                ctx.Record(k + "voxel.elementSize", R(el)); ctx.Record(k + "voxel.boundsVolume", R(bs.x * bs.y * bs.z));
                ctx.Record(k + "voxel.length", R(len)); ctx.Record(k + "voxel.maxCrossSection", R(mcs));
                ctx.Record(k + "voxel.areaAll", R(areaAll)); ctx.Record(k + "voxel.areaFixed", R(areaFixed));
                ctx.Record(k + "sim.bodyLength", R(bodyLen)); ctx.Record(k + "sim.maxCrossSection", R(bodyMcs)); ctx.Record(k + "sim.wings", wings);

                // the wings' state before the first call (the CoL computation at load leaves history): per wing
                // Cl/Cd/e/stall/rawAoAmax as the instance fields hold them, summed and hashed into a few numbers
                {
                    double sCl = 0, sCd = 0, sE = 0, sStall = 0, sAoAmax = 0; int n = 0;
                    var wingRows = new StringBuilder();
                    foreach (Part p in EditorLogic.fetch.ship.parts)
                        foreach (PartModule m in p.Modules)
                        {
                            bool wing = false;
                            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType) if (t.Name == "FARWingAerodynamicModel") wing = true;
                            if (!wing) continue;
                            double cl = Convert.ToDouble(Reflect.Get(m, "Cl")), cd = Convert.ToDouble(Reflect.Get(m, "Cd")), e = Convert.ToDouble(Reflect.Get(m, "e"));
                            double st = Convert.ToDouble(Reflect.Get(m, "stall")), am = Convert.ToDouble(Reflect.Get(m, "rawAoAmax"));
                            sCl += cl; sCd += cd; sE += e; sStall += st; sAoAmax += am; ++n;
                            wingRows.Append($"{p.partInfo.name},{p.persistentId},{cl:G9},{cd:G9},{e:G9},{st:G9},{am:G9}\n");
                        }
                    ctx.Record(k + "prewing.n", n); ctx.Record(k + "prewing.sumCl", R(sCl)); ctx.Record(k + "prewing.sumCd", R(sCd));
                    ctx.Record(k + "prewing.sumE", R(sE)); ctx.Record(k + "prewing.sumStall", R(sStall)); ctx.Record(k + "prewing.sumRawAoAmax", R(sAoAmax));
                    File.WriteAllText(Path.Combine(ctx.ResultsDir, $"prewing-{craft}.csv"), "part,id,Cl,Cd,e,stall,rawAoAmax\n" + wingRows);
                }

                if (!sweepsOnly)
                {
                    ctx.Step($"{craft}: instant sim grid");
                    var cases = new List<double[]>();   // alpha, beta, phi, mach, pitch, flaps, spoilers
                    foreach (string ms in machs)
                        foreach (string a in alphas)
                            cases.Add(new[] { double.Parse(a.Trim(), Inv), 0, 0, double.Parse(ms.Trim(), Inv), 0, 0, 0 });
                    // control, sideslip, roll, flaps and spoilers at a subsonic and a supersonic point
                    foreach (double m in new[] { 0.5, 1.5 })
                    {
                        cases.Add(new[] { 5, 0, 0, m, 0.5, 0, 0 });
                        cases.Add(new[] { 5, 0, 0, m, -0.5, 0, 0 });
                        cases.Add(new[] { 5, 5, 0, m, 0, 0, 0 });
                        cases.Add(new[] { 5, 0, 10, m, 0, 0, 0 });
                        cases.Add(new[] { 5, 0, 0, m, 0, 2, 0 });
                        cases.Add(new[] { 5, 0, 0, m, 0, 0, 1 });
                    }
                    // the grid twice: the sim carries state between calls (wing interaction, stall), so a first-call
                    // transient shows up as a difference between the passes
                    for (int pass = 1; pass <= 2; ++pass)
                        foreach (double[] c in cases)
                        {
                            var o = FarSim.Instant(c[0], c[1], c[2], c[3], c[4], (int)c[5], c[6] != 0);
                            string tag = $"a{c[0]:0}b{c[1]:0}p{c[2]:0}m{c[3]:0.##}pi{c[4]:0.#}f{c[5]:0}s{c[6]:0}";
                            foreach (string f in new[] { "Cl", "Cd", "Cm", "Cy", "Cn", "C_roll" })
                                ctx.Record(k + (pass == 1 ? "inst." : "inst2.") + tag + "." + f, R(o[f]));
                            grid.Append(string.Join(",", new[] { craft, pass.ToString(Inv), c[0].ToString(Inv), c[1].ToString(Inv), c[2].ToString(Inv), c[3].ToString(Inv), c[4].ToString(Inv), c[5].ToString(Inv), c[6].ToString(Inv),
                                o["Cl"].ToString("G9", Inv), o["Cd"].ToString("G9", Inv), o["Cm"].ToString("G9", Inv), o["Cy"].ToString("G9", Inv), o["Cn"].ToString("G9", Inv), o["C_roll"].ToString("G9", Inv) })).Append('\n');
                        }

                    ctx.Step($"{craft}: stability derivatives");
                    // the middle point is evaluated twice in a row: a reference length that depends on the previous call shows as stab vs stab.R
                    foreach (var pt in new[] { new[] { 0.35, 2000.0 }, new[] { 0.8, 8000.0 }, new[] { 0.8, 8000.0, 1 }, new[] { 1.6, 12000.0 } })
                    {
                        string tag = (pt.Length > 2 ? "R." : "") + $"m{pt[0]:0.##}h{pt[1] / 1000:0}";
                        Dictionary<string, double> d = null;
                        string err = null;
                        try { d = FarSim.StabilityDerivs(body, pt[1], pt[0], 0, false); }
                        catch (Exception ex) { err = ex.InnerException?.Message ?? ex.Message; }
                        if (d == null) { ctx.Record(k + "stab." + tag + ".error", err); Log.Warn("stability derivs " + tag + ": " + err); continue; }
                        foreach (var kv in d) ctx.Record(k + "stab." + tag + "." + kv.Key, R(kv.Value));
                    }
                }

                ctx.Step($"{craft}: sweeps");
                foreach (bool viscOn in visc.HasValue && viscousToo ? new[] { false, true } : new[] { false })
                {
                    FarSim.ViscousToggle(viscOn);
                    string vk = viscOn ? "visc." : "";
                    // each sweep at every fixed value asked for; the recorded keys carry the value only when there are several
                    foreach (var (machSweep, at) in sweepAlphas.Select(a => (true, a)).Concat(sweepMachs.Select(m => (false, m))))
                    {
                        bool several = (machSweep ? sweepAlphas : sweepMachs).Length > 1;
                        string name = (machSweep ? "machSweep" : "aoaSweep") + (several ? string.Format(Inv, machSweep ? "@a{0}" : "@M{0}", at) : "");
                        bool ok = machSweep
                                      ? FarSim.Sweep(true, at, 0, 0.1, 3.0, 30, 0, false, body, 5000, out double[] x, out List<double[]> y, out List<string> names)
                                      : FarSim.Sweep(false, at, 0, -10, 30, 41, 0, false, body, 5000, out x, out y, out names);
                        Assert.True(ok, name);
                        double[] sums = new double[y.Count];
                        for (int i = 0; i < x.Length; ++i)
                        {
                            for (int s = 0; s < y.Count; ++s)
                            {
                                // the AoA sweep carries a second, reverse pass per coefficient (Cl2...) for the stall hysteresis
                                sweeps.Append(string.Join(",", new[] { craft, machSweep ? "machSweep" : "aoaSweep", viscOn ? "1" : "0", at.ToString("R", Inv), x[i].ToString("G9", Inv), names[s], y[s][i].ToString("G9", Inv) })).Append('\n');
                                if (!double.IsNaN(y[s][i]) && !double.IsInfinity(y[s][i])) sums[s] += y[s][i];
                            }
                        }
                        // every point lands in the CSV; the recorded values are the series sums and a few points
                        for (int s = 0; s < y.Count; ++s) ctx.Record(k + vk + name + ".sum." + names[s], R(sums[s]));
                        foreach (int i in new[] { 0, x.Length / 4, x.Length / 2, 3 * x.Length / 4, x.Length - 1 })
                            for (int s = 0; s < y.Count; ++s) ctx.Record(k + vk + name + $".x{x[i]:0.##}." + names[s], R(y[s][i]));
                    }
                }
                FarSim.ViscousToggle(false);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-editor-grid.csv"), grid.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-editor-sweeps.csv"), sweeps.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-editor-areas.csv"), areas.ToString());
        }

        /// <summary>
        /// What FARWingAerodynamicModel.ControlSurfaceCamber changes about how each craft flies, from FAR's editor
        /// simulation with it on and off in the same session (only the switch differs). It acts below Mach 1 only (its
        /// lift goes as 1 - M^2 and its centre shift stops at Mach 1), so the points are subsonic. Per craft, Mach and
        /// setting, from an AoA sweep at neutral pitch: the lift slope and pitching moment slope (-4..6 deg), the static
        /// margin (-dCm/dCl, of the MAC), the trim AoA (Cm = 0) and the Cl there, the pitch control power (dCm per unit
        /// input at 4 deg), Cl max and its AoA, and the best L/D; at the landing point (machs' first) also with flaps
        /// down (flapsLevel). Then FAR's stability derivatives at a low and a cruise point, with the short-period and
        /// phugoid frequency and damping estimated from them. Params: crafts ("+"-separated, default Aeris 4A+Stearwing
        /// A300+Gull+Dove), facility (SPH/VAB, default SPH), machs (default 0.2,0.5,0.8,0.95), flapsLevel (default 3),
        /// stabPoints (mach@altM, default 0.3@1000+0.7@8000). Writes camber-ab-sweeps.csv and camber-ab-summary.csv.
        /// </summary>
        // The parts a craft file names that this install has not loaded, space-separated; empty when all are there.
        private static string MissingParts(string craftPath)
        {
            if (!File.Exists(craftPath))
                return "(no craft file)";
            var missing = new HashSet<string>();
            foreach (string raw in File.ReadAllLines(craftPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("part = ", StringComparison.Ordinal))
                    continue;
                string name = line.Substring(7).Trim();
                int id = name.LastIndexOf('_');
                if (id > 0)
                    name = name.Substring(0, id);
                if (PartLoader.getPartInfoByName(name) == null && PartLoader.getPartInfoByName(name.Replace('.', '_')) == null)
                    missing.Add(name);
            }
            return string.Join(" ", missing);
        }

        [UITest("Far_ControlCamber_AB", StartsAt = StartState.SpaceCenter, TimeoutSec = 3600)]
        public static IEnumerator Far_ControlCamber_AB(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            var sw = Reflect.FindType("ferram4.FARWingAerodynamicModel")?.GetField("ControlSurfaceCamber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            // a build without the switch (upstream: camber always on) is measured once, as it is ("on" column)
            bool hasSwitch = sw != null;
            bool original = hasSwitch && (bool)sw.GetValue(null);
            ctx.Record("controlSurfaceCamberDefault", hasSwitch ? original.ToString() : "n/a (no switch: camber always on)");
            string facility = (ctx.Param("facility") ?? "SPH").ToUpperInvariant();
            StartState state = facility == "VAB" ? StartState.Vab : StartState.Sph;
            string[] crafts = (ctx.Param("crafts") ?? "Aeris 4A+Stearwing A300+Gull+Dove").Split('+');
            double[] machs = (ctx.Param("machs") ?? "0.2,0.5,0.8,0.95").Split(',').Select(v => double.Parse(v.Trim(), Inv)).ToArray();
            int flapsLevel = (int)ctx.ParamDouble("flapsLevel", 3);
            var stabPoints = (ctx.Param("stabPoints") ?? "0.3@1000+0.7@8000").Split('+')
                .Select(s => s.Split('@').Select(v => double.Parse(v.Trim(), Inv)).ToArray()).ToArray();
            CelestialBody body = FlightGlobals.GetHomeBody();
            double g = body.GeeASL * 9.80665;
            var sweepsCsv = new StringBuilder("craft,camber,mach,flaps,alpha,pitch,Cl,Cd,Cm\n");
            var summary = new StringBuilder("craft,point,metric,on,off,delta,deltaPct\n");
            var inputSweep = (ctx.Param("inputSweep") ?? "").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split('@').Select(v => double.Parse(v.Trim(), Inv)).ToArray()).ToArray();
            var inputCsv = new StringBuilder("craft,camber,mach,aoaDeg,input,Cl,Cm\n");
            var alphas = new List<double>();
            for (double a = -4; a <= 20.001; a += 1) alphas.Add(a);

            try
            {
                foreach (string craftRaw in crafts)
                {
                    string craft = craftRaw.Trim();
                    ctx.Step("Craft " + craft);
                    // the test save's craft, else the install's own (the stock and mod example craft)
                    string path = ctx.CraftPath(craft, facility);
                    if (!File.Exists(path))
                        path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", facility, craft + ".craft"));
                    // a craft with parts this install lacks is skipped (the game's load error popup would end the test)
                    string missing = MissingParts(path);
                    if (missing.Length > 0)
                    {
                        ctx.Record(craft + ".skipped", "missing parts: " + missing);
                        continue;
                    }
                    yield return Nav.EnsureState(ctx, state, path);
                    Ugui.DismissPopups();
                    yield return Wait.Until(FarSim.Ready, 60, "FAR editor sim ready", shot: false);
                    yield return Wait.Seconds(8);   // voxelization settles after the craft loads
                    string k = craft + ".";
                    int surfaces = EditorLogic.fetch.ship.parts.Sum(p => p.Modules.Cast<PartModule>().Count(m => m.GetType().Name == "FARControllableSurface"));
                    ctx.Record(k + "controlSurfaces", surfaces);

                    var results = new Dictionary<bool, Dictionary<string, double>>();
                    foreach (bool camber in hasSwitch ? new[] { true, false } : new[] { true })
                    {
                        if (hasSwitch)
                            sw.SetValue(null, camber);
                        FarSim.Instant(5, 0, 0, 0.5, 0, 0, false);   // the sim carries state between calls: settle it first
                        var m = new Dictionary<string, double>();
                        var points = machs.Select(mach => (mach, flaps: 0)).ToList();
                        points.Add((machs[0], flapsLevel));
                        foreach (var (mach, flaps) in points)
                        {
                            string pk = string.Format(Inv, "M{0}{1}.", mach, flaps > 0 ? "f" + flaps : "");
                            var cl = new double[alphas.Count]; var cd = new double[alphas.Count]; var cm = new double[alphas.Count];
                            for (int i = 0; i < alphas.Count; ++i)
                            {
                                var o = FarSim.Instant(alphas[i], 0, 0, mach, 0, flaps, false);
                                cl[i] = o["Cl"]; cd[i] = o["Cd"]; cm[i] = o["Cm"];
                                sweepsCsv.Append(string.Join(",", craft, camber ? "on" : "off", mach.ToString(Inv), flaps.ToString(Inv), alphas[i].ToString(Inv), "0",
                                    cl[i].ToString("G9", Inv), cd[i].ToString("G9", Inv), cm[i].ToString("G9", Inv))).Append('\n');
                            }
                            // slopes over -4..6 deg (per deg), least squares
                            var lin = Enumerable.Range(0, alphas.Count).Where(i => alphas[i] <= 6).ToArray();
                            double Slope(double[] y)
                            {
                                double mx = lin.Average(i => alphas[i]), my = lin.Average(i => y[i]);
                                return lin.Sum(i => (alphas[i] - mx) * (y[i] - my)) / lin.Sum(i => (alphas[i] - mx) * (alphas[i] - mx));
                            }
                            double cla = Slope(cl), cma = Slope(cm);
                            m[pk + "ClPerDeg"] = cla;
                            m[pk + "CmPerDeg"] = cma;
                            m[pk + "staticMarginMAC"] = -cma / cla;
                            m[pk + "Cl0"] = cl[alphas.IndexOf(0)];
                            m[pk + "Cm0"] = cm[alphas.IndexOf(0)];
                            // trim: the first Cm sign change from the bottom of the sweep
                            double trim = double.NaN, clTrim = double.NaN;
                            for (int i = 1; i < alphas.Count && double.IsNaN(trim); ++i)
                                if (cm[i - 1] * cm[i] <= 0 && cm[i - 1] != cm[i])
                                {
                                    double u = cm[i - 1] / (cm[i - 1] - cm[i]);
                                    trim = alphas[i - 1] + u;
                                    clTrim = cl[i - 1] + u * (cl[i] - cl[i - 1]);
                                }
                            m[pk + "trimAoA"] = trim;
                            m[pk + "ClAtTrim"] = clTrim;
                            int iMax = Enumerable.Range(0, alphas.Count).OrderByDescending(i => cl[i]).First();
                            m[pk + "ClMax"] = cl[iMax];
                            m[pk + "AoAAtClMax"] = alphas[iMax];
                            m[pk + "LDmax"] = Enumerable.Range(0, alphas.Count).Where(i => cl[i] > 0 && cd[i] > 0).Select(i => cl[i] / cd[i]).DefaultIfEmpty(double.NaN).Max();
                            var up = FarSim.Instant(4, 0, 0, mach, 0.5, flaps, false);
                            var dn = FarSim.Instant(4, 0, 0, mach, -0.5, flaps, false);
                            m[pk + "CmPerPitchInput"] = up["Cm"] - dn["Cm"];
                            m[pk + "ClPerPitchInput"] = up["Cl"] - dn["Cl"];
                        }
                        // inputSweep: Cm and Cl at pitch inputs -1..1 (0.25 apart) at each mach@aoaDeg, as inM<mach>a<aoa> /
                        // Cm@<input x 100> (and Cl@), and every point in input-sweep.csv
                        foreach (double[] ip in inputSweep)
                        {
                            string pk = string.Format(Inv, "inM{0}a{1}.", ip[0], ip[1]).Replace("-", "m");
                            for (int j = -4; j <= 4; ++j)
                            {
                                double input = j * 0.25;
                                var o = FarSim.Instant(ip[1], 0, 0, ip[0], input, 0, false);
                                string tag = (j * 25).ToString("+0;-0;0", Inv);
                                m[pk + "Cm@" + tag] = o["Cm"];
                                m[pk + "Cl@" + tag] = o["Cl"];
                                inputCsv.Append(string.Join(",", craft, camber ? "on" : "off", ip[0].ToString(Inv), ip[1].ToString(Inv), input.ToString(Inv),
                                    o["Cl"].ToString("G9", Inv), o["Cm"].ToString("G9", Inv))).Append('\n');
                            }
                        }
                        foreach (double[] sp in stabPoints)
                        {
                            string pk = string.Format(Inv, "stab.M{0}@{1}km.", sp[0], sp[1] / 1000);
                            Dictionary<string, double> d = null;
                            try { d = FarSim.StabilityDerivs(body, sp[1], sp[0], 0, false); }
                            catch (Exception ex) { Log.Warn("stability derivs " + pk + ": " + (ex.InnerException?.Message ?? ex.Message)); }
                            if (d == null) continue;
                            double u0 = d["nominalVelocity"], zw = d["d03"], xw = d["d04"], mw = d["d05"], zu = d["d06"], xu = d["d07"], mq = d["d11"], me = d["d14"];
                            m[pk + "trimAoA"] = d["stableAoA"];
                            m[pk + "Zw"] = zw; m[pk + "Mw"] = mw; m[pk + "Mq"] = mq; m[pk + "Me"] = me; m[pk + "Zu"] = zu; m[pk + "Xu"] = xu;
                            double spW2 = zw * mq - mw * u0;
                            double spW = spW2 > 0 ? Math.Sqrt(spW2) : double.NaN;
                            m[pk + "shortPeriodRadS"] = spW;
                            m[pk + "shortPeriodDamping"] = -(zw + mq) / (2 * spW);
                            double phW2 = -g * zu / u0;
                            double phW = phW2 > 0 ? Math.Sqrt(phW2) : double.NaN;
                            m[pk + "phugoidRadS"] = phW;
                            m[pk + "phugoidDamping"] = -xu / (2 * phW);
                        }
                        results[camber] = m;
                    }
                    foreach (string key in results[true].Keys)
                    {
                        double on = results[true][key];
                        double off = results.TryGetValue(false, out var offs) && offs.TryGetValue(key, out double o) ? o : double.NaN;
                        double delta = off - on, pct = on != 0 ? 100 * delta / Math.Abs(on) : double.NaN;
                        int dot = key.LastIndexOf('.');
                        summary.Append(string.Join(",", craft, key.Substring(0, dot), key.Substring(dot + 1), on.ToString("G6", Inv), off.ToString("G6", Inv),
                            delta.ToString("G4", Inv), pct.ToString("F1", Inv))).Append('\n');
                        ctx.Record(k + key, string.Format(Inv, "on {0:G5} off {1:G5} ({2:+0.0;-0.0}%)", on, off, pct));
                    }
                }
            }
            finally
            {
                if (hasSwitch)
                    sw.SetValue(null, original);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "camber-ab-sweeps.csv"), sweepsCsv.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "camber-ab-summary.csv"), summary.ToString());
            if (inputSweep.Length > 0)
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "input-sweep.csv"), inputCsv.ToString());
        }

        // The vessel's moment of inertia (t m2) about an axis through its centre of mass: every rigidbody's own inertia
        // about the axis plus its mass at its distance from it.
        private static double PitchInertia(Vessel v, Vector3 axis)
        {
            Vector3d com = v.CoMD;
            double inertia = 0;
            foreach (Part p in v.parts)
            {
                Rigidbody rb = p.Rigidbody;
                if (rb == null) continue;
                Vector3d r = (Vector3d)rb.worldCenterOfMass - com;
                double along = Vector3d.Dot(r, (Vector3d)axis);
                inertia += rb.mass * (r.sqrMagnitude - along * along);
                Quaternion q = rb.rotation * rb.inertiaTensorRotation;
                Vector3 t = rb.inertiaTensor;
                for (int i = 0; i < 3; ++i)
                {
                    Vector3 e = q * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
                    float c = Vector3.Dot(e, axis);
                    inertia += t[i] * c * c;
                }
            }
            return inertia;
        }

        /// <summary>
        /// Whether FARWingAerodynamicModel.ControlSurfaceCamber changes how a plane answers its pitch control in flight,
        /// as against what FAR computes it applies. Each craft is launched to the runway (reaction wheels, SAS and
        /// throttle off), then for camber on and off and pitch input +u and -u: placed at the condition (Mach, altitude,
        /// AoA, over the ocean), held there for `settle` frames with the input applied (the surfaces deflected and
        /// settled), and released for `frames` frames, over which the root's pitch acceleration is fitted and the pitching
        /// moment FAR applied (its forces at their aerodynamic centres, about the CoM) averaged. The pitch control's moment
        /// as the vessel felt it is its pitch inertia times the difference in acceleration between +u and -u; FAR's is
        /// the difference in its moment. Each pair is flown `reps` times. Params: crafts ("+"-separated stock SPH craft,
        /// default Gull+Velociteze+Ravenspear Mk3+Stearwing A300), cond (mach@altM@aoaDeg, default 0.4@2000@3), u (0.5),
        /// settle (50), frames (12), reps (2). Writes camber-flight.csv (every pair) and records the means.
        /// </summary>
        [UITest("Far_ControlCamber_Flight", StartsAt = StartState.SpaceCenter, TimeoutSec = 3600)]
        public static IEnumerator Far_ControlCamber_Flight(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            ctx.Record("kjrLoaded", AssemblyLoader.loadedAssemblies.Any(a => a.name == "KerbalJointReinforcement"));
            var sw = Reflect.FindType("ferram4.FARWingAerodynamicModel")?.GetField("ControlSurfaceCamber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.True(sw != null, "FAR has FARWingAerodynamicModel.ControlSurfaceCamber");
            bool original = (bool)sw.GetValue(null);
            string[] crafts = (ctx.Param("crafts") ?? "Gull+Velociteze+Ravenspear Mk3+Stearwing A300").Split('+');
            double[] cond = (ctx.Param("cond") ?? "0.4@2000@3").Split('@').Select(x => double.Parse(x.Trim(), Inv)).ToArray();
            var c = new FarFlightProbe.Condition { Name = "camber", Mach = cond[0], AltM = cond[1], AoADeg = cond[2], Lat = 0, Lon = -60 };
            float u = (float)ctx.ParamDouble("u", 0.5);
            int settle = (int)ctx.ParamDouble("settle", 50), frames = (int)ctx.ParamDouble("frames", 12), reps = (int)ctx.ParamDouble("reps", 2);
            var csv = new StringBuilder("craft,camber,rep,input,pitchAccelRadS2,farMomentKNm,inertiaTM2,aoaStartDeg,aoaEndDeg,mach\n");
            var fixedWait = new WaitForFixedUpdate();
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            try
            {
                foreach (string craftRaw in crafts)
                {
                    string craft = craftRaw.Trim();
                    string path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "SPH", craft + ".craft"));
                    string missing = MissingParts(path);
                    if (missing.Length > 0)
                    {
                        ctx.Record(craft + ".skipped", "missing parts: " + missing);
                        continue;
                    }
                    ctx.Step("Launch " + craft);
                    yield return Nav.EnsureState(ctx, StartState.Flight, path, "Runway");
                    yield return Wait.Seconds(5);   // FAR voxelizes the craft after load
                    Vessel v = FlightGlobals.ActiveVessel;
                    Flight.IgnoreMaxTemperature(true);
                    v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                    v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                    int wheels = 0;
                    foreach (Part p in v.parts)
                        foreach (var rw in p.Modules.GetModules<ModuleReactionWheel>())
                        {
                            rw.wheelState = ModuleReactionWheel.WheelState.Disabled;
                            rw.authorityLimiter = 0;
                            ++wheels;
                        }
                    ctx.Record(craft + ".reactionWheelsOff", wheels);
                    Ctrl.Attach();
                    Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: 0);
                    yield return FarFlightProbe.Place(c);
                    ctx.Record(craft + ".gearRetracted", Ctrl.RetractGear());
                    yield return Wait.Seconds(3);   // the gear up and FAR's revoxelization

                    // felt and FAR moments per camber setting: lists of the +u minus -u differences
                    var felt = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                    var far = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                    for (int rep = 0; rep < reps; ++rep)
                        foreach (bool camber in new[] { true, false })
                        {
                            sw.SetValue(null, camber);
                            var accel = new Dictionary<float, double>();
                            var moment = new Dictionary<float, double>();
                            double inertia = double.NaN;
                            foreach (float input in new[] { u, -u })
                            {
                                Ctrl.Set(pitch: input);
                                yield return FarFlightProbe.Place(c);
                                // held with the input on: the surfaces deflect and settle; nothing recorded
                                var none = new List<FarFlightProbe.Sample>();
                                yield return FarFlightProbe.Hold(c, settle, 0, none, pin: true, predict: false);
                                v = FlightGlobals.ActiveVessel;
                                double aoa0 = Flight.AoA;
                                var t = new List<double>(); var rate = new List<double>(); var m = new List<double>();
                                for (int i = 0; i < frames; ++i)
                                {
                                    yield return fixedWait;
                                    v = FlightGlobals.ActiveVessel;
                                    Vector3 axis = FarApi.PitchUpAxis(v);
                                    if (i == 0) inertia = PitchInertia(v, axis);
                                    t.Add(Planetarium.GetUniversalTime());
                                    rate.Add(Vector3.Dot(v.rootPart.Rigidbody.angularVelocity, axis));
                                    if (FarApi.FlightForceAndTorque(v, out _, out Vector3 torque))
                                        m.Add(Vector3.Dot(torque, axis));
                                }
                                double mt = t.Average(), mr = rate.Average();
                                double a = t.Select((x, i) => (x - mt) * (rate[i] - mr)).Sum() / t.Select(x => (x - mt) * (x - mt)).Sum();
                                accel[input] = a;
                                moment[input] = m.Count > 0 ? m.Average() : double.NaN;
                                csv.Append(string.Join(",", craft, camber ? "on" : "off", rep.ToString(Inv), input.ToString(Inv), a.ToString("G6", Inv),
                                    moment[input].ToString("G6", Inv), inertia.ToString("G6", Inv), aoa0.ToString("F3", Inv), Flight.AoA.ToString("F3", Inv),
                                    v.mach.ToString("F3", Inv))).Append('\n');
                            }
                            felt[camber].Add(inertia * (accel[u] - accel[-u]));
                            far[camber].Add(moment[u] - moment[-u]);
                        }
                    Ctrl.Set(pitch: 0);
                    foreach (bool camber in new[] { true, false })
                    {
                        string k = craft + "." + (camber ? "on" : "off") + ".";
                        ctx.Record(k + "feltKNm", string.Join(" ", felt[camber].Select(x => x.ToString("F1", Inv))));
                        ctx.Record(k + "farKNm", string.Join(" ", far[camber].Select(x => x.ToString("F1", Inv))));
                        ctx.Record(k + "feltOverFar", Math.Round(felt[camber].Average() / far[camber].Average(), 3));
                    }
                    ctx.Record(craft + ".feltOffOverOn", Math.Round(felt[false].Average() / felt[true].Average(), 3));
                    ctx.Record(craft + ".farOffOverOn", Math.Round(far[false].Average() / far[true].Average(), 3));
                }
            }
            finally
            {
                sw.SetValue(null, original);
                Ctrl.Neutral();
                FarFlightProbe.ResumeOtherPredictors(trajectories);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "camber-flight.csv"), csv.ToString());
        }

        /// <summary>
        /// FAR's wing interaction for every control surface of each craft (WingLiveVsSim.Interactions), in the editor after
        /// one evaluation of FAR's simulation (Mach 0.7, 3 deg) so the upstream values are current. Params: crafts
        /// ("+"-separated), facility (SPH/VAB). Writes wing-interactions.txt.
        /// </summary>
        [UITest("Far_WingInteractions", StartsAt = StartState.SpaceCenter, TimeoutSec = 1800)]
        public static IEnumerator Far_WingInteractions(TestContext ctx)
        {
            string facility = (ctx.Param("facility") ?? "SPH").ToUpperInvariant();
            StartState state = facility == "VAB" ? StartState.Vab : StartState.Sph;
            var txt = new StringBuilder();
            foreach (string craftRaw in (ctx.Param("crafts") ?? "Aeris 3A").Split('+'))
            {
                string craft = craftRaw.Trim();
                string path = ctx.CraftPath(craft, facility);
                if (!File.Exists(path))
                    path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", facility, craft + ".craft"));
                ctx.Step("Craft " + craft);
                yield return Nav.EnsureState(ctx, state, path);
                Ugui.DismissPopups();
                yield return Wait.Until(FarSim.Ready, 60, "FAR editor sim ready", shot: false);
                yield return Wait.Seconds(8);
                yield return Snap.Shot(ctx, craft.Replace(' ', '_'));
                // the state the dump shows: aoaDeg, mach, pitch input (default 3 deg, Mach 0.7, no input); dumpInputs adds a
                // dump at each further input
                double aoa = ctx.ParamDouble("aoaDeg", 3), mach = ctx.ParamDouble("mach", 0.7);
                FarSim.Instant(aoa, 0, 0, mach, ctx.ParamDouble("input", 0), 0, false);
                string dump = WingLiveVsSim.Interactions(EditorLogic.fetch.ship.parts);
                txt.Append("## ").Append(craft).Append('\n').Append(dump).Append('\n');
                foreach (string s in (ctx.Param("dumpInputs") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    double input = double.Parse(s.Trim(), Inv);
                    FarSim.Instant(aoa, 0, 0, mach, input, 0, false);
                    txt.Append("## ").Append(craft).Append(" input ").Append(s.Trim()).Append('\n')
                       .Append(WingLiveVsSim.Interactions(EditorLogic.fetch.ship.parts)).Append('\n');
                }
                ctx.Record(craft + ".surfaces", dump.Split('\n').Length - 1);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "wing-interactions.txt"), txt.ToString());
        }

        /// <summary>
        /// Every FAR wing part's mesh in the editor, for an outside model of the planform: each vertex of its renderers'
        /// meshes in world space relative to the craft's center of mass (the parts' masses with their resources), with the
        /// part, whether it is a control surface, and the root part's axes. planform.csv, one row per vertex, and the axes
        /// and CoM as comment lines. Params: crafts ("+"-separated), facility (SPH).
        /// </summary>
        [UITest("Far_Planform", StartsAt = StartState.SpaceCenter, TimeoutSec = 1800)]
        public static IEnumerator Far_Planform(TestContext ctx)
        {
            string facility = (ctx.Param("facility") ?? "SPH").ToUpperInvariant();
            StartState state = facility == "VAB" ? StartState.Vab : StartState.Sph;
            var csv = new StringBuilder("craft,part,name,control,mass,x,y,z\n");
            foreach (string craftRaw in (ctx.Param("crafts") ?? "Aeris 3A").Split('+'))
            {
                string craft = craftRaw.Trim();
                string path = ctx.CraftPath(craft, facility);
                if (!File.Exists(path))
                    path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", facility, craft + ".craft"));
                ctx.Step("Craft " + craft);
                yield return Nav.EnsureState(ctx, state, path);
                Ugui.DismissPopups();
                yield return Wait.Seconds(5);
                List<Part> parts = EditorLogic.fetch.ship.parts;
                double mass = 0;
                Vector3d mr = Vector3d.zero;
                foreach (Part p in parts)
                {
                    double m = p.mass + p.GetResourceMass();
                    mass += m;
                    mr += (Vector3d)p.transform.TransformPoint(p.CoMOffset) * m;
                }
                Vector3d com = mr / mass;
                Transform root = EditorLogic.RootPart.transform;
                csv.AppendFormat(Inv, "# {0} mass {1:F3} com {2:F4} {3:F4} {4:F4} rootUp {5:F5} {6:F5} {7:F5} rootForward {8:F5} {9:F5} {10:F5} rootRight {11:F5} {12:F5} {13:F5} rootPos {14:F4} {15:F4} {16:F4}\n",
                    craft, mass, com.x, com.y, com.z, root.up.x, root.up.y, root.up.z, root.forward.x, root.forward.y, root.forward.z,
                    root.right.x, root.right.y, root.right.z, root.position.x - com.x, root.position.y - com.y, root.position.z - com.z);
                int wings = 0;
                foreach (Part p in parts)
                {
                    PartModule w = p.Modules.Cast<PartModule>().FirstOrDefault(x => x.GetType().Name == "FARWingAerodynamicModel" || x.GetType().Name == "FARControllableSurface");
                    if (w == null) continue;
                    ++wings;
                    bool control = w.GetType().Name == "FARControllableSurface";
                    foreach (MeshFilter mf in p.FindModelComponents<MeshFilter>())
                    {
                        if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                        Renderer r = mf.GetComponent<Renderer>();
                        if (r == null || !r.enabled) continue;
                        foreach (Vector3 v in mf.sharedMesh.vertices)
                        {
                            Vector3d wv = (Vector3d)mf.transform.TransformPoint(v) - com;
                            // the part's index in the craft's part list: the key WingLiveVsSim.Interactions uses too
                            csv.AppendFormat(Inv, "{0},i{1},{2},{3},{4:F4},{5:F4},{6:F4},{7:F4}\n", craft, parts.IndexOf(p), p.partInfo?.name, control ? 1 : 0,
                                p.mass + p.GetResourceMass(), wv.x, wv.y, wv.z);
                        }
                    }
                }
                ctx.Record(craft + ".wingParts", wings);
                ctx.Record(craft + ".massT", Math.Round(mass, 2));
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "planform.csv"), csv.ToString());
        }

        // The engines' thrust moment about the CoM on an axis (kN m): each thrust transform's share of the engine's thrust,
        // along -forward, at its position.
        private static double ThrustMoment(Vessel v, Vector3 axis)
        {
            Vector3d com = v.CoMD;
            double m = 0;
            foreach (Part p in v.parts)
                foreach (var e in p.Modules.GetModules<ModuleEngines>())
                {
                    if (e.thrustTransforms == null || e.thrustTransforms.Count == 0 || e.finalThrust <= 0) continue;
                    double share = e.finalThrust / e.thrustTransforms.Count;
                    foreach (Transform t in e.thrustTransforms)
                        m += Vector3d.Dot(Vector3d.Cross((Vector3d)t.position - com, -(Vector3d)t.forward * share), (Vector3d)axis);
                }
            return m;
        }

        /// <summary>
        /// FARWingAerodynamicModel.ControlSurfaceCamber in steady flight: each craft air-started level at the point and
        /// flown on AtmosphereAutopilot's cruise (course 90, the altitude, the speed from the Mach), and once steady
        /// (altitude within 60 m, speed within 4 m/s, vertical speed under 3 m/s for steadySec) measured for windowSec:
        /// the pitch input it trims at, its AoA, FAR's reported pitching moment about the CoM (its forces at their
        /// aerodynamic centres) and the engines' thrust moment. Phases on, off, on again (the last checks the first
        /// repeats). Flying steady, what the vessel feels about its pitch axis sums to about zero; FAR's moment plus the
        /// thrust's moment that does not is what FAR says it applies and the vessel does not feel. Reaction wheels off.
        /// Params: crafts ("+"-separated stock SPH craft), point (altM:mach, default 3000:0.5), steadySec (8), windowSec
        /// (10), settleTimeoutSec (90). Writes camber-cruise.csv (every frame of every window) and records the means.
        /// </summary>
        [UITest("Far_ControlCamber_Cruise", StartsAt = StartState.SpaceCenter, TimeoutSec = 5400)]
        public static IEnumerator Far_ControlCamber_Cruise(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            ctx.Record("kjrLoaded", AssemblyLoader.loadedAssemblies.Any(a => a.name == "KerbalJointReinforcement"));
            Assert.True(Aa.Present, "AtmosphereAutopilot is installed");
            var sw = Reflect.FindType("ferram4.FARWingAerodynamicModel")?.GetField("ControlSurfaceCamber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            // without the switch (upstream FAR) only the "on" window is flown
            bool hasSwitch = sw != null;
            ctx.Record("hasSwitch", hasSwitch);
            bool original = hasSwitch && (bool)sw.GetValue(null);
            string[] phases = hasSwitch ? new[] { "on", "off", "on2" } : new[] { "on" };
            string[] crafts = (ctx.Param("crafts") ?? "Gull+Velociteze+Ravenspear Mk3+Stearwing A300+Mallard+Aeris 3A+Skywinder AE1").Split('+');
            string[] pt = (ctx.Param("point") ?? "3000:0.5").Split(':');
            double altM = double.Parse(pt[0], Inv), mach = double.Parse(pt[1], Inv);
            double steadySec = ctx.ParamDouble("steadySec", 8), windowSec = ctx.ParamDouble("windowSec", 10), timeout = ctx.ParamDouble("settleTimeoutSec", 90);
            var csv = new StringBuilder("craft,phase,ut,altM,srfSpeed,mach,aoaDeg,pitchInput,farMomentKNm,thrustMomentKNm,pitchRateDegS,g\n");
            // joints=on: each control surface's couple and what its joints pass on (WingLiveVsSim.SurfaceJoints), at the
            // middle of every window, into camber-joints.txt
            bool joints = (ctx.Param("joints", "off") ?? "off").Equals("on", StringComparison.OrdinalIgnoreCase);
            if (joints)
                ctx.Record("jointsArmed", WingLiveVsSim.Arm());
            // arcs=on: FAR's wing interaction overlay (a fork's FARDebugValues.showWingInteractions), shot after the first
            // window with the UI hidden
            var arcsField = Reflect.FindType("FerramAerospaceResearch.FARDebugValues")?.GetField("showWingInteractions");
            bool arcs = (ctx.Param("arcs", "off") ?? "off").Equals("on", StringComparison.OrdinalIgnoreCase) && arcsField != null;
            if (arcs) arcsField.SetValue(null, true);
            ctx.Record("arcs", arcs);
            var jointsTxt = new StringBuilder();
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            try
            {
                foreach (string craftRaw in crafts)
                {
                    string craft = craftRaw.Trim();
                    // the test save's craft, else the install's own
                    string path = ctx.CraftPath(craft, "SPH");
                    if (!File.Exists(path))
                        path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "SPH", craft + ".craft"));
                    string missing = MissingParts(path);
                    if (missing.Length > 0)
                    {
                        ctx.Record(craft + ".skipped", "missing parts: " + missing);
                        continue;
                    }
                    ctx.Step("Air start " + craft);
                    sw?.SetValue(null, true);
                    yield return Nav.EnsureState(ctx, StartState.Flight, path, "Runway");
                    yield return Wait.Seconds(5);   // FAR voxelizes the craft after load
                    Vessel v = FlightGlobals.ActiveVessel;
                    Flight.IgnoreMaxTemperature(true);
                    int wheels = 0;
                    foreach (Part p in v.parts)
                        foreach (PartModule m in p.Modules)
                            if (m is ModuleReactionWheel rw)
                            {
                                rw.wheelState = ModuleReactionWheel.WheelState.Disabled;
                                rw.authorityLimiter = 0;
                                ++wheels;
                            }
                    ctx.Record(craft + ".reactionWheelsOff", wheels);
                    Ctrl.Sas(false);
                    Ctrl.Stage();
                    CelestialBody body = v.mainBody;
                    double p0 = body.GetPressure(altM);
                    double speed = mach * body.GetSpeedOfSound(p0, body.GetDensity(p0, body.GetTemperature(altM)));
                    // in daylight when the overlay is shot, else where it always was
                    var start = new FarFlightProbe.Condition { Name = "air start", AltM = altM, Mach = mach, AoADeg = 3, Lat = 0, Lon = arcs ? DaylightLon(body, altM) : -60 };
                    if (arcs) arcsField.SetValue(null, true);   // FAR reloads its debug settings with the scene
                    yield return FarFlightProbe.Place(start);
                    Flight.PointAtAoA(start.AoADeg);
                    Ctrl.RetractGear();
                    Ctrl.SetThrottle(1f);
                    Aa.Cruise(FlightGlobals.ActiveVessel, altM, speed, 90);
                    int parts = FlightGlobals.ActiveVessel.parts.Count;
                    ctx.Record(craft + ".parts", parts);

                    foreach (string phase in phases)
                    {
                        sw?.SetValue(null, phase != "off");
                        ctx.Step($"{craft}: camber {phase}, steady then measured");
                        // steady for steadySec, or the timeout (then measured anyway and flagged)
                        double since = double.NaN, deadline = Planetarium.GetUniversalTime() + timeout;
                        bool steady = false;
                        while (Planetarium.GetUniversalTime() < deadline)
                        {
                            Vessel w = FlightGlobals.ActiveVessel;
                            if (w == null || w.parts.Count < parts) break;
                            bool now = Math.Abs(w.altitude - altM) < 60 && Math.Abs(w.srfSpeed - speed) < 4 && Math.Abs(w.verticalSpeed) < 3;
                            double ut = Planetarium.GetUniversalTime();
                            if (!now) since = double.NaN;
                            else if (double.IsNaN(since)) since = ut;
                            else if (ut - since >= steadySec) { steady = true; break; }
                            // the settling too (what led up to a breakup), FAR's and the thrust moment left blank
                            if (w.rootPart != null && w.rootPart.Rigidbody != null)
                                csv.Append(string.Join(",", craft, phase + "-settle", ut.ToString("F3", Inv), w.altitude.ToString("F1", Inv),
                                    w.srfSpeed.ToString("F2", Inv), w.mach.ToString("F4", Inv), Flight.AoA.ToString("F4", Inv), w.ctrlState.pitch.ToString("F5", Inv),
                                    "", "", (Vector3.Dot(w.rootPart.Rigidbody.angularVelocity, FarApi.PitchUpAxis(w)) * Mathf.Rad2Deg).ToString("F4", Inv),
                                    w.geeForce.ToString("F3", Inv))).Append('\n');
                            yield return null;
                        }
                        v = FlightGlobals.ActiveVessel;
                        if (v == null || v.parts.Count < parts)
                        {
                            ctx.Record(craft + "." + phase + ".broken", v == null ? "no vessel" : $"{parts - v.parts.Count} parts lost");
                            break;
                        }
                        ctx.Record(craft + "." + phase + ".steady", steady);
                        var input = new List<double>(); var aoa = new List<double>(); var far = new List<double>(); var thrust = new List<double>();
                        var fixedWait = new WaitForFixedUpdate();
                        double end = Planetarium.GetUniversalTime() + windowSec;
                        bool jointsTaken = !joints;
                        while (Planetarium.GetUniversalTime() < end)
                        {
                            yield return fixedWait;
                            if (!jointsTaken && Planetarium.GetUniversalTime() > end - windowSec / 2)
                            {
                                jointsTaken = true;
                                jointsTxt.Append(craft).Append(' ').Append(phase).Append(": ")
                                         .Append(WingLiveVsSim.SurfaceJoints(FlightGlobals.ActiveVessel).Replace(" || ", "\n    ")).Append("\n\n");
                            }
                            v = FlightGlobals.ActiveVessel;
                            Vector3 axis = FarApi.PitchUpAxis(v);
                            if (!FarApi.FlightForceAndTorque(v, out _, out Vector3 torque)) continue;
                            double fm = Vector3.Dot(torque, axis), tm = ThrustMoment(v, axis);
                            input.Add(v.ctrlState.pitch); aoa.Add(Flight.AoA); far.Add(fm); thrust.Add(tm);
                            csv.Append(string.Join(",", craft, phase, Planetarium.GetUniversalTime().ToString("F3", Inv), v.altitude.ToString("F1", Inv),
                                v.srfSpeed.ToString("F2", Inv), v.mach.ToString("F4", Inv), Flight.AoA.ToString("F4", Inv), v.ctrlState.pitch.ToString("F5", Inv),
                                fm.ToString("G6", Inv), tm.ToString("G6", Inv),
                                (Vector3.Dot(v.rootPart.Rigidbody.angularVelocity, axis) * Mathf.Rad2Deg).ToString("F4", Inv),
                                v.geeForce.ToString("F3", Inv))).Append('\n');
                        }
                        string k = craft + "." + phase + ".";
                        FarFlightProbe.Stats(input, out double mi, out double si);
                        FarFlightProbe.Stats(aoa, out double ma, out double sa);
                        FarFlightProbe.Stats(far, out double mf, out double sf);
                        FarFlightProbe.Stats(thrust, out double mt, out _);
                        ctx.Record(k + "pitchInput", string.Format(Inv, "{0:F4} ± {1:F4}", mi, si));
                        ctx.Record(k + "aoaDeg", string.Format(Inv, "{0:F3} ± {1:F3}", ma, sa));
                        ctx.Record(k + "farMomentKNm", string.Format(Inv, "{0:F2} ± {1:F2}", mf, sf));
                        ctx.Record(k + "thrustMomentKNm", Math.Round(mt, 2));
                        ctx.Record(k + "unfeltKNm", Math.Round(mf + mt, 2));
                        if (arcs && phase == "on")
                            yield return ArcShots(ctx, FlightGlobals.ActiveVessel, craft.Replace(' ', '_') + "_cruise");
                    }
                    Aa.Off(FlightGlobals.ActiveVessel);
                }
            }
            finally
            {
                sw?.SetValue(null, original);
                FarFlightProbe.ResumeOtherPredictors(trajectories);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "camber-cruise.csv"), csv.ToString());
            if (joints)
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "camber-joints.txt"), jointsTxt.ToString());
        }

        /// <summary>
        /// Does the vessel take the pitching moment it is given? Each craft is placed at a condition, wings level at an
        /// angle of attack, reaction wheels off, and held at each fixed pitch input in turn (re-placed for each). Once
        /// the surfaces have moved, the moment the integrator applied over a window of physics steps is set against the
        /// rate of the vessel's angular momentum (QueuedTorque.Balance), and each control surface's couple against its own
        /// inertia (WingLiveVsSim.SurfaceJoints: dw/step is the spin it adds in a step, against the 50 rad/s clamp).
        /// Needs nothing from a FAR fork. Params: crafts ("+"-separated), facility (SPH), point (altM:mach:aoaDeg, default
        /// 1000:0.7:3), inputs (default -1,0,1), settleSteps (25), windowSteps (25), maxAngVel (rad/s on every part's
        /// rigidbody, 0 keeps KSP's).
        /// </summary>
        [UITest("Far_CoupleBalance", StartsAt = StartState.SpaceCenter, TimeoutSec = 3600)]
        public static IEnumerator Far_CoupleBalance(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            ctx.Record("kjrLoaded", AssemblyLoader.loadedAssemblies.Any(a => a.name == "KerbalJointReinforcement"));
            string facility = (ctx.Param("facility") ?? "SPH").ToUpperInvariant();
            string[] crafts = (ctx.Param("crafts") ?? "Aeris 3A").Split('+');
            double[] pt = (ctx.Param("point") ?? "1000:0.7:3").Split(':').Select(x => double.Parse(x.Trim(), Inv)).ToArray();
            float[] inputs = (ctx.Param("inputs") ?? "-1,0,1").Split(',').Select(x => float.Parse(x.Trim(), Inv)).ToArray();
            int settle = (int)ctx.ParamDouble("settleSteps", 25), window = (int)ctx.ParamDouble("windowSteps", 25);
            float maxAngVel = (float)ctx.ParamDouble("maxAngVel", 0);
            bool relaunch = !(ctx.Param("relaunch", "on") ?? "on").Equals("off", StringComparison.OrdinalIgnoreCase);
            // camber=on|off: a fork FAR's ControlSurfaceCamber switch for the run (upstream has none: always on)
            var sw = Reflect.FindType("ferram4.FARWingAerodynamicModel")?.GetField("ControlSurfaceCamber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            string camberParam = ctx.Param("camber");
            bool? camberWas = sw != null ? (bool?)(bool)sw.GetValue(null) : null;
            if (sw != null && camberParam != null)
                sw.SetValue(null, camberParam.Equals("on", StringComparison.OrdinalIgnoreCase));
            ctx.Record("controlSurfaceCamber", sw != null ? sw.GetValue(null).ToString() : "no switch (on)");
            // arcs=on: FAR's wing interaction overlay (a fork's FARDebugValues.showWingInteractions), paused at the end of
            // the window at input arcsInput (-1) and shot from above, the side and behind with the UI hidden
            var arcsField = Reflect.FindType("FerramAerospaceResearch.FARDebugValues")?.GetField("showWingInteractions");
            bool arcs = (ctx.Param("arcs", "off") ?? "off").Equals("on", StringComparison.OrdinalIgnoreCase) && arcsField != null;
            float arcsInput = (float)ctx.ParamDouble("arcsInput", -1);
            object arcsWas = arcsField?.GetValue(null);
            if (arcs) arcsField.SetValue(null, true);
            ctx.Record("arcs", arcs);
            QueuedTorque.Arm();
            WingLiveVsSim.Arm();
            var txt = new StringBuilder();
            var csv = new StringBuilder("craft,input,altM,mach,qkPa,aoaDeg,appliedKNm,dLdtKNm,lostKNm,maxDwPerStep\n");
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            var fixedWait = new WaitForFixedUpdate();
            try
            {
                foreach (string craftRaw in crafts)
                {
                    string craft = craftRaw.Trim();
                    string path = ctx.CraftPath(craft, facility);
                    if (!File.Exists(path))
                        path = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", facility, craft + ".craft"));
                    string missing = MissingParts(path);
                    if (missing.Length > 0)
                    {
                        ctx.Record(craft + ".skipped", "missing parts: " + missing);
                        continue;
                    }
                    int parts = 0;
                    double lon = -60;
                    bool launched = false;
                    foreach (float input in inputs)
                    {
                        string k = craft + ".in" + input.ToString("+0.##;-0.##;0", Inv) + ".";
                        // a fresh vessel for each input (relaunch, default on): re-placing one still pitching from the last
                        // input tore big craft apart as they came off rails
                        if (!launched || relaunch)
                        {
                            ctx.Step($"Launch {craft} for input {input}");
                            if (launched)
                            {
                                Ctrl.Detach();
                                yield return Nav.EnsureState(ctx, facility == "VAB" ? StartState.Vab : StartState.Sph, path);
                            }
                            yield return Nav.EnsureState(ctx, StartState.Flight, path, facility == "VAB" ? "LaunchPad" : "Runway");
                            launched = true;
                            // off the ground at once: a big RP-1 stack on the pad lost wing panels and tanks within 3 s of loading
                            parts = FlightGlobals.ActiveVessel.parts.Count;
                            Flight.IgnoreMaxTemperature(true);
                            // in daylight when the overlay is shot, else where it always was
                            lon = arcs ? DaylightLon(FlightGlobals.ActiveVessel.mainBody, pt[0]) : -60;
                            if (arcs) arcsField.SetValue(null, true);   // FAR reloads its debug settings with the scene
                            yield return FarFlightProbe.Place(new FarFlightProbe.Condition { Name = "off the pad", AltM = pt[0], Mach = pt[1], AoADeg = pt[2], Lat = 0, Lon = lon });
                            yield return Wait.Seconds(5);   // FAR voxelizes the craft after load
                            ctx.Record(k + "partsLostAtLoad", parts - FlightGlobals.ActiveVessel.parts.Count);
                        }
                        ctx.Step($"{craft}: pitch input {input}");
                        Vessel v = FlightGlobals.ActiveVessel;
                        if (v == null || v.parts.Count < parts)
                        {
                            ctx.Record(k + "broken", v == null ? "no vessel" : $"{parts - v.parts.Count} parts lost");
                            break;
                        }
                        foreach (Part p in v.parts)
                            foreach (PartModule m in p.Modules)
                                if (m is ModuleReactionWheel rw)
                                {
                                    rw.wheelState = ModuleReactionWheel.WheelState.Disabled;
                                    rw.authorityLimiter = 0;
                                }
                        Ctrl.Sas(false);
                        Ctrl.Attach(true);
                        Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: 0);
                        var at = new FarFlightProbe.Condition { Name = "couple " + input, AltM = pt[0], Mach = pt[1], AoADeg = pt[2], Lat = 0, Lon = lon };
                        yield return FarFlightProbe.Place(at);
                        v = FlightGlobals.ActiveVessel;
                        if (maxAngVel > 0)
                            foreach (Part p in v.parts)
                                if (p.Rigidbody != null)
                                    p.Rigidbody.maxAngularVelocity = maxAngVel;
                        Ctrl.Set(pitch: input);
                        for (int i = 0; i < settle; ++i)
                            yield return fixedWait;
                        QueuedTorque.ResetWindow();
                        string joints = null;
                        for (int i = 0; i < window; ++i)
                        {
                            yield return fixedWait;
                            if (i == window / 2)
                                joints = WingLiveVsSim.SurfaceJoints(FlightGlobals.ActiveVessel);
                        }
                        v = FlightGlobals.ActiveVessel;
                        QueuedTorque.Balance(out double applied, out double dLdt, out int steps);
                        // the largest spin a control surface's couple adds in a step
                        double maxDw = 0;
                        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(joints ?? "", @"dw/step (\S+) rad/s"))
                            if (double.TryParse(mm.Groups[1].Value, NumberStyles.Float, Inv, out double dw))
                                maxDw = Math.Max(maxDw, Math.Abs(dw));
                        ctx.Record(k + "balance", string.Format(Inv, "applied {0:F1} kN m, dL/dt {1:F1} kN m, lost {2:F1} kN m over {3} steps; q {4:F1} kPa mach {5:F2} aoa {6:F1}; max surface dw/step {7:G3} rad/s",
                            applied, dLdt, applied - dLdt, steps, v.dynamicPressurekPa, v.mach, Flight.AoA, maxDw));
                        csv.Append(string.Join(",", craft, input.ToString(Inv), v.altitude.ToString("F0", Inv), v.mach.ToString("F3", Inv),
                            v.dynamicPressurekPa.ToString("F2", Inv), Flight.AoA.ToString("F2", Inv), applied.ToString("F2", Inv),
                            dLdt.ToString("F2", Inv), (applied - dLdt).ToString("F2", Inv), maxDw.ToString("G4", Inv))).Append('\n');
                        txt.Append(craft).Append(" input ").Append(input.ToString(Inv)).Append(": ").Append(QueuedTorque.Report()).Append("\n    ")
                           .Append((joints ?? "").Replace(" || ", "\n    ")).Append("\n\n");
                        if (arcs && input == arcsInput)
                            yield return ArcShots(ctx, v, craft.Replace(' ', '_') + "_in" + input.ToString("+0.##;-0.##;0", Inv));
                        Ctrl.Set(pitch: 0);
                    }
                    Ctrl.Detach();
                }
            }
            finally
            {
                Ctrl.Detach();
                FarFlightProbe.ResumeOtherPredictors(trajectories);
                if (camberWas.HasValue) sw.SetValue(null, camberWas.Value);
                if (arcsField != null && arcsWas != null) arcsField.SetValue(null, arcsWas);
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "couple-balance.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "couple-balance.txt"), txt.ToString());
        }

        // A longitude on the equator in the afternoon (the sun 30 deg west of overhead, so the vessel throws a shadow), the
        // first one east of that whose terrain is at least 1500 m below altM.
        private static double DaylightLon(CelestialBody body, double altM)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || sun == body) return -60;
            double lon = body.GetLongitude(sun.position) + 30;
            for (int i = 0; i < 36; ++i, lon += 10)
                if (body.TerrainAltitude(0, lon) < altM - 1500)
                    return ((lon + 540) % 360) - 180;
            return -60;
        }

        // The UI hidden, the vessel shot from above, the side and behind at a distance sized to it:
        // wingarcs_<name>_<view>.png in the results.
        private static IEnumerator ArcShots(TestContext ctx, Vessel v, string name)
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null || v == null) yield break;
            float size = 0;
            foreach (Part p in v.parts)
                size = Math.Max(size, 2 * (p.transform.position - v.CoM).magnitude);
            GameEvents.onHideUI.Fire();
            try
            {
                foreach (var (view, pitch, hdg, dist) in new[] { ("above", 1.35f, 0f, 1.6f), ("side", 0.12f, 1.5708f, 1.5f), ("rear", 0.5f, 3.1416f, 1.5f) })
                {
                    cam.camPitch = pitch;
                    cam.camHdg = hdg;
                    cam.SetDistance(Math.Max(20f, dist * size));
                    for (int i = 0; i < 6; ++i) yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(ctx.ResultsDir, $"wingarcs_{name}_{view}.png"));
                    yield return new WaitForEndOfFrame();
                    yield return new WaitForEndOfFrame();
                }
            }
            finally
            {
                GameEvents.onShowUI.Fire();
            }
        }
    }
}
