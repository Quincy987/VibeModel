@echo off
REM VibeModel build script - runs build.ps1
REM Usage: build.cmd [RevitVersion] [--debug] [--no-restart]
REM Examples:
REM   build.cmd                 - Build for Revit 2023, restart
REM   build.cmd 2024            - Build for Revit 2024, restart
REM   build.cmd --debug         - Build with debug logging enabled
REM   build.cmd --no-restart    - Build without restarting Revit

setlocal

set REVIT_VERSION=2023
set EXTRA_ARGS=

:parse_args
if "%~1"=="" goto run
if "%~1"=="--debug" (
    set EXTRA_ARGS=%EXTRA_ARGS% -Debug
    shift
    goto parse_args
)
if "%~1"=="--no-restart" (
    set EXTRA_ARGS=%EXTRA_ARGS% -NoRestart
    shift
    goto parse_args
)
if "%~1"=="2022" set REVIT_VERSION=2022
if "%~1"=="2023" set REVIT_VERSION=2023
if "%~1"=="2024" set REVIT_VERSION=2024
if "%~1"=="2025" set REVIT_VERSION=2025
shift
goto parse_args

:run
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1" -RevitVersion %REVIT_VERSION% %EXTRA_ARGS%
