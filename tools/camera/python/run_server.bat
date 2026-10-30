@echo off
chcp 65001 >nul
title Face Recognition API Service (LAN)

echo =======================================================
echo     Face Recognition API Service Launcher (LAN)
echo =======================================================

set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%.."

call "%SCRIPT_DIR%..\run_server.bat"
