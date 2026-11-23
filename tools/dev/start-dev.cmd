@echo off
cd /d "%~dp0..\.."
dotnet run --project src\LolRemote.Companion -- --debug-dir "%CD%\debug"
pause
