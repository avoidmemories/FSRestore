@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK not found.
    pause
    exit /b 1
)
echo Publishing single-file FSRestore.exe...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o .\publish
if errorlevel 1 (
    echo.
    echo Publish failed.
    pause
    exit /b 1
)
echo.
echo Done: publish\FSRestore.exe
pause
