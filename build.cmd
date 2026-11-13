@echo off
rem Builds bin\TrayView.exe and bin\TrayView-Setup.exe (the same file; the name makes it install)
rem with the C# compiler that ships with the .NET Framework in Windows. No SDK or Visual Studio needed.
rem The exe is AnyCPU: it runs natively on x64 and on ARM64 (.NET Framework 4.8.1, part of Windows 11).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- ^
  /win32manifest:src\app.manifest /win32icon:src\app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /out:bin\TrayView.exe src\*.cs || exit /b 1
copy /y bin\TrayView.exe bin\TrayView-Setup.exe >nul || exit /b 1
