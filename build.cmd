@echo off
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%NET%\csc.exe" /nologo /target:winexe /out:TVRemote.exe /lib:"%NET%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:TVRemote.xaml TVRemote.cs
