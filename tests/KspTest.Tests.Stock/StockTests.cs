using System;
using System.Collections;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>
    /// Tests that need only stock KSP. Each declares where it starts (StartsAt + Craft); the runner
    /// gets the game there from wherever the previous test ended, so several can run per session.
    /// PARAMS in run.cfg override craft / craftDir / launchSite and the per-test numbers.
    /// </summary>
    public static class StockTests
    {
        [UITest("Smoke_MainMenuToVab", StartsAt = StartState.MainMenu)]
        public static IEnumerator Smoke_MainMenuToVab(TestContext ctx)
        {
            string craft = ctx.CraftPath(ctx.Param("craft", "UITest_Plane"), ctx.Param("craftDir", "SPH"));

            ctx.Step("Load save");
            yield return Nav.LoadSave(ctx);
            yield return Snap.Shot(ctx, "space-center");

            ctx.Step("Enter VAB with craft");
            yield return Nav.EnterEditor(ctx, EditorFacility.VAB, craft);
            ctx.Record("partCount", EditorLogic.fetch.ship.parts.Count);
            Assert.True(EditorLogic.fetch.ship.parts.Count > 0, "craft has parts");
            yield return Snap.Shot(ctx, "vab");
        }

        [UITest("Launch_StockButton", StartsAt = StartState.Vab, Craft = "UITest_Plane", CraftDir = "SPH")]
        public static IEnumerator Launch_StockButton(TestContext ctx)
        {
            ctx.Step("Launch");
            yield return Nav.Launch(ctx);
            var v = FlightGlobals.ActiveVessel;
            Assert.NotNull(v, "active vessel");
            ctx.Record("situation", v.situation.ToString());
            ctx.Record("altitude", v.altitude);
            Assert.True(v.situation == Vessel.Situations.PRELAUNCH || v.situation == Vessel.Situations.LANDED,
                "vessel on the pad, situation=" + v.situation);
            yield return Snap.Shot(ctx, "pad");
        }

        /// <summary>
        /// Opens a PAW in the editor and flips its first toggle field; then in flight activates the first
        /// engine through its PAW button and checks the module state.
        /// Params: pawPart (part name/title to use in the editor; default root), pawToggle (field name; default first toggle).
        /// </summary>
        [UITest("Paw_EditorAndFlight", StartsAt = StartState.Vab, Craft = "UITest_Plane", CraftDir = "SPH")]
        public static IEnumerator Paw_EditorAndFlight(TestContext ctx)
        {
            ctx.Step("Open PAW in editor");
            Part part = ctx.Param("pawPart") != null ? Paw.Find(ctx.Param("pawPart")) : EditorLogic.RootPart;
            yield return Paw.Open(part);
            Log.Info(Paw.Dump(part));
            yield return Snap.Shot(ctx, "paw-editor");

            ctx.Step("Flip a toggle");
            string toggleName = ctx.Param("pawToggle");
            if (toggleName == null)
            {
                foreach (var item in Paw.Window(part).ListItems)
                    if (item is UIPartActionToggle t && t.Field != null) { toggleName = t.Field.name; break; }
            }
            if (toggleName == null)
            {
                Log.Warn("no toggle field on " + part.partInfo.title + "; skipping the toggle step");
            }
            else
            {
                bool before = Paw.Get<bool>(part, toggleName);
                yield return Paw.SetToggle(part, toggleName, !before);
                Assert.Equal(!before, Paw.Get<bool>(part, toggleName), $"toggle {toggleName} flipped");
                yield return Paw.SetToggle(part, toggleName, before);
                ctx.Record("toggle", toggleName);
            }
            Paw.Close(part);

            ctx.Step("Launch");
            yield return Nav.Launch(ctx);

            ctx.Step("Activate an engine via its PAW");
            Part enginePart = null;
            ModuleEngines engine = null;
            foreach (var p in FlightGlobals.ActiveVessel.parts)
            {
                foreach (PartModule m in p.Modules)
                    if (m is ModuleEngines me) { enginePart = p; engine = me; break; }
                if (engine != null) break;
            }
            Assert.NotNull(engine, "an engine part on the vessel");
            yield return Paw.ClickEvent(enginePart, "Activate");
            yield return Wait.Until(() => engine.EngineIgnited, 10, "engine ignited (EngineIgnited flag)");
            ctx.Record("engine", enginePart.partInfo.title);
            yield return Snap.Shot(ctx, "paw-flight");
            yield return Paw.ClickEvent(enginePart, "Shutdown");
            yield return Wait.Until(() => !engine.EngineIgnited, 10, "engine shut down");
        }

        /// <summary>
        /// Takes an aircraft off the runway under manual control and does a barrel roll with the stick.
        /// Params: rotateSpeed (m/s), climbPitch (0..1), climbAlt (m), rollInput (-1..1), rollSeconds, minRollDeg.
        /// </summary>
        [UITest("Ctrl_BarrelRoll", StartsAt = StartState.Flight, Craft = "UITest_Plane", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator Ctrl_BarrelRoll(TestContext ctx)
        {
            float rotateSpeed = (float)ctx.ParamDouble("rotateSpeed", 70);
            float climbPitch = (float)ctx.ParamDouble("climbPitch", 0.4);
            double climbAlt = ctx.ParamDouble("climbAlt", 800);
            float rollInput = (float)ctx.ParamDouble("rollInput", 1.0);
            double rollSeconds = ctx.ParamDouble("rollSeconds", 4);
            double minRoll = ctx.ParamDouble("minRollDeg", 300);
            double startAlt = Flight.Altitude;

            ctx.Step("Takeoff");
            Ctrl.Attach();
            Ctrl.Sas(false);
            Ctrl.Brakes(false);
            Ctrl.SetThrottle(1f);
            Ctrl.Stage();
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.SurfaceSpeed > rotateSpeed; }, 120, $"surface speed > {rotateSpeed} m/s");
            Ctrl.Set(pitch: climbPitch);
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.Altitude - startAlt > climbAlt; }, 180, $"climbed {climbAlt} m");
            Ctrl.Gear(false);
            Ctrl.Set(pitch: 0.05f);
            yield return Wait.GameSeconds(3);
            ctx.Record("speedBeforeRoll", Flight.SurfaceSpeed);
            ctx.Record("altBeforeRoll", Flight.Altitude);
            yield return Snap.Shot(ctx, "before-roll");

            ctx.Step("Barrel roll");
            double rolled = 0;
            var integrate = Ctrl.IntegrateRoll(rollSeconds + 1.0, r => rolled = r);
            TestRunner.Instance.StartCoroutine(integrate);
            yield return Ctrl.Hold(pitch: 0.1f, roll: rollInput, seconds: rollSeconds);
            yield return Wait.GameSeconds(1.0);
            Flight.AssertAlive();
            ctx.Record("rollDeg", System.Math.Round(rolled, 1));
            ctx.Record("altAfterRoll", Flight.Altitude);
            yield return Snap.Shot(ctx, "after-roll");
            Assert.True(System.Math.Abs(rolled) >= minRoll, $"rolled {rolled:F0} deg, expected at least {minRoll}");
            Ctrl.Detach();
        }

        /// <summary>Driver self-test: spawns stock PopupDialogs and dismisses them by button text.</summary>
        [UITest("Popup_Dismiss", StartsAt = StartState.SpaceCenter)]
        public static IEnumerator Popup_Dismiss(TestContext ctx)
        {
            ctx.Step("Single-button popup");
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "uitestPopup1",
                "KspTest", "This popup should be dismissed by the harness.", "OK", true, HighLogic.UISkin);
            yield return Wait.Until(() => Ugui.FindPopup("uitestPopup1") != null, 5, "popup open");
            yield return Snap.Shot(ctx, "popup");
            yield return Ugui.ClickPopup("OK", "uitestPopup1");
            yield return Wait.PopupClosed("uitestPopup1", 5);

            ctx.Step("Multi-option popup");
            string chosen = null;
            var dialog = new MultiOptionDialog("uitestPopup2", "Pick the second option.", "KspTest", HighLogic.UISkin,
                new DialogGUIButton("First", () => chosen = "first"),
                new DialogGUIButton("Second", () => chosen = "second"),
                new DialogGUIButton("Cancel", () => chosen = "cancel"));
            PopupDialog.SpawnPopupDialog(dialog, true, HighLogic.UISkin);
            yield return Ugui.ClickPopup("Second", "uitestPopup2");
            yield return Wait.Until(() => chosen != null, 5, "dialog callback fired");
            Assert.Equal("second", chosen, "chosen option");
            yield return Wait.PopupClosed("uitestPopup2", 5);
        }
    }
}
