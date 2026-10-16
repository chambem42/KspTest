# KspTest

In-game automated tests for Kerbal Space Program 1.12 mods. A test launches the game, loads a save,
walks through scenes, drives windows (PAW, PopupDialog, IMGUI mod windows), flies, asserts on game
state and quits; the run returns pass/fail with screenshots and logs. Runs on Windows, Linux and macOS.

## Packages

| Package | What |
|---|---|
| `KspTest` | the in-game harness: `TestRunner`, `Nav`, `Wait`, `Ugui`, `Imgui`, `Paw`, `Ctrl`, `Flight`, `Assert`, `Snap` (compile-time reference for in-game test projects) |
| `KspTest.MechJeb` | `Mj` helpers that drive MechJeb (SmartASS, ascent, landing) for any mod's tests |
| `KspTest.RP1` | RP-1 integration: launches through KCT simulations, drives the KCT and RP-1 windows |
| `KspTest.Runner` | runs tests from outside the game: deploys the harness (it carries it) and test assemblies, resets the save, launches and watches KSP, collects results |
| `KspTest.Xunit` | `dotnet test` support: `[KspFact]` and `Ksp.Run` |
| `KspTest.Tool` | the `ksptest` command line tool |

Packed into `../nuget-local` (a `NuGet.Config` in the parent folder points every repo there) until published.

## Testing a mod

A mod keeps its tests in two projects of its own; the mod itself never references KspTest.

1. **In-game tests** (net48, loaded by KSP): reference `KspTest` (and `KspTest.MechJeb` / `KspTest.RP1` if
   needed), the mod, and KSP's assemblies, none copied to the output:

   ```csharp
   [UITest("Mj_AscentToOrbit", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket")]
   public static IEnumerator Mj_AscentToOrbit(TestContext ctx) { ... }
   ```

2. **`dotnet test` project** (net8.0, xUnit): reference `KspTest.Xunit`, and the in-game project for its output only:

   ```xml
   <ProjectReference Include="../X.KspTests/X.KspTests.csproj"
                     ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" OutputItemType="KspTestPlugin" />
   ```

   ```csharp
   [KspFact]
   public void AscentToOrbit() => Ksp.Run(output, "Mj_AscentToOrbit", "orbitAltKm=250;ascentType=PSG", save: "rp1-career");
   ```

   Settings come from `ksptest.json` beside the test assembly or the environment (`KSPDIR`, `KSPTEST_SAVES`,
   `KSPTEST_RESULTS`, `KSPTEST_LOCK_FILE`, `KSPTEST_MUTE`). With no install configured every `[KspFact]` is skipped.

MechJeb2's `MechJebKspTest` and `MechJebKspTestRun` are a worked example.

## ksptest

```
dotnet tool install -g KspTest.Tool        # from the local feed
ksptest deploy --install DIR --tests MechJebKspTest=path/MechJebKspTest.dll
ksptest list --install DIR
ksptest run Mj_LaunchToRendezvous --install DIR --save rp1-career --saves DIR --params "targetInc=45;hohmann=off" --mute
```

Only one KSP runs at a time on a machine: every run waits while any KSP is running or another launcher holds the
lock file (`--lock`, default `$KSPTEST_LOCK_FILE` or one per user), then holds it. A script that holds the lock across
several runs passes it on with `KSPTEST_LOCK_PID`. `run-test.ps1` and `deploy.ps1` are wrappers over `ksptest` for the
scripts that still call them.

Results land in `results/<timestamp>-<test>/`: `result.json` (status, steps, recorded values,
screenshots), `harness.log`, `KSP.log`, `screenshots/<test>/NN-[s<k>-<scenario>-]<name>.png` (the test's
own shots; `<window>-before-<button>` for each IMGUI form as it stands when its button is pressed;
`wait-<what>` for waits on game state such as flight ready or an entry ending - not scene loads, delays or
field edits; and `end-<status>` when the test ends; `waitShots=off` keeps only the test's own),
and on failure `diagnostics.txt` (what was on screen: open popups, clickable uGUI buttons, IMGUI windows
with their labels/buttons/fields, input locks, log tail).

## Layout

| Path | What |
|---|---|
| `src/KspTest*` | the packages above |
| `tests/` | the harness's own tests, and tests still waiting to move to their mods' repos |
| `saves/<template>/` | save templates copied fresh into `<install>/saves/ksptest` before every run (see `saves/README.md`) |
| `GameData/KspTest/` | the harness as deployed (built here) |

The harness is inert unless `GameData/KspTest/PluginData/run.cfg` exists (the runner creates and
deletes it), so it can stay installed. Test assemblies deploy to `GameData/KspTest/Tests/<name>/`.

## Sessions and start states

All tests named in one `-Test` list run in **one KSP session** (one load), in order. Each test declares
where it expects to start - `StartsAt = StartState.SpaceCenter | Vab | Sph | Flight` plus `Craft`
(and `CraftDir`, `LaunchSite`) - and before it runs `Nav.EnsureState` walks the game there from
wherever the previous test finished: same craft already in that editor -> nothing; same craft in
flight -> revert to launch; a vessel left on the pad -> recovered; otherwise via the Space Center.
`StartState.Any` means the test navigates itself. Game state (the save) carries over between tests
by design; the runner resets it from the save template once per session.

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
- Parameters: `--params "craft=X"` (or `Ksp.Run(..., "craft=X")`) -> `ctx.Param("craft")` / `ctx.ParamDouble`.
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
3. Re-run with `--keep` to leave KSP open at the end state.
4. `Log.Info(Imgui.Dump())` / `Ugui.Dump()` / `Paw.Dump(part)` inside a test print what is clickable.

## Build

```
dotnet build KspTest.slnx -c Release [-p:KspDir=PATH]     # in-game projects compile against that install
dotnet pack KspTest.slnx -c Release                       # into ../nuget-local
```

SDK-style projects. The in-game ones are net48 against the KSP install set by `KspDir` (the `KSPDIR`
environment variable, else Steam's install on each OS): KSP and Unity from its Managed folder, MechJeb
(`MechJebPath`) and RP-1 from its GameData. MechJeb builds differ between installs (`VesselState.thrustAvailable`
vs `ThrustAvailable`), so build against the install the tests run in; the RP-1 helpers use reflection for
branch-only fields so they compile against any RP0.
