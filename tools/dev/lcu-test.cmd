@echo off
setlocal
cd /d "%~dp0..\.."
if not exist logs mkdir logs
if exist debug\script-out rmdir /s /q debug\script-out
mkdir debug\script-out
echo RUNNING> debug\script-out\status.txt
taskkill /im LolRemote.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
dotnet build src\LolRemote.Companion -c Debug > logs\lcu-test-build.log 2>&1
if errorlevel 1 (
  echo BUILD_FAILED> debug\script-out\status.txt
  exit /b 1
)
if exist debug\scripts\with-companion.txt (
  start "LolRemote dev" /min dotnet run --no-build --project src\LolRemote.Companion -- --debug-dir "%CD%\debug\dev-dump"
  timeout /t 10 /nobreak >nul
)
if exist debug\scripts\with-probe.txt dotnet run --no-build --project src\LolRemote.Companion -- --probe-calls "%CD%\debug\calls" "%CD%\debug\probe-calls.json"
dotnet run --no-build --project src\LolRemote.Companion -- --lcu-script "%CD%\debug\scripts\current.json" "%CD%\debug\script-out"
echo DONE> debug\script-out\status.txt
exit /b 0
