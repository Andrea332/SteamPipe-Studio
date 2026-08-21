@echo off
setlocal

rem Publishes SteamPipe Studio for every runtime identifier declared in
rem SteamPipeStudio.App.csproj. Framework-dependent single-file builds: the target
rem machine needs the .NET 10 runtime, but not the SDK.
rem
rem Cross-building is safe from Windows -- the native Skia/HarfBuzz/Avalonia
rem binaries for every platform ship inside the Avalonia NuGet packages, so no Mac
rem and no Linux box is involved in producing the osx-* and linux-x64 output.
rem
rem Every failure below is caught with `|| goto`, never with `if errorlevel 1`.
rem That is not style: `if errorlevel N` is a SIGNED "greater or equal" test, and
rem the .NET host exits with -2147450725 when it cannot find an SDK matching
rem global.json. Being negative, that code is not >= 1, so `if errorlevel 1` walks
rem straight past it -- the script would report every build as successful while
rem producing nothing. `||` fires on any non-zero code, negative included.

rem The repository root is one level up from this script. Resolving it explicitly
rem is what makes the script independent of the current directory: double-clicking
rem it, launching it from a shortcut with a different "Start in", or running it as
rem administrator (which sets the working directory to C:\Windows\System32) would
rem otherwise fail with MSB1009, because the project path below is relative.
cd /d "%~dp0.."

set "PROJECT=src/SteamPipeStudio.App"
set "RIDS=win-x64 osx-arm64 osx-x64 linux-x64"
set "CURRENT="
set "SDKURL=https://dotnet.microsoft.com/download/dotnet/10.0"

rem ---------------------------------------------------------------------------
rem Preflight. Visual Studio and Windows Update both install the .NET 10 *runtime*
rem on their own, so `dotnet` exists and answers on machines that cannot build this
rem project at all. Without this check the run reaches MSBuild and dies with
rem MSB4236 "the SDK Microsoft.NET.Sdk was not found" -- an error that mentions
rem neither .NET 10 nor the SDK, and is worse still when workloads are installed,
rem because the workload resolver fails first and hides the real cause.
rem
rem Asking `dotnet --version` is enough: with global.json present it resolves the
rem pinned SDK and fails loudly when nothing matches. The version rule therefore
rem lives in global.json alone and is not restated here, where it would drift.
rem ---------------------------------------------------------------------------
where dotnet >nul 2>&1 || goto :nodotnet
dotnet --version >nul 2>&1 || goto :nosdk

echo Building %PROJECT%
echo Targets: %RIDS%
echo.

for %%R in (%RIDS%) do (
    set "CURRENT=%%R"
    echo === %%R ===
    dotnet publish %PROJECT% -c Release -r %%R -p:PublishSingleFile=true --self-contained false --nologo -v minimal || goto :failed
    echo.
)

echo All builds finished. Output:
for %%R in (%RIDS%) do echo     %%R  -^>  %PROJECT:/=\%\bin\Release\net10.0\%%R\publish
echo.
echo Note: the linux-x64 and osx-* binaries have no execute bit -- NTFS cannot
echo store one. Package them with tar, or chmod +x after transferring.
pause
exit /b 0

:nodotnet
echo *** dotnet was not found on PATH ***
echo Install the .NET 10 SDK, x64 build: %SDKURL%
pause
exit /b 1

:nosdk
echo *** No installed .NET SDK satisfies global.json ***
echo.
dotnet --version
echo.
echo The SDK is required, not the runtime: seeing Microsoft.NETCore.App 10.x
echo in "dotnet --info" is not enough to build. Install the x64 SDK from
echo %SDKURL% and open a new console afterwards, so the updated PATH is picked up.
pause
exit /b 1

:failed
echo.
echo *** BUILD FAILED for %CURRENT% ***
pause
exit /b 1
