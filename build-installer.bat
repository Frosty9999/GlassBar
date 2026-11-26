@echo off
rem Builds the GlassBar installer into "out-setup". Needs the .NET 8 SDK and Inno Setup 6:
rem   winget install Microsoft.DotNet.SDK.8
rem   winget install JRSoftware.InnoSetup
cd /d "%~dp0"

set ISCC=
for %%P in ("%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe") do if exist %%P set ISCC=%%P
if not defined ISCC (
  echo Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup
  pause
  exit /b 1
)

rem Self-contained, so people who install GlassBar don't need .NET installed.
if exist out-installer rmdir /s /q out-installer
dotnet publish GlassBar.csproj -c Release -r win-x64 --self-contained true -o out-installer
if errorlevel 1 goto failed

for /f "tokens=3 delims=<>" %%V in ('findstr /c:"<Version>" GlassBar.csproj') do set VERSION=%%V
%ISCC% /Qp /DAppVersion=%VERSION% /DSourceDir=..\out-installer installer\GlassBar.iss
if errorlevel 1 goto failed

echo.
echo Done: out-setup\GlassBar-Setup-v%VERSION%.exe
pause
exit /b 0

:failed
echo.
echo Build failed. Copy the red error lines above and send them over.
pause
exit /b 1
