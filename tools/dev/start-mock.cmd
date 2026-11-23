@echo off
cd /d "%~dp0..\..\src\LolRemote.MockLcu"
dotnet run --no-launch-profile
pause
