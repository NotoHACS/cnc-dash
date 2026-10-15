@echo off
rem CncDash collector self-update — mirrors the mtconnect-alarm-ntfy update flow.
rem Config (config_local.json, state.json) is preserved. Never overwritten.
setlocal
set REPO=%CNC_UPDATE_REPO%
if "%REPO%"=="" set REPO=NotoHACS/cnc-dash
set SERVICE=%CNC_DASH_SERVICE%
if "%SERVICE%"=="" set SERVICE=cncdash-collector

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update_helper.ps1" -Repo "%REPO%" -ServiceName "%SERVICE%"
if errorlevel 1 goto failed
echo.
echo Update finished (see update.log for detail). Press any key to close.
pause >nul
(goto) 2>nul & move /y "%~dp0update.bat.new" "%~f0" >nul 2>&1
exit /b 0

:failed
echo.
echo Update FAILED - real errors and steps are in update.log. Press any key to close.
pause >nul
exit /b 1
