@echo off
rem Builds TaskbarMonitor.exe using the C# compiler included with Windows (no SDK needed).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WINMD=%WINDIR%\System32\WinMetadata

if not exist "%FW%\csc.exe" (
    echo csc.exe not found in "%FW%". Is .NET Framework 4.x installed?
    exit /b 1
)

"%FW%\csc.exe" /nologo /target:winexe /optimize ^
    /out:TaskbarMonitor.exe ^
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /r:"%FW%\System.Runtime.dll" ^
    /r:"%WINMD%\Windows.Foundation.winmd" ^
    /r:"%WINMD%\Windows.Media.winmd" ^
    /r:"%WINMD%\Windows.Storage.winmd" ^
    src\*.cs

if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built TaskbarMonitor.exe
