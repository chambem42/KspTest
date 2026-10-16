using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RP0;
using UnityEngine;

namespace KspTest.RP1
{
    /// <summary>
    /// Registers the KCT simulation flow as the harness's Launch() implementation when RP-1 is loaded.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Rp1Integration : MonoBehaviour
    {
        private void Awake()
        {
            // simFailures=false unticks the sim's "Enable Part Failures": it starts ticked whatever the save's
            // DisableFailuresInSim says, so TestFlight failures (ignitions included) are on in a sim unless this is set
            Nav.LaunchOverride = ctx =>
            {
                var spec = SimSpec.OnPad();
                string f = ctx.Param("simFailures")?.Trim().ToLowerInvariant();
                if (f != null) spec.Failures = f == "on" || f == "true" || f == "1";
                return Rp1.Simulate(ctx, spec);
            };
            // a KCT simulation must not be saved or recovered: just leave, RP-1 restores its backup save
            Nav.LeaveFlightOverride = ctx => Rp1.IsSimulatedFlight ? Rp1.LeaveSimulation(ctx) : null;
            Log.Info("RP-1 integration: Launch() = KCT simulation on the pad");
            Destroy(this);
        }
    }

    /// <summary>What to type into the KCT "Simulation Configuration" dialog.</summary>
    public class SimSpec
    {
        public string Mode;                 // selection-grid label: Circular, Elliptical, Hyperbolic, Reentry
        public string Body;                 // body name (CelestialBody.name, e.g. "Mars"); null leaves the dialog's choice
        public bool StartInOrbit;
        public double? OrbitAltKm, PeKm, ApKm, IncDeg, LanDeg, ArgPeDeg, EntrySpeed, EntryAltKm, LeadTimeSec;
        /// <summary>Absolute UT (s) the simulation starts at; null leaves the dialog's "From Now" as it is.</summary>
        public double? UT;
        public string Arrival;              // Reentry: "Apoapsis" or "Entry speed" grid label
        public string Attitude;             // Reentry: Prograde / Retrograde / Leave
        public bool? LogTelemetry;
        public double? PropellantPct;       // Reentry: engine propellant remaining, % of capacity
        public bool? Failures;
        // Hyperbolic: origin body name, then the dialog's text fields as typed (null leaves a field as it is)
        public string Origin, TransferTime, InsertionDv, HypPeKm, TimeToPe;

        public static SimSpec OnPad() => new SimSpec { StartInOrbit = false };
        public static SimSpec Hyperbolic(string target, string origin, string transferTime, string insertionDv, string peKm, double departureUT) =>
            new SimSpec { StartInOrbit = true, Mode = "Hyperbolic", Body = target, Origin = origin, TransferTime = transferTime, InsertionDv = insertionDv, HypPeKm = peKm, UT = departureUT };
        public static SimSpec Circular(double altKm, double incDeg = 0) => new SimSpec { StartInOrbit = true, Mode = "Circular", OrbitAltKm = altKm, IncDeg = incDeg };
        public static SimSpec Elliptical(double peKm, double apKm, double incDeg = 0) => new SimSpec { StartInOrbit = true, Mode = "Elliptical", PeKm = peKm, ApKm = apKm, IncDeg = incDeg };
        public static SimSpec Reentry(double peKm, double entrySpeed, double? entryAltKm = null, string attitude = "Retrograde") =>
            new SimSpec { StartInOrbit = true, Mode = "Reentry", PeKm = peKm, Arrival = "Entry speed", EntrySpeed = entrySpeed, EntryAltKm = entryAltKm, Attitude = attitude, LogTelemetry = true };
    }

    /// <summary>
    /// RP-1 / KCT driver. Windows are IMGUI, so everything goes through Imgui by label + window title:
    /// "Integration Info" (KCT editor window), "Simulation Configuration", "Simulation" (in flight),
    /// "Space Center Management" (build list), and the RP-1 top window.
    /// </summary>
    public static class Rp1
    {
        public const string EditorWindow = "Integration Info";
        public const string SimConfigWindow = "Simulation Configuration";
        public const string SimWindow = "Simulation";
        public const string BuildListWindow = "Space Center Management";
        public const string TopWindowTitle = "RP-1";

        public static SpaceCenterManagement Scm => SpaceCenterManagement.Instance ?? throw new UITestFailure("SpaceCenterManagement.Instance is null");
        public static SimulationParams SimParams => Scm.SimulationParams;
        public static bool IsSimulatedFlight => SpaceCenterManagement.Instance != null && SpaceCenterManagement.Instance.IsSimulatedFlight;

        /// <summary>SimulationParams.SimOrbitMode as a string ("Circular", "Reentry", ...) via reflection, since the
        /// enum only exists on newer RP-1 builds; "(none)" when the field is absent.</summary>
        public static string SimMode
        {
            get
            {
                try { return Reflect.Get(SimParams, "SimOrbitMode")?.ToString() ?? "(null)"; }
                catch (UITestFailure) { return "(none)"; }
            }
        }

        private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

        // ---- RP-1 top window ----

        public static IEnumerator ShowTopWindow(UIBase.UITab? tab = null)
        {
            if (UIHolder.Instance == null) throw new UITestFailure("RP-1 UIHolder is not present in this scene");
            UIHolder.Instance.ShowWindow();
            if (tab.HasValue) TopWindow.SwitchTabTo(tab.Value);
            yield return Imgui.WaitWindow(TopWindowTitle);
        }

        public static IEnumerator ClickTopWindowTab(string tabLabel)
        {
            yield return Imgui.Click(tabLabel, TopWindowTitle, exact: true);
        }

        // ---- KCT editor window and the simulation dialog ----

        public static IEnumerator ShowKctEditorWindow()
        {
            if (!HighLogic.LoadedSceneIsEditor) throw new UITestFailure("ShowKctEditorWindow: not in the editor");
            if (KCT_GUI.IsPrimarilyDisabled) throw new UITestFailure("KCT is disabled by the active preset");
            KCT_GUI.ToggleVisibility(true);
            KCT_GUI.GUIStates.ShowEditorGUI = true;
            yield return Imgui.WaitWindow(EditorWindow);
        }

        public static IEnumerator OpenSimConfig()
        {
            if (!Imgui.WindowOpen(SimConfigWindow))
            {
                yield return ShowKctEditorWindow();
                yield return Imgui.Click("Simulate", EditorWindow, exact: true);
            }
            yield return Imgui.WaitWindow(SimConfigWindow);
        }

        public static IEnumerator SetSimMode(string gridLabel) => Imgui.Select(gridLabel, SimConfigWindow);
        public static IEnumerator SetSimField(string precedingLabel, string value) => Imgui.SetTextAfterLabel(precedingLabel, value, SimConfigWindow);
        public static IEnumerator SetSimToggle(string label, bool on) => Imgui.Toggle(label, on, SimConfigWindow);

        /// <summary>Fills the dialog from a spec (only the fields the spec sets).</summary>
        public static IEnumerator ConfigureSim(SimSpec s)
        {
            yield return OpenSimConfig();
            if (s.Body != null)
            {
                // straight into the dialog's parameters (its "Select" opens a separate body chooser window); a body
                // other than home implies starting in orbit
                CelestialBody b = FlightGlobals.Bodies.Find(x => x.name == s.Body || x.bodyName == s.Body);
                if (b == null) throw new UITestFailure($"no body named {s.Body}");
                SimParams.SimulationBody = b;
                yield return null;
            }
            yield return SetSimToggle("Start in orbit", s.StartInOrbit);
            if (s.Failures.HasValue) yield return SetSimToggle("Enable Part Failures", s.Failures.Value);
            if (!s.StartInOrbit) yield break;
            if (s.Mode != null) yield return SetSimMode(s.Mode);
            if (s.OrbitAltKm.HasValue) yield return SetSimField("Orbit Altitude", Num(s.OrbitAltKm.Value));
            if (s.Mode == "Reentry")
            {
                if (s.PeKm.HasValue) yield return SetSimField("Periapsis (km)", Num(s.PeKm.Value));
                if (s.Arrival != null) yield return Imgui.Select(s.Arrival, SimConfigWindow, exact: false);
                if (s.ApKm.HasValue) yield return SetSimField("Apoapsis (km)", Num(s.ApKm.Value));
                if (s.EntrySpeed.HasValue) yield return SetSimField("Entry speed", Num(s.EntrySpeed.Value));
                if (s.EntryAltKm.HasValue) yield return SetSimField("Starting altitude", Num(s.EntryAltKm.Value));
                if (s.LeadTimeSec.HasValue) yield return SetSimField("Lead time", Num(s.LeadTimeSec.Value));
                if (s.Attitude != null) yield return Imgui.Select(s.Attitude, SimConfigWindow);
                if (s.PropellantPct.HasValue) yield return SetSimField("Propellant remaining", Num(s.PropellantPct.Value));
                if (s.LogTelemetry.HasValue) yield return SetSimToggle("Log thermal telemetry", s.LogTelemetry.Value);
            }
            else if (s.Mode == "Hyperbolic")
            {
                if (s.Origin != null)
                {
                    // like Body: the dialog's origin "Select" opens the separate body chooser
                    CelestialBody o = FlightGlobals.Bodies.Find(x => x.name == s.Origin || x.bodyName == s.Origin);
                    if (o == null) throw new UITestFailure($"no body named {s.Origin}");
                    Reflect.Set(SimParams, "SimOriginBody", o);
                    yield return null;
                }
                if (s.TransferTime != null) yield return SetSimField("Transfer time", s.TransferTime);
                if (s.InsertionDv != null) yield return SetSimField("Insertion ΔV", s.InsertionDv);
                if (s.HypPeKm != null) yield return SetSimField("Periapsis altitude", s.HypPeKm);
                if (s.TimeToPe != null) yield return SetSimField("Time to periapsis", s.TimeToPe);
            }
            else
            {
                if (s.PeKm.HasValue) yield return SetSimField("Orbit Periapsis", Num(s.PeKm.Value));
                if (s.ApKm.HasValue) yield return SetSimField("Orbit Apoapsis", Num(s.ApKm.Value));
            }
            if (s.IncDeg.HasValue) yield return SetSimField("Inclination", Num(s.IncDeg.Value));
            if (s.LanDeg.HasValue) yield return SetSimField("LAN", Num(s.LanDeg.Value));
            if (s.ArgPeDeg.HasValue) yield return SetSimField("Argument of Periapsis", Num(s.ArgPeDeg.Value));
            if (s.UT.HasValue)
            {
                // "Time:" with the colon: "Lead time (s):" would match a bare "time". Hyperbolic labels it "Departure:".
                yield return SetSimToggle("From Now", false);
                yield return SetSimField(s.Mode == "Hyperbolic" ? "Departure:" : "Time:", s.UT.Value.ToString("F0", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Clicks Simulate in the dialog and waits for the simulated flight to be up.</summary>
        public static IEnumerator StartSim(bool waitPlaced = true)
        {
            yield return Imgui.Click("Simulate", SimConfigWindow, exact: true);
            yield return Wait.Scene(GameScenes.FLIGHT, 600);
            yield return Wait.FlightReady(300);
            if (!IsSimulatedFlight) throw new UITestFailure("in flight but SpaceCenterManagement.IsSimulatedFlight is false\n" + Ugui.Dump());
            Nav.MarkLaunched();
            if (waitPlaced) yield return WaitSimPlaced();
            Log.Info("simulation running: " + Flight.Telemetry());
        }

        /// <summary>Configure + Simulate in one step (what Nav.Launch does under RP-1).</summary>
        public static IEnumerator Simulate(TestContext ctx, SimSpec spec)
        {
            yield return ConfigureSim(spec);
            yield return StartSim(waitPlaced: spec.StartInOrbit);
        }

        /// <summary>Waits until RP-1 has moved the vessel onto the requested orbit/trajectory.</summary>
        public static IEnumerator WaitSimPlaced(double timeoutSec = 120)
        {
            yield return Wait.Until(() => IsSimulatedFlight && SimParams.IsVesselMoved, timeoutSec, "RP-1 sim vessel placed (SimulationParams.IsVesselMoved)");
            yield return Wait.Seconds(1.0);
        }

        /// <summary>API-level start: fills SimulationParams directly and calls the private KCT_GUI.StartSim.</summary>
        public static IEnumerator StartSimDirect(Action<SimulationParams> configure)
        {
            var p = SimParams;
            configure(p);
            Reflect.CallStatic(typeof(KCT_GUI), "StartSim", p);
            yield return Wait.Scene(GameScenes.FLIGHT, 600);
            yield return Wait.FlightReady(300);
            if (p.SimulateInOrbit) yield return WaitSimPlaced();
        }

        // ---- in-flight simulation window ----

        public static IEnumerator ShowSimWindow()
        {
            if (!IsSimulatedFlight) throw new UITestFailure("not in a simulated flight");
            KCT_GUI.GUIStates.ShowSimulationGUI = true;
            yield return Imgui.WaitWindow(SimWindow);
        }

        public static IEnumerator RevertToEditorViaWindow()
        {
            yield return ShowSimWindow();
            yield return Imgui.Click("Revert to Editor", SimWindow, exact: true);
            yield return Wait.Scene(GameScenes.EDITOR, 600);
            yield return Wait.EditorReady(true, 300);
        }

        /// <summary>Leaves a simulated flight for the Space Center without saving it (RP-1 restores its pre-sim backup).</summary>
        public static IEnumerator LeaveSimulation(TestContext ctx)
        {
            Log.Info("leaving the KCT simulation");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
        }

        public static IEnumerator RestartSimViaWindow()
        {
            yield return ShowSimWindow();
            yield return Imgui.Click("Restart Simulation", SimWindow, exact: true);
            yield return Wait.Seconds(1);
            yield return Wait.FlightReady(300);
        }

        // ---- reentry telemetry ----

        public static string ReentryLogDir => Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "RP-1_ReentryLogs");

        public static string NewestReentryLog()
        {
            if (!Directory.Exists(ReentryLogDir)) return null;
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(ReentryLogDir, "*.csv"))
            {
                if (f.EndsWith(".parts.csv", StringComparison.OrdinalIgnoreCase)) continue;   // the companion, not the trace
                var t = File.GetLastWriteTime(f);
                if (t > newestTime) { newestTime = t; newest = f; }
            }
            return newest;
        }

        /// <summary>Reads a CSV that may still be open for writing.</summary>
        public static string[] ReadLines(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static void DismissKctPopups() => Ugui.DismissPopups();

        // ---- editor reentry analysis (FAR's editor window, "Reentry" tab; chambm's FAR fork) ----

        public const string FarWindowTitle = "FAR Analysis";
        private static Type FarReentryApi => Reflect.FindType("FerramAerospaceResearch.Reentry.FARReentryAPI") ?? throw new UITestFailure("this FAR has no FARReentryAPI (reentry analysis tab)");

        /// <summary>Opens FAR's editor window on its Reentry tab (editor only) and returns the tab object.</summary>
        public static IEnumerator ShowReentryTab()
        {
            if (!HighLogic.LoadedSceneIsEditor) throw new UITestFailure("ShowReentryTab: not in the editor");
            var egType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI") ?? throw new UITestFailure("FAR EditorGUI type not found");
            object eg = Reflect.GetStatic(egType, "Instance") ?? throw new UITestFailure("FAR EditorGUI.Instance is null");
            Reflect.SetStatic(egType, "showGUI", true);
            // The mode dropdown is a custom control the IMGUI driver cannot click; pick the entry directly.
            object dropdown = Reflect.Get(eg, "modeDropdown");
            var options = (Array)Reflect.Get(dropdown, "typeOptions");
            int idx = -1;
            for (int i = 0; i < options.Length; ++i) if (options.GetValue(i).ToString() == "REENTRY") idx = i;
            if (idx < 0) throw new UITestFailure("FAR editor window has no REENTRY mode");
            Reflect.Set(dropdown, "selectedOption", idx);
            yield return Imgui.WaitWindow(FarWindowTitle);
            yield return Imgui.WaitVisible("AoA × Pe", FarWindowTitle);
        }

        private static object ReentryTab
        {
            get
            {
                var egType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
                object eg = egType != null ? Reflect.GetStatic(egType, "Instance") : null;
                return eg != null ? Reflect.Get(eg, "_reentry") : null;
            }
        }

        // The tab's arrival (periapsis + apoapsis), attitude and propellant fields, shared by Predict and Analyse.
        private static IEnumerator SetReentryInputs(double apKm, string attitude, double? propellantPct, double incDeg = 0)
        {
            yield return Imgui.Select("Apoapsis", FarWindowTitle);
            yield return Imgui.SetTextAfterLabel("Apoapsis (km)", Num(apKm), FarWindowTitle);
            yield return Imgui.SetTextAfterLabel("Inclination (deg)", Num(incDeg), FarWindowTitle);
            if (attitude != null) yield return Imgui.Select(attitude, FarWindowTitle);
            if (propellantPct.HasValue) yield return Imgui.SetTextAfterLabel("Propellant %", Num(propellantPct.Value), FarWindowTitle);
            yield return Wait.Until(() => (bool)Reflect.GetStatic(FarReentryApi, "Ready"), 60, "FAR editor voxelization (FARReentryAPI.Ready)", shot: false);
        }

        /// <summary>
        /// Predicts one entry on FAR's Reentry tab (what clicking a heatmap cell does) for a periapsis + apoapsis
        /// arrival at the given AoA and attitude, and returns when the trace has been written (LastPredictionPath;
        /// the .parts.csv sits next to it). The entry starts at the top of the atmosphere.
        /// </summary>
        public static IEnumerator PredictReentry(double peKm, double apKm, double aoaDeg, string attitude = "Nose first", double? propellantPct = null, bool liftDown = false, double incDeg = 0)
        {
            yield return ShowReentryTab();
            yield return SetReentryInputs(apKm, attitude, propellantPct, incDeg);
            object tab = ReentryTab ?? throw new UITestFailure("FAR EditorGUI has no _reentry tab");
            string before = LastPredictionPath;
            Reflect.Call(tab, "PredictEntry", peKm * 1000, aoaDeg, liftDown);
            Reflect.Call(tab, "WriteLastTrace");
            yield return null;
            Log.Info("prediction: " + LastSummary);
            if (LastPredictionPath == before) throw new UITestFailure("prediction did not run: " + LastSummary);
        }

        /// <summary>
        /// Replays FAR's thermal model along a flown telemetry CSV (missionTime, altitude, srfSpeed) on the editor craft
        /// at the given AoA and attitude, and returns when the trace has been written (LastPredictionPath): a
        /// prediction whose trajectory is the flight's, so its temperatures against the flight's isolate the thermal
        /// model from the aero.
        /// </summary>
        public static IEnumerator ReplayReentry(string flownCsv, double aoaDeg, string attitude = "Nose first", double? propellantPct = null, double? atmoTempOffset = null)
        {
            yield return ShowReentryTab();
            yield return SetReentryInputs(250, attitude, propellantPct);
            object tab = ReentryTab ?? throw new UITestFailure("FAR EditorGUI has no _reentry tab");
            string before = LastPredictionPath;
            // The flight's atmosphere temperature offset (latitude / time of day / season), so the replay samples the
            // same air the flight flew through rather than the editor's representative state.
            var atmType = Reflect.FindType("FerramAerospaceResearch.Reentry.ReentryAtmosphere");
            if (atmoTempOffset.HasValue) Reflect.SetStatic(atmType, "TemperatureOffsetOverride", atmoTempOffset.Value);
            try
            {
                Reflect.Call(tab, "ReplayFlown", flownCsv, aoaDeg);
                Reflect.Call(tab, "WriteLastTrace");
            }
            finally { Reflect.SetStatic(atmType, "TemperatureOffsetOverride", double.NaN); }
            yield return null;
            Log.Info("replay: " + LastSummary);
            if (LastPredictionPath == before) throw new UITestFailure("replay did not run: " + LastSummary);
        }

        /// <summary>
        /// Sets every engine propellant on the editor ship to a percentage of capacity with stock APIs alone (what
        /// FAR's PropellantLoad.Apply does through RP-1's sim setup), so a flight can be set up the same way on a
        /// FAR build without the reentry API. Returns the number of resources changed.
        /// </summary>
        /// <summary>
        /// Switches every procedural avionics part on the ship in the editor to an avionics config (e.g. "Deep-Space",
        /// which keeps control outside the home body's sphere of influence; "Near-Earth" does not), at the highest tech
        /// level of it unlocked or else its first, as the part's config selector does. Returns how many parts it switched.
        /// </summary>
        public static int SetAvionicsConfig(string config)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                                                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var ship = EditorLogic.fetch?.ship;
            if (ship == null) return 0;
            Type mgr = typeof(RP0.ProceduralAvionics.ModuleProceduralAvionics).Assembly.GetType("RP0.ProceduralAvionics.ProceduralAvionicsTechManager");
            if (mgr == null) throw new UITestFailure("no ProceduralAvionicsTechManager");
            string level = mgr.GetMethod("GetMaxUnlockedTech", any)?.Invoke(null, new object[] { config }) as string;
            if (string.IsNullOrEmpty(level))
                level = mgr.GetMethod("GetFirstTech", any)?.Invoke(null, new object[] { config }) as string;
            if (string.IsNullOrEmpty(level)) throw new UITestFailure($"no avionics config {config}");
            var changed = typeof(RP0.ProceduralAvionics.ModuleProceduralAvionics).GetMethod("AvionicsConfigChanged", any, null, Type.EmptyTypes, null);
            int n = 0;
            foreach (Part p in ship.parts)
                foreach (var m in p.Modules.GetModules<RP0.ProceduralAvionics.ModuleProceduralAvionics>())
                {
                    m.avionicsConfigName = config;
                    m.avionicsTechLevel = level;
                    changed?.Invoke(m, null);
                    ++n;
                }
            Log.Info($"avionics: {n} part(s) set to {config} ({level})");
            return n;
        }

        public static int ApplyPropellantLoadDirect(double percent)
        {
            var props = new System.Collections.Generic.HashSet<string>();
            var parts = EditorLogic.fetch.ship.Parts;
            foreach (var p in parts)
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                    foreach (var pr in e.propellants) props.Add(pr.name);
            int changed = 0;
            foreach (var p in parts)
                foreach (PartResource r in p.Resources)
                {
                    if (!props.Contains(r.resourceName)) continue;
                    double want = r.maxAmount * Math.Max(0, Math.Min(100, percent)) / 100.0;
                    if (Math.Abs(r.amount - want) < 1e-6) continue;
                    r.amount = want;
                    ++changed;
                }
            if (changed > 0) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            Log.Info($"propellant load {percent:F0}% (direct): {changed} resources changed, {props.Count} engine propellants");
            return changed;
        }

        /// <summary>One cell of the FAR Reentry tab's swept grid, read back by reflection.</summary>
        public sealed class ReentryCell
        {
            public double PeKm, AoA, AoAHeld, PeakSkinFrac, PeakInternalFrac, MeanPeakFrac, PeakG, ComOffsetPercent;
            public int LostParts, PartCount;
            public bool LiftDown, LiftUpward, Survived, AttitudeNotHeld;
            public string Outcome, LimitingPart, LostNames;
            public double WorstFrac => Math.Max(PeakSkinFrac, PeakInternalFrac);
            public override string ToString() =>
                $"Pe {PeKm:F0} km AoA {AoA:F0} (held {AoAHeld:F1}{(AttitudeNotHeld ? ", not holdable" : "")}{(double.IsNaN(ComOffsetPercent) ? "" : $", CoM offset {ComOffsetPercent:P0}")}) {(LiftDown ? "roof down" : "roof up")}, lift {(LiftUpward ? "up" : "down")}: {Outcome}, skin {PeakSkinFrac * 100:F0}% int {PeakInternalFrac * 100:F0}% on {LimitingPart}, {PeakG:F1} g, lost {LostParts}/{PartCount} {LostNames}";
        }

        /// <summary>Every computed cell of the last sweep on FAR's Reentry tab.</summary>
        public static List<ReentryCell> ReentryCells()
        {
            object tab = ReentryTab ?? throw new UITestFailure("FAR EditorGUI has no _reentry tab");
            var cells = (Array)Reflect.Get(tab, "_cells");
            var pe = (double[])Reflect.Get(tab, "_peList");
            var aoa = (double[])Reflect.Get(tab, "_aoaList");
            var list = new List<ReentryCell>();
            if (cells == null) return list;
            for (int j = 0; j < pe.Length; ++j)
                for (int i = 0; i < aoa.Length; ++i)
                {
                    object r = cells.GetValue(j, i);
                    if (r == null || (int)Reflect.Get(r, "PartCount") == 0) continue;
                    list.Add(new ReentryCell
                    {
                        PeKm = pe[j] / 1000, AoA = aoa[i], AoAHeld = (double)Reflect.Get(r, "AoAHeld"), ComOffsetPercent = (double)Reflect.Get(r, "ComOffsetPercent"),
                        LiftUpward = (bool)Reflect.Get(r, "LiftUpward"), AttitudeNotHeld = (bool)Reflect.Get(r, "AttitudeNotHeld"),
                        PeakSkinFrac = (double)Reflect.Get(r, "PeakSkinFrac"), PeakInternalFrac = (double)Reflect.Get(r, "PeakInternalFrac"),
                        MeanPeakFrac = (double)Reflect.Get(r, "MeanPeakFrac"), PeakG = (double)Reflect.Get(r, "PeakG"),
                        LostParts = (int)Reflect.Get(r, "LostParts"), PartCount = (int)Reflect.Get(r, "PartCount"),
                        LiftDown = (bool)Reflect.Get(r, "LiftDown"), Survived = (bool)Reflect.Get(r, "Survived"),
                        Outcome = Reflect.Get(r, "Outcome").ToString(), LimitingPart = (string)Reflect.Get(r, "LimitingPart"), LostNames = (string)Reflect.Get(r, "LostNames"),
                    });
                }
            return list;
        }

        /// <summary>
        /// The cell worth flying to check the model: "near" = the surviving cell closest under its limit
        /// (worst part fraction highest but below 1, within the g limit), "lost" = the cell that loses exactly one
        /// part with the least margin over the limit, "least" = the cell with the least thermal excess whether or
        /// not it survived, "best" = the tab's own best cell. Null when none qualifies.
        /// </summary>
        public static ReentryCell PickBorderlineCell(string mode, double gLimit)
        {
            var cells = ReentryCells();
            ReentryCell pick = null;
            mode = (mode ?? "near").ToLowerInvariant();
            if (mode.StartsWith("aoa:"))
            {
                // A given angle column (nearest): the surviving cell closest under its limit there, else the least excess.
                double want = double.Parse(mode.Substring(4), CultureInfo.InvariantCulture);
                double col = double.NaN;
                foreach (var c in cells) if (double.IsNaN(col) || Math.Abs(c.AoA - want) < Math.Abs(col - want) - 1e-9) col = c.AoA;
                ReentryCell least = null;
                foreach (var c in cells)
                {
                    if (Math.Abs(c.AoA - col) > 1e-9 || c.AttitudeNotHeld) continue;
                    if (c.Survived && c.LostParts == 0 && c.WorstFrac < 1 && c.PeakG <= gLimit && (pick == null || c.WorstFrac > pick.WorstFrac)) pick = c;
                    if (c.Outcome != "SkippedOut" && (least == null || c.WorstFrac < least.WorstFrac)) least = c;
                }
                return pick ?? least;
            }
            if (mode.StartsWith("cell:"))
            {
                // An exact cell, "cell:<aoa>,<pe>" (nearest on each axis): fly it whatever the editor thinks of it.
                string[] ap = mode.Substring(5).Split(',');
                double wantAoA = double.Parse(ap[0], CultureInfo.InvariantCulture), wantPe = double.Parse(ap[1], CultureInfo.InvariantCulture);
                foreach (var c in cells)
                    if (pick == null || Math.Abs(c.AoA - wantAoA) + Math.Abs(c.PeKm - wantPe) < Math.Abs(pick.AoA - wantAoA) + Math.Abs(pick.PeKm - wantPe) - 1e-9) pick = c;
                return pick;
            }
            if (mode.StartsWith("pe:"))
            {
                // A given periapsis row (nearest), first angle column: for comparing two configurations of one entry.
                double want = double.Parse(mode.Substring(3), CultureInfo.InvariantCulture);
                foreach (var c in cells)
                    if (pick == null || Math.Abs(c.PeKm - want) < Math.Abs(pick.PeKm - want) - 1e-9) pick = c;
                return pick;
            }
            switch (mode)
            {
                case "lost":
                    // Exactly one part lost, least over the limit; else the fewest parts lost.
                    foreach (var c in cells)
                        if (!c.AttitudeNotHeld && c.LostParts == 1 && c.Outcome != "SkippedOut" && (pick == null || c.WorstFrac < pick.WorstFrac)) pick = c;
                    if (pick == null)
                        foreach (var c in cells)
                            if (!c.AttitudeNotHeld && c.LostParts > 0 && c.Outcome != "SkippedOut" &&
                                (pick == null || c.LostParts < pick.LostParts || c.LostParts == pick.LostParts && c.WorstFrac < pick.WorstFrac)) pick = c;
                    break;
                case "least":
                    foreach (var c in cells)
                        if (!c.AttitudeNotHeld && c.Outcome != "SkippedOut" && (pick == null || c.WorstFrac < pick.WorstFrac)) pick = c;
                    break;
                case "best":
                    object tab = ReentryTab;
                    object best = tab != null ? Reflect.Get(tab, "_best") : null;
                    if (best == null) return null;
                    double bestPe = (double)Reflect.Get(best, "PeAltitude") / 1000, bestAoA = (double)Reflect.Get(best, "AoADeg");
                    foreach (var c in cells) if (Math.Abs(c.PeKm - bestPe) < 1e-6 && Math.Abs(c.AoA - bestAoA) < 1e-6) pick = c;
                    break;
                default:
                    foreach (var c in cells)
                        if (!c.AttitudeNotHeld && c.Survived && c.LostParts == 0 && c.WorstFrac < 1 && c.PeakG <= gLimit && (pick == null || c.WorstFrac > pick.WorstFrac)) pick = c;
                    // Nothing survives the grid: the least-bad cell still says something.
                    if (pick == null) return PickBorderlineCell("least", gLimit);
                    break;
            }
            return pick;
        }

        /// <summary>
        /// RO's descent mode (AdjustableCoMShifter) on every part that has it, in the editor: the shifted centre
        /// of mass is what gives a capsule a trim angle. Persists into the craft the simulation launches.
        /// </summary>
        public static int SetDescentMode(bool on)
        {
            int changed = 0;
            foreach (var p in EditorLogic.fetch.ship.Parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.moduleName != "AdjustableCoMShifter") continue;
                    bool cur = (bool)Reflect.Get(m, "IsDescentMode");
                    if (cur == on) continue;
                    Reflect.Call(m, "ToggleMode");
                    ++changed;
                }
            if (changed > 0) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            Log.Info($"descent mode {(on ? "on" : "off")}: {changed} CoM shifters toggled");
            return changed;
        }

        /// <summary>The descent-mode CoM offset limit (0..1) on every shifter in the editor; the module recomputes the part's CoM.</summary>
        public static int SetComOffsetLimit(double fraction)
        {
            int changed = 0;
            foreach (var p in EditorLogic.fetch.ship.Parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.moduleName != "AdjustableCoMShifter") continue;
                    Reflect.Set(m, "offsetPercent", (float)fraction);
                    Reflect.Call(m, "UpdateCoM");
                    ++changed;
                }
            if (changed > 0) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            Log.Info($"CoM offset limit {fraction:P0}: {changed} shifters set");
            return changed;
        }

        /// <summary>Predicts one cell in detail on the FAR tab (what a click does) and writes its trace CSV.</summary>
        public static IEnumerator PredictCell(ReentryCell cell)
        {
            object tab = ReentryTab ?? throw new UITestFailure("FAR EditorGUI has no _reentry tab");
            string before = LastPredictionPath;
            Reflect.Call(tab, "PredictEntry", cell.PeKm * 1000, cell.AoA, cell.LiftDown);
            Reflect.Call(tab, "WriteLastTrace");
            yield return null;
            Log.Info("prediction: " + LastSummary);
            if (LastPredictionPath == before) throw new UITestFailure("prediction did not run: " + LastSummary);
        }

        /// <summary>Runs the AoA x Pe heatmap sweep on FAR's Reentry tab and waits for it to finish.</summary>
        public static IEnumerator AnalyseReentry(double peMinKm, double peMaxKm, int peSteps, double aoaMin, double aoaMax, int aoaSteps, double apKm, string attitude = "Nose first", double? propellantPct = null, double timeoutSec = 600, bool bothRoof = true, double incDeg = 0)
        {
            yield return ShowReentryTab();
            yield return Imgui.Toggle("Roof up and down", bothRoof, FarWindowTitle);
            yield return Imgui.SetTextAfterLabel("AoA (deg)", Num(aoaMin), FarWindowTitle);
            yield return Imgui.SetTextNth(1, Num(aoaMax), FarWindowTitle);   // draw order: AoA from, to, Pe from, to, nAoA, nPe, ...
            yield return Imgui.SetTextAfterLabel("Periapsis (km)", Num(peMinKm), FarWindowTitle);
            yield return Imgui.SetTextNth(3, Num(peMaxKm), FarWindowTitle);
            yield return Imgui.SetTextAfterLabel("AoA × Pe", aoaSteps.ToString(), FarWindowTitle);
            yield return Imgui.SetTextNth(5, peSteps.ToString(), FarWindowTitle);
            yield return SetReentryInputs(apKm, attitude, propellantPct, incDeg);
            string summaryBefore = LastSummary;
            yield return Imgui.Click("Run Sweep", FarWindowTitle, exact: true);
            yield return Wait.Until(() => LastSummary != summaryBefore && LastSummary.Length > 0, timeoutSec, "reentry analysis sweep to finish", shot: false);
            Log.Info("analysis: " + LastSummary);
        }

        /// <summary>Writes a Reentry-mode setup (return from an apoapsis, prograde, telemetry on) straight into SimulationParams.</summary>
        public static void SetReentrySimParams(double peKm, double apKm, double entryAltKm, bool prograde = true)
        {
            var p = SimParams;
            p.SimulationBody = Planetarium.fetch.Home;
            p.SimulateInOrbit = true;
            Reflect.Set(p, "SimOrbitMode", Enum.Parse(Reflect.FindType("RP0.SimOrbitMode"), "Reentry"));
            Reflect.Set(p, "SimReentryArrival", Enum.Parse(Reflect.FindType("RP0.SimReentryArrival"), "Apoapsis"));
            Reflect.Set(p, "SimReentryAttitude", Enum.Parse(Reflect.FindType("RP0.SimReentryAttitude"), prograde ? "Prograde" : "Retrograde"));
            p.SimOrbitPe = peKm * 1000;
            p.SimOrbitAp = apKm * 1000;
            Reflect.Set(p, "SimEntryAltitude", entryAltKm * 1000);
            Reflect.Set(p, "LogReentryTelemetry", true);
            Log.Info($"SimulationParams: Reentry Pe {peKm} km, Ap {apKm} km, interface {entryAltKm} km");
        }

        public static string LastSummary => (string)Reflect.GetStatic(FarReentryApi, "LastSummary") ?? "";
        public static string LastPredictionPath => (string)Reflect.GetStatic(FarReentryApi, "LastPredictionPath") ?? "";

        // ---- angle-of-attack hold through the stock SAS ----

        /// <summary>
        /// Holds an angle of attack in the pitch plane with wings level (roof toward local up) by feeding the
        /// stock SAS a locked target rotation every physics frame, the way a pilot holds AoA through a reentry.
        /// Positive AoA = nose above the surface velocity.
        /// </summary>
        public class AoAHold : MonoBehaviour
        {
            public double AoADeg;
            public double LastAoA = double.NaN;
            public float LastPitchInput;

            private void FixedUpdate()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.packed || v.srfSpeed < 50) return;
                Vector3d vel = v.srf_velocity.normalized;
                Vector3d up = (v.CoMD - v.mainBody.position).normalized;
                Vector3d y = (up - vel * Vector3d.Dot(up, vel)).normalized;   // "up" perpendicular to the velocity
                double a = AoADeg * Math.PI / 180.0;
                Vector3d nose = vel * Math.Cos(a) + y * Math.Sin(a);
                Vector3d roof = -vel * Math.Sin(a) + y * Math.Cos(a);
                // ReferenceTransform: up = nose, forward = belly
                Quaternion target = Quaternion.LookRotation(-(Vector3)roof, (Vector3)nose);
                if (v.Autopilot != null)
                {
                    if (v.Autopilot.Mode != VesselAutopilot.AutopilotMode.StabilityAssist) v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
                    if (!v.ActionGroups[KSPActionGroup.SAS]) v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                    v.Autopilot.SAS.LockRotation(target);
                }
                LastAoA = Flight.AoA;
                LastPitchInput = v.ctrlState != null ? v.ctrlState.pitch : 0f;
            }
        }

        private static AoAHold _aoaHold;

        public static AoAHold StartAoAHold(double aoaDeg)
        {
            if (_aoaHold == null) _aoaHold = new GameObject("KspTest.AoAHold").AddComponent<AoAHold>();
            _aoaHold.AoADeg = aoaDeg;
            Log.Info($"AoA hold {aoaDeg:F1} deg via SAS");
            return _aoaHold;
        }

        public static void StopAoAHold()
        {
            if (_aoaHold == null) return;
            UnityEngine.Object.Destroy(_aoaHold.gameObject);
            _aoaHold = null;
        }
    }
}
