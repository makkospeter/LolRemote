@echo off
cd /d "%~dp0..\.."
if not exist logs mkdir logs
dotnet build src\LolRemote.Companion -c Release > logs\probe-build.log 2>&1
if errorlevel 1 (
  echo FORDITASI HIBA - lasd logs\probe-build.log
  timeout /t 10
  exit /b 1
)
dotnet run -c Release --no-build --project src\LolRemote.Companion -- --probe-calls "%CD%\debug\calls" "%CD%\debug\probe-calls.json"
echo KESZ> "%CD%\debug\calls\done.txt"
