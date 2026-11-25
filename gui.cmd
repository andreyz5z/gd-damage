@echo off
rem Build before launching so the editor always runs from the current source.
rem Launch only after a successful build.
setlocal
set GUI_DIR=%~dp0bin\Release\net6.0-windows
set GUI_EXE=%GUI_DIR%\gdc-gui.exe

dotnet build -c Release --nologo -v q "%~dp0GdcGui.csproj"
if errorlevel 1 (
    echo.
    echo Build failed. The editor was not launched.
    echo.
    echo An open editor can lock the executable. Close it and try again.
    echo To open another window from the existing build, run:
    echo     "%GUI_EXE%"
    exit /b 1
)

if not exist "%GUI_EXE%" (
    echo Executable missing after the build: "%GUI_EXE%"
    exit /b 1
)
start "" "%GUI_EXE%" %*
