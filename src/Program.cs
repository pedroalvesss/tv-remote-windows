// Controle remoto da TV Samsung (Tizen) na bandeja do Windows.
// Compile com build.cmd: usa só o .NET Framework que já vem no Windows.
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using TvRemote.Tv;
using TvRemote.Ui;

namespace TvRemote
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Uma cópia só: abrir de novo não cria um segundo ícone nem uma segunda conexão com a TV.
            bool isFirstInstance;
            using (new Mutex(true, "TVRemote", out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    return;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var tv = new TvClient();
                var window = new RemoteWindow(tv);
                using (var tray = new TrayIcon(window.Toggle, tv.Rescan, app.Shutdown))
                {
                    Action updateTip = () => tray.SetText(
                        window.Capturing ? "Setas controlam a TV · Esc para parar" : tv.Name + ": " + tv.Status);
                    tv.Changed += updateTip;
                    window.CaptureChanged += () =>
                    {
                        tray.SetActive(window.Capturing);
                        updateTip();
                    };

                    // --open abre o painel ao iniciar (útil para atalhos e testes).
                    app.Startup += (sender, e) =>
                    {
                        tv.Run();
                        if (args.Contains("--open"))
                        {
                            window.Toggle();
                        }
                    };
                    app.Run();
                }
            }
        }
    }
}
