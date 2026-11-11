@echo off
setlocal
for /f "tokens=2 delims=:" %%C in ('chcp') do set "releaseHtmlCodePage=%%C"
chcp 65001 >nul
set "releaseHtmlShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
where.exe pwsh.exe >nul 2>&1
if not errorlevel 1 set "releaseHtmlShell=pwsh.exe"
"%releaseHtmlShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\create-release-page.ps1" -OutputPath "%~dp0index.html" %*
set "releaseHtmlExitCode=%ERRORLEVEL%"
if defined releaseHtmlCodePage chcp %releaseHtmlCodePage% >nul
if not "%releaseHtmlExitCode%"=="0" (
    echo HTML creation failed.
    pause
    exit /b 1
)
echo Created: "%~dp0index.html"
exit /b 0
