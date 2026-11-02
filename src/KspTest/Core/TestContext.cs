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

        /// <summary>The start the runner brought the game to for this test: StartsAt, its craft (absolute path) and launch site.</summary>
        public StartState StartsAt { get; internal set; }
        public string Craft { get; internal set; }
        public string LaunchSite { get; internal set; }

        /// <summary>
        /// Absolute path of a craft: a path as it is, else the test save's Ships/subfolder, else the game's own
        /// Ships/subfolder (the stock crafts) when only that has it.  Without a subfolder, VAB and then SPH.
        /// </summary>
        public string CraftPath(string name, string subfolder = null)
        {
            if (!name.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)) name += ".craft";
            if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
            foreach (string sub in subfolder != null ? new[] { subfolder } : new[] { "VAB", "SPH" })
                foreach (string ships in new[] { Path.Combine(SaveDir, "Ships"), Path.Combine(KSPUtil.ApplicationRootPath, "Ships") })
                    if (File.Exists(Path.Combine(ships, sub, name)))
                        return Path.GetFullPath(Path.Combine(ships, sub, name));
            return Path.GetFullPath(Path.Combine(SaveDir, "Ships", subfolder ?? "VAB", name));
        }

        public string Param(string key, string fallback = null)
        {
            return Config.Params.TryGetValue(key, out var v) ? v : fallback;
        }

        public double ParamDouble(string key, double fallback)
        {
            return Config.Params.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
        }

        public float ParamFloat(string key, float fallback) => (float)ParamDouble(key, fallback);

        public int ParamInt(string key, int fallback) => (int)ParamDouble(key, fallback);

        /// <summary>A number, or null when the run does not set it (or sets it to 0 or less).</summary>
        public double? ParamDoubleOrNull(string key) => ParamDouble(key, 0) is var d && d > 0 ? d : (double?)null;

        /// <summary>on/off, true/false, yes/no or 1/0.</summary>
        public bool ParamBool(string key, bool fallback)
        {
            string v = Param(key);
            if (v == null) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "yes": case "1": return true;
                case "off": case "false": case "no": case "0": return false;
                default: throw new UITestFailure($"param {key}={v}: expected on/off");
            }
        }

        /// <summary>A list: "a+b+c" by default, trimmed, empty entries left out.</summary>
        public string[] ParamList(string key, string fallback, char separator = '+') =>
            Array.FindAll(Array.ConvertAll((Param(key, fallback) ?? "").Split(separator), s => s.Trim()), s => s.Length > 0);

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
