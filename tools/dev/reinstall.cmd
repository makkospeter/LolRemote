@echo off
setlocal
cd /d "%~dp0..\.."
if not exist logs mkdir logs
echo RUNNING> logs\reinstall-status.txt
set "SETUP=%TEMP%\LolRemote-Setup-1.4.0.exe"
echo [1/4] Letoltes: LolRemote-Setup.exe (v1.4.0)
curl.exe -L --fail -o "%SETUP%" https://github.com/makkospeter/LolRemote/releases/download/v1.4.0/LolRemote-Setup.exe > logs\reinstall.log 2>&1
if errorlevel 1 (
  echo DOWNLOAD_FAILED> logs\reinstall-status.txt
  echo Letoltesi hiba.
  timeout /t 20
  exit /b 1
)
taskkill /im LolRemote.exe /f >nul 2>&1
echo [2/4] Regi verzio eltavolitasa + [3/4] uj telepitese (rendszergazdai engedely kell: kattints az IGEN gombra)
> "%TEMP%\lolremote-elevated.cmd" (
  echo @echo off
  echo if exist "%ProgramFiles%\LolRemote\unins000.exe" "%ProgramFiles%\LolRemote\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  echo timeout /t 3 /nobreak ^>nul
  echo "%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  echo echo %%errorlevel%%^> "%TEMP%\lolremote-install-exit.txt"
)
if exist "%TEMP%\lolremote-install-exit.txt" del "%TEMP%\lolremote-install-exit.txt"
powershell -NoProfile -Command "Start-Process -FilePath '%TEMP%\lolremote-elevated.cmd' -Verb RunAs -Wait -WindowStyle Hidden" >> logs\reinstall.log 2>&1
if not exist "%TEMP%\lolremote-install-exit.txt" (
  echo ELEVATION_DENIED> logs\reinstall-status.txt
  echo A telepites nem futott le, nem kapott rendszergazdai engedelyt.
  timeout /t 20
  exit /b 1
)
type "%TEMP%\lolremote-install-exit.txt" >> logs\reinstall.log
echo [4/4] Inditas
if not exist "%ProgramFiles%\LolRemote\LolRemote.exe" (
  echo NOT_INSTALLED> logs\reinstall-status.txt
  echo Nem talalom a telepitett programot.
  timeout /t 20
  exit /b 1
)
powershell -NoProfile -Command "(Get-Item '%ProgramFiles%\LolRemote\LolRemote.exe').VersionInfo.ProductVersion" >> logs\reinstall.log 2>&1
start "" "%ProgramFiles%\LolRemote\LolRemote.exe"
echo DONE> logs\reinstall-status.txt
echo KESZ.
timeout /t 5
exit /b 0
