@echo off
setlocal
cd /d "%~dp0..\.."
set "PATH=%PATH%;C:\Program Files\Git\cmd"
if not exist logs mkdir logs
set "VERSION=%~1"
if "%VERSION%"=="" for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content src\LolRemote.Companion\LolRemote.Companion.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "VERSION=%%v"
if "%VERSION%"=="" set "VERSION=1.0.0"
echo RUNNING> logs\publish-status.txt
if exist tools\ci\release.yml (
  if not exist .github\workflows mkdir .github\workflows
  move /y tools\ci\release.yml .github\workflows\release.yml >nul
)
echo === LolRemote feltoltese GitHubra (v%VERSION%) === > logs\publish.log
git --version >> logs\publish.log 2>&1 || goto fail
if not exist .git git init -b main >> logs\publish.log 2>&1
git config user.name >nul 2>&1 || git config user.name "makkospeter"
git config user.email >nul 2>&1 || git config user.email "makkospeter@users.noreply.github.com"
(
  echo LolRemote %VERSION%
  echo.
  echo Co-Authored-By: Claude Opus 5.5 ^<noreply@anthropic.com^>
  echo Claude-Session: https://claude.ai/code/session_01EdPdjfkajnQDY9pTP5szT5
) > logs\commit-message.txt
git add -A >> logs\publish.log 2>&1 || goto fail
git commit -F logs\commit-message.txt >> logs\publish.log 2>&1
git remote get-url origin >nul 2>&1 || git remote add origin https://github.com/makkospeter/LolRemote.git
git fetch origin main >> logs\publish.log 2>&1
if not errorlevel 1 git merge -s ours --allow-unrelated-histories origin/main -m "GitHub kezdo commit osszefuzese" >> logs\publish.log 2>&1
echo.
echo Ha bejelentkezo ablak jon fel, jelentkezz be a makkospeter fiokkal.
git push -u origin main >> logs\publish.log 2>&1 || goto fail
git tag -f v%VERSION% >> logs\publish.log 2>&1
git push -f origin v%VERSION% >> logs\publish.log 2>&1 || goto fail
echo DONE> logs\publish-status.txt
echo KESZ: https://github.com/makkospeter/LolRemote
timeout /t 10
exit /b 0
:fail
echo FAILED> logs\publish-status.txt
echo HIBA - reszletek: logs\publish.log
timeout /t 30
exit /b 1
