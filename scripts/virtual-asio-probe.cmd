@echo off
rem Double-click me while a track is playing. Read-only; never touches OBS.
rem Verdict: attach\virtual-asio-status.txt
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0virtual-asio-probe.ps1" %*
endlocal
