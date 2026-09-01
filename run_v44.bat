@echo off
setlocal
cd /d "%~dp0"
title LocalTTS v4.4

if not exist "LocalTTS_v4.4.exe" (
  call build.bat
  if errorlevel 1 exit /b 1
)

start "" "LocalTTS_v4.4.exe"
if errorlevel 1 (
  echo LocalTTS v4.4 could not start.
  pause
)
endlocal
