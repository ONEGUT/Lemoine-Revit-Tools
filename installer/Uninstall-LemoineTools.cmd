@echo off
rem Double-click wrapper for Uninstall-LemoineTools.ps1.
rem Runs PowerShell with the execution policy bypassed for this process only,
rem so no admin rights and no machine-wide policy change are needed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-LemoineTools.ps1" %*
pause
