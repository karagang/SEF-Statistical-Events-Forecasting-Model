@echo off
cd /d "%~dp0"
SEFM.exe forecast
if errorlevel 1 echo Forecasts encountered an error. Read the messages above.
pause
