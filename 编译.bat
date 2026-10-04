@echo off
rem ============================================================
rem  One-click rebuild.
rem
rem  This file is intentionally PURE ASCII and has NO BOM:
rem  cmd.exe mangles non-ASCII batch files. All Chinese text
rem  lives in build.ps1, which must be UTF-8 *WITH BOM*
rem  (PowerShell 5.1 decodes a BOM-less .ps1 as ANSI/GBK and
rem  then fails with mojibake parse errors).
rem  Do NOT add non-ASCII characters to this file.
rem ============================================================
setlocal
cd /d "%~dp0"

rem --- self-heal: restore build.ps1's UTF-8 BOM if an editor stripped it
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='build.ps1'; $b=[IO.File]::ReadAllBytes($p); if(-not($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)){ [IO.File]::WriteAllBytes($p, [byte[]](0xEF,0xBB,0xBF)+$b); Write-Host '[fix] build.ps1 had no UTF-8 BOM; restored it.' }"
if errorlevel 1 goto fail

powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1"
if errorlevel 1 goto fail

echo.
pause
exit /b 0

:fail
echo.
echo [FAILED] Build did not complete. See the messages above.
pause
exit /b 1