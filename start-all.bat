@echo off
chcp 65001 >nul
echo ==========================================
echo DSA Vehicle - Startup Script
echo ==========================================
echo.

:: Start Backend (ASP.NET)
echo [1/3] Starting ASP.NET Backend...
start "Backend" powershell -NoExit -Command "cd '%~dp0DSAVehicle'; dotnet run"

:: Wait for backend to start (needs time to compile + connect to SQL Server)
timeout /t 20 /nobreak >nul

:: Start Python AI Service
echo [2/3] Starting Python AI Service...
start "Python AI" powershell -NoExit -Command "cd '%~dp0AiService'; .\face_detection_env\Scripts\Activate.ps1; $env:PYTHONPATH='.'; uvicorn app.main:app --host 0.0.0.0 --port 8000"

:: Wait for Python to start (needs 30-60s to load models)
timeout /t 15 /nobreak >nul

:: Start React UI
echo [3/3] Starting React UI...
start "React UI" powershell -NoExit -Command "cd '%~dp0ui'; npm run dev"

echo.
echo ==========================================
echo All services started!
echo ==========================================
echo Backend:    http://localhost:5085
echo Python AI:  http://localhost:8000 (WAIT 30-60s for models to load!)
echo React UI:   http://localhost:5173
echo ==========================================
echo.
echo IMPORTANT: Python AI needs 30-60 seconds to load face recognition models.
echo Wait until you see "Uvicorn running on http://0.0.0.0:8000" in Python window.
echo ==========================================
pause
