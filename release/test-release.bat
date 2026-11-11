@echo off
setlocal
for /f "tokens=2 delims=:" %%a in ('chcp') do set "OLD_CP=%%a"
chcp 65001 >nul
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
where pwsh.exe >nul 2>&1
if not errorlevel 1 set "PS=pwsh.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\test-release.ps1" %*
set "RESULT=%ERRORLEVEL%"
if not defined NICOBE_TEST_NO_PAUSE pause
chcp %OLD_CP% >nul
exit /b %RESULT%
