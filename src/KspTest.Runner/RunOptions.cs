using System;
using System.Collections.Generic;

namespace KspTest.Runner
{
    /// <summary>What to run and how: the same knobs run-test.ps1 had.</summary>
    public sealed class RunOptions
    {
        /// <summary>Test names, comma separated, wildcards allowed; "list" discovers the tests deployed; "all" runs them all.</summary>
        public string Test = "";

        /// <summary>The KSP install (its root folder).</summary>
        public string Install = "";

        /// <summary>Save template folder copied fresh into the install's saves/&lt;SaveFolder&gt; before the run; null with NoReset.</summary>
        public string? SaveTemplate;

        /// <summary>The install's save folder the tests play in.</summary>
        public string SaveFolder = "ksptest";

        /// <summary>Reuse saves/&lt;SaveFolder&gt; as it is.</summary>
        public bool NoReset;

        public int TimeoutSec = 900;

        /// <summary>Leave KSP open after the tests.</summary>
        public bool Keep;

        public int Width = 1920;
        public int Height = 1080;

        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the run's results folder goes.</summary>
        public string ResultsRoot = "results";

        /// <summary>Where files someone saved in the test save (crafts, subassemblies) are kept before a reset deletes them.</summary>
        public string? SaveBackupRoot;

        /// <summary>
        ///     A KSP whose load goes quiet this long before the first test starts (KSP.log and KSPCF's texture cache both
        ///     unwritten) is killed and started again, up to LoadRetries times.  A modded install now and then hangs in the
        ///     texture load; a healthy RP-1 load never went more than 19 s without a KSP.log line, the long quiet one is
        ///     KSPCF's FastLoader rebuilding its texture cache, which writes the cache as it goes.
        /// </summary>
        public int LoadStallSec = 120;

        public int LoadRetries = 2;

        /// <summary>Once the tests run: KSP.log and the harness's log both silent this long is a frozen game, killed.</summary>
        public int RunStallSec = 600;

        /// <summary>bdwgc's GC_FREE_SPACE_DIVISOR for this KSP (0: leave it unset).</summary>
        public int GcDivisor;

        /// <summary>The install's MASTER_VOLUME at 0 for the run, the player's put back after it.</summary>
        public bool Mute;

        /// <summary>How long to wait for another KSP (any install) or another run holding the lock before giving up.</summary>
        public int WaitForOtherKspSec = 7200;

        /// <summary>The lock file every KSP launcher on this machine shares; null: no locking.</summary>
        public string? LockFile;

        /// <summary>Progress and warnings.</summary>
        public Action<string> Log = Console.WriteLine;

        /// <summary>"key=value;key=value" into Params.</summary>
        public RunOptions WithParams(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return this;
            foreach (string kv in text!.Split(';', ','))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0)
                    Params[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1).Trim();
            }

            return this;
        }
    }
}
