@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patchday-e2e.ps1" %*
if errorlevel 1 pause
