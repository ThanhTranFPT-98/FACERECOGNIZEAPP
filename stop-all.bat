@echo off
chcp 65001 >nul
echo ==========================================
echo DSA Vehicle - Stop All Services
echo ==========================================
echo.

echo Stopping all services...

:: Kill Python AI Service
taskkill /F /IM python.exe 2>nul
echo [1/3] Python AI Service stopped

:: Kill ASP.NET Backend
taskkill /F /IM DSAVehicle.exe 2>nul
taskkill /F /IM dotnet.exe 2>nul
echo [2/3] ASP.NET Backend stopped

:: Kill Node.js (React UI)
taskkill /F /IM node.exe 2>nul
echo [3/3] React UI stopped

echo.
echo ==========================================
echo All services stopped!
echo ==========================================
pause
