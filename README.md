# KspTest

In-game automated tests for Kerbal Space Program 1.12 mods. A test launches the game, loads a save,
walks through scenes, drives windows (PAW, PopupDialog, IMGUI mod windows), flies, asserts on game
state and quits; the run returns pass/fail with screenshots and logs. Runs on Windows, Linux and macOS.

## Packages

| Package | What |
|---|---|
| `KspTest` | the in-game harness: `TestRunner`, `Nav`, `Wait`, `Ugui`, `Imgui`, `Paw`, `Ctrl`, `Flight`, `Assert`, `Snap` (compile-time reference for in-game test projects) |
| `KspTest.MechJeb` | `Mj` helpers that drive MechJeb (SmartASS, ascent, landing) for any mod's tests |
| `KspTest.AtmosphereAutopilot` | `Aa` helpers that fly on AtmosphereAutopilot (cruise, its controllers' state); by reflection, so it loads with or without AA |
| `KspTest.RP1` | RP-1 integration: launches through KCT simulations, drives the KCT and RP-1 windows |
| `KspTest.Runner` | runs tests from outside the game: deploys the harness (it carries it) and test assemblies, resets the save, launches and watches KSP, collects results |
| `KspTest.Xunit` | `dotnet test` support: `[KspFact]` and `Ksp.Run` |
| `KspTest.Tool` | the `ksptest` command line tool |

Packed into `../nuget-local` (a `NuGet.Config` in the parent folder points every repo there) until published.

## Testing a mod

A mod keeps its tests in two projects of its own; the mod itself never references KspTest.

1. **In-game tests** (net48, loaded by KSP): reference `KspTest` (and `KspTest.MechJeb` / `KspTest.RP1` /
   `KspTest.AtmosphereAutopilot` if needed). The `KspTest` package references KSP's and Unity's assemblies from the
   install in `KspDir` (or the `KSPDIR` environment variable, or `KspData`), none copied to the output; set
   `KspTestKspReferences` to false to reference them yourself.

   ```csharp
   [UITest("Mj_AscentToOrbit", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket")]
   public static IEnumerator Mj_AscentToOrbit(TestContext ctx) { ... }
   ```

   Keep tests that use `KspTest.MechJeb` or `KspTest.RP1` in an assembly of their own: KSP drops an assembly whose
   types do not load, so one that needs MechJeb must not go into an install without it.

2. **`dotnet test` project** (net8.0, xUnit): reference `KspTest.Xunit`, and the in-game projects for their output
   only, each with the files under GameData it needs (`KspRequires`; without them it isn't deployed):

   ```xml
   <ProjectReference Include="../X.KspTests/X.KspTests.csproj"
                     ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" OutputItemType="KspTestPlugin" />
   <ProjectReference Include="../X.KspTests.MechJeb/X.KspTests.MechJeb.csproj"
                     ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" OutputItemType="KspTestPlugin"
                     KspRequires="MechJeb2/Plugins/MechJeb2.dll" />
   <KspTestCraft Include="crafts/*.craft" />
   ```

   ```csharp
   [KspFact]
   public void PlaneCruise() => Ksp.Run(output, "X_Cruise", "altM=3000");          // a new sandbox game
   [KspFact(Requires = "MechJeb2/Plugins/MechJeb2.dll")]
   public void AscentToOrbit() => Ksp.Run(output, "Mj_AscentToOrbit", "orbitAltKm=250", save: "rp1-career");
   ```

   Without a `save` template the run starts a new game (`RunOptions.NewGame`: sandbox, science or career) in an
   emptied `saves/ksptest`, the way the main menu's New Game does; the project's `KspTestCraft`s go into its SPH or
   VAB first. Test classes run one after another (the package's `xunit.runner.json`).

   Settings come from `ksptest.json` beside the test assembly or the environment, which wins: `install` (`KSPDIR`),
   `saves` (`KSPTEST_SAVES`), `results` (`KSPTEST_RESULTS`), `lockFile` (`KSPTEST_LOCK_FILE`), `mute`
   (`KSPTEST_MUTE`), `crafts` (`KSPTEST_CRAFTS`, `;` separated: more crafts for every run) and `params`
   (`KSPTEST_PARAMS`: parameters over the tests' own), so anyone can fly a mod's tests on their own crafts. With no
   install configured every `[KspFact]` is skipped.

MechJeb2's `MechJebKspTest` and `MechJebKspTestRun`, and FAR's `KspTests/`, are worked examples.

### From source (a git submodule)

Until the packages are published, a repo can carry KspTest as a submodule and import
`build/KspTest.FromSource.targets` at the end of each test project instead of the PackageReferences:

```xml
<PropertyGroup>
  <KspTestModules>MechJeb;AtmosphereAutopilot</KspTestModules>   <!-- in-game projects: MechJeb, RP1, AtmosphereAutopilot -->
</PropertyGroup>
<Import Project="../KspTest/build/KspTest.FromSource.targets" />
```

It gives an in-game project the harness and the named modules, and a `dotnet test` project `KspTest.Xunit` with the
runner and the harness it deploys, as the packages would. On an install without MechJeb or RP-1, an in-game project
that needs one builds empty and a `KspRequires` reference to it is left out, so the tests build on any install and
those skip. FAR's `KspTests/` uses it (with `KSPTEST_ROOT` pointing at another checkout to work on both at once).

## ksptest

```
dotnet tool install -g KspTest.Tool        # from the local feed
ksptest deploy --install DIR --tests MechJebKspTest=path/MechJebKspTest.dll
ksptest list --install DIR
ksptest run Mj_LaunchToRendezvous --install DIR --save rp1-career --saves DIR --params "targetInc=45;hohmann=off" --mute
ksptest run Far_ArCruise --install DIR --new-game sandbox --craft "my crafts" --params "crafts=My Plane"
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
| `saves/<template>/` | save templates copied fresh into `<install>/saves/ksptest` before a run that names one (see `saves/README.md`); without one the run starts a new game |
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
by design; the runner resets it from the save template (or empties it for a new game) once per session.

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
- **Nav**: `LoadSave` (a new game when the save folder has none), `NewGame(folder, mode)`,
  `EnterEditor(facility, craft)`, `LoadCraft`, `Launch()` (stock Launch button,
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
(`MechJebPath`) and RP-1 from its GameData. The harness reaches every other mod (FAR, AtmosphereAutopilot,
Trajectories, B9 procedural wings) by reflection, so it loads and its probes skip where a mod is absent.
MechJeb builds differ between installs (`VesselState.thrustAvailable` vs `ThrustAvailable`), so build against the
install the tests run in; the RP-1 helpers use reflection for branch-only fields so they compile against any RP0.
