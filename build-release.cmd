@echo off
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
echo RUNNING> logs\release-status.txt
echo === LolRemote.exe keszitese (egyetlen fajl, nem kell hozza .NET) ===
taskkill /im LolRemote.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
if exist release rmdir /s /q release
dotnet publish src\LolRemote.Companion -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release > logs\release-build.log 2>&1
if errorlevel 1 goto fail
echo DONE> logs\release-status.txt
echo.
echo KESZ: release\LolRemote.exe
timeout /t 10
exit /b 0
:fail
echo FAILED> logs\release-status.txt
echo HIBA a forditas kozben - reszletek: logs\release-build.log
timeout /t 20
exit /b 1
