# KspTest's tests on Linux: the .NET SDK to build and run them, Xvfb and Mesa's software OpenGL for KSP to draw on,
# and Mono for CKAN's ckan.exe.  The game itself is not in the image: KSP's Linux build goes in the ksptest-ksp volume
# (see README.md), and the instances KspTest makes from it in ksptest-instances.  KSP runs with -batchmode (draws
# nothing): with Mesa's software OpenGL it hangs in the first frame of a flight scene.
FROM mcr.microsoft.com/dotnet/sdk:8.0

RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb xauth x11-utils libgl1 libgl1-mesa-dri libglu1-mesa libegl1 \
        libxcursor1 libxrandr2 libxi6 libxinerama1 libxss1 libxext6 libx11-6 libgtk-3-0 \
        libpulse0 libasound2 libudev1 \
        mono-runtime mono-devel ca-certificates-mono \
        rsync procps \
    && rm -rf /var/lib/apt/lists/*

# the latest CKAN release (KSPTEST_CKAN points the runner at it; it runs on Mono)
ADD https://github.com/KSP-CKAN/CKAN/releases/latest/download/ckan.exe /opt/ckan/ckan.exe

ENV KSPTEST_GAME=/ksp \
    KSPTEST_CKAN=/opt/ckan/ckan.exe \
    KSPTEST_INSTANCES=/instances \
    KSPTEST_RESULTS=/out/results \
    KSPTEST_KSP_ARGS=-batchmode \
    DISPLAY=:99 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

COPY run-tests.sh /usr/local/bin/run-tests
RUN chmod +x /usr/local/bin/run-tests
ENTRYPOINT ["run-tests"]
