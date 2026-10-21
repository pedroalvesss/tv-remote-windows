using System.Collections.Generic;
using System.Windows.Media;

namespace TvRemote.Ui
{
    // Cores feitas em código: cada uma vira um brush congelado uma vez só, reaproveitado em todo desenho
    // (recomendação de desempenho do WPF: brush congelado não precisa avisar mudanças e ocupa menos memória).
    internal static class Palette
    {
        private static readonly Dictionary<string, SolidColorBrush> s_brushes = new Dictionary<string, SolidColorBrush>();

        internal static SolidColorBrush FromHex(string hex)
        {
            SolidColorBrush brush;
            if (!s_brushes.TryGetValue(hex, out brush))
            {
                brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
                brush.Freeze();
                s_brushes[hex] = brush;
            }

            return brush;
        }
    }
}
