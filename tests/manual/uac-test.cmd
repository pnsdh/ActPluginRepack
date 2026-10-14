@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uac-test.ps1"
if errorlevel 1 pause
