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
    /// Marks a public static IEnumerator method (optionally taking a TestContext) as an in-game test.
    /// Requires is a comma-separated list of environment tags (see Env): "RP-1", "MechJeb", "FAR", ...
    /// A test whose requirements are missing reports "skipped" instead of running.
    /// StartsAt + Craft/CraftDir describe the precondition; the run.cfg PARAMS "craft"/"craftDir" override Craft/CraftDir.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UITestAttribute : Attribute
    {
        public string Name { get; }
        public string Requires { get; set; }
        public StartState StartsAt { get; set; } = StartState.Any;
        public string Craft { get; set; }
        public string CraftDir { get; set; }       // VAB or SPH; defaults from StartsAt
        public string LaunchSite { get; set; }     // e.g. "Runway" for Flight starts of planes
        public double TimeoutSec { get; set; }     // per-test; 0 = share the run timeout

        public UITestAttribute(string name = null)
        {
            Name = name;
        }
    }
}
