@echo off
setlocal
cd /d "%~dp0..\.."
if not exist logs mkdir logs
set "MOCKARGS="
if /i "%~1"=="practice" set "MOCKARGS=--practice"
if exist logs\selftest-result.txt del logs\selftest-result.txt
echo RUNNING> logs\selftest-status.txt
echo === LolRemote onteszt ===
echo [1/3] Forditas (naplo: logs\selftest-build.log)
dotnet build src\LolRemote.MockLcu -c Debug > logs\selftest-build.log 2>&1
if errorlevel 1 goto buildfail
dotnet build src\LolRemote.Companion -c Debug >> logs\selftest-build.log 2>&1
if errorlevel 1 goto buildfail
echo [2/3] Mock kliens inditasa
start "LolRemote Mock (onteszt)" /min dotnet run --no-build --no-launch-profile --project src\LolRemote.MockLcu -- %MOCKARGS%
timeout /t 6 /nobreak >nul
echo [3/3] Tesztkor futtatasa (kb. 1-2 perc)
dotnet run --no-build --project src\LolRemote.Companion -- --mock --selftest "%CD%\logs\selftest-result.txt" > logs\selftest-console.log 2>&1
echo DONE> logs\selftest-status.txt
type logs\selftest-result.txt
timeout /t 15
exit /b 0
:buildfail
echo BUILD_FAILED> logs\selftest-status.txt
echo FORDITASI HIBA - reszletek: logs\selftest-build.log
timeout /t 20
exit /b 1
