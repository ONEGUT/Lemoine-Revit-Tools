@echo off
rem Double-click wrapper for Install-LemoineTools.ps1.
rem Runs PowerShell with the execution policy bypassed for this process only,
rem so no admin rights and no machine-wide policy change are needed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LemoineTools.ps1" %*
pause
