@echo off
REM build-mods-all-platforms.bat
REM
REM cmd.exe entry point for build-mods-all-platforms.ps1. Forwards all
REM arguments to the PowerShell script unchanged, so use PowerShell-style
REM flags, e.g.:
REM
REM   Tools\build-mods-all-platforms.bat -Groups "NY Modpack"
REM
REM   Tools\build-mods-all-platforms.bat -Groups "NY Modpack","China Modpack" -Versions "v2.1.0","v1.0.0"
REM
REM For Linux/macOS, use build-mods-all-platforms.sh instead (different,
REM comma-separated flag syntax - see that file's header comment).

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-mods-all-platforms.ps1" %*
exit /b %ERRORLEVEL%
