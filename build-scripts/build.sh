#!/usr/bin/env bash
#
# Publishes SteamPipe Studio for every runtime identifier declared in
# SteamPipeStudio.App.csproj. Framework-dependent single-file builds: the target
# machine needs the .NET 10 runtime, but not the SDK.
#
# Cross-building works from any host -- the native Skia/HarfBuzz/Avalonia binaries
# for every platform ship inside the Avalonia NuGet packages, so this produces the
# Windows output from Linux just as happily as the reverse.

set -euo pipefail

# The repository root is one level up from this script. Resolving it explicitly is
# what makes the script independent of the directory it is invoked from; the
# project path below is relative and would otherwise fail with MSB1009.
cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/.."

PROJECT="src/SteamPipeStudio.App"
RIDS=(win-x64 osx-arm64 osx-x64 linux-x64)
SDK_URL="https://dotnet.microsoft.com/download/dotnet/10.0"

# -----------------------------------------------------------------------------
# Preflight. The .NET 10 runtime arrives on its own -- bundled with Visual Studio,
# pushed by Windows Update, pulled in as a distro dependency -- so `dotnet` exists
# and answers on machines that cannot build this project at all. Without this
# check the run reaches MSBuild and dies with MSB4236 "the SDK Microsoft.NET.Sdk
# was not found", an error that mentions neither .NET 10 nor the SDK, and that
# gets worse when workloads are installed: the workload resolver fails first and
# hides the real cause.
#
# Asking `dotnet --version` is enough: with global.json present it resolves the
# pinned SDK and fails loudly when nothing matches. The version rule therefore
# lives in global.json alone and is not restated here, where it would drift.
# -----------------------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "*** dotnet was not found on PATH ***" >&2
    echo "Install the .NET 10 SDK: $SDK_URL" >&2
    exit 1
fi

if ! dotnet --version >/dev/null 2>&1; then
    echo "*** No installed .NET SDK satisfies global.json ***" >&2
    echo >&2
    dotnet --version >&2 || true
    echo >&2
    echo "The SDK is required, not the runtime: seeing Microsoft.NETCore.App 10.x" >&2
    echo "in 'dotnet --info' is not enough to build. Install the SDK from $SDK_URL" >&2
    exit 1
fi

echo "Building $PROJECT"
echo "Targets: ${RIDS[*]}"
echo

for RID in "${RIDS[@]}"; do
    echo "=== $RID ==="
    dotnet publish "$PROJECT" -c Release -r "$RID" \
        -p:PublishSingleFile=true --self-contained false --nologo -v minimal
    echo
done

# Restore the execute bit on the Unix binaries. On Linux and macOS `dotnet publish`
# already sets it, so this is belt-and-braces; the case it exists for is a tree that
# came from a Windows build. Running THIS script under Git Bash on Windows does not
# fix that case: the NTFS mount is `noacl`, so chmod is a silent no-op there and the
# binary still needs `chmod +x` once it reaches the target machine. Package with
# tar, which preserves the bit; zip drops it on either host.
for RID in "${RIDS[@]}"; do
    BINARY="$PROJECT/bin/Release/net10.0/$RID/publish/SteamPipeStudio"
    if [ -f "$BINARY" ]; then
        chmod +x "$BINARY"
    fi
done

echo "All builds finished. Output:"
for RID in "${RIDS[@]}"; do
    echo "    $RID  ->  $PROJECT/bin/Release/net10.0/$RID/publish"
done
