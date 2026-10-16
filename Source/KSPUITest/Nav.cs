using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Scene navigation using the same stock calls the game's own menus make
    /// (MainMenu.OnLoadDialogPipelineFinished, Game.Start, EditorDriver, EditorLogic.launchVessel).
    /// </summary>
    public static class Nav
    {
        /// <summary>
        /// Integration hook: when a mod replaces the stock launch flow (KCT/RP-1 rewires the Launch
        /// button), its helper library sets this so Launch() goes through that flow instead.
        /// </summary>
        public static Func<TestContext, IEnumerator> LaunchOverride;

        /// <summary>Main menu -> Space Center with the given save folder loaded.</summary>
        public static IEnumerator LoadSave(TestContext ctx, string saveFolder = null, bool dismissPopups = true)
        {
            saveFolder = saveFolder ?? ctx.SaveFolder;
            yield return Wait.Scene(GameScenes.MAINMENU, 600);
            // MainMenu needs a moment to finish its own Start; QuickStart waits similarly
            yield return Wait.Seconds(1.0);

            string sfs = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder, "persistent.sfs");
            if (!File.Exists(sfs)) throw new UITestFailure("save not found: " + sfs);
            Log.Info($"loading save '{saveFolder}'");

            var node = GamePersistence.LoadSFSFile("persistent", saveFolder);
            if (node == null) throw new UITestFailure("could not read " + sfs);
            var game = GamePersistence.LoadGameCfg(node, saveFolder, true, false);
            if (game == null) throw new UITestFailure("save is incompatible or failed to load: " + sfs);

            Directory.CreateDirectory(Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder, "Ships", "VAB"));
            Directory.CreateDirectory(Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder, "Ships", "SPH"));

            HighLogic.CurrentGame = game;
            HighLogic.SaveFolder = saveFolder;
            if (GamePersistence.UpdateScenarioModules(game))
                GamePersistence.SaveGame(game, "persistent", saveFolder, SaveMode.OVERWRITE);
            GameEvents.onGameStatePostLoad.Fire(node);
            game.startScene = GameScenes.SPACECENTER;
            game.Start();
            InputLockManager.ClearControlLocks();

            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
            yield return Wait.Seconds(1.0);
            // mods greet a fresh save with dialogs (kOS connectivity manager, first-run tips); clear them
            if (dismissPopups && Ugui.OpenPopups().Count > 0)
            {
                Ugui.DismissPopups();
                yield return null;
            }
            Log.Info("space center loaded");
        }

        /// <summary>Space Center -> editor, optionally loading a craft (absolute path) on entry.</summary>
        public static IEnumerator EnterEditor(TestContext ctx, EditorFacility facility = EditorFacility.VAB, string craftAbsPath = null)
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER)
                throw new UITestFailure($"EnterEditor called from {HighLogic.LoadedScene}, expected SPACECENTER");
            if (craftAbsPath != null)
            {
                if (!File.Exists(craftAbsPath)) throw new UITestFailure("craft not found: " + craftAbsPath);
                Log.Info($"entering {facility} with craft {craftAbsPath}");
                EditorDriver.StartAndLoadVessel(craftAbsPath, facility);
                LoadedCraft = craftAbsPath;
                LoadedFacility = facility;
            }
            else
            {
                Log.Info($"entering {facility} (clean)");
                EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
                EditorDriver.StartEditor(facility);
                LoadedCraft = null;
                LoadedFacility = facility;
            }
            InputLockManager.ClearControlLocks();
            yield return Wait.Scene(GameScenes.EDITOR, 600);
            yield return Wait.EditorReady(requireShip: craftAbsPath != null, timeoutSec: 300);
            Ugui.DismissPopups();   // modal mod prompts (kOS) lock the editor top bar
            Log.Info("editor ready" + (craftAbsPath != null ? $", ship has {EditorLogic.fetch.ship.parts.Count} parts" : ""));
        }

        /// <summary>Loads a craft (absolute path) into the already-open editor.</summary>
        public static IEnumerator LoadCraft(TestContext ctx, string craftAbsPath)
        {
            if (!Wait.IsEditorReady()) throw new UITestFailure("LoadCraft: editor is not ready");
            if (!File.Exists(craftAbsPath)) throw new UITestFailure("craft not found: " + craftAbsPath);
            Log.Info("loading craft " + craftAbsPath);
            EditorLogic.LoadShipFromFile(craftAbsPath);
            LoadedCraft = craftAbsPath;
            yield return Wait.EditorReady(requireShip: true, timeoutSec: 120);
        }

        /// <summary>
        /// "Start the simulation": the stock path clicks the editor's Launch button (EditorLogic.launchBtn),
        /// i.e. EditorLogic.launchVessel -> FlightDriver.StartWithNewLaunch. Mods that hijack that button
        /// (KCT) register LaunchOverride instead. Ends with the flight scene ready.
        /// </summary>
        public static IEnumerator Launch(TestContext ctx)
        {
            if (!Wait.IsEditorReady() || EditorLogic.fetch.ship == null || EditorLogic.fetch.ship.parts.Count == 0)
                throw new UITestFailure("Launch: editor has no craft loaded");

            if (LaunchOverride != null)
            {
                Log.Info("launching via override (" + LaunchOverride.Method.DeclaringType?.Name + ")");
                yield return LaunchOverride(ctx);
            }
            else
            {
                if (Env.HasRP1)
                    Log.Warn("RP-1 is loaded but no launch override is registered; the stock Launch button is rewired by KCT");
                var btn = EditorLogic.fetch.launchBtn;
                if (btn == null) throw new UITestFailure("EditorLogic.launchBtn is null");
                if (!btn.interactable)
                {
                    // a modal PopupDialog (kOS manager prompt, tips) locks the editor's top bar; the stock delta-v
                    // recalculation after a ship change (a gear toggle counts) greys the button until it is done
                    Ugui.DismissPopups();
                    float t0 = Time.realtimeSinceStartup;
                    while (!btn.interactable && Time.realtimeSinceStartup - t0 < 60) yield return null;
                }
                if (btn.interactable)
                {
                    Log.Info("clicking the stock Launch button");
                    Ugui.Click(btn);
                }
                else
                {
                    Log.Warn("Launch button still not interactable after 60 s; locks: " + InputLockManager.PrintLockStack() + "; calling EditorLogic.launchVessel directly");
                    EditorLogic.fetch.launchVessel();
                }
                // KSP's pre-flight checks (launch site obstructed, no control, ...) put up a "TestWarning"
                // dialog whose proceed option auto-fires after 120 s; take it right away instead.
                // Clearing an occupied pad/runway that way recovers the old vessel, whose recovery summary (a uGUI
                // panel with a Done button) then holds the launch sequence until it is closed.
                yield return Wait.Until(() =>
                {
                    if (HighLogic.LoadedScene == GameScenes.FLIGHT) return true;
                    if (Ugui.FindPopup("TestWarning") != null) Ugui.ClickPopupOption("TestWarning", 0);
                    var done = Ugui.FindButton("Done");
                    if (done != null && done.interactable && done.gameObject.activeInHierarchy) { Log.Info("dismissing the recovery summary"); Ugui.Click(done); }
                    return false;
                }, 600, "flight scene after Launch", shot: false);
                yield return Wait.FlightReady(300);
            }
            LaunchedCraft = LoadedCraft;
            Ugui.DismissPopups();
            Log.Info($"in flight: vessel '{FlightGlobals.ActiveVessel?.vesselName}' at {FlightGlobals.ActiveVessel?.altitude:F0} m");
        }

        // ---- state machine for multi-test sessions ----

        /// <summary>Craft file the editor currently holds (set by EnterEditor/LoadCraft) and the one last launched.</summary>
        public static string LoadedCraft { get; private set; }
        public static string LaunchedCraft { get; private set; }

        /// <summary>For integrations that reach flight by their own route (e.g. Rp1.StartSim): record what is flying.</summary>
        public static void MarkLaunched() => LaunchedCraft = LoadedCraft;
        public static EditorFacility LoadedFacility { get; private set; }

        public static string Describe()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.EDITOR: return $"EDITOR({LoadedFacility} craft={System.IO.Path.GetFileName(LoadedCraft ?? "?")})";
                case GameScenes.FLIGHT: return $"FLIGHT(vessel={FlightGlobals.ActiveVessel?.vesselName} craft={System.IO.Path.GetFileName(LaunchedCraft ?? "?")})";
                default: return HighLogic.LoadedScene.ToString();
            }
        }

        /// <summary>
        /// Brings the game from wherever it is to the requested start state, reusing what is already
        /// there (same craft in the same editor, same craft in flight -> revert to launch) and taking the
        /// shortest sensible route otherwise. craftAbs is required for Vab/Sph/Flight.
        /// </summary>
        public static IEnumerator EnsureState(TestContext ctx, StartState want, string craftAbs = null, string launchSite = null)
        {
            Log.Info($"EnsureState {want} (craft={System.IO.Path.GetFileName(craftAbs ?? "-")}) from {Describe()}");
            switch (want)
            {
                case StartState.Any:
                    yield break;

                case StartState.MainMenu:
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU) yield return ToMainMenu(ctx);
                    yield break;

                case StartState.SpaceCenter:
                    yield return EnsureSpaceCenter(ctx);
                    yield break;

                case StartState.Vab:
                case StartState.Sph:
                    yield return EnsureEditor(ctx, want == StartState.Vab ? EditorFacility.VAB : EditorFacility.SPH, craftAbs);
                    yield break;

                case StartState.Flight:
                    if (craftAbs == null) throw new UITestFailure("StartsAt Flight needs a Craft");
                    if (HighLogic.LoadedSceneIsFlight && LaunchedCraft == craftAbs && FlightDriver.CanRevertToPostInit)
                    {
                        yield return RevertToLaunch(ctx);
                        yield break;
                    }
                    var facility = craftAbs.IndexOf("\\SPH\\", StringComparison.OrdinalIgnoreCase) >= 0 || craftAbs.IndexOf("/SPH/", StringComparison.OrdinalIgnoreCase) >= 0
                        ? EditorFacility.SPH : EditorFacility.VAB;
                    yield return EnsureEditor(ctx, facility, craftAbs);
                    if (launchSite != null) EditorLogic.fetch.launchSiteName = launchSite;
                    yield return Launch(ctx);
                    yield break;
            }
        }

        private static IEnumerator EnsureSpaceCenter(TestContext ctx)
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.SPACECENTER: yield break;
                case GameScenes.MAINMENU: yield return LoadSave(ctx); yield break;
                default: yield return ToSpaceCenter(ctx); yield break;
            }
        }

        private static IEnumerator EnsureEditor(TestContext ctx, EditorFacility facility, string craftAbs)
        {
            if (craftAbs == null) throw new UITestFailure("StartsAt Vab/Sph needs a Craft");
            if (HighLogic.LoadedSceneIsEditor && Wait.IsEditorReady() && LoadedFacility == facility)
            {
                if (LoadedCraft != craftAbs) yield return LoadCraft(ctx, craftAbs);
                yield break;
            }
            // From a flight that can still revert, revert rather than walk out through the space center: the revert
            // takes the flying vessel and its crew back, so the next launch is not left with nobody to fly it (a craft
            // launched with "No Control" cannot be flown by MechJeb either).
            if (HighLogic.LoadedSceneIsFlight && LoadedFacility == facility && FlightDriver.CanRevertToPrelaunch)
            {
                yield return RevertToEditor(ctx, facility);
                if (LoadedCraft != craftAbs) yield return LoadCraft(ctx, craftAbs);
                yield break;
            }
            yield return EnsureSpaceCenter(ctx);
            yield return EnterEditor(ctx, facility, craftAbs);
        }

        /// <summary>Flight -> same flight restarted at launch (stock "Revert to Launch").</summary>
        public static IEnumerator RevertToLaunch(TestContext ctx)
        {
            if (!HighLogic.LoadedSceneIsFlight) throw new UITestFailure("RevertToLaunch: not in flight");
            if (!FlightDriver.CanRevertToPostInit) throw new UITestFailure("RevertToLaunch: revert not available");
            Log.Info("reverting to launch");
            var oldDriver = FlightDriver.fetch;
            FlightDriver.RevertToLaunch();
            yield return Wait.Until(() => FlightDriver.fetch != null && FlightDriver.fetch != oldDriver, 300, "flight scene reloaded after revert", shot: false);
            yield return Wait.FlightReady(300);
        }

        public static IEnumerator ToMainMenu(TestContext ctx)
        {
            Log.Info("going to the main menu");
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER)
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.MAINMENU);
            yield return Wait.Scene(GameScenes.MAINMENU, 600);
            yield return Wait.Seconds(1.0);
        }

        /// <summary>
        /// Integration hook for leaving a flight (KCT simulations must not be saved or recovered).
        /// Return null to let the stock logic run.
        /// </summary>
        public static Func<TestContext, IEnumerator> LeaveFlightOverride;

        /// <summary>
        /// Flight/editor -> Space Center. A vessel still sitting where it was launched is recovered
        /// (as a player would), so the launch site is clear for the next test; anything else is saved.
        /// </summary>
        public static IEnumerator ToSpaceCenter(TestContext ctx)
        {
            Log.Info("going to Space Center from " + Describe());
            if (HighLogic.LoadedSceneIsFlight)
            {
                var custom = LeaveFlightOverride?.Invoke(ctx);
                if (custom != null)
                {
                    yield return custom;
                }
                else
                {
                    var v = FlightGlobals.ActiveVessel;
                    bool onSite = v != null && (v.situation == Vessel.Situations.PRELAUNCH
                        || (v.LandedOrSplashed && (v.landedAt ?? "").IndexOf("Launch", StringComparison.OrdinalIgnoreCase) >= 0)
                        || (v.LandedOrSplashed && (v.landedAt ?? "").IndexOf("Runway", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (onSite)
                    {
                        Log.Info($"recovering '{v.vesselName}' from {v.landedAt}");
                        GameEvents.OnVesselRecoveryRequested.Fire(v);   // stock recover: removes it and returns to the KSC
                    }
                    else
                    {
                        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                        HighLogic.LoadScene(GameScenes.SPACECENTER);
                    }
                }
            }
            else
            {
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
            yield return Wait.Seconds(1.0);
            Ugui.DismissPopups();   // recovery report, mod prompts
        }

        /// <summary>
        /// Points the editor camera at the craft from a named view. "nose": head on from a little above the nose,
        /// close enough that the wingspan fills the frame. "side", "quarter", "top": from the right, front right and
        /// raised, above. The craft's nose is its root part's up axis (a cockpit's forward), flattened.
        /// </summary>
        public static void EditorCamera(string view, Bounds craft, double aboveDeg = 10, double fill = 0.9, double fovDeg = 20, double distanceOverride = 0)
        {
            // EditorLogic.editorCamera is the Unity Camera; the controller (VABCamera / SPHCamera) with pivotPosition and
            // SetCamCoordsFromPosition is a component of its own
            // both controllers can be alive in either facility; take every one of them out of the loop
            object cam = null; int off = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name == "SPHCamera" || mb.GetType().Name == "VABCamera")) { cam = cam ?? mb; mb.enabled = false; ++off; }
            if (cam == null) { Log.Warn("no VABCamera/SPHCamera found"); return; }
            Camera unity = EditorLogic.fetch.editorCamera ?? Camera.main;
            Log.Info($"editor camera: {off} controllers disabled; camera '{unity.name}' parent '{unity.transform.parent?.name}' at {unity.transform.position}");
            Vector3 nose = Vector3.ProjectOnPlane(EditorLogic.RootPart.transform.up, Vector3.up);
            if (nose.sqrMagnitude < 1e-4f) nose = Vector3.ProjectOnPlane(EditorLogic.RootPart.transform.forward, Vector3.up);
            nose.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, nose).normalized;
            double span = 2 * Math.Max(Math.Abs(Vector3.Dot(craft.extents, right)), Math.Abs(Vector3.Dot(craft.extents, nose)) * 0 + Math.Abs(Vector3.Dot(craft.extents, right)));
            double length = 2 * Math.Abs(Vector3.Dot(craft.extents, nose)), height = 2 * craft.extents.y;
            unity.fieldOfView = (float)fovDeg;   // telephoto: flatter perspective, the voxels larger on screen
            double hfov = 2 * Math.Atan(Math.Tan(fovDeg * Math.PI / 360.0) * unity.aspect);
            Vector3 dir; double extent;
            switch (view.ToLowerInvariant())
            {
                case "nose": dir = nose; extent = span; break;
                case "top": dir = Vector3.up * 0.999f + nose * 0.05f; extent = Math.Max(span, length); break;
                case "quarter": dir = (nose + right).normalized * 0.9f + Vector3.up * 0.45f; extent = Math.Max(span, length); break;
                default: dir = right; extent = length; break;   // side
            }
            double dist = distanceOverride > 0 ? distanceOverride : extent / fill / (2 * Math.Tan(hfov / 2));   // measured to the craft's center
            Vector3 pos = craft.center + (Quaternion.AngleAxis(-(float)aboveDeg, Vector3.Cross(Vector3.up, dir).normalized) * dir.normalized) * (float)dist;
            if (view.ToLowerInvariant() != "nose") pos = craft.center + dir.normalized * (float)dist;
            // The controller recomputes its pose every frame from its own state, so take it out of the loop and place
            // the Unity camera directly. It stays off for the rest of the session (a test that needs the stock camera
            // back can re-enable the component).
            // the editor renders with a near and a far camera (the far one draws what lies beyond the near clip, e.g. a
            // 37 m plane seen from 100 m): pose and field of view go to every camera in the scene
            var names = new System.Collections.Generic.List<string>();
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c.name.StartsWith("UI")) continue;   // the UI cameras stay where they are
                // the scene cameras hang under the editor's camera pivot, which keeps easing after the controllers are
                // off; take them off it so nothing carries them once posed
                if (c.transform.parent != null) c.transform.SetParent(null, true);
                c.fieldOfView = (float)fovDeg;
                c.transform.position = pos;
                c.transform.LookAt(craft.center, Vector3.up);
                names.Add(c.name);
            }
            Log.Info("cameras posed: " + string.Join(", ", names));
            Log.Info($"editor camera ({cam.GetType().Name}) {view}: dist {dist:F1} fov {unity.fieldOfView:F0}/{hfov * 180 / Math.PI:F0} span {span:F1} length {length:F1} pivot {craft.center} pos {pos}");
        }

        /// <summary>Flight -> editor via the stock "Revert to VAB/SPH" path.</summary>
        public static IEnumerator RevertToEditor(TestContext ctx, EditorFacility facility = EditorFacility.VAB)
        {
            if (!HighLogic.LoadedSceneIsFlight) throw new UITestFailure("RevertToEditor: not in flight");
            Log.Info("reverting to " + facility);
            FlightDriver.RevertToPrelaunch(facility);
            LoadedCraft = LaunchedCraft;
            LoadedFacility = facility;
            yield return Wait.Scene(GameScenes.EDITOR, 600);
            yield return Wait.EditorReady(requireShip: true, timeoutSec: 300);
            Ugui.DismissPopups();
            // A vessel-recovery summary ("<ship> Recovery Dialog", a uGUI panel with a Done button, not a PopupDialog)
            // can follow a revert; it holds the MissionSummaryDialog / EditorLogic_launchSequence locks, so Launch
            // would wait forever behind it.
            yield return Wait.Frames(2);
            var done = Ugui.FindButton("Done");
            if (done != null && done.interactable)
            {
                Log.Info("dismissing the recovery summary after the revert");
                Ugui.Click(done);
                yield return Wait.Frames(2);
            }
        }
    }
}
