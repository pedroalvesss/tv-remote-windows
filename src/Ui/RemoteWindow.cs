using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TvRemote.Interop;
using TvRemote.Tv;

namespace TvRemote.Ui
{
    // O painel que abre acima do ícone da bandeja. O visual está em RemoteWindow.xaml;
    // botões de lá com Uid="KEY_..." mandam essa tecla para a TV.
    internal sealed class RemoteWindow
    {
        // A margem de 16 da sombra fica fora da tela: o painel fica a 10 da borda.
        private const double ShadowMargin = 6;

        // Cabeçalho, abas, espaçamentos e a margem da sombra somam uns 170: o resto da altura da tela fica para a aba.
        private const double ChromeHeight = 170;

        // Clicar no ícone com o painel aberto já o fecha (perde o foco); não reabre logo em seguida.
        private const double ReopenGuardMs = 300;

        private static readonly string[] s_pages = { "Home", "Remote", "Apps", "Inputs" };
        private static readonly int[] s_sleepChoices = { 30, 60, 120 };
        private static readonly Input[] s_inputs =
        {
            new Input("HDMI 1", "KEY_HDMI1", "\uE7F8"),
            new Input("HDMI 2", "KEY_HDMI2", "\uE7F8"),
            new Input("HDMI 3", "KEY_HDMI3", "\uE7F8"),
            new Input("TV", "KEY_TV", "\uE7F4"),
            new Input("Menu de entradas da TV", "KEY_SOURCE", "\uE8AB"),
        };

        private static readonly Regex s_youTubeLink = new Regex(
            @"(?:youtube\.com/(?:watch\?(?:\S*&)?v=|shorts/|live/|embed/)|youtu\.be/)([\w-]{11})");

        private readonly TvClient _tv;
        private readonly Window _window;
        private readonly Style _buttonStyle;
        private readonly AppIcons _icons;
        private readonly KeyboardCapture _capture;
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly DispatcherTimer _sleepTimer = new DispatcherTimer();
        private DateTime _hiddenAt;

        // Vídeo do link à mostra; o último aberto não volta a aparecer.
        private string _linkVideo;
        private string _playedVideo;

        // A TV não diz se está tocando: o botão alterna entre pausar e continuar.
        private bool _isPaused;

        // Minutos do timer de desligar; 0 = sem timer.
        private int _sleepMinutes;

        public RemoteWindow(TvClient tv)
        {
            _tv = tv;
            using (Stream xaml = typeof(RemoteWindow).Assembly.GetManifestResourceStream("TvRemote.Ui.RemoteWindow.xaml"))
            {
                _window = (Window)XamlReader.Load(xaml);
            }

            _buttonStyle = (Style)_window.FindResource("Btn");
            _icons = new AppIcons((FontFamily)_window.FindResource("Icons"), Resource("Separator"));
            _capture = new KeyboardCapture(() => _window.IsActive && Keyboard.FocusedElement is TextBox);
            _capture.KeyPressed += OnCapturedKey;

            WireStaticButtons();
            WireTabs();
            WireTextFields();
            BuildSleepChoices();
            BuildInputs();

            _sleepTimer.Tick += (sender, e) =>
            {
                if (_tv.IsOn)
                {
                    _tv.Press("KEY_POWER");
                }

                SetSleep(0);
            };
            _poll.Tick += (sender, e) => _tv.RefreshAppsAsync();
            _window.Deactivated += (sender, e) => Hide();

            // A altura muda com a aba: o painel fica preso ao canto.
            _window.SizeChanged += (sender, e) => Place();
            _window.PreviewKeyDown += OnPanelKey;
            _tv.Changed += RenderStatus;
            _tv.AppsChanged += RenderApps;
            _tv.KeyboardOpened += ShowKeyboard;
            _tv.KeyboardClosed += () => Find<UIElement>("TypeModule").Visibility = Visibility.Collapsed;
            RenderApps();
        }

        public event Action CaptureChanged = delegate { };

        public bool Capturing
        {
            get { return _capture.IsActive; }
        }

        public void Toggle()
        {
            bool justHidden = (DateTime.Now - _hiddenAt).TotalMilliseconds < ReopenGuardMs;
            if (_window.IsVisible || justHidden)
            {
                _window.Hide();
                return;
            }

            Open();
        }

        private void Open()
        {
            Find<ScrollViewer>("Pages").MaxHeight = SystemParameters.WorkArea.Height - ChromeHeight;
            Find<Border>("Backdrop").Background = GlassBackdrop.Capture();
            try
            {
                ShowLink(Clipboard.GetText());
            }
            catch (Exception)
            {
                // A área de transferência pode estar ocupada por outro programa: fica sem o cartão do link.
            }

            _window.Show();
            Place();
            _window.Activate();
            _poll.Start();
            _tv.RefreshAppsAsync();
        }

        private void Hide()
        {
            _window.Hide();
            _hiddenAt = DateTime.Now;
            _poll.Stop();

            // Fechado, o painel passa o tempo parado na bandeja: devolve ao Windows a memória que não está usando.
            using (Process self = Process.GetCurrentProcess())
            {
                NativeMethods.EmptyWorkingSet(self.Handle);
            }
        }

        private void Place()
        {
            Rect area = SystemParameters.WorkArea;
            _window.Left = area.Right - _window.ActualWidth + ShadowMargin;
            _window.Top = area.Bottom - _window.ActualHeight + ShadowMargin;
        }

        private void WireStaticButtons()
        {
            // Botões só com ícone: o texto da dica vira o nome lido pelo leitor de tela.
            foreach (FrameworkElement element in Descendants(_window).OfType<FrameworkElement>().Where(element => element.ToolTip is string))
            {
                AutomationProperties.SetName(element, (string)element.ToolTip);
            }

            foreach (Button button in Descendants(_window).OfType<Button>().Where(button => button.Uid.StartsWith("KEY_")))
            {
                string key = button.Uid;
                button.Click += (sender, e) => _tv.Press(key);
            }

            Find<Button>("Capture").Click += (sender, e) => SetCapture(!Capturing);
            Find<Button>("Mirror").Click += (sender, e) => Mirror();
            Find<Button>("PlayPause").Click += (sender, e) => TogglePlayPause();
            Find<Button>("PlayLink").Click += (sender, e) => OpenLink();
            Find<Button>("TypeOpen").Click += (sender, e) => ShowKeyboard();
            Find<Button>("SeeApps").Click += (sender, e) => Find<RadioButton>("TabApps").IsChecked = true;
            Find<Button>("SeeInputs").Click += (sender, e) => Find<RadioButton>("TabInputs").IsChecked = true;
        }

        private void WireTabs()
        {
            foreach (string name in s_pages)
            {
                UIElement page = Find<UIElement>("Page" + name);
                Find<RadioButton>("Tab" + name).Checked += (sender, e) =>
                {
                    foreach (string other in s_pages)
                    {
                        Find<UIElement>("Page" + other).Visibility = Visibility.Collapsed;
                    }

                    page.Visibility = Visibility.Visible;
                };
            }
        }

        private void WireTextFields()
        {
            TextBox search = Find<TextBox>("Search");
            search.TextChanged += (sender, e) =>
            {
                // Um link do YouTube colado na busca vira o cartão "Abrir na TV".
                if (ShowLink(search.Text))
                {
                    search.Text = "";
                    Find<RadioButton>("TabHome").IsChecked = true;
                    return;
                }

                Find<UIElement>("SearchHint").Visibility = search.Text == "" ? Visibility.Visible : Visibility.Collapsed;
                RenderApps();
            };

            TextBox typeBox = Find<TextBox>("TypeBox");
            typeBox.TextChanged += (sender, e) =>
                Find<UIElement>("TypeHint").Visibility = typeBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            typeBox.KeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter)
                {
                    return;
                }

                _tv.Type(typeBox.Text);
                typeBox.Text = "";
                Find<UIElement>("TypeModule").Visibility = Visibility.Collapsed;
                e.Handled = true;
            };
        }

        // A TV abriu o teclado na tela (ou o botão Aa foi tocado): o painel abre com o campo pronto para digitar.
        private void ShowKeyboard()
        {
            Find<RadioButton>("TabHome").IsChecked = true;
            Find<UIElement>("TypeModule").Visibility = Visibility.Visible;
            if (!_window.IsVisible)
            {
                Open();
            }

            _window.Activate();
            Find<TextBox>("TypeBox").Focus();
        }

        private bool ShowLink(string text)
        {
            Match match = s_youTubeLink.Match(text ?? "");
            bool isNew = match.Success && match.Groups[1].Value != _playedVideo;
            if (isNew)
            {
                _linkVideo = match.Groups[1].Value;
                Find<TextBlock>("LinkText").Text = "youtu.be/" + _linkVideo;
            }

            Find<UIElement>("LinkModule").Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            return isNew;
        }

        private async void OpenLink()
        {
            if (await _tv.PlayYouTubeAsync(_linkVideo))
            {
                _playedVideo = _linkVideo;
                Find<UIElement>("LinkModule").Visibility = Visibility.Collapsed;
            }
            else
            {
                Find<TextBlock>("LinkText").Text = "A TV não abriu o vídeo";
            }
        }

        private void TogglePlayPause()
        {
            _tv.Press(_isPaused ? "KEY_PLAY" : "KEY_PAUSE");
            _isPaused = !_isPaused;
            Find<TextBlock>("PlayPauseGlyph").Text = _isPaused ? "\uE768" : "\uE769";
        }

        private void BuildSleepChoices()
        {
            foreach (int minutes in s_sleepChoices)
            {
                int choice = minutes;
                var chip = new Button
                {
                    Style = _buttonStyle,
                    Tag = "14",
                    Height = 28,
                    Margin = new Thickness(3),
                    DataContext = minutes,
                    Content = new TextBlock { Text = minutes < 60 ? minutes + " min" : minutes / 60 + " h", FontSize = 12 },
                };

                // Tocar de novo no mesmo cancela.
                chip.Click += (sender, e) => SetSleep(choice == _sleepMinutes ? 0 : choice);
                Find<Panel>("SleepChoices").Children.Add(chip);
            }
        }

        // Desligar com timer: o próprio app manda o comando na hora, então funciona mesmo se a TV não tiver essa opção.
        private void SetSleep(int minutes)
        {
            _sleepTimer.Stop();
            _sleepMinutes = minutes;
            if (minutes > 0)
            {
                _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
                _sleepTimer.Start();
            }

            Find<TextBlock>("SleepText").Text = minutes > 0 ? "às " + DateTime.Now.AddMinutes(minutes).ToString("HH:mm") : "";
            foreach (Button chip in Find<Panel>("SleepChoices").Children.OfType<Button>())
            {
                bool isActive = (int)chip.DataContext == minutes;
                chip.Background = Resource(isActive ? "Blue" : "Fill");
                chip.Foreground = isActive ? Brushes.White : Resource("Label");
            }
        }

        // Espelhar a tela: abre o "Transmitir" do Windows (Win+K), que acha a TV e espelha por Miracast.
        private void Mirror()
        {
            _window.Hide();
            NativeMethods.keybd_event(NativeMethods.WindowsKey, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.KKey, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.KKey, 0, NativeMethods.KeyUpFlag, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.WindowsKey, 0, NativeMethods.KeyUpFlag, UIntPtr.Zero);
        }

        // Modo teclado: setas, Enter, Backspace e as teclas de volume e mídia viram o controle em qualquer janela, até apertar Esc.
        private void SetCapture(bool isOn)
        {
            if (isOn == Capturing)
            {
                return;
            }

            if (isOn)
            {
                _capture.Start();
            }
            else
            {
                _capture.Stop();
            }

            Button button = Find<Button>("Capture");
            button.Background = Resource(isOn ? "Blue" : "Fill");
            button.Foreground = isOn ? Brushes.White : Resource("Label");
            Find<UIElement>("CaptureHint").Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
            CaptureChanged();
        }

        private void OnCapturedKey(string key)
        {
            if (key == KeyboardCapture.Escape)
            {
                _window.Dispatcher.BeginInvoke(new Action(() => SetCapture(false)));
            }
            else if (key == KeyboardCapture.PlayPause)
            {
                _window.Dispatcher.BeginInvoke(new Action(TogglePlayPause));
            }
            else
            {
                _tv.Press("KEY_" + key);
            }
        }

        // Atalhos com o painel aberto.
        private void OnPanelKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _window.Hide();
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            string key = PanelShortcut(e.Key);
            if (key == null)
            {
                return;
            }

            _tv.Press("KEY_" + key);
            e.Handled = true;
        }

        private static string PanelShortcut(Key key)
        {
            switch (key)
            {
                case Key.Up: return "UP";
                case Key.Down: return "DOWN";
                case Key.Left: return "LEFT";
                case Key.Right: return "RIGHT";
                case Key.Enter: return "ENTER";
                case Key.Back: return "RETURN";
                case Key.H: return "HOME";
                case Key.M: return "MUTE";
                case Key.OemPlus:
                case Key.Add: return "VOLUP";
                case Key.OemMinus:
                case Key.Subtract: return "VOLDOWN";
                default: return null;
            }
        }

        private void RenderStatus()
        {
            bool isConnected = _tv.State == ConnectionState.Connected;
            Find<TextBlock>("TvName").Text = _tv.Name;
            Find<TextBlock>("StatusText").Text = isConnected ? (_tv.IsOn ? "Ligada" : "Em espera") : _tv.Status;
            Find<Ellipse>("StatusDot").Fill = Palette.FromHex(StatusColor(isConnected));

            // Vermelho com a TV ligada (desliga), verde com ela em espera ou desligada (liga).
            Button power = Find<Button>("Power");
            power.Background = _tv.IsOn ? Palette.FromHex("#33FF4245") : Resource("Fill");
            power.Foreground = Palette.FromHex(_tv.IsOn ? "#FF4245" : "#30D158");
            power.ToolTip = _tv.IsOn ? "Desligar" : "Ligar";
            AutomationProperties.SetName(power, (string)power.ToolTip);
        }

        private string StatusColor(bool isConnected)
        {
            if (isConnected)
            {
                return _tv.IsOn ? "#30D158" : "#636366";
            }

            switch (_tv.State)
            {
                case ConnectionState.Waiting: return "#FF9230";
                case ConnectionState.Failed: return "#FF4245";
                default: return "#636366";
            }
        }

        private void RenderApps()
        {
            string query = Find<TextBox>("Search").Text.Trim();
            Panel grid = Find<Panel>("AppsGrid");
            grid.Children.Clear();
            foreach (TvApp app in _tv.Apps.Where(app => app.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                grid.Children.Add(CreateAppButton(app, 48));
            }

            Panel favorites = Find<Panel>("Favorites");
            favorites.Children.Clear();
            foreach (TvApp app in _tv.Apps.Take(4))
            {
                favorites.Children.Add(CreateAppButton(app, 44));
            }

            TvApp onScreen = _tv.Apps.FirstOrDefault(app => app.IsVisible);
            Find<TextBlock>("NowName").Text = onScreen != null ? onScreen.Name : "Nenhum app aberto";
            Find<UIElement>("Media").Visibility = onScreen != null ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("NowIcon").Child = onScreen != null
                ? _icons.Create(onScreen, 40)
                : new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = Resource("Fill"),
                    Child = _icons.CreateGlyph("\uE7F4", 16, Resource("Secondary")),
                };
        }

        private void BuildInputs()
        {
            Panel list = Find<Panel>("InputsList");
            for (int i = 0; i < s_inputs.Length; i++)
            {
                Input input = s_inputs[i];
                if (i > 0)
                {
                    list.Children.Add(new Rectangle { Height = 1, Fill = Resource("Separator"), Margin = new Thickness(40, 0, 8, 0) });
                }

                var name = new TextBlock { Text = input.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                TextBlock chevron = _icons.CreateGlyph("\uE76C", 10, Resource("Tertiary"));
                Grid.SetColumn(name, 1);
                Grid.SetColumn(chevron, 2);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(_icons.CreateGlyph(input.Glyph, 14, Resource("Secondary")));
                row.Children.Add(name);
                row.Children.Add(chevron);

                var item = new Button
                {
                    Style = _buttonStyle,
                    Background = Brushes.Transparent,
                    Tag = "10",
                    Height = 36,
                    Padding = new Thickness(8, 0, 10, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = row,
                };
                AutomationProperties.SetName(item, input.Name);
                item.Click += (sender, e) => _tv.Press(input.Key);
                list.Children.Add(item);

                // O menu de entradas só aparece na lista completa, não nos atalhos do Início.
                if (input.Key == "KEY_SOURCE")
                {
                    continue;
                }

                var chip = new Button
                {
                    Style = _buttonStyle,
                    Tag = "14",
                    Height = 28,
                    Margin = new Thickness(3),
                    Content = new TextBlock { Text = input.Name, FontSize = 12 },
                };
                chip.Click += (sender, e) => _tv.Press(input.Key);
                Find<Panel>("InputChips").Children.Add(chip);
            }
        }

        // Ícone, nome embaixo e um ponto azul quando o app está na tela (como no Dock).
        private Button CreateAppButton(TvApp app, double size)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(_icons.Create(app, size));
            stack.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 6, 0, 0),
            });
            stack.Children.Add(new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 3, 0, 0),
                Fill = app.IsVisible ? Resource("Blue") : Brushes.Transparent,
            });

            var button = new Button
            {
                Style = _buttonStyle,
                Background = Brushes.Transparent,
                Tag = "10",
                Padding = new Thickness(2, 6, 2, 2),
                Content = stack,
                ToolTip = "Abrir " + app.Name,
            };
            AutomationProperties.SetName(button, app.Name);
            button.Click += (sender, e) => _tv.Launch(app.Id);
            return button;
        }

        private T Find<T>(string name) where T : class
        {
            return (T)_window.FindName(name);
        }

        private Brush Resource(string key)
        {
            return (Brush)_window.FindResource(key);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        // Uma entrada da TV: nome, tecla e símbolo da lista.
        private sealed class Input
        {
            public Input(string name, string key, string glyph)
            {
                Name = name;
                Key = key;
                Glyph = glyph;
            }

            public string Name { get; private set; }

            public string Key { get; private set; }

            public string Glyph { get; private set; }
        }
    }
}
