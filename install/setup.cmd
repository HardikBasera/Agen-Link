@echo off
REM ===================================================================
REM  Agen-Link setup - DOUBLE-CLICK THIS FILE.
REM  It runs setup.ps1 with -ExecutionPolicy Bypass so Windows' script
REM  block ("downloaded from the internet" / "not digitally signed")
REM  cannot stop it, and keeps this window open at the end so you can
REM  read the result. (Double-clicking setup.ps1 directly fails.)
REM ===================================================================
setlocal
set "AGENLINK_LAUNCHER=1"
echo Starting Agen-Link setup. This downloads npm packages, so it needs internet.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" echo Setup finished successfully. You can close this window.
if not "%RC%"=="0" echo Setup FAILED (exit %RC%). Read the messages above for the reason.
echo.
pause
endlocal
