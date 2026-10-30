@echo off
chcp 65001 >nul
title Face Recognition API Service (LAN)

echo =======================================================
echo     Face Recognition API Service Launcher (LAN)
echo =======================================================

set "ROOT_DIR=%~dp0"
cd /d "%ROOT_DIR%"

set "PYTHON_EXE="

if exist "%ROOT_DIR%python\.venv\Scripts\python.exe" (
    set "PYTHON_EXE=%ROOT_DIR%python\.venv\Scripts\python.exe"
    echo [Environment] Using local project venv: %ROOT_DIR%python\.venv
) else if exist "C:\Home\Develop\venv\Scripts\python.exe" (
    set "PYTHON_EXE=C:\Home\Develop\venv\Scripts\python.exe"
    echo [Environment] Using develop venv: C:\Home\Develop\venv
) else (
    for /f "tokens=*" %%i in ('where python 2^>nul') do (
        set "PYTHON_EXE=%%i"
        goto :found_system_py
    )
    :found_system_py
    if defined PYTHON_EXE (
        echo [Environment] Using system Python: %PYTHON_EXE%
    ) else (
        echo [ERROR] No Python environment detected!
        echo Please ensure python\.venv or C:\Home\Develop\venv exists.
        pause
        exit /b 1
    )
)

echo.
echo Starting Face API Server...
echo Press Ctrl+C to stop the service.
echo.

"%PYTHON_EXE%" python\face_api_server.py

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Service exited with code %ERRORLEVEL%.
    pause
)
