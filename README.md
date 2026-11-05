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
| `KspTest.Far` | FAR by reflection: its prediction API (`FarApi`, `FarSim`), flight forces, voxels, wing state, replays; loads anywhere |
| `KspTest.Trajectories` | Trajectories by reflection: its API (`TrajApi`), entry accuracy measurements, its probes; loads anywhere |
| `KspTest.Trajectories.MechJeb` | Trajectories' descent profiles flown on SmartASS (`TrajProfileFlight`, `ProfileHold`), and its stock tests; needs MechJeb |
| `KspTest.Trajectories.RP1` | Trajectories' tests in RP-1 (KCT reentry simulations); needs MechJeb and RP-1 |
| `KspTest.WaypointManager` | WaypointManager probes (`WaypointProbe`); loads anywhere |
| `KspTest.WaypointManager.RP1` | WaypointManager's test in an RP-1 simulation; needs RP-1 |
| `KspTest.ROLib` | ROLib's tests (the intake scaler, on RO's parts with AJE and FAR) and the crafts they fly; loads anywhere |

A mod's module holds both the helpers other mods' tests use and the mod's own tests, one module per set of mods it
needs (`KspTest.Trajectories` with nothing, `.MechJeb` with MechJeb, `.RP1` with RP-1 too), so a module never needs
more than its users have. Its tests are there for whoever loads it, but a run of `all` only takes the tests of the run's
own test assemblies; another module's run by name.
| `KspTest.Runner` | runs tests from outside the game: deploys the harness (it carries it) and test assemblies, resets the save, launches and watches KSP, collects results |
| `KspTest.Xunit` | `dotnet test` support: a test for every `[UITest]` (a source generator), `[KspFact]` and `Ksp.Run` |
| `KspTest.Tool` | the `ksptest` command line tool |

Packed into `../nuget-local` (a `NuGet.Config` in the parent folder points every repo there) until published.

## Testing a mod

A mod keeps its tests in two projects of its own; the mod itself never references KspTest.

1. **In-game tests** (net48, loaded by KSP): reference `KspTest` (and `KspTest.MechJeb` / `KspTest.RP1` /
   `KspTest.AtmosphereAutopilot` if needed). The `KspTest` package references KSP's and Unity's assemblies from the
   install in `KspDir` (or the `KSPDIR` environment variable, or `KspData`), none copied to the output; set
   `KspTestKspReferences` to false to reference them yourself.

   ```csharp
   [UITest(Kind = "Check", Requires = "MechJeb", Mods = "MechJeb2", StartsAt = StartState.Flight, Craft = "UITest_Rocket")]
   public static IEnumerator Mj_AscentToOrbit(TestContext ctx)
   {
       double alt = ctx.ParamDouble("orbitAltKm", 250);   // ParamBool, ParamInt, ParamList("a+b") too
       ...
   }
   ```

   A test is `Namespace.Class.Method`, and goes by its method name where that is unique. Besides where it starts, its
   `[UITest]` says what it runs in, for the `dotnet test` side: `Mods` (besides the project's: CKAN identifiers, or a
   mod built locally as the path of its `GameData/ModName` folder from this project's folder), `Save` (a save template
   the test project ships) or `NewGame` (`sandbox`, the default, `science` or `career`), `Kind` (the trait to filter on:
   `Check`, `Measure`, ...), `Isolated` (a KSP session of its own) and `TimeoutSec`. The runner brings the game to
   StartsAt with the craft (found in the save's or the game's Ships/VAB or SPH) before the test starts, clearing any
   popup on the way; `Nav.Restart(ctx)` goes back there, and `ctx.Craft` is the craft's path. After every test the
   runner detaches the controls, closes the PAWs and stops any warp.

   Keep tests that use `KspTest.MechJeb` or `KspTest.RP1` in an assembly of their own: KSP drops an assembly whose
   types do not load, so one that needs MechJeb must not go into an install without it.

2. **`dotnet test` project** (net8.0, xUnit): reference `KspTest.Xunit`, and the in-game projects for their output
   only, each with the files under GameData it needs (`KspRequires`; without them it isn't deployed). It has no test
   code: every `[UITest]` in those projects is a test of the same full name (`dotnet test --filter Kind=Check`, or
   `--filter FullyQualifiedName~Mj_`), made by KspTest.Xunit's source generator from the attributes.

   ```xml
   <ProjectReference Include="../X.KspTests/X.KspTests.csproj"
                     ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" OutputItemType="KspTestPlugin" />
   <ProjectReference Include="../X.KspTests.MechJeb/X.KspTests.MechJeb.csproj"
                     ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" OutputItemType="KspTestPlugin"
                     KspRequires="MechJeb2/Plugins/MechJeb2.dll" />
   <KspTestCraft Include="crafts/*.craft" />
   <KspTestSave Include="saves/my-career" />     <!-- [UITest(Save = "my-career")] -->
   ```

   Tests that run alike (the same mods, save or new game) share one KSP session, and each reports its own result and
   time as soon as KSP has finished it. Tests run in order of class and then method name, in `dotnet test` and in
   the session alike. While they run, the console shows each session's progress as it happens (`[xUnit.net ...]`
   lines: an instance made, waiting for another KSP, KSP loaded, each test started and its result), through the xUnit
   diagnostic messages KspTest.Xunit's `xunit.runner.json` turns on. Without a `Save` the run starts a new game in an emptied save of its own (below), the way the main menu's New Game
   does; the project's `KspTestCraft`s go into its SPH or VAB first. A save template is always one the project ships,
   never a player's own. A test the harness skips (a mod it `Requires` is not loaded) is skipped in `dotnet test` too.
   A hand-written `[KspFact]` calling `Ksp.Run("k=v")` (the in-game test of its name) still works, for a run with
   parameters of its own.

   Settings come from `ksptest.json` beside the test assembly or the environment, which wins: `install` (`KSPDIR`),
   `results` (`KSPTEST_RESULTS`), `lockFile` (`KSPTEST_LOCK_FILE`), `mute`
   (`KSPTEST_MUTE`), `textures` (`KSPTEST_TEXTURES`, see Low-res textures below), `crafts` (`KSPTEST_CRAFTS`, `;` separated: more crafts for every run), `maxKsps`
   (`KSPTEST_MAX_KSPS`, below) and `params` (`KSPTEST_PARAMS`: parameters over the tests' own), so anyone can fly a
   mod's tests on their own crafts. With no install configured, the tests run in a KSP instance KspTest makes (below).

   `maxKsps` above 1 runs that many KSPs at once: each session of more than a couple of minutes is split into up to
   that many shards, runs of its tests in plan order of about the same time by each test's last duration in the
   results folder (30 s for one never run), and every session starts as soon as the tests are planned, each in a thin
   clone of its instance (`<instance>@N`, below) holding a slot. The local mods go into the instance itself, once,
   before the first clone's KSP starts (the clones' mod folders are its); each clone gets the harness in its own
   GameData/KspTest. `dotnet test` reports each test as its shard's KSP finishes it. FAR's 65 tests: 17 minutes one
   at a time, 7 with `KSPTEST_MAX_KSPS=3` (12 cores, 64 GB, an 8 GB GPU; each KSP ~3.7 GB).

   The durations can come from more results folders, `durationsFrom` (`KSPTEST_DURATIONS`, `;` separated), for runs
   alike kept elsewhere: a coverage run's tests take many times as long, so they split by earlier coverage runs. The
   longest session waiting gets the next slot. A file a run collects (`collect`) is written into a folder of the
   clone's own, not the instance's (a coverage report beside a mod's assemblies), and is the instance's again in a run
   that does not collect it. `timeoutScale` (`KSPTEST_TIMEOUT_SCALE`) stretches every in-game wait and test timeout for
   a run known to be slow: FAR's coverage, three instrumented KSPs at once, takes 33 minutes with it at 2, where one
   KSP took 63.

### KSP instances

With no `install` configured, each test runs in a KSP instance with just the mods it needs, which KspTest makes the
first time and reuses after. The test project declares them, as CKAN identifiers (`Id` or `Id=version`), and local
mods: the path of a `GameData/ModName` folder, for a mod built from source or one that is not on CKAN:

```xml
<KspMod Include="FerramAerospaceResearchContinued;KSPBurst" />
<KspMod Include="..\..\StandInEngines\GameData\StandInEngines" />  <!-- a local mod -->
<KspOverlay Include="..\..\GameData\FerramAerospaceResearch" />    <!-- the same, named for a mod over CKAN's copy -->
```

A test adds its own with `[UITest(Mods = "MechJeb2")]`, and `mods` in `ksptest.json` (`KSPTEST_MODS`) adds to every
run, CKAN identifiers or paths alike. Each distinct set of mods is its own instance, a local mod counting by its folder
name as a CKAN mod does by its identifier. The local mods are copied into the instance (made the same as their folder)
for each run, so it always has their latest build; `KspTest-local.txt` in the instance says which are in.

An instance is a stock game from the user's KSP install (any install: its mods aren't copied), with `KSP_x64_Data`,
`MonoBleedingEdge` and `GameData/Squad` as junctions to it, so it costs tens of MB plus its mods. CKAN installs the
mods and their dependencies from the user's CKAN download cache. The instance's mods and their versions are in
`KspTest-instance.json` in its folder, copied into each run's results as `ksp-instance.json`. Instances are never
updated: delete one (`ksptest instances delete`) to get newer mods. Windows only so far.

Three things are the user's to choose, once per machine, saved in `%APPDATA%\KspTest\settings.json`: the KSP install to
copy the stock game from (`gameSource`; KspTest suggests the ones it finds), whether to use an existing `ckan.exe` or
download the latest (`ckan`), and whether the instances go in the default folder (`%LOCALAPPDATA%\KspTest\instances`)
or another (`instances`). `ksptest setup` asks on the console (from a checkout: `dotnet run --project
src/KspTest.Tool -- setup`). `dotnet test` gives the tests no console to ask on, so until then they skip and say to run
it. `KSPTEST_GAME`, `KSPTEST_CKAN` and `KSPTEST_INSTANCES` override the file, and `KSPTEST_SETTINGS` moves it.
`ckanRepos` (`KSPTEST_CKAN_REPOS`), ";" separated, adds CKAN repositories to new instances: a name from CKAN's list of
them, such as `MechJeb2-dev` for MechJeb's dev builds (`MechJeb2-dev` in the mods), or `name=url`.

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
ksptest setup                                   # the KSP install, CKAN and instances folder for KSP instances
ksptest run Far_ComOffset_Editor --instance --mods "FerramAerospaceResearchContinued;KSPBurst" --overlay GameData/FerramAerospaceResearch
ksptest instances                               # list them; ksptest instances delete NAME|--all
```

Only one KSP runs at a time on a machine: every run waits while any KSP is running or another launcher holds the
lock file (`--lock`, default `$KSPTEST_LOCK_FILE` or one per user), then holds it. A script that holds the lock across
several runs passes it on with `KSPTEST_LOCK_PID`. `run-test.ps1` and `deploy.ps1` are wrappers over `ksptest` for the
scripts that still call them.

Results land in `results/<timestamp>-<test>/`: `result.json` (status, steps, recorded values,
screenshots), `harness.log`, `KSP.log`, `screenshots/<test>/NN-[s<k>-<scenario>-]<name>.png` (the test's
own shots (`Snap.Shot`), and `fail`, `crash` or `timeout` as a test fails, before its cleanup runs; the params
add the drivers' own: `clickShots=on` an IMGUI form as it stands when its button is pressed
(`<window>-before-<button>`), `waitShots=on` each wait on game state such as flight ready or an entry ending
(`wait-<what>`; not scene loads, delays or field edits), `passShots=on` a passing test's end (`end-pass`)),
and on failure `diagnostics.txt` (what was on screen: open popups, clickable uGUI buttons, IMGUI windows
with their labels/buttons/fields, input locks, log tail).

### Low-res textures

`--textures low` (`-Textures low` in `run-test.ps1`, `KSPTEST_TEXTURES`) has the harness hold Unity's
`masterTextureLimit` at 3, an eighth of every mipmapped texture's resolution, from before the loader reads them: an RP-1
instance drops from ~16 to ~9-10 GiB, most of it GPU memory the process commits. Screenshots (`Snap.Shot`, so the wait,
end and Serve `shot`s too) and video captures switch to full textures first (~1.5 s, then 3 frames) and go back 5 s
after the last one. `settings.cfg` is never written with it: the harness puts its limit back whenever KSP applies its
own `TEXTURE_QUALITY`. In a Serve session, `textures mode=full|low` holds full textures (for looking at the window) and
lets them go. Not reduced: textures without mipmaps, RSS planet textures (KSPTextureLoader uploads them itself) and the
CPU copies of readable textures. `-batchmode` alone saves nothing: KSP still creates the graphics device and uploads
everything.

### Side by side: thin clones

`--clone N` plays in clone N of `--install` (`<instances>/<install>@N`, `--clone-root` elsewhere; `ksptest clone
--install DIR --clone N` makes one without a run), beside KSPs in the other clones: up to `--max-ksps` (2 with
`--clone`, `KSPTEST_MAX_KSPS`). A clone is made in ~5 s and takes ~60 MB: each mod folder and most root folders are
junctions to the master's, KSP_x64_Data's asset files hard links (same volume; else copies), and its own are the root
files (settings.cfg, PartDatabase.cfg; KSP.log, as KSP runs there), saves, Logs, KSP_x64_Data/Plugins and KSPBurst's
state in PluginData (it recompiles once per clone: its arguments name the install), GameData's root files (the
ModuleManager cache, taken from the master when the master's is newer) and `KspTest`, `AtmosphereAutopilot`,
`QuickMods` and `Kopernicus/Config`. Each run brings its clone up to date first. Mods writing into their own folders
(settings at exit, ContractConfigurator's logs, Kerbalism's cache) write the master's, last writer winning.

The lock gives such runs slots, one per install: a run waits while another plays in its clone, while the slots are
taken, while a one-KSP run holds the lock or a KSP no slot owns is running, and while another clone's KSP is still
loading, so loads go one at a time and a ModuleManager cache a clone rebuilt is handed to the master before the next
clone copies it. Give each Serve session its own `port` (and `ksptest serve --port`); each KSP writes its Player.log
into its results, named `<time>-<test>@N`. Two RP-1 clones with `--textures low` take ~10 GiB and ~2.7 GB of GPU memory
each.

### Serve sessions

For work that loads, measures and flies a craft over and over, a Serve session keeps one KSP up and answers requests
over HTTP on 127.0.0.1, each run on the main thread in turn, with a JSON answer:

```
ksptest run Serve --install DIR --new-game sandbox --timeout 43200 --params "craft=My Plane;port=8642"
ksptest serve help                               # the requests and what they take
ksptest serve load craft="My Plane"
ksptest serve far mach=0.5 aoas=0,2,4
ksptest serve dash point=15000:2 holdSec=120
ksptest serve-plugin tools/ServePlugins/ServePlugins.csproj   # requests in progress, loaded without a restart
ksptest serve quit
```

A request is a `public static IEnumerator` method marked `[ServeCommand("name", "help")]` taking `(TestContext,
NameValueCollection query, Dictionary<string, object> answer)`, in the harness, its modules or the deployed test
assemblies. `ksptest serve-plugin` builds a project of them under a fresh assembly name (Mono loads no name twice and
unloads nothing) and hands it to the session's `plugin` request, so new or changed requests take effect at once. The name
reaches the project as `ServePluginName`, which it makes its `AssemblyName` and `OutputPath` (as `tools/ServePlugins` does);
the projects it references are not rebuilt.

| Requests | Where |
|---|---|
| status, load, reload, shots, shot, textures, parts, field, save, get, set, call (any loaded type's members), plugin, uitest, help, quit | the harness |
| catalog, gear, pitchlimit, closeup, takeoff | the harness, for craft work |
| far, farsections, farlat, dash | KspTest.Far |
| fly, aafly | KspTest.AtmosphereAutopilot |

New requests start in `tools/ServePlugins` (its `example` is a template) and move into the harness or a module once
they settle.

A test can be written the same way: a `[UITest]` in `tools/ServePlugins` runs in the session with `ksptest serve uitest
name=TEST k=v ...` as soon as `serve-plugin` has loaded it (the newest plugin's copy wins; a deployed test runs too). It is
brought to its `StartsAt` as the runner would, the request's other values its params (not the session's), and the answer
has its status (pass, fail, crash, skipped), message and recorded values. Once it passes it moves to its module's `Tests/`.

## Layout

| Path | What |
|---|---|
| `src/KspTest*` | the packages above |
| `src/KspTest/Core/` | the in-game runner: discovery, run.cfg, test context, assertions, logging |
| `src/KspTest/Drive/` | driving the game: scene navigation, waits, uGUI, IMGUI and PAW drivers |
| `src/KspTest/Flight/` | flight helpers, manual controls, flight traces |
| `src/KspTest/Capture/` | screenshots, editor three-views and map-view time lapses |
| `src/KspTest/Serve/` | Serve sessions: a KSP that stays up answering requests |
| `src/KspTest/Profiling/` | frame time, GC, allocations, worker threads |
| `src/KspTest/Probes/` | stock physics: drag cubes, solar occlusion, forces, torques, RCS, yaw |
| `src/KspTest.<Mod>[.<Dependency>]/` | one mod's module: its helpers at the top, its tests in `Tests/` |
| `tests/KspTest.Tests.Stock`, `.Run` | the harness's own in-game tests, and the `dotnet test` project that runs them |
| `tests/KspTest.Tests.MechJeb`, `.RP1` | the other tests still waiting to move to their mods' modules or repos |
| `saves/<template>/` | save templates copied fresh into the run's save before a run that names one (see `saves/README.md`); without one the run starts a new game |
| `tools/ServePlugins/` | Serve requests in progress, loaded into a Serve session with `ksptest serve-plugin` |
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
The save is the run's own, named after its tests: `saves/ksptest-<Test>`, or `saves/ksptest-<First>+<N>-<hash>` for
a session of several (the hash over them all), so a run never resets a save another test or a Serve session (`ksptest-Serve`)
plays in. `--save-folder` names one instead.

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
- Every wait has a timeout (`Wait.Until(cond, sec, "what")`); the whole test has `[UITest(TimeoutSec)]` (else the
  run's `timeoutSec`, 900), which the runner outside follows: no timeout to set on the `[KspFact]` side.
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
  `SetSlider(part, field, v)`, `SetFloatEdit(part, field, v)` through the real `UIPartAction*` items; `InvokeDirect` / `SetDirect`
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
