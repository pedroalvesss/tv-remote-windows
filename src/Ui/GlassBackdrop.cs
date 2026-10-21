using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using TvRemote.Interop;

namespace TvRemote.Ui
{
    // Vidro: antes de abrir, fotografa a faixa da tela atrás do painel e desfoca, como o material do macOS.
    // ponytail: é uma foto, não ao vivo (o painel fica aberto pouco tempo); o desfoque nativo do Windows não aceita cantos de 26.
    internal static class GlassBackdrop
    {
        private const double PanelWidth = 320;
        private const double EdgeMargin = 10;
        private const double BlurRadius = 30;

        // A foto já sai em um quarto da resolução: depois de desfocada, mais detalhe não aparece, e fica bem mais rápido.
        private const double Downscale = 4;

        internal static ImageBrush Capture()
        {
            Rect area = SystemParameters.WorkArea;
            double width = PanelWidth;
            double height = area.Height - EdgeMargin;
            double scale = ScreenScale();
            double pixelScale = scale / Downscale;
            BitmapSource shot = CaptureScreen(
                (int)((area.Right - EdgeMargin - PanelWidth) * scale), (int)(area.Top * scale),
                (int)(width * scale), (int)(height * scale),
                (int)(width * pixelScale), (int)(height * pixelScale));

            // A foto sai um pouco das bordas para o desfoque não clarear os cantos.
            var image = new Image
            {
                Source = shot,
                Width = width + 2 * BlurRadius,
                Height = height + 2 * BlurRadius,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = BlurRadius },
            };
            var frame = new Canvas { Width = width, Height = height };
            Canvas.SetLeft(image, -BlurRadius);
            Canvas.SetTop(image, -BlurRadius);
            frame.Children.Add(image);
            frame.Measure(new Size(width, height));
            frame.Arrange(new Rect(0, 0, width, height));

            var glass = new RenderTargetBitmap(
                (int)(width * pixelScale), (int)(height * pixelScale), 96 * pixelScale, 96 * pixelScale, PixelFormats.Pbgra32);
            glass.Render(frame);
            glass.Freeze();
            var brush = new ImageBrush(glass) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Bottom };
            brush.Freeze();
            return brush;
        }

        // Pixels de tela por unidade do WPF (1,5 com a escala de 150% do Windows).
        private static double ScreenScale()
        {
            using (System.Drawing.Graphics screen = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                return screen.DpiX / 96;
            }
        }

        // Copia um pedaço da tela já reduzido (GDI StretchBlt em modo meio-tom, que faz a média dos pixels).
        private static BitmapSource CaptureScreen(int x, int y, int width, int height, int outputWidth, int outputHeight)
        {
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memory = NativeMethods.CreateCompatibleDC(screen);
            IntPtr bitmap = NativeMethods.CreateCompatibleBitmap(screen, outputWidth, outputHeight);
            try
            {
                IntPtr previous = NativeMethods.SelectObject(memory, bitmap);
                NativeMethods.SetStretchBltMode(memory, NativeMethods.HalftoneStretchMode);
                NativeMethods.StretchBlt(memory, 0, 0, outputWidth, outputHeight, screen, x, y, width, height, NativeMethods.SourceCopy);
                NativeMethods.SelectObject(memory, previous);
                BitmapSource pixels = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // O GDI deixa a transparência zerada; sem esta conversão a foto sairia invisível.
                return new FormatConvertedBitmap(pixels, PixelFormats.Bgr32, null, 0);
            }
            finally
            {
                NativeMethods.DeleteObject(bitmap);
                NativeMethods.DeleteDC(memory);
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
