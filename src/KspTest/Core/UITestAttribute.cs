using System;

namespace KspTest
{
    /// <summary>Where a test expects the game to be when it starts; Nav.EnsureState gets it there from wherever the previous test ended.</summary>
    public enum StartState
    {
        Any,          // the test navigates itself
        MainMenu,
        SpaceCenter,
        Vab,          // editor (VAB) with Craft loaded
        Sph,          // editor (SPH) with Craft loaded
        Flight,       // freshly launched Craft on the pad/runway (reverts to launch if already flying it)
    }

    /// <summary>
    /// Marks a public static IEnumerator method (optionally taking a TestContext) as an in-game test. The test is
    /// Namespace.Class.Method, and goes by its method name where that is unique (TestRunner.DiscoverTests).
    /// Requires is a comma-separated list of environment tags (see Env): "RP-1", "MechJeb", "FAR", ...
    /// A test whose requirements are missing reports "skipped" instead of running.
    /// StartsAt + Craft/CraftDir describe the precondition; the run.cfg PARAMS "craft"/"craftDir" override Craft/CraftDir.
    /// Mods, Save, NewGame, Kind and Isolated are for the runner outside the game: KspTest.Xunit makes a dotnet test of
    /// every [UITest] in a test project's in-game assemblies, which runs it in an install or instance that has them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UITestAttribute : Attribute
    {
        /// <summary>Another name the test can be asked for by (older tests named themselves; the method name is enough).</summary>
        public string Name { get; }
        public string Requires { get; set; }

        /// <summary>
        ///     Mods the test's KSP instance has besides its project's, ";" separated: CKAN identifiers ("Id" or "Id=version"),
        ///     or a mod built locally, as the path of its GameData/ModName folder relative to this project's folder.
        /// </summary>
        public string Mods { get; set; }

        /// <summary>A save template checked into the repository: the name of a folder its test project ships (KspTestSave).</summary>
        public string Save { get; set; }

        /// <summary>Without a Save, the mode of the new game the test starts in: sandbox (the default), science or career.</summary>
        public string NewGame { get; set; }

        /// <summary>The dotnet test's "Kind" trait: Check (asserts what it measures), Measure (records numbers), ...</summary>
        public string Kind { get; set; }

        /// <summary>A KSP session of its own, never shared with other tests (it leaves the game in a state others must not start from).</summary>
        public bool Isolated { get; set; }

        /// <summary>Runs only when asked for by name: "all" and wildcards leave it out (a Serve session, which waits for requests).</summary>
        public bool Explicit { get; set; }

        public StartState StartsAt { get; set; } = StartState.Any;
        public string Craft { get; set; }
        public string CraftDir { get; set; }       // VAB or SPH; SPH for an SPH start, else wherever the craft is found
        public string LaunchSite { get; set; }     // e.g. "Runway" for Flight starts of planes
        public double TimeoutSec { get; set; }     // per-test, the runner outside follows it; 0 = the run's timeoutSec

        public UITestAttribute(string name = null)
        {
            Name = name;
        }
    }
}
