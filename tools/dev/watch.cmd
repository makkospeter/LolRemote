@echo off
setlocal
cd /d "%~dp0..\.."
title LolRemote teszt-figyelo (ne zard be, amig Claude tesztel)
echo LolRemote teszt-figyelo fut. Claude innen inditja a teszteket.
echo Bezaras: Ctrl+C vagy az ablak bezarasa.
if not exist debug\scripts mkdir debug\scripts
:loop
if exist debug\scripts\run-lcu.txt (
  del debug\scripts\run-lcu.txt
  echo [%time%] LCU teszt...
  call tools\dev\lcu-test.cmd
  echo [%time%] kesz.
)
if exist debug\scripts\run-selftest.txt (
  del debug\scripts\run-selftest.txt
  echo [%time%] Onteszt...
  call tools\dev\selftest.cmd nopause
  echo [%time%] kesz.
)
if exist debug\scripts\run-selftest-strict.txt (
  del debug\scripts\run-selftest-strict.txt
  call tools\dev\selftest.cmd strict
)
if exist debug\scripts\run-publish.txt (
  del debug\scripts\run-publish.txt
  call tools\dev\publish-github.cmd
)
if exist debug\scripts\run-reinstall.txt (
  del debug\scripts\run-reinstall.txt
  call tools\dev\reinstall.cmd
)
if exist debug\scripts\stop.txt (
  del debug\scripts\stop.txt
  exit /b 0
)
timeout /t 2 /nobreak >nul
goto loop
