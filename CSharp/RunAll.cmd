@echo off
cd /d "%~dp0"
SEFM.exe all
if errorlevel 1 echo The run encountered an error. Read the messages above.
pause
