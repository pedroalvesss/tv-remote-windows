using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using TvRemote.Interop;

namespace TvRemote.Ui
{
    // O ícone de TV na bandeja do Windows, com o menu do clique direito.
    internal sealed class TrayIcon : IDisposable
    {
        // Texto de dica da bandeja: o Windows aceita no máximo 63 caracteres.
        private const int MaxTipLength = 63;

        private readonly NotifyIcon _notifyIcon;

        // Desenhados uma vez só: o modo teclado só troca entre os dois.
        private readonly Icon _normalIcon;
        private readonly Icon _activeIcon;

        public TrayIcon(Action onOpen, Action onRescan, Action onExit)
        {
            _normalIcon = Draw(false);
            _activeIcon = Draw(true);
            var menu = new ContextMenuStrip();
            menu.Items.Add("Procurar a TV de novo", null, (sender, e) => onRescan());
            menu.Items.Add("Sair", null, (sender, e) => onExit());
            _notifyIcon = new NotifyIcon { Icon = _normalIcon, Text = "Controle da TV", Visible = true, ContextMenuStrip = menu };
            _notifyIcon.MouseClick += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    onOpen();
                }
            };
        }

        // No modo teclado o ícone fica azul, para lembrar que as teclas estão indo para a TV.
        public void SetActive(bool isActive)
        {
            _notifyIcon.Icon = isActive ? _activeIcon : _normalIcon;
        }

        public void SetText(string text)
        {
            _notifyIcon.Text = text.Length > MaxTipLength ? text.Substring(0, MaxTipLength) : text;
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _normalIcon.Dispose();
            _activeIcon.Dispose();
        }

        // Desenha o símbolo de TV da fonte de ícones do Windows: branco com a barra de tarefas escura, preto com ela clara.
        private static Icon Draw(bool isActive)
        {
            object lightTheme = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
            bool isLight = lightTheme is int && (int)lightTheme == 1;
            Color color = isActive ? Color.FromArgb(0, 145, 255) : isLight ? Color.Black : Color.White;

            string glyphFont = "Segoe Fluent Icons";
            using (var probe = new Font(glyphFont, 10))
            {
                if (probe.Name != glyphFont)
                {
                    glyphFont = "Segoe MDL2 Assets";
                }
            }

            int size = SystemInformation.SmallIconSize.Width;
            using (var bitmap = new Bitmap(size, size))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var font = new Font(glyphFont, size * 0.8f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString("\uE7F4", font, brush, new RectangleF(0, 0, size, size), center);

                // A cópia é dona do próprio desenho; o original é liberado na hora, sem vazar.
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }
    }
}
