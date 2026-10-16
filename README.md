# KspTest

In-game automated UI tests for KSP 1.12 (stock, MechJeb, RP-1/KCT). A test launches the game,
loads a save, walks through scenes, drives windows (PAW, PopupDialog, IMGUI mod windows), flies,
asserts on game state, and quits; one PowerShell command returns pass/fail with screenshots and logs.

```
.\deploy.ps1 -Build [-Install D:\Games\KSP-Vanilla]     # build + copy DLLs into an install
.\run-test.ps1 -Test list                                # what tests that install can run
.\run-test.ps1 -Test Smoke_MainMenuToVab -Install D:\Games\KSP-StockFAR -Save stock-far
.\run-test.ps1 -Test "Launch_StockButton,Paw_EditorAndFlight,Mj_SmartAss,Ctrl_BarrelRoll,Mj_AscentToOrbit" -Install D:\Games\KSP-StockFAR -Save stock-far
.\run-test.ps1 -Test "Mj_*" -Params "orbitAltKm=120;inclination=30;warp=3"      # wildcard; string params survive -File
.\run-test.ps1 -Test Kct_SimConfig_Reentry -Install D:\Games\KSP-RP1-Stock -Save rp1-career
```

Results land in `results\<timestamp>-<test>\`: `result.json` (status, steps, recorded values,
screenshots), `harness.log`, `KSP.log`, `screenshots\<test>\NN-[s<k>-<scenario>-]<name>.png` (the test's
own shots; `<window>-before-<button>` for each IMGUI form as it stands when its button is pressed;
`wait-<what>` for waits on game state such as flight ready or an entry ending - not scene loads, delays or
field edits; and `end-<status>` when the test ends; `-Params "waitShots=off"` keeps only the test's own),
and on failure `diagnostics.txt` (what was on screen:
open popups, clickable uGUI buttons, IMGUI windows with their labels/buttons/fields, input locks,
log tail). Exit code 0 = pass or skipped.

`savesp1-career-ut30` and `-ut45` are copies of `rp1-career` whose game clock is 30 / 45 degrees of
Earth's rotation later (+7180 / +10770 s): the planet's azimuth in Unity's world frame is a function of
the clock, so `Solar_Occlusion` run from each shows stock's world-frame solar shading moving with
nothing about the vessel changed. Regenerate with the script in the notes if `rp1-career` changes.

## Layout

| Path | What |
|---|---|
| `Source/KspTest` | harness core: `TestRunner`, `Nav`, `Wait`, `Ugui`, `Imgui`, `Paw`, `Ctrl`, `Flight`, `Assert`, `Snap` |
| `Source/KspTest.MechJeb` | `Mj` helpers (SmartASS, ascent, landing) - deployed only where MechJeb2.dll exists |
| `Source/KspTest.RP1` | `Rp1` helpers (KCT editor window, Simulation Configuration dialog, RP-1 top window) - deployed only where RP0.dll exists |
| `Source/Tests.*` | the tests: `[UITest("Name", Requires = "MechJeb")] public static IEnumerator Name(TestContext ctx)` |
| `saves/<template>/` | golden saves copied fresh into `<install>\saves\uitest` before every run (see `saves/README.md`) |
| `run-test.ps1` | runner: refuses if that install is already running, resets the save, disables QuickStart for the run, writes `PluginData/run.cfg`, launches windowed, waits, collects |
| `deploy.ps1` | msbuild + copy with `.bak` backups |

The harness is inert unless `GameData/KspTest/PluginData/run.cfg` exists (the runner creates and
deletes it), so the DLLs can stay installed.

## Sessions and start states

All tests named in one `-Test` list run in **one KSP session** (one load), in order. Each test declares
where it expects to start - `StartsAt = StartState.SpaceCenter | Vab | Sph | Flight` plus `Craft`
(and `CraftDir`, `LaunchSite`) - and before it runs `Nav.EnsureState` walks the game there from
wherever the previous test finished: same craft already in that editor -> nothing; same craft in
flight -> revert to launch; a vessel left on the pad -> recovered; otherwise via the Space Center.
`StartState.Any` means the test navigates itself. Game state (the save) carries over between tests
by design; `run-test.ps1` resets it from the golden template once per session.

An on-screen box (bottom-left) shows the running test, its current step and what it is waiting for.

## Writing a test

```csharp
[UITest("Kct_SimConfig_Circular", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "UITest_Sounding")]
public static IEnumerator Kct_SimConfig_Circular(TestContext ctx)
{
    ctx.Step("Simulate a 300 km orbit");
    yield return Rp1.ConfigureSim(SimSpec.Circular(300, 28.5));   // Imgui clicks/typing by label
    yield return Rp1.StartSim();
    Assert.Near(300, Flight.Apoapsis / 1000, 7, "apoapsis km");
    ctx.Record("apKm", Flight.Apoapsis / 1000);
    yield return Snap.Shot(ctx, "orbit");
}
```

- Yield nested `IEnumerator`s freely; the runner pumps them and turns any exception into a
  `fail` (UITestFailure) or `crash` with stack, screenshot and diagnostics.
- Every wait has a timeout (`Wait.Until(cond, sec, "what")`); the whole test has `timeoutSec`.
- Parameters: `-Params @{ craft = 'X' }` -> `ctx.Param("craft")` / `ctx.ParamDouble`.
- `Env.HasRP1 / HasMechJeb`; `Requires` makes a test report `skipped` where a mod is absent.

## Drivers

- **Ugui** (stock uGUI, PopupDialog): `ClickButton("Launch")`, `ClickPopup("OK", "dialogName")`,
  `DismissPopups()`, `Dump()`. Clicks go through `ExecuteEvents` (pointer down/up/click).
- **Imgui** (RP-1, KCT, MechJeb windows): `Click("Simulate", window: "Simulation Configuration")`,
  `Toggle(" Start in orbit", true, window)`, `Select("Reentry", window)`,
  `SetTextAfterLabel("Orbit Periapsis", "150", window)`, `WaitWindow(title)`, `IsVisible`, `Dump()`.
  Harmony postfixes on `GUI.DoButton/DoToggle/SelectionGrid/DoTextField/Label/Window` record what
  each Repaint draws and force one queued control's return value during the Layout pass.
- **Paw**: `Open(part)`, `ClickEvent(part, "Activate")`, `SetToggle(part, field, on)`,
  `SetSlider(part, field, v)` through the real `UIPartAction*` items; `InvokeDirect` / `SetDirect`
  for API-level steps; `Find("partName")`, `Dump(part)`.
- **Ctrl**: `Attach()`, `Set(pitch, yaw, roll, throttle)`, `Hold(..., seconds)`, `Sas`, `SasMode`,
  `Rcs`, `Gear`, `Brakes`, `Stage()`, `IntegrateRoll`. One `FlightInputCallback` on
  `vessel.OnFlyByWire` (or `OnPostAutopilotUpdate` with `overrideAutopilots: true`).
- **Mj**: `SmartAss(target)`, `SmartAssSurface(hdg, pitch)`, `Ascent(altM, inc)`, `WaitAscentDone`,
  `LandAt(body, lat, lon)`, `LandSomewhere()`, `WaitLanded`, `DistanceTo`.
- **Nav**: `LoadSave`, `EnterEditor(facility, craft)`, `LoadCraft`, `Launch()` (stock Launch button,
  or the KCT simulation dialog when RP-1 is loaded), `RevertToEditor`, `ToSpaceCenter`.
- **Flight**: telemetry, `Warp`, `WarpTo`, `Unpause`, `ClearLocks`, `AssertAlive`.

## Debugging a failing test

1. Read `results\...\result.json` message and `diagnostics.txt` - the IMGUI/uGUI dumps list the exact
   labels and window titles that were on screen when it gave up.
2. Look at the failure screenshot.
3. Re-run with `-Keep` to leave KSP open at the end state.
4. `Log.Info(Imgui.Dump())` / `Ugui.Dump()` / `Paw.Dump(part)` inside a test print what is clickable.

## Build

Old-style csproj, .NET 4.8, x64. References come from `RP-1\.refs` (`-p:ReferencePath=`),
MechJeb from `-p:MechJebPath=` and RP0 from `-p:RP0Path=`; `deploy.ps1 -Build` points both at the
target install's own DLLs (MJ builds differ between installs - `VesselState.thrustAvailable` vs
`ThrustAvailable` - and the RP-1 helpers use reflection for branch-only fields so they compile
against any RP0).
New `.cs` files need a `<Compile Include>` line.
