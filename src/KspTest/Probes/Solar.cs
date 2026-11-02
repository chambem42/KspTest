using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Samples every part's stock solar occlusion (PartThermalData.sunAreaMultiplier: the fraction of the part's
    /// sun-facing area the parts in front of it leave unshaded) over a window, records per-part means and the
    /// frame angle stock judged them in, and appends every sample to a CSV in the results folder.
    /// </summary>
    public static class Solar
    {
        public class Result
        {
            public int Samples;
            public double FrameDegMean, FrameDegMin = double.MaxValue, FrameDegMax = double.MinValue;
            public readonly Dictionary<uint, string> Name = new Dictionary<uint, string>();
            public readonly Dictionary<uint, double> Mean = new Dictionary<uint, double>(), Min = new Dictionary<uint, double>(), Max = new Dictionary<uint, double>();
        }

        /// <summary>One sample per second for seconds (wall clock), on the active vessel.</summary>
        public static IEnumerator Sample(TestContext ctx, string keyPrefix, string csvName, double seconds, Action<Result> done)
        {
            var r = new Result();
            var sum = new Dictionary<uint, double>();
            string csvPath = Path.Combine(ctx.ResultsDir, csvName);
            bool fresh = !File.Exists(csvPath);
            var csv = new StringBuilder();
            if (fresh) csv.Append("cycle,ut,part,partId,sunAreaMultiplier,bodyAreaMultiplier,skinTemperature,solarFrameDeg,directSunlight\n");
            var inv = CultureInfo.InvariantCulture;
            double next = Time.realtimeSinceStartup, end = Time.realtimeSinceStartup + seconds;
            double frameSum = 0;
            yield return Wait.Until(() =>
            {
                var av = FlightGlobals.ActiveVessel;
                if (av == null) return true;
                if (Time.realtimeSinceStartup < next) return Time.realtimeSinceStartup >= end;
                next += 1.0;
                ++r.Samples;
                double frame = Flight.SolarFrameAngle(av);
                frameSum += frame; r.FrameDegMin = Math.Min(r.FrameDegMin, frame); r.FrameDegMax = Math.Max(r.FrameDegMax, frame);
                double ut = Planetarium.GetUniversalTime();
                foreach (var p in av.parts)
                {
                    if (p.ptd == null) continue;
                    double m = p.ptd.sunAreaMultiplier;
                    uint id = p.persistentId;
                    if (!sum.ContainsKey(id)) { sum[id] = 0; r.Min[id] = double.MaxValue; r.Max[id] = double.MinValue; r.Name[id] = p.partInfo?.name ?? p.name; }
                    sum[id] += m; r.Min[id] = Math.Min(r.Min[id], m); r.Max[id] = Math.Max(r.Max[id], m);
                    csv.Append(keyPrefix).Append(',').Append(ut.ToString("F1", inv)).Append(',').Append(r.Name[id]).Append(',').Append(id).Append(',')
                       .Append(m.ToString("G5", inv)).Append(',').Append(p.ptd.bodyAreaMultiplier.ToString("G5", inv)).Append(',')
                       .Append(p.skinTemperature.ToString("F1", inv)).Append(',').Append(frame.ToString("F2", inv)).Append(',').Append(av.directSunlight).Append('\n');
                }
                return Time.realtimeSinceStartup >= end;
            }, seconds + 30, "the solar sampling window to pass", shot: false);
            File.AppendAllText(csvPath, csv.ToString());
            r.FrameDegMean = frameSum / Math.Max(1, r.Samples);
            foreach (var id in sum.Keys) r.Mean[id] = sum[id] / Math.Max(1, r.Samples);

            string k = string.IsNullOrEmpty(keyPrefix) ? "" : keyPrefix + ".";
            ctx.Record(k + "samples", r.Samples);
            ctx.Record(k + "solarFrameDeg", Math.Round(r.FrameDegMean, 2));
            ctx.Record(k + "solarFrameDegRange", $"{r.FrameDegMin:F2}..{r.FrameDegMax:F2}");
            // the parts that anything shades: everything with a mean under 1
            foreach (var id in r.Mean.Keys)
                if (r.Mean[id] < 0.999) ctx.Record($"{k}sun.{r.Name[id]}#{id}", $"{r.Mean[id]:F3} ({r.Min[id]:F3}..{r.Max[id]:F3})");
            done?.Invoke(r);
        }
    }
}
