@echo off
setlocal

rem Publishes SteamPipe Studio for every runtime identifier declared in
rem SteamPipeStudio.App.csproj. Framework-dependent single-file builds: the target
rem machine needs the .NET 10 runtime, but not the SDK.
rem
rem Cross-building is safe from Windows -- the native Skia/HarfBuzz/Avalonia
rem binaries for every platform ship inside the Avalonia NuGet packages, so no Mac
rem and no Linux box is involved in producing the osx-* and linux-x64 output.

rem The repository root is one level up from this script. Resolving it explicitly
rem is what makes the script independent of the current directory: double-clicking
rem it, launching it from a shortcut with a different "Start in", or running it as
rem administrator (which sets the working directory to C:\Windows\System32) would
rem otherwise fail with MSB1009, because the project path below is relative.
cd /d "%~dp0.."

set "PROJECT=src/SteamPipeStudio.App"
set "RIDS=win-x64 osx-arm64 osx-x64 linux-x64"
set "CURRENT="

echo Building %PROJECT%
echo Targets: %RIDS%
echo.

for %%R in (%RIDS%) do (
    set "CURRENT=%%R"
    echo === %%R ===
    dotnet publish %PROJECT% -c Release -r %%R -p:PublishSingleFile=true --self-contained false --nologo -v minimal
    if errorlevel 1 goto :failed
    echo.
)

echo All builds finished. Output:
for %%R in (%RIDS%) do echo     %%R  -^>  %PROJECT:/=\%\bin\Release\net10.0\%%R\publish
echo.
echo Note: the linux-x64 and osx-* binaries have no execute bit -- NTFS cannot
echo store one. Package them with tar, or chmod +x after transferring.
pause
exit /b 0

:failed
echo.
echo *** BUILD FAILED for %CURRENT% ***
pause
exit /b 1
