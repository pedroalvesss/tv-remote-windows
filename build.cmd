@echo off
rem Compila o TVRemote.exe com o compilador C# que vem no .NET Framework do Windows (nada para instalar).
rem /nowarn:4014: as chamadas async sem await sao de proposito (atualizacoes que correm em segundo plano).
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /nowarn:4014 /out:TVRemote.exe /lib:"%NET%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:src\Ui\RemoteWindow.xaml,TvRemote.Ui.RemoteWindow.xaml /recurse:src\*.cs
