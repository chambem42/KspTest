# KspTest on Linux, in Docker

The image builds a repository's tests and runs them with `dotnet test` against KSP's Linux build, on a virtual display
(Xvfb, Mesa's software OpenGL). KspTest makes its KSP instances there as on Windows, with CKAN (on Mono).

## KSP's Linux build, once

It goes into a Docker volume, not a host folder: KSP loads thousands of files, slowly through a host mount. steamcmd
asks for your Steam password and Steam Guard code, so run it in a terminal of your own:

```
docker run -it --rm -v ksptest-steam:/root/Steam -v ksptest-ksp:/ksp steamcmd/steamcmd:latest +@sSteamCmdForcePlatformType linux +force_install_dir /ksp +login YOUR_STEAM_NAME +app_update 220200 validate +quit
```

`ksptest-steam` keeps the login, so the same command (without a password) updates it later.

## The image and a run

```
docker build -t ksptest-linux docker
docker run --rm -v ksptest-ksp:/ksp -v ksptest-instances:/instances -v "$PWD":/src:ro -v "$PWD/linux-results":/out ksptest-linux
```

- `/ksp` is mounted writable: KSP opens its assemblies and bundles for writing (it does not write them), and the
  instances link to its files.
- `/src`: the repository, copied into the container (its `bin/` and `obj/` left out) and built there against `/ksp`.
- Arguments after the image name go to `dotnet test`: a project (default `tests/KspTest.Tests.Run`), `--filter ...`.
- `ksptest-instances` keeps the instances between runs, `/out/results` gets each run's results.
- KSP runs with `-batchmode` (`KSPTEST_KSP_ARGS`): it draws nothing, so screenshots come out black. Drawing with Mesa's
  software OpenGL (`-e KSPTEST_KSP_ARGS=`) gets through loading, the space center and the editors, slowly (about
  100 s to the main menu, 10 CPU cores busy), but hangs in the first frame of a flight scene.

The self-tests on 2026-10-19, KSP 1.12.5 Linux: all five pass in one KSP session of 64 s (batch mode; about 25 s of
that is loading and the new game).

The settings come from the image's environment (`KSPTEST_GAME=/ksp`, `KSPTEST_CKAN`, `KSPTEST_INSTANCES`), so nothing
asks. Instances on Linux mirror the game's folders with a symbolic link per file: KSP finds its root from
`KSP_Data/..`, which a linked folder would point back at the source.
