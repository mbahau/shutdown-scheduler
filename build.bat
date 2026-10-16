@echo off
rem Builds ShutdownScheduler.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK needed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe. .NET Framework 4.x is required ^(included with Windows 10 and 11^).
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize /out:ShutdownScheduler.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ShutdownScheduler.cs
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Built ShutdownScheduler.exe
pause
