@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK not found. Install .NET 8 SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
dotnet build -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see errors above.
    pause
    exit /b 1
)
if not exist "bin\Release\net8.0-windows\FSRestore.exe" (
    echo.
    echo Build succeeded but FSRestore.exe was not produced.
    pause
    exit /b 1
)
start "" "bin\Release\net8.0-windows\FSRestore.exe"
exit /b 0
