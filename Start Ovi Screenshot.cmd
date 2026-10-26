@echo off
set "OVI_CAPTURE_EXE=%~dp0artifacts\windows\Debug\app\Ovi.Screenshot.exe"
if exist "%~dp0artifacts\windows\Debug\current-app.txt" set /p OVI_CAPTURE_EXE=<"%~dp0artifacts\windows\Debug\current-app.txt"
if not exist "%OVI_CAPTURE_EXE%" (
    echo Build the app first with scripts\build.ps1.
    pause
    exit /b 1
)
start "" "%OVI_CAPTURE_EXE%"
