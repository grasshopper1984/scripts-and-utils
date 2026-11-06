@echo off
if not exist "%~dp0bin\AgentRoom.exe" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if not exist "%~dp0bin\AgentRoom.exe" exit /b 1
start "" "%~dp0bin\AgentRoom.exe"
