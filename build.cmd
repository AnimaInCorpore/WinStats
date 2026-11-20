@echo off
rem Builds WinStats.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:"%~dp0WinStats.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll ^
  "%~dp0WinStats.cs"
