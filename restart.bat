@echo off
echo Stopping app...
taskkill /F /IM WebApplication1.exe 2>nul
taskkill /F /IM dotnet.exe 2>nul
timeout /t 3 /nobreak >nul
echo Starting...
cd /d "%~dp0"
start "" dotnet run
timeout /t 8 /nobreak >nul
echo.
echo Open: http://localhost:5240
echo Login: admin  Password: admin
echo If login fails: http://localhost:5240/login/reset-dev
echo.
start http://localhost:5240
pause
