#!/bin/bash
# A mod's line coverage in KspTest's in-game CI (Windows jobs; the run and report actions call this): AltCover
# instruments the mod's assemblies where its build put them, the in-game tests run on them, and each KSP session's counts
# merge into one report.
#   coverage.sh instrument DLL...  the assemblies (in one folder under a GameData) instrumented in place, with AltCover's
#                                  recorder, FastVisit and the report beside them, put there only once all of it is
#                                  ready (a failure leaves the plain build); KSPTEST_COLLECT set so each session's counts
#                                  are copied into its results when KSP exits
#   coverage.sh collect TAG        each session's counts, from the results, as $COV/ingame-TAG-<n>.xml
#   coverage.sh report ROOT        every count in $COV merged into $COV/report.md and the job summary (ROOT: see
#                                  coverage_report.py)
# Uses KSPTEST_WORK, KSPTEST_GAME and KSPTEST_RESULTS (the setup action's), COV (default $KSPTEST_WORK/cov) and ALTCOVER
# (the AltCover version).
set -eo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ALTCOVER="${ALTCOVER:-9.0.145}"
COV="${COV:-$KSPTEST_WORK/cov}"
win() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }
unix() { if command -v cygpath >/dev/null; then cygpath -u "$1"; else echo "$1"; fi; }

instrument() {
  [ $# -gt 0 ] || { echo "coverage.sh instrument DLL..."; return 2; }
  local dir f name
  dir="$(cd "$(dirname "$1")" && pwd)"
  for f; do
    [ -f "$f" ] || { echo "::error::no $f to instrument (built?)"; return 1; }
    [ "$(cd "$(dirname "$f")" && pwd)" = "$dir" ] || { echo "::error::the assemblies to cover share one folder (one recorder): $f"; return 1; }
  done
  # the report goes beside the recorder (--portable): inside GameData, where a KSP session's collect finds it
  case "$dir" in */GameData/*) ;; *) echo "::error::$dir is not in a GameData folder"; return 1 ;; esac
  local rel="GameData/${dir##*/GameData/}/coverage.xml"

  curl -fsSL -o "$KSPTEST_WORK/altcover.nupkg" "https://www.nuget.org/api/v2/package/altcover/$ALTCOVER"
  7z x -y -bso0 -bsp0 "-o$KSPTEST_WORK/altcover" "$KSPTEST_WORK/altcover.nupkg" 'tools/net472/*'
  rm -rf "$COV/in" "$COV/out"; mkdir -p "$COV/in"
  for f; do cp "$f" "$COV/in/"; [ -f "${f%.dll}.pdb" ] && cp "${f%.dll}.pdb" "$COV/in/"; done
  # --linecover: sequence points only (the report counts lines; branch probes cost about a fifth of the run). The game's
  # assemblies and its mods' resolve the mod's references
  # (not the assemblies being instrumented, nor KspTest's harness, should the game have copies)
  local deps=() skip=" " g
  for f; do skip+="$(basename "$f") "; done
  g="$(unix "$KSPTEST_GAME")"
  while IFS= read -r f; do
    case "$skip" in *" $(basename "$f") "*) continue ;; esac
    deps+=("-d=$(win "$f")")
  done < <(find "$g"/KSP*_Data/Managed "$g"/GameData -name '*.dll' -not -path '*/GameData/KspTest/*' 2>/dev/null)
  # -p: not code a package put into the build (Krafs.Publicizer's attribute, say), only the mod's own
  "$KSPTEST_WORK/altcover/tools/net472/AltCover.exe" -i="$(win "$COV/in")" -o="$(win "$COV/out")" --save --portable --linecover \
    -a=ExcludeFromCodeCoverage '-p=[\\/]\.nuget[\\/]' "${deps[@]}" > "$COV/instrument.log" 2>&1 || { tail -20 "$COV/instrument.log"; return 1; }
  # every visit call goes to FastVisit, which passes only a point's first visit on to AltCover's recorder
  dotnet build "$HERE/FastVisit" -c Release -v q -nologo -o "$COV/fastvisit"
  local out=()
  for f; do out+=("$COV/out/$(basename "$f")"); done
  dotnet run -c Release --project "$HERE/CovPatch" -- "-r=$(win "$(ls -d "$g"/KSP*_Data/Managed | head -1)")" "-r=$(win "$g/GameData")" "${out[@]}"
  cp "$COV/fastvisit/KspTest.Coverage.FastVisit.dll" "$COV/out/"

  # all ready: into the build's folder
  for f; do
    name="$(basename "$f")"
    cp "$COV/out/$name" "$dir/"
    if [ -f "$COV/out/${name%.dll}.pdb" ]; then cp "$COV/out/${name%.dll}.pdb" "$dir/"; else rm -f "$dir/${name%.dll}.pdb"; fi
  done
  cp "$COV/out/AltCover.Recorder.g.dll" "$COV/out/KspTest.Coverage.FastVisit.dll" "$COV/out/coverage.xml" "$dir/"
  {
    echo "KSPTEST_COLLECT=$rel"
    echo "KSPTEST_COVERAGE_REPORT=$rel"
  } >> "$GITHUB_ENV"
  echo "instrumented: $*; each session's counts collected from $rel"
}

# the counts of every session that got as far as quitting KSP, failed tests or not
collect() {
  local tag=${1:?coverage.sh collect TAG} i=0 f
  mkdir -p "$COV"
  for f in "$(unix "$KSPTEST_RESULTS")"/*/collected/"${KSPTEST_COVERAGE_REPORT:?not instrumented}"; do
    [ -f "$f" ] || continue
    i=$((i + 1)); cp "$f" "$COV/ingame-$tag-$i.xml"
  done
  echo "$i KSP session report(s)"
}

report() {
  local root=${1:?coverage.sh report ROOT} reports=() f
  for f in "$COV"/*.xml; do [ -f "$f" ] && reports+=("$f"); done
  echo "${#reports[@]} report(s)"
  if [ ${#reports[@]} -eq 0 ]; then echo "::warning::no coverage counts to report"; return 0; fi
  python -I "$HERE/coverage_report.py" "$root" "$COV/report.md" "${reports[@]}"
  cat "$COV/report.md" >> "$GITHUB_STEP_SUMMARY"
}

case "$1" in
  instrument | collect | report) "$@" ;;
  *) echo "coverage.sh instrument DLL...|collect TAG|report ROOT"; exit 2 ;;
esac
