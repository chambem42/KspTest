using HarmonyLib;

namespace KspTest
{
    /// <summary>
    ///     Stock KSP's welcome tutorial for a new game (ScenarioNewGameIntro: Gene at the KSC, Wernher in the VAB and SPH, the
    ///     Tracking Station), which locks input until its dialogs are clicked through, marked done as it loads during a run.
    ///     CreateNewGame writes it into the new save itself, so leaving it out of the game is not enough.
    /// </summary>
    public static class NewGameIntro
    {
        private static Harmony _harmony;

        public static void Suppress()
        {
            if (_harmony != null)
                return;
            _harmony = new Harmony("KspTest.NewGameIntro");
            _harmony.Patch(AccessTools.Method(typeof(ScenarioNewGameIntro), nameof(ScenarioNewGameIntro.OnLoad)),
                postfix: new HarmonyMethod(typeof(NewGameIntro), nameof(AfterLoad)));
        }

        private static void AfterLoad(ScenarioNewGameIntro __instance)
        {
            __instance.kscComplete = true;
            __instance.editorComplete = true;
            __instance.tsComplete = true;
        }
    }
}
