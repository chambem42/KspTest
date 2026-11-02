using System.Collections;
using System.Linq;
using RP0;
using ROUtils.DataTypes;
using UnityEngine;

namespace KspTest.RP1
{
    /// <summary>
    /// A new RP-1 career the tests can build, simulate and fly anything in, made as Nav.NewGame starts one: RP-1's
    /// difficulty requirements met before the game is made (so it stops for no popup), and once at the Space Center
    /// KCT's first-run window off, every tech node researched with its parts bought and part upgrades unlocked, the
    /// facilities at their top level, a pad launch complex for any craft as the active one, funds and four crew. The run
    /// parameter rp1Career=new leaves the career as RP-1 starts it.
    /// </summary>
    public static class Rp1Career
    {
        private static bool AsRp1StartsIt => TestRunner.Config != null && TestRunner.Config.Params.TryGetValue("rp1Career", out string v) &&
                                             v == "new";

        /// <summary>What RP-1's FirstStart would otherwise change, and say so in a popup.</summary>
        public static void SetParameters(Game.Modes mode, GameParameters parameters)
        {
            if (mode != Game.Modes.CAREER)
                return;
            parameters.Difficulty.BypassEntryPurchaseAfterResearch = false;
            parameters.CustomParams<GameParameters.AdvancedParams>().PartUpgradesInCareer = true;
        }

        public static IEnumerator Ready(Game.Modes mode)
        {
            if (mode != Game.Modes.CAREER || AsRp1StartsIt)
                yield break;
            // RP-1 makes the starting launch complexes on the career's first Space Center visit
            yield return Wait.Until(() => SpaceCenterManagement.Instance != null && !SpaceCenterManagement.Instance.IsFirstStart, 60,
                "RP-1's first Space Center visit", shot: false);
            SpaceCenterManagement scm = SpaceCenterManagement.Instance;
            scm.DontShowFirstRunAgain = true;
            KCT_GUI.GUIStates.ShowFirstRun = false;

            int techs = UnlockAllTech();
            foreach (Upgradeables.UpgradeableFacility f in Object.FindObjectsOfType<Upgradeables.UpgradeableFacility>())
                f.SetLevel(f.MaxLevel);
            LCSpaceCenter sc = scm.ActiveSC;
            sc.LaunchComplexes.Add(new LaunchComplex(new LCData("KspTest", 10000f, 10000f, new Vector3(100f, 200f, 100f), LaunchComplexType.Pad, true,
                new PersistentDictionaryValueTypes<string, double>()), sc) { IsOperational = true });
            sc.SwitchLaunchComplex(sc.LaunchComplexes.Count - 1);
            Funding.Instance?.SetFunds(1e9, TransactionReasons.Cheating);
            for (int i = 0; i < 4; i++)
                HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew).rosterStatus = ProtoCrewMember.RosterStatus.Available;
            Log.Info($"RP-1 career for tests: {techs} tech nodes researched and their parts bought, facilities at top level, pad LC 'KspTest' active, 4 crew");
            yield return null;
        }

        /// <summary>Every node researched, its parts bought and its part upgrades unlocked, as RP-1's ScenarioHandler.UnlockTech does; the nodes.</summary>
        private static int UnlockAllTech()
        {
            ILookup<string, AvailablePart> parts = PartLoader.LoadedPartsList.ToLookup(p => p.TechRequired);
            int n = 0;
            foreach (ProtoTechNode node in AssetBase.RnDTechTree.GetTreeTechs())
            {
                node.state = RDTech.State.Available;
                node.partsPurchased = parts[node.techID].ToList();
                ResearchAndDevelopment.Instance.SetTechState(node.techID, node);
                foreach (PartUpgradeHandler.Upgrade up in PartUpgradeManager.Handler.GetUpgradesForTech(node.techID))
                    PartUpgradeManager.Handler.SetUnlocked(up.name, true);
                n++;
            }

            return n;
        }
    }
}
