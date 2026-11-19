@echo off
rem ShardForge Manager: start, stop and update ShardForge without a console window.
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0manager\ShardForge-Manager.ps1"
