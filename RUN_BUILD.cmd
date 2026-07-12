@echo off
setlocal
title Nexus Monach Build
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Portable.ps1"
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" (
  echo.
  echo Build failed. See the error message above.
  pause
  exit /b %BUILD_EXIT%
)
echo.
echo Portable archive created successfully.
pause
exit /b 0
