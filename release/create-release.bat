@echo off
setlocal
for /f "tokens=2 delims=:" %%C in ('chcp') do set "releaseCodePage=%%C"
chcp 65001 >nul
set "releaseShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
where.exe pwsh.exe >nul 2>&1
if not errorlevel 1 set "releaseShell=pwsh.exe"
"%releaseShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\create-release.ps1" %*
set "releaseExitCode=%ERRORLEVEL%"
if defined releaseCodePage chcp %releaseCodePage% >nul
if not "%releaseExitCode%"=="0" (
    echo Release creation failed.
    pause
    exit /b 1
)
exit /b 0
