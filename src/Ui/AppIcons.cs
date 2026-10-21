using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using TvRemote.Tv;

namespace TvRemote.Ui
{
    // Ícones dos apps: o oficial baixado da App Store, ou a marca na cor dela enquanto ele não chega.
    // Cantos de ícone de app da Apple: 22,5% do lado.
    internal sealed class AppIcons
    {
        private const double CornerRatio = 0.225;

        // Parte do nome → fundo, cor e marca do ícone (caractere de E000 a F8FF = símbolo da fonte de ícones).
        private static readonly Brand[] s_brands =
        {
            new Brand("netflix", "#000000", "#E50914", "N"),
            new Brand("youtube", "#FF0000", "#FFFFFF", "\uE768"),
            new Brand("prime", "#1A98FF", "#FFFFFF", "prime"),
            new Brand("disney", "#0B1C5A", "#FFFFFF", "D+"),
            new Brand("max", "#002BE7", "#FFFFFF", "max"),
            new Brand("apple tv", "#000000", "#FFFFFF", "tv"),
            new Brand("paramount", "#0064FF", "#FFFFFF", "P+"),
            new Brand("spotify", "#1DB954", "#000000", "S"),
            new Brand("twitch", "#9146FF", "#FFFFFF", "T"),
            new Brand("browser", "#0091FF", "#FFFFFF", "\uE774"),
        };

        // Uma sombra só, congelada e usada por todos os ícones.
        private static readonly DropShadowEffect s_shadow = CreateShadow();

        // Cada ícone é lido do disco e decodificado uma vez só; as atualizações da lista reaproveitam.
        private readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>();
        private readonly FontFamily _glyphFont;
        private readonly Brush _border;

        public AppIcons(FontFamily glyphFont, Brush border)
        {
            _glyphFont = glyphFont;
            _border = border;
        }

        public FrameworkElement Create(TvApp app, double size)
        {
            var icon = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * CornerRatio),
                BorderBrush = _border,
                BorderThickness = new Thickness(0.5),
                Effect = s_shadow,
            };

            ImageSource image = LoadImage(app.Id);
            if (image != null)
            {
                icon.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                return icon;
            }

            Brand brand = s_brands.FirstOrDefault(candidate => app.Name.ToLowerInvariant().Contains(candidate.Keyword))
                ?? new Brand("", "#3A3A3C", "#FFFFFF", app.Name.Substring(0, 1));
            icon.Background = Palette.FromHex(brand.Background);
            icon.Child = brand.IsSymbol
                ? CreateGlyph(brand.Mark, size * 0.4, Palette.FromHex(brand.Foreground))
                : new TextBlock
                {
                    Text = brand.Mark,
                    Foreground = Palette.FromHex(brand.Foreground),
                    FontWeight = FontWeights.Bold,
                    FontSize = size * (brand.Mark.Length <= 2 ? 0.46 : brand.Mark.Length <= 3 ? 0.34 : 0.27),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            return icon;
        }

        public TextBlock CreateGlyph(string glyph, double size, Brush color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = _glyphFont,
                FontSize = size,
                Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private ImageSource LoadImage(string appId)
        {
            ImageSource image;
            if (_images.TryGetValue(appId, out image))
            {
                return image;
            }

            string file = AppCatalog.IconFile(appId);
            if (file == null)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(file);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            _images[appId] = bitmap;
            return bitmap;
        }

        private static DropShadowEffect CreateShadow()
        {
            var shadow = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Colors.Black };
            shadow.Freeze();
            return shadow;
        }

        // Como desenhar o ícone de um app enquanto o oficial não foi baixado.
        private sealed class Brand
        {
            public Brand(string keyword, string background, string foreground, string mark)
            {
                Keyword = keyword;
                Background = background;
                Foreground = foreground;
                Mark = mark;
            }

            public string Keyword { get; private set; }

            public string Background { get; private set; }

            public string Foreground { get; private set; }

            public string Mark { get; private set; }

            public bool IsSymbol
            {
                get { return Mark.Length == 1 && Mark[0] >= '\uE000' && Mark[0] <= '\uF8FF'; }
            }
        }
    }
}
