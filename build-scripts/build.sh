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
