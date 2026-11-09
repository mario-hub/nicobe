@echo off
setlocal
for %%I in ("%~dp0..\app-encoder") do set "NICOBE_APP=%%~fI"
set "TEMP=%NICOBE_APP%\temp"
set "TMP=%TEMP%"
if not exist "%TEMP%" mkdir "%TEMP%"
start "" /D "%NICOBE_APP%" "%NICOBE_APP%\NicoBE.Encoder.exe" %*
