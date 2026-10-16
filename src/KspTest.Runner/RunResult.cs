using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace KspTest.Runner
{
    /// <summary>A run's outcome: the harness's result.json, or why there is none.</summary>
    public sealed class RunResult
    {
        /// <summary>pass, fail, skipped, crash, timeout from the harness; load-hang, frozen, no-result from the runner.</summary>
        public string Status = "unknown";

        public double DurationSec;
        public string? Scene;
        public string ResultsDir = "";
        public List<TestResult> Tests = new List<TestResult>();

        /// <summary>Lines with "Exception" that KSP.log got while the tests ran (between the harness's BEGIN and END).</summary>
        public List<string> Exceptions = new List<string>();

        public bool Passed => Status == "pass" || Status == "skipped";

        public TestResult? this[string test] => Tests.FirstOrDefault(t => t.Test == test);

        public static RunResult Read(string resultsDir, string fallbackStatus)
        {
            var r = new RunResult { ResultsDir = resultsDir };
            string file = Path.Combine(resultsDir, "result.json");
            if (!File.Exists(file))
            {
                r.Status = fallbackStatus == "unknown" ? "no-result" : fallbackStatus;
                return r;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            r.Status = Str(root, "status") ?? "unknown";
            r.DurationSec = Num(root, "durationSec");
            r.Scene = Str(root, "scene");
            if (root.TryGetProperty("tests", out JsonElement tests))
                foreach (JsonElement t in tests.EnumerateArray())
                    r.Tests.Add(TestResult.From(t));

            string log = Path.Combine(resultsDir, "KSP.log");
            if (File.Exists(log))
            {
                bool inRun = false;
                foreach (string line in File.ReadLines(log))
                {
                    if (line.Contains("[KspTest] BEGIN"))
                        inRun = true;
                    else if (inRun && line.Contains("Exception"))
                        r.Exceptions.Add(line.Trim());
                    if (line.Contains("[KspTest] END"))
                        break;
                }

                if (r.Exceptions.Count > 0)
                    File.WriteAllLines(Path.Combine(resultsDir, "exceptions.txt"), r.Exceptions);
            }

            return r;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            foreach (TestResult t in Tests)
                sb.Append(t.Report());
            if (Tests.Count > 0 || Scene != null)
                sb.AppendLine($"==== RUN: {Status.ToUpperInvariant()}  ({DurationSec}s total, {Tests.Count} tests, ended in {Scene}) ====");
            else
                sb.AppendLine($"==== RUN: {Status.ToUpperInvariant()} - no result.json (KSP crashed or the harness never started); see {Path.Combine(ResultsDir, "KSP.log")}");
            if (Exceptions.Count > 0)
                sb.AppendLine($"  exceptions in KSP.log during the run: {Exceptions.Count} (first: {Exceptions[0]})");
            sb.AppendLine($"  results: {ResultsDir}");
            return sb.ToString();
        }

        internal static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

        internal static double Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }

    public sealed class TestResult
    {
        public string Test = "";
        public string Status = "";
        public string Message = "";
        public double DurationSec;
        public List<(string Name, string Status, double Sec)> Steps = new List<(string, string, double)>();

        /// <summary>What the test recorded (ctx.Record), as text.</summary>
        public Dictionary<string, string> Values = new Dictionary<string, string>();

        public List<string> Screenshots = new List<string>();

        public bool Passed => Status == "pass" || Status == "skipped";

        internal static TestResult From(JsonElement t)
        {
            var r = new TestResult
            {
                Test = RunResult.Str(t, "test") ?? "",
                Status = RunResult.Str(t, "status") ?? "",
                Message = RunResult.Str(t, "message") ?? "",
                DurationSec = RunResult.Num(t, "durationSec"),
            };
            if (t.TryGetProperty("steps", out JsonElement steps))
                foreach (JsonElement s in steps.EnumerateArray())
                    r.Steps.Add((RunResult.Str(s, "name") ?? "", RunResult.Str(s, "status") ?? "", RunResult.Num(s, "sec")));
            if (t.TryGetProperty("values", out JsonElement values) && values.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in values.EnumerateObject())
                    r.Values[p.Name] = p.Value.ToString();
            if (t.TryGetProperty("screenshots", out JsonElement shots) && shots.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in shots.EnumerateArray())
                    r.Screenshots.Add(s.ToString());
            return r;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {Test}: {Status.ToUpperInvariant()}  ({DurationSec}s) ====");
            if (!string.IsNullOrEmpty(Message))
                sb.AppendLine(Message);
            foreach (var s in Steps)
                sb.AppendLine($"  [{s.Status,-7}] {s.Sec,6:F1}s  {s.Name}");
            foreach (var v in Values)
                sb.AppendLine($"  {v.Key} = {v.Value}");
            if (Screenshots.Count > 0)
                sb.AppendLine($"  screenshots: {string.Join(", ", Screenshots)}");
            return sb.ToString();
        }
    }
}
