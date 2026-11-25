@echo off
rem Start/stop Konnyaku on a dev machine. See dev.ps1 or docs/chay-local.md. Examples: dev up, dev status, dev down
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev.ps1" %*
