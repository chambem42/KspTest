using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KspTest
{
    /// <summary>Per-test state handed to the test method: config, parameters, step log, recorded values.</summary>
    public class TestContext
    {
        public class StepRecord
        {
            public string Name;
            public string Status = "running";
            public float StartedAt;
            public float EndedAt;
        }

        public RunConfig Config { get; }
        public string TestName { get; }
        public List<StepRecord> Steps { get; } = new List<StepRecord>();
        public List<string> Screenshots { get; } = new List<string>();
        public int ShotCount;
        /// <summary>The scenario a multi-scenario test is on (1-based index); screenshots carry both in their name.</summary>
        public string ScenarioName;
        public int ScenarioIndex;
        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>();

        public TestContext(RunConfig config, string testName)
        {
            Config = config;
            TestName = testName;
        }

        public string ResultsDir => Config.ResultsDir;
        public string SaveFolder => Config.Save;
        public string SaveDir => Path.Combine(KSPUtil.ApplicationRootPath, "saves", Config.Save);

        /// <summary>
        /// Absolute path of a craft: a path as it is, else the test save's Ships/subfolder (VAB by default), else the
        /// game's own Ships/subfolder (the stock crafts) when only that has it.
        /// </summary>
        public string CraftPath(string name, string subfolder = "VAB")
        {
            if (!name.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)) name += ".craft";
            if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
            string inSave = Path.GetFullPath(Path.Combine(SaveDir, "Ships", subfolder, name));
            string stock = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "Ships", subfolder, name));
            return !File.Exists(inSave) && File.Exists(stock) ? stock : inSave;
        }

        public string Param(string key, string fallback = null)
        {
            return Config.Params.TryGetValue(key, out var v) ? v : fallback;
        }

        public double ParamDouble(string key, double fallback)
        {
            return Config.Params.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
        }

        /// <summary>
        /// The scenario a multi-scenario test is on, for the on-screen status ("scenario 2/4: capsule-descent").
        /// Null when the test has no scenarios; cleared by the runner when the test ends.
        /// </summary>
        public static string ScenarioStatus;

        /// <summary>Announces the scenario a test is starting (index is 1-based).</summary>
        public void Scenario(string name, int index, int total)
        {
            ScenarioStatus = $"scenario {index}/{total}: {name}";
            ScenarioName = name;
            ScenarioIndex = index;
            Log.Info($"SCENARIO {index}/{total}: {name}");
        }

        /// <summary>Marks the start of a named step (the previous step is closed as passed).</summary>
        public void Step(string name)
        {
            CloseStep("pass");
            Steps.Add(new StepRecord { Name = name, StartedAt = Time.realtimeSinceStartup });
            Log.Info($"STEP {Steps.Count}: {name}");
        }

        internal void CloseStep(string status)
        {
            if (Steps.Count == 0) return;
            var last = Steps[Steps.Count - 1];
            if (last.Status != "running") return;
            last.Status = status;
            last.EndedAt = Time.realtimeSinceStartup;
        }

        /// <summary>Records a value (e.g. an asserted number) into result.json under "values".</summary>
        public void Record(string key, object value)
        {
            Values[key] = value;
            Log.Info($"RECORD {key} = {value}");
        }
    }
}
