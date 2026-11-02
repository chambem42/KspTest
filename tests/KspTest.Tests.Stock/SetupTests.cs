using System.Collections;
using KspTest;

namespace KspTest.Tests.Stock
{
    /// <summary>
    /// What a test asks to run in: a CKAN mod by its identifier and a mod built locally by its GameData folder, each in
    /// that test's KSP and in no other test's, and a save template checked in with the tests.
    /// </summary>
    public static class SetupTests
    {
        private const string CkanMod = "PersistentRotation";
        private const string LocalMod = "KspTestLocalMod";

        /// <summary>A CKAN mod by its identifier: KspTest installs it in the test's instance.</summary>
        [UITest(Kind = "Check", Mods = CkanMod, StartsAt = StartState.MainMenu)]
        public static IEnumerator Mods_Ckan()
        {
            Assert.True(Env.HasAssembly(CkanMod), $"{CkanMod} (from CKAN) is loaded");
            Assert.False(Env.HasAssembly(LocalMod), $"{LocalMod} (another test's) is not loaded");
            yield break;
        }

        /// <summary>
        /// A mod built locally (tests/KspTest.LocalMod), by the path of its GameData folder: like a CKAN mod, an instance of
        /// its own (its manifest lists the mod as local) with the mod copied in.
        /// </summary>
        [UITest(Kind = "Check", Mods = "../KspTest.LocalMod/GameData/" + LocalMod, StartsAt = StartState.MainMenu)]
        public static IEnumerator Mods_Local()
        {
            Assert.True(Env.HasAssembly(LocalMod), $"{LocalMod} (built locally) is loaded");
            Assert.False(Env.HasAssembly(CkanMod), $"{CkanMod} (another test's) is not loaded");
            string manifest = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "KspTest-instance.json");
            if (System.IO.File.Exists(manifest))
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(manifest), "\"local\":\\s*\\[\\s*\"" + LocalMod + "\""),
                    $"the instance was made for {LocalMod} (its manifest's local mods)");
            yield break;
        }

        /// <summary>Neither: a test that asks for no mods has none of the others' (a local mod an earlier run copied in is gone).</summary>
        [UITest(Kind = "Check", StartsAt = StartState.MainMenu)]
        public static IEnumerator Mods_None()
        {
            Assert.False(Env.HasAssembly(CkanMod), $"{CkanMod} is not loaded");
            Assert.False(Env.HasAssembly(LocalMod), $"{LocalMod} is not loaded");
            yield break;
        }

        /// <summary>A save template the test project ships (saves/stock-new, Seed 424242): the test plays in a copy of it.</summary>
        [UITest(Kind = "Check", Save = "stock-new", StartsAt = StartState.SpaceCenter)]
        public static IEnumerator Save_Template()
        {
            Assert.Equal(424242, HighLogic.CurrentGame.Seed, "the game's seed (the template's)");
            yield break;
        }
    }
}
