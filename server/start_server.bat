@echo off
cd /d "%~dp0"
echo Starting Tile Streaming Server...
echo Leave this window open while using the headset / Unity.
echo Press Ctrl+C or close this window to stop.
echo.
".venv\Scripts\python.exe" app.py --tiles tiles_out --host 0.0.0.0 --port 8080
pause
