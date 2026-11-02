#!/bin/bash
# Copies the repository mounted at /src to /work (bin/ and obj/ of the host's builds left out), starts a virtual display
# and runs dotnet test there against the Linux KSP in /ksp; arguments go to dotnet test (default: the self-tests).
set -e
rsync -a --delete --exclude bin/ --exclude obj/ --exclude results/ --exclude TestResults/ /src/ /work/
cd /work
Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp >/tmp/xvfb.log 2>&1 &
for i in $(seq 50); do xdpyinfo -display :99 >/dev/null 2>&1 && break; sleep 0.2; done
[ $# -eq 0 ] && set -- tests/KspTest.Tests.Run
exec dotnet test -p:KspDir=/ksp --logger "console;verbosity=normal" "$@"
