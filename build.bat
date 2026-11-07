@echo off
rem Build LanSpeaker.exe with the .NET Framework 4 compiler bundled with Windows
rem   build.bat        -> ..\build\LanSpeaker.exe (release)
rem   build.bat test   -> LanSpeaker_test.exe (with --selftest / --screens / --make-icon)
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OPTS=-nologo -target:winexe -optimize+ -win32icon:app.ico -win32manifest:app.manifest -r:System.Windows.Forms.dll -r:System.Drawing.dll
if /i "%1"=="test" (
  "%CSC%" %OPTS% -define:SELFTEST -out:LanSpeaker_test.exe LanSpeaker.cs
) else (
  if not exist ..\build mkdir ..\build
  "%CSC%" %OPTS% -out:..\build\LanSpeaker.exe LanSpeaker.cs
)
if errorlevel 1 pause
