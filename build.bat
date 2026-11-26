@echo off
rem Builds GlassBar into the "out" folder next to this file. Needs the .NET 8 SDK (or newer):
rem   winget install Microsoft.DotNet.SDK.8
cd /d "%~dp0"
dotnet publish GlassBar.csproj -c Release -r win-x64 --self-contained false -o out
if errorlevel 1 (
  echo.
  echo Build failed. Copy the red error lines above and send them over.
  pause
  exit /b 1
)
echo.
echo Done. Run out\GlassBar.exe
pause
