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
        /// <summary>
        /// FAR's editor static analysis (the instant sim) at fixed angles of attack: Cl, Cd, Cm at Mach 0.5 and the
        /// pitch input that zeroes Cm, for comparing FAR builds on a craft with a CoM-offset fuselage. Params:
        /// craft (default PoofySkirt), mach (default 0.5), alphas (default "0,5,10,15").
        /// </summary>
        [UITest("Far_ComOffset_Editor", StartsAt = StartState.Sph, Craft = "PoofySkirt", TimeoutSec = 300)]
        public static IEnumerator Far_ComOffset_Editor(TestContext ctx)
        {
            Ugui.DismissPopups();
            yield return Wait.Seconds(8);   // FAR voxelizes the craft after load
            double mach = ctx.ParamDouble("mach", 0.5);
            foreach (string a in (ctx.Param("alphas") ?? "0,5,10,15").Split(','))
            {
                double alpha = double.Parse(a.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(FarApi.EditorClCdCm(alpha, mach, 0.0, out double cl, out double cd, out double cm0), "FAR editor sim call");
                // Secant on the pitch input for Cm = 0.
                FarApi.EditorClCdCm(alpha, mach, -0.5, out _, out _, out double cm1);
                double p0 = 0.0, p1 = -0.5, c0 = cm0, c1 = cm1;
                for (int i = 0; i < 4 && Math.Abs(c1 - c0) > 1e-9; ++i)
                {
                    double p2 = p1 - c1 * (p1 - p0) / (c1 - c0);
                    p2 = Math.Max(-1, Math.Min(1, p2));
                    FarApi.EditorClCdCm(alpha, mach, p2, out _, out _, out double c2);
                    p0 = p1; c0 = c1; p1 = p2; c1 = c2;
                }
                string k = $"alpha{alpha:0}.";
                ctx.Record(k + "Cl", Math.Round(cl, 4)); ctx.Record(k + "Cd", Math.Round(cd, 4)); ctx.Record(k + "Cm", Math.Round(cm0, 5));
                ctx.Record(k + "trimPitchInput", Math.Round(p1, 4)); ctx.Record(k + "CmAtTrim", Math.Round(c1, 6));
            }
        }

        /// <summary>
        /// FAR's voxel grid on a craft with landing gear and control surfaces, sampled in the editor with the gear
        /// deployed (as loaded), retracted and deployed again: element size, bounds, per-part face areas and Cl/Cd/Cm. Made
        /// for VehicleVoxel's sizing change (element size from the bounds of the parts that are neither control
        /// surfaces nor wheels), where the element and every other part's areas should stop moving with the gear.
        /// Params: craft (default PoofySkirt) or crafts (a "+"-separated list, each loaded in turn and its values
        /// prefixed with the craft name), mach (default 0.5), alphas (default "0,5").
        /// </summary>
        [UITest("Far_VoxelSize_Editor", StartsAt = StartState.Sph, Craft = "PoofySkirt", TimeoutSec = 900)]
        public static IEnumerator Far_VoxelSize_Editor(TestContext ctx)
        {
            double mach = ctx.ParamDouble("mach", 0.5);
            string[] alphas = (ctx.Param("alphas") ?? "0,5").Split(',');
            var rows = new System.Text.StringBuilder(FarApi.PartAreasHeader);
            string[] crafts = ctx.Param("crafts") != null ? ctx.Param("crafts").Split('+') : new[] { (string)null };
            foreach (string craftName in crafts)
            {
                string prefix = "";
                if (craftName != null)
                {
                    ctx.Step("Craft " + craftName.Trim());
                    yield return Nav.EnsureState(ctx, StartState.Sph, ctx.CraftPath(craftName.Trim(), "SPH"));
                    prefix = craftName.Trim() + ".";
                }
                Ugui.DismissPopups();
                yield return Wait.Seconds(8);   // FAR voxelizes the craft after load
                yield return VoxelSizeEditorCraft(ctx, prefix, mach, alphas, rows);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(ctx.ResultsDir, "voxelsize-editor.csv"), rows.ToString());
        }

        private static IEnumerator VoxelSizeEditorCraft(TestContext ctx, string prefix, double mach, string[] alphas, System.Text.StringBuilder rows)
        {
            var parts = EditorLogic.fetch.ship.parts;
            var gear = new System.Collections.Generic.List<ModuleWheels.ModuleWheelDeployment>();
            foreach (Part p in parts) gear.AddRange(p.FindModulesImplementing<ModuleWheels.ModuleWheelDeployment>());
            ctx.Record(prefix + "gearParts", gear.Count);

            // the SPH loads the gear deployed; EventToggle propagates to symmetry partners, so toggle one per symmetry group
            var toggles = new System.Collections.Generic.List<ModuleWheels.ModuleWheelDeployment>();
            foreach (var d in gear)
            {
                bool covered = false;
                foreach (var t in toggles) if (t.part == d.part || t.part.symmetryCounterparts.Contains(d.part)) covered = true;
                if (!covered) toggles.Add(d);
            }
            foreach (string stateName in new[] { "gearDown", "gearUp", "gearDown2" })
            {
                string state = prefix + stateName;
                if (stateName != "gearDown")
                {
                    FarApi.PartAreas(parts, null, null, out double before, out _);
                    foreach (var d in toggles) d.Events["EventToggle"].Invoke();
                    yield return Wait.Seconds(3);
                    // the deploy animation re-voxelizes the craft (rate limited); wait for the areas to move, then settle
                    double after = before;
                    yield return Wait.Until(() => { FarApi.PartAreas(parts, null, null, out after, out _); return Math.Abs(after - before) > 1e-6; }, 25, "re-voxelization after the gear toggle", shot: false);
                    yield return Wait.Seconds(5);
                }
                string gs = "";
                foreach (var d in gear) gs += (gs.Length > 0 ? "|" : "") + d.stateString + ":" + d.fsm?.CurrentState?.name;
                ctx.Record(state + ".gearStates", gs);
                Assert.True(FarApi.VoxelInfo(out double el, out Vector3 bs, out double len, out double mcs), "FAR voxel info (" + state + ")");
                FarApi.PartAreas(parts, state, rows, out double areaAll, out double areaFixed);
                ctx.Record(state + ".elementSize", Math.Round(el, 5));
                ctx.Record(state + ".boundsX", Math.Round(bs.x, 3)); ctx.Record(state + ".boundsY", Math.Round(bs.y, 3)); ctx.Record(state + ".boundsZ", Math.Round(bs.z, 3));
                ctx.Record(state + ".boundsVolume", Math.Round(bs.x * bs.y * bs.z, 3));
                ctx.Record(state + ".length", Math.Round(len, 3)); ctx.Record(state + ".maxCrossSection", Math.Round(mcs, 4));
                ctx.Record(state + ".areaAll", Math.Round(areaAll, 3)); ctx.Record(state + ".areaFixed", Math.Round(areaFixed, 3));
                foreach (string a in alphas)
                {
                    double alpha = double.Parse(a.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(FarApi.EditorClCdCm(alpha, mach, 0.0, out double cl, out double cd, out double cm), "FAR editor sim call");
                    string k = $"{state}.alpha{alpha:0}.";
                    ctx.Record(k + "Cl", Math.Round(cl, 4)); ctx.Record(k + "Cd", Math.Round(cd, 5)); ctx.Record(k + "Cm", Math.Round(cm, 5));
                }
            }
        }

        /// <summary>
        /// Screenshots of FAR's debug voxel display in the editor with the gear deployed, retracted and deployed
        /// again, from fixed camera views, to compare voxel size and placement between FAR builds by eye. Params:
        /// crafts ("+"-separated) or craft (default PoofySkirt), views (default "nose": head on from aboveDeg above,
        /// the wingspan filling fill of the frame; also side, quarter, top), aboveDeg (default 10), fill (default 0.9),
        /// fov (camera vertical field of view in degrees, default 20), distance (m from the craft's center, overrides fill).
        /// </summary>
        [UITest("Far_VoxelShot_Editor", StartsAt = StartState.Sph, Craft = "PoofySkirt", TimeoutSec = 900)]
        public static IEnumerator Far_VoxelShot_Editor(TestContext ctx)
        {
            string[] crafts = ctx.Param("crafts") != null ? ctx.Param("crafts").Split('+') : new[] { (string)null };
            string[] views = (ctx.Param("views") ?? "nose").Split(',');
            double aboveDeg = ctx.ParamDouble("aboveDeg", 10), fill = ctx.ParamDouble("fill", 0.9), fov = ctx.ParamDouble("fov", 20), distance = ctx.ParamDouble("distance", 0);
            foreach (string craftName in crafts)
            {
                string prefix = "";
                if (craftName != null)
                {
                    ctx.Step("Craft " + craftName.Trim());
                    yield return Nav.EnsureState(ctx, StartState.Sph, ctx.CraftPath(craftName.Trim(), "SPH"));
                    prefix = craftName.Trim() + "-";
                }
                Ugui.DismissPopups();
                yield return Wait.Seconds(8);   // FAR voxelizes the craft after load
                var parts = EditorLogic.fetch.ship.parts;
                var gear = new System.Collections.Generic.List<ModuleWheels.ModuleWheelDeployment>();
                foreach (Part p in parts) gear.AddRange(p.FindModulesImplementing<ModuleWheels.ModuleWheelDeployment>());
                var toggles = new System.Collections.Generic.List<ModuleWheels.ModuleWheelDeployment>();
                foreach (var d in gear)
                {
                    bool covered = false;
                    foreach (var t in toggles) if (t.part == d.part || t.part.symmetryCounterparts.Contains(d.part)) covered = true;
                    if (!covered) toggles.Add(d);
                }
                var b = new Bounds(parts[0].transform.position, Vector3.zero);
                foreach (Part p in parts) foreach (var r in p.GetComponentsInChildren<Renderer>()) if (r.enabled) b.Encapsulate(r.bounds);
                foreach (string state in new[] { "gearDown", "gearUp", "gearDown2" })
                {
                    if (state != "gearDown")
                    {
                        FarApi.PartAreas(parts, null, null, out double before, out _);
                        foreach (var d in toggles) d.Events["EventToggle"].Invoke();
                        yield return Wait.Seconds(3);
                        double after = before;
                        yield return Wait.Until(() => { FarApi.PartAreas(parts, null, null, out after, out _); return Math.Abs(after - before) > 1e-6; }, 25, "re-voxelization after the gear toggle", shot: false);
                        yield return Wait.Seconds(5);
                    }
                    Assert.True(FarApi.VoxelInfo(out double el, out _, out _, out _), "FAR voxel info");
                    ctx.Record(prefix + state + ".elementSize", Math.Round(el, 5));
                    Assert.True(FarApi.ShowVoxels(), "FAR voxel display");
                    yield return Wait.Seconds(1);
                    foreach (string view in views)
                    {
                        Nav.EditorCamera(view.Trim(), b, aboveDeg, fill, fov, distance);
                        yield return Wait.Seconds(1);
                        yield return Snap.Shot(ctx, $"{prefix}{state}-{view.Trim()}-el{el:0.0000}");
                    }
                    FarApi.HideVoxels();
                }
            }
        }

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
