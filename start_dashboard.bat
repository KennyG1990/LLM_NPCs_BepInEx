@echo off
rem Start the Going Medieval LLM NPCs dashboard server (port 8714).
cd /d "%~dp0dashboard"
python dashboard_server.py
echo.
echo Dashboard server exited.
pause
