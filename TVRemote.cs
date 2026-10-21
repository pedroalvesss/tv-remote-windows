// Controle remoto da TV Samsung (Tizen) na bandeja do Windows.
// Compile com build.cmd: usa só o .NET Framework que já vem no Windows. O visual está em TVRemote.xaml.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

static class Program {
    [STAThread]
    static void Main(string[] args) {
        bool first;
        using (new Mutex(true, "TVRemote", out first)) {
            if (!first) return;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var tv = new Tv();
            var remote = new Remote(tv);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Procurar a TV de novo", null, (s, e) => tv.Rescan());
            menu.Items.Add("Sair", null, (s, e) => app.Shutdown());
            var tray = new System.Windows.Forms.NotifyIcon { Icon = TrayIcon(), Text = "Controle da TV", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) remote.Toggle(); };
            Action tip = () => {
                var text = remote.Capturing ? "Setas controlam a TV · Esc para parar" : tv.Name + ": " + tv.Status;
                tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            };
            tv.Changed += tip;
            // No modo teclado o ícone fica azul, para lembrar que as setas estão indo para a TV.
            remote.CaptureChanged += () => { tray.Icon = TrayIcon(remote.Capturing); tip(); };
            app.Startup += (s, e) => { tv.Run(); if (args.Contains("--open")) remote.Toggle(); };
            app.Run();
            tray.Visible = false;
        }
    }

    static System.Drawing.Icon TrayIcon(bool active = false) {
        var light = (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) ?? 0) == 1;
        var glyphs = "Segoe Fluent Icons";
        using (var f = new System.Drawing.Font(glyphs, 10)) if (f.Name != glyphs) glyphs = "Segoe MDL2 Assets";
        int size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var color = active ? System.Drawing.Color.FromArgb(0, 145, 255) : light ? System.Drawing.Color.Black : System.Drawing.Color.White;
        using (var bmp = new System.Drawing.Bitmap(size, size))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var font = new System.Drawing.Font(glyphs, size * 0.8f, System.Drawing.GraphicsUnit.Pixel))
        using (var brush = new System.Drawing.SolidBrush(color)) {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var center = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            g.DrawString("\uE7F4", font, brush, new System.Drawing.RectangleF(0, 0, size, size), center);
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
    }
}

class TvApp {
    public string Id, Name;
    public bool Visible;
}

// A conexão com a TV: acha na rede, pareia, manda teclas, vê e abre apps.
class Tv {
    static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TVRemote");
    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    static readonly HttpClient Web = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    // A TV (2023+) não lista os apps instalados, mas responde por ID: estes são perguntados um a um.
    // Ao lado, o mesmo app na App Store, de onde vem o ícone oficial.
    // ponytail: lista fixa, app fora dela não aparece. Para incluir um, ache o ID e acrescente aqui.
    static readonly string[][] Catalog = {
        new[] { "3201907018807", "com.netflix.Netflix" },
        new[] { "111299001912", "com.google.ios.youtube" },
        new[] { "3201910019365", "com.amazon.aiv.AIVApp" },
        new[] { "3201901017640", "com.disney.disneyplus" },
        new[] { "3202301029760", "com.wbd.stream" },       // HBO Max
        new[] { "3201807016597", "com.apple.tv" },
        new[] { "3202110025305", "com.cbs.canada.app" },   // Paramount+
        new[] { "3201606009684", "com.spotify.client" },
        new[] { "3202203026841", "tv.twitch" },
        new[] { "3202010022079", null },                   // Internet
    };
    public string Ip, Name = "TV", Country, Status = "Procurando a TV…", State = "off";
    public bool On; // tela ligada; em espera a TV continua na rede, mas com a tela apagada
    public List<TvApp> Apps = new List<TvApp>();
    public event Action Changed = delegate { };
    public event Action AppsChanged = delegate { };
    public event Action KeyboardOpened = delegate { }, KeyboardClosed = delegate { }; // teclado na tela da TV
    ClientWebSocket ws;
    bool paired, rescan, refreshing;
    readonly HashSet<string> iconsTried = new HashSet<string>();
    readonly SemaphoreSlim sending = new SemaphoreSlim(1);

    public Tv() {
        Directory.CreateDirectory(Dir);
        Ip = Load("ip");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        // A TV usa certificado autoassinado: aceita só o dela.
        ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            errors == SslPolicyErrors.None || (sender is HttpWebRequest && ((HttpWebRequest)sender).Address.Host == Ip);
    }

    public bool Connected { get { return paired && ws != null && ws.State == WebSocketState.Open; } }

    public async void Run() {
        while (true) {
            if (Ip == null || rescan) {
                rescan = false;
                Set("Procurando a TV na rede…", "off");
                var found = await Discover();
                if (found != null) Save("ip", Ip = found);
                else if (Ip == null) { Set("Nenhuma TV Samsung na rede", "off"); await Task.Delay(10000); continue; }
            }
            if (!await ReadInfo()) { On = false; Set("TV desligada", "off"); await Task.Delay(5000); continue; }
            var apps = RefreshApps();
            try { await Session(); }
            catch (Exception e) { Set("Sem conexão (" + e.GetBaseException().Message + ")", "error"); }
            await apps;
            paired = false;
            await Task.Delay(3000);
        }
    }

    public void Rescan() {
        rescan = true;
        if (ws != null) ws.Abort();
    }

    public async void Press(string key) {
        if (!Connected) { if (key == "KEY_POWER") Wake(); return; }
        await Send("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"Click\",\"DataOfCmd\":\"" + key +
                   "\",\"Option\":\"false\",\"TypeOfRemote\":\"SendRemoteKey\"}}");
    }

    public async void Launch(string id) {
        if (Ip == null) return;
        bool opened = false;
        try { opened = (await Http.PostAsync("http://" + Ip + ":8001/api/v2/applications/" + id, null)).IsSuccessStatusCode; }
        catch { }
        if (!opened)
            await Send("{\"method\":\"ms.channel.emit\",\"params\":{\"event\":\"ed.apps.launch\",\"to\":\"host\",\"data\":{\"appId\":\"" +
                       id + "\",\"action_type\":\"DEEP_LINK\"}}}");
        await Task.Delay(2500);
        await RefreshApps();
    }

    // Texto inteiro de uma vez no teclado aberto na tela da TV; o fim confirma, como o Enter do teclado dela.
    public async void Type(string text) {
        await Send("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) +
                   "\",\"DataOfCmd\":\"base64\",\"TypeOfRemote\":\"SendInputString\"}}");
        await Send("{\"method\":\"ms.remote.control\",\"params\":{\"TypeOfRemote\":\"SendInputEnd\"}}");
    }

    // Abre um vídeo no YouTube da TV pelo DIAL (porta 8080), o mesmo caminho do "transmitir" do celular.
    public async Task<bool> PlayYouTube(string video) {
        if (Ip == null) return false;
        try {
            var reply = await Http.PostAsync("http://" + Ip + ":8080/ws/apps/YouTube", new StringContent("v=" + video, Encoding.UTF8, "text/plain"));
            return reply.IsSuccessStatusCode;
        } catch { return false; }
    }

    public async Task RefreshApps() {
        if (Ip == null || refreshing) return;
        refreshing = true;
        try {
            var info = Fetch("http://" + Ip + ":8001/api/v2/");
            var replies = await Task.WhenAll(Catalog.Select(c => Probe("http://" + Ip + ":8001/api/v2/applications/" + c[0])));
            var json = await info;
            var on = json != null && Field(json, "PowerState") == "on";
            if (on != On) { On = on; Changed(); }
            var apps = new List<TvApp>();
            for (int i = 0; i < Catalog.Length; i++) {
                if (replies[i] == null) {
                    // A TV demorou para responder: mantém o app como estava, para a lista não piscar.
                    var known = Apps.FirstOrDefault(a => a.Id == Catalog[i][0]);
                    if (known != null) apps.Add(known);
                    continue;
                }
                var name = Field(replies[i], "name");
                if (name == null) continue; // não instalado
                apps.Add(new TvApp { Id = Catalog[i][0], Name = name.Split(new[] { " - " }, StringSplitOptions.None)[0],
                                     Visible = Regex.IsMatch(replies[i], "\"visible\"\\s*:\\s*true") });
            }
            Func<List<TvApp>, string> key = list => string.Join(",", list.Select(a => a.Id + a.Visible));
            if (key(apps) != key(Apps)) { Apps = apps; AppsChanged(); FetchIcons(); }
        } finally { refreshing = false; }
    }

    // Como Fetch, mas separa "não existe" (404 → texto vazio) de "não respondeu" (null).
    static async Task<string> Probe(string url) {
        try {
            var reply = await Http.GetAsync(url);
            return reply.StatusCode == HttpStatusCode.NotFound ? "" : reply.IsSuccessStatusCode ? await reply.Content.ReadAsStringAsync() : null;
        } catch { return null; }
    }

    public static string IconFile(string id) {
        var file = System.IO.Path.Combine(Dir, "icons", id + ".jpg");
        return File.Exists(file) ? file : null;
    }

    // Ícones oficiais pela busca pública da App Store (na loja do país da TV), guardados em %APPDATA%\TVRemote\icons.
    async void FetchIcons() {
        bool got = false;
        foreach (var c in Catalog) {
            if (c[1] == null || IconFile(c[0]) != null || !Apps.Any(a => a.Id == c[0]) || !iconsTried.Add(c[0])) continue;
            try {
                var json = await Web.GetStringAsync("https://itunes.apple.com/lookup?bundleId=" + c[1] + "&country=" + (Country ?? "us"));
                var url = Field(json, "artworkUrl512");
                if (url == null) continue;
                var image = await Web.GetByteArrayAsync(url.Replace("512x512bb", "128x128bb"));
                Directory.CreateDirectory(System.IO.Path.Combine(Dir, "icons"));
                File.WriteAllBytes(System.IO.Path.Combine(Dir, "icons", c[0] + ".jpg"), image);
                got = true;
            } catch { }
        }
        if (got) AppsChanged();
    }

    async Task Session() {
        var token = Load("token");
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes("Controle do PC"));
        var url = "wss://" + Ip + ":8002/api/v2/channels/samsung.remote.control?name=" + Uri.EscapeDataString(name) +
                  (token == null ? "" : "&token=" + token);
        ws = new ClientWebSocket();
        // Sem resposta em 30 s, desiste e pede de novo: o pedido na TV não expira sozinho.
        var pairing = new CancellationTokenSource(30000);
        Set("Conectando…", "wait");
        await ws.ConnectAsync(new Uri(url), pairing.Token);
        Set("Permita o acesso na tela da TV", "wait");
        var buf = new byte[65536];
        while (ws.State == WebSocketState.Open) {
            var text = new StringBuilder();
            WebSocketReceiveResult r;
            do {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), paired ? CancellationToken.None : pairing.Token);
                text.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Close) return;
            var msg = text.ToString();
            var ev = Field(msg, "event");
            if (ev == "ms.channel.connect") {
                var t = Field(msg, "token");
                if (t != null) Save("token", t);
                paired = true;
                Set("Conectada", "ok");
            } else if (ev == "ms.channel.unauthorized") {
                Set("A TV recusou o acesso", "error");
                return;
            } else if (ev == "ms.remote.imeStart") {
                KeyboardOpened();
            } else if (ev == "ms.remote.imeEnd") {
                KeyboardClosed();
            }
        }
    }

    async Task Send(string json) {
        if (!Connected) return;
        await sending.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { ws.Abort(); }
        finally { sending.Release(); }
    }

    async Task<bool> ReadInfo() {
        var json = await Fetch("http://" + Ip + ":8001/api/v2/");
        if (json == null || !json.Contains("Tizen")) return false;
        Name = Field(json, "name") ?? "TV";
        Country = Field(json, "countryCode");
        On = Field(json, "PowerState") == "on";
        var mac = Field(json, "wifiMac");
        if (mac != null) Save("mac", mac);
        return true;
    }

    // Liga pela rede (Wake-on-LAN). Precisa de "Ligar com celular" ativo na TV.
    void Wake() {
        var mac = Load("mac");
        if (mac == null) return;
        var m = mac.Split(':', '-').Select(h => Convert.ToByte(h, 16)).ToArray();
        var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(m, 16).SelectMany(b => b)).ToArray();
        using (var udp = new UdpClient { EnableBroadcast = true }) udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
        Set("Ligando a TV…", "wait");
    }

    static async Task<string> Discover() {
        // ponytail: varre só a /24 de cada placa com gateway; rede maior → ponha o IP em %APPDATA%\TVRemote\ip.txt.
        var hosts = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => u.Address.ToString())
            .Select(a => a.Substring(0, a.LastIndexOf('.') + 1)).Distinct()
            .SelectMany(p => Enumerable.Range(1, 254).Select(i => p + i)).ToList();
        var replies = await Task.WhenAll(hosts.Select(ip => Fetch("http://" + ip + ":8001/api/v2/")));
        int found = Array.FindIndex(replies, r => r != null && r.Contains("Tizen"));
        return found < 0 ? null : hosts[found];
    }

    static async Task<string> Fetch(string url) {
        try { return await Http.GetStringAsync(url); }
        catch { return null; }
    }

    static string Field(string json, string name) {
        var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string Load(string name) {
        var file = System.IO.Path.Combine(Dir, name + ".txt");
        var value = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        return value == "" ? null : value;
    }

    static void Save(string name, string value) { File.WriteAllText(System.IO.Path.Combine(Dir, name + ".txt"), value); }

    void Set(string status, string state) { Status = status; State = state; Changed(); }
}

// O painel que abre acima do ícone da bandeja.
class Remote {
    static readonly string[] Inputs = { "HDMI 1|KEY_HDMI1", "HDMI 2|KEY_HDMI2", "HDMI 3|KEY_HDMI3", "TV|KEY_TV" };
    // parte do nome → fundo, cor e marca do ícone (caractere de E000 a F8FF = símbolo da fonte de ícones)
    static readonly string[][] Brands = {
        new[] { "netflix", "#000000", "#E50914", "N" },
        new[] { "youtube", "#FF0000", "#FFFFFF", "\uE768" },
        new[] { "prime", "#1A98FF", "#FFFFFF", "prime" },
        new[] { "disney", "#0B1C5A", "#FFFFFF", "D+" },
        new[] { "max", "#002BE7", "#FFFFFF", "max" },
        new[] { "apple tv", "#000000", "#FFFFFF", "tv" },
        new[] { "paramount", "#0064FF", "#FFFFFF", "P+" },
        new[] { "spotify", "#1DB954", "#000000", "S" },
        new[] { "twitch", "#9146FF", "#FFFFFF", "T" },
        new[] { "browser", "#0091FF", "#FFFFFF", "\uE774" },
    };
    readonly Tv tv;
    readonly Window win;
    readonly Style btn;
    readonly FontFamily icons;
    readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
    DateTime hiddenAt;

    public Remote(Tv tv) {
        this.tv = tv;
        using (var xaml = typeof(Remote).Assembly.GetManifestResourceStream("TVRemote.xaml")) win = (Window)XamlReader.Load(xaml);
        btn = (Style)win.FindResource("Btn");
        icons = (FontFamily)win.FindResource("Icons");

        // Botões só com ícone: o texto da dica vira o nome lido pelo leitor de tela.
        foreach (var c in Descendants(win).OfType<FrameworkElement>().Where(c => c.ToolTip is string))
            AutomationProperties.SetName(c, (string)c.ToolTip);
        foreach (var b in Descendants(win).OfType<Button>().Where(b => b.Uid.StartsWith("KEY_"))) {
            var key = b.Uid;
            b.Click += (s, e) => tv.Press(key);
        }
        var pages = new[] { "Home", "Remote", "Apps", "Inputs" };
        foreach (var p in pages) {
            var page = Find<UIElement>("Page" + p);
            Find<RadioButton>("Tab" + p).Checked += (s, e) => {
                foreach (var q in pages) Find<UIElement>("Page" + q).Visibility = Visibility.Collapsed;
                page.Visibility = Visibility.Visible;
            };
        }
        Find<Button>("Capture").Click += (s, e) => SetCapture(!Capturing);
        Find<Button>("Mirror").Click += (s, e) => Mirror();
        Find<Button>("PlayPause").Click += (s, e) => PlayPause();
        Find<Button>("PlayLink").Click += (s, e) => OpenLink();
        Find<Button>("TypeOpen").Click += (s, e) => ShowKeyboard();
        Find<Button>("SeeApps").Click += (s, e) => Find<RadioButton>("TabApps").IsChecked = true;
        Find<Button>("SeeInputs").Click += (s, e) => Find<RadioButton>("TabInputs").IsChecked = true;
        var search = Find<TextBox>("Search");
        search.TextChanged += (s, e) => {
            if (ShowLink(search.Text)) { search.Text = ""; Find<RadioButton>("TabHome").IsChecked = true; return; }
            Find<UIElement>("SearchHint").Visibility = search.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            RenderApps();
        };
        var typeBox = Find<TextBox>("TypeBox");
        typeBox.TextChanged += (s, e) => Find<UIElement>("TypeHint").Visibility = typeBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        typeBox.KeyDown += (s, e) => {
            if (e.Key != Key.Enter) return;
            tv.Type(typeBox.Text);
            typeBox.Text = "";
            Find<UIElement>("TypeModule").Visibility = Visibility.Collapsed;
            e.Handled = true;
        };
        foreach (var minutes in new[] { 30, 60, 120 }) {
            var chip = new Button { Style = btn, Tag = "14", Height = 28, Margin = new Thickness(3), DataContext = minutes,
                                    Content = new TextBlock { Text = minutes < 60 ? minutes + " min" : minutes / 60 + " h", FontSize = 12 } };
            var choice = minutes;
            chip.Click += (s, e) => SetSleep(choice == sleepMinutes ? 0 : choice); // tocar de novo no mesmo cancela
            Find<Panel>("SleepChoices").Children.Add(chip);
        }
        sleep.Tick += (s, e) => { if (tv.On) tv.Press("KEY_POWER"); SetSleep(0); };

        win.Deactivated += (s, e) => { win.Hide(); hiddenAt = DateTime.Now; poll.Stop(); };
        win.SizeChanged += (s, e) => Place(); // a altura muda com a aba: o painel fica preso ao canto
        win.PreviewKeyDown += OnKey;
        poll.Tick += (s, e) => tv.RefreshApps();
        tv.Changed += RenderStatus;
        tv.AppsChanged += RenderApps;
        tv.KeyboardOpened += ShowKeyboard;
        tv.KeyboardClosed += () => Find<UIElement>("TypeModule").Visibility = Visibility.Collapsed;
        RenderInputs();
        RenderApps();
    }

    public void Toggle() {
        // Clicar no ícone com o painel aberto já o fecha (perde o foco); não reabre logo em seguida.
        if (win.IsVisible || (DateTime.Now - hiddenAt).TotalMilliseconds < 300) { win.Hide(); return; }
        Open();
    }

    void Open() {
        // Cabeçalho, abas, espaçamentos e a margem da sombra somam uns 170: o resto da altura da tela fica para a aba.
        Find<ScrollViewer>("Pages").MaxHeight = SystemParameters.WorkArea.Height - 170;
        Backdrop();
        try { ShowLink(Clipboard.GetText()); } catch { } // a área de transferência pode estar ocupada por outro programa
        win.Show();
        Place();
        win.Activate();
        poll.Start();
        tv.RefreshApps();
    }

    // A TV abriu o teclado na tela (ou o botão Aa foi tocado): o painel abre com o campo pronto para digitar.
    void ShowKeyboard() {
        Find<RadioButton>("TabHome").IsChecked = true;
        Find<UIElement>("TypeModule").Visibility = Visibility.Visible;
        if (!win.IsVisible) Open();
        win.Activate();
        Find<TextBox>("TypeBox").Focus();
    }

    static readonly Regex YouTubeLink = new Regex(@"(?:youtube\.com/(?:watch\?(?:\S*&)?v=|shorts/|live/|embed/)|youtu\.be/)([\w-]{11})");
    string video, played; // vídeo do link à mostra; o último aberto não volta a aparecer

    bool ShowLink(string text) {
        var m = YouTubeLink.Match(text ?? "");
        bool show = m.Success && m.Groups[1].Value != played;
        if (show) {
            video = m.Groups[1].Value;
            Find<TextBlock>("LinkText").Text = "youtu.be/" + video;
        }
        Find<UIElement>("LinkModule").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        return show;
    }

    async void OpenLink() {
        if (await tv.PlayYouTube(video)) {
            played = video;
            Find<UIElement>("LinkModule").Visibility = Visibility.Collapsed;
        } else {
            Find<TextBlock>("LinkText").Text = "A TV não abriu o vídeo";
        }
    }

    bool paused; // a TV não diz se está tocando: o botão alterna entre pausar e continuar

    void PlayPause() {
        tv.Press(paused ? "KEY_PLAY" : "KEY_PAUSE");
        paused = !paused;
        Find<TextBlock>("PlayPauseGlyph").Text = paused ? "\uE768" : "\uE769";
    }

    readonly DispatcherTimer sleep = new DispatcherTimer();
    int sleepMinutes; // 0 = sem timer

    // Desligar com timer: o próprio app manda o comando na hora, então funciona mesmo se a TV não tiver essa opção.
    void SetSleep(int minutes) {
        sleep.Stop();
        sleepMinutes = minutes;
        if (minutes > 0) { sleep.Interval = TimeSpan.FromMinutes(minutes); sleep.Start(); }
        Find<TextBlock>("SleepText").Text = minutes > 0 ? "às " + DateTime.Now.AddMinutes(minutes).ToString("HH:mm") : "";
        foreach (var chip in Find<Panel>("SleepChoices").Children.OfType<Button>()) {
            bool active = (int)chip.DataContext == minutes;
            chip.Background = (Brush)win.FindResource(active ? "Blue" : "Fill");
            chip.Foreground = active ? Brushes.White : (Brush)win.FindResource("Label");
        }
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    // Espelhar a tela: abre o "Transmitir" do Windows (Win+K), que acha a TV e espelha por Miracast.
    void Mirror() {
        win.Hide();
        keybd_event(0x5B, 0, 0, UIntPtr.Zero); // Windows
        keybd_event(0x4B, 0, 0, UIntPtr.Zero); // K
        keybd_event(0x4B, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    void Place() {
        var area = SystemParameters.WorkArea;
        win.Left = area.Right - win.ActualWidth + 6; // a margem de 16 da sombra fica fora: o painel fica a 10 da borda
        win.Top = area.Bottom - win.ActualHeight + 6;
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

    // Vidro: antes de abrir, fotografa a faixa da tela atrás do painel e desfoca, como o material do macOS.
    // ponytail: é uma foto, não ao vivo (o painel fica aberto pouco tempo); o desfoque nativo do Windows não aceita cantos de 26.
    void Backdrop() {
        var area = SystemParameters.WorkArea;
        double scale, x = area.Right - 10 - 320, y = area.Top, w = 320, h = area.Height - 10, blur = 30;
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96;
        BitmapSource shot;
        using (var bmp = new System.Drawing.Bitmap((int)(w * scale), (int)(h * scale))) {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen((int)(x * scale), (int)(y * scale), 0, 0, bmp.Size);
            var handle = bmp.GetHbitmap();
            try { shot = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { DeleteObject(handle); }
        }
        // A foto sai um pouco das bordas para o desfoque não clarear os cantos; um quarto da resolução basta depois de desfocada.
        var image = new Image { Source = shot, Width = w + 2 * blur, Height = h + 2 * blur, Stretch = Stretch.Fill,
                                Effect = new BlurEffect { Radius = blur } };
        var frame = new Canvas { Width = w, Height = h };
        Canvas.SetLeft(image, -blur);
        Canvas.SetTop(image, -blur);
        frame.Children.Add(image);
        frame.Measure(new Size(w, h));
        frame.Arrange(new Rect(0, 0, w, h));
        double quarter = scale / 4;
        var glass = new RenderTargetBitmap((int)(w * quarter), (int)(h * quarter), 96 * quarter, 96 * quarter, PixelFormats.Pbgra32);
        glass.Render(frame);
        Find<Border>("Backdrop").Background = new ImageBrush(glass) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Bottom };
    }

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    public event Action CaptureChanged = delegate { };
    HookProc hookProc; // guardado num campo para o coletor de lixo não levar o callback
    IntPtr hook;

    public bool Capturing { get { return hook != IntPtr.Zero; } }

    // Modo teclado: setas, Enter, Backspace e as teclas de volume e mídia viram o controle em qualquer janela, até apertar Esc.
    void SetCapture(bool on) {
        if (on == Capturing) return;
        if (on) {
            hookProc = OnGlobalKey;
            hook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0); // 13 = teclado, baixo nível
        } else {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        var button = Find<Button>("Capture");
        button.Background = (Brush)win.FindResource(on ? "Blue" : "Fill");
        button.Foreground = on ? Brushes.White : (Brush)win.FindResource("Label");
        Find<UIElement>("CaptureHint").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CaptureChanged();
    }

    IntPtr OnGlobalKey(int code, IntPtr message, IntPtr data) {
        if (code >= 0) {
            string key = null;
            switch (Marshal.ReadInt32(data)) {
                case 0x25: key = "LEFT"; break;
                case 0x26: key = "UP"; break;
                case 0x27: key = "RIGHT"; break;
                case 0x28: key = "DOWN"; break;
                case 0x0D: key = "ENTER"; break;
                case 0x08: key = "RETURN"; break;   // Backspace
                case 0x1B: key = "ESC"; break;
                case 0xAD: key = "MUTE"; break;     // teclas de volume e mídia do teclado
                case 0xAE: key = "VOLDOWN"; break;
                case 0xAF: key = "VOLUP"; break;
                case 0xB0: key = "FF"; break;       // próxima faixa
                case 0xB1: key = "REWIND"; break;   // faixa anterior
                case 0xB2: key = "STOP"; break;
                case 0xB3: key = "PLAYPAUSE"; break;
            }
            // Digitando no próprio painel (teclado da TV, busca): as teclas ficam com ele.
            if (key != null && win.IsActive && Keyboard.FocusedElement is TextBox) key = null;
            if (key != null) {
                bool down = message == (IntPtr)0x100 || message == (IntPtr)0x104; // WM_KEYDOWN, WM_SYSKEYDOWN
                if (down && key == "ESC") win.Dispatcher.BeginInvoke(new Action(() => SetCapture(false)));
                else if (down && key == "PLAYPAUSE") win.Dispatcher.BeginInvoke(new Action(PlayPause));
                else if (down) tv.Press("KEY_" + key);
                return (IntPtr)1; // a tecla não chega à janela em foco
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    void OnKey(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) { win.Hide(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        string key = null;
        switch (e.Key) {
            case Key.Up: key = "UP"; break;
            case Key.Down: key = "DOWN"; break;
            case Key.Left: key = "LEFT"; break;
            case Key.Right: key = "RIGHT"; break;
            case Key.Enter: key = "ENTER"; break;
            case Key.Back: key = "RETURN"; break;
            case Key.H: key = "HOME"; break;
            case Key.M: key = "MUTE"; break;
            case Key.OemPlus: case Key.Add: key = "VOLUP"; break;
            case Key.OemMinus: case Key.Subtract: key = "VOLDOWN"; break;
        }
        if (key == null) return;
        tv.Press("KEY_" + key);
        e.Handled = true;
    }

    void RenderStatus() {
        bool connected = tv.State == "ok";
        Find<TextBlock>("TvName").Text = tv.Name;
        Find<TextBlock>("StatusText").Text = connected ? (tv.On ? "Ligada" : "Em espera") : tv.Status;
        Find<Ellipse>("StatusDot").Fill = Hex(connected ? (tv.On ? "#30D158" : "#636366")
            : tv.State == "wait" ? "#FF9230" : tv.State == "error" ? "#FF4245" : "#636366");
        var power = Find<Button>("Power");
        power.Background = tv.On ? Hex("#33FF4245") : (Brush)win.FindResource("Fill");
        power.Foreground = Hex(tv.On ? "#FF4245" : "#30D158");
        power.ToolTip = tv.On ? "Desligar" : "Ligar";
        AutomationProperties.SetName(power, (string)power.ToolTip);
    }

    void RenderApps() {
        var query = Find<TextBox>("Search").Text.Trim();
        var grid = Find<Panel>("AppsGrid");
        grid.Children.Clear();
        foreach (var a in tv.Apps.Where(a => a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) grid.Children.Add(AppButton(a, 48));
        var favorites = Find<Panel>("Favorites");
        favorites.Children.Clear();
        foreach (var a in tv.Apps.Take(4)) favorites.Children.Add(AppButton(a, 44));

        var now = tv.Apps.FirstOrDefault(a => a.Visible);
        Find<TextBlock>("NowName").Text = now != null ? now.Name : "Nenhum app aberto";
        Find<UIElement>("Media").Visibility = now != null ? Visibility.Visible : Visibility.Collapsed;
        Find<Border>("NowIcon").Child = now != null ? Icon(now, 40)
            : new Border { CornerRadius = new CornerRadius(9), Background = (Brush)win.FindResource("Fill"),
                           Child = Glyph("\uE7F4", 16, (Brush)win.FindResource("Secondary")) };
    }

    void RenderInputs() {
        var list = Find<Panel>("InputsList");
        var rows = Inputs.Concat(new[] { "Menu de entradas da TV|KEY_SOURCE" }).ToArray();
        for (int i = 0; i < rows.Length; i++) {
            var p = rows[i].Split('|');
            var key = p[1];
            if (i > 0) list.Children.Add(new Rectangle { Height = 1, Fill = (Brush)win.FindResource("Separator"), Margin = new Thickness(40, 0, 8, 0) });
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = p[0], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            var chevron = Glyph("\uE76C", 10, (Brush)win.FindResource("Tertiary"));
            Grid.SetColumn(name, 1);
            Grid.SetColumn(chevron, 2);
            row.Children.Add(Glyph(key == "KEY_TV" ? "\uE7F4" : key == "KEY_SOURCE" ? "\uE8AB" : "\uE7F8", 14, (Brush)win.FindResource("Secondary")));
            row.Children.Add(name);
            row.Children.Add(chevron);
            var item = new Button {
                Style = btn, Background = Brushes.Transparent, Tag = "10", Height = 36, Padding = new Thickness(8, 0, 10, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Content = row,
            };
            AutomationProperties.SetName(item, p[0]);
            item.Click += (s, e) => tv.Press(key);
            list.Children.Add(item);

            if (key == "KEY_SOURCE") continue;
            var chip = new Button { Style = btn, Tag = "14", Height = 28, Margin = new Thickness(3), Content = new TextBlock { Text = p[0], FontSize = 12 } };
            chip.Click += (s, e) => tv.Press(key);
            Find<Panel>("InputChips").Children.Add(chip);
        }
    }

    // Ícone, nome embaixo e um ponto azul quando o app está na tela (como no Dock).
    Button AppButton(TvApp a, double size) {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Icon(a, size));
        stack.Children.Add(new TextBlock { Text = a.Name, FontSize = 11, TextAlignment = TextAlignment.Center,
                                           TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
        stack.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 3, 0, 0),
                                         Fill = a.Visible ? (Brush)win.FindResource("Blue") : Brushes.Transparent });
        var b = new Button { Style = btn, Background = Brushes.Transparent, Tag = "10", Padding = new Thickness(2, 6, 2, 2),
                             Content = stack, ToolTip = "Abrir " + a.Name };
        AutomationProperties.SetName(b, a.Name);
        b.Click += (s, e) => tv.Launch(a.Id);
        return b;
    }

    // Quadrado com cantos de ícone de app (22,5% do lado): o ícone oficial, ou a marca na cor dela enquanto ele não chega.
    FrameworkElement Icon(TvApp a, double size) {
        var file = Tv.IconFile(a.Id);
        if (file != null) {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return new Border {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.225),
                Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
                BorderBrush = (Brush)win.FindResource("Separator"), BorderThickness = new Thickness(0.5),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Colors.Black },
            };
        }
        var brand = Brands.FirstOrDefault(b => a.Name.ToLowerInvariant().Contains(b[0]))
                    ?? new[] { "", "#3A3A3C", "#FFFFFF", a.Name.Substring(0, 1) };
        var mark = brand[3];
        bool symbol = mark.Length == 1 && mark[0] >= '\uE000' && mark[0] <= '\uF8FF';
        var text = symbol ? Glyph(mark, size * 0.4, Hex(brand[2]))
            : new TextBlock { Text = mark, Foreground = Hex(brand[2]), FontWeight = FontWeights.Bold,
                              FontSize = size * (mark.Length <= 2 ? 0.46 : mark.Length <= 3 ? 0.34 : 0.27),
                              HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.225), Background = Hex(brand[1]),
            BorderBrush = (Brush)win.FindResource("Separator"), BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Colors.Black },
            Child = text,
        };
    }

    TextBlock Glyph(string glyph, double size, Brush color) {
        return new TextBlock { Text = glyph, FontFamily = icons, FontSize = size, Foreground = color,
                               HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    T Find<T>(string name) where T : class { return (T)win.FindName(name); }

    static SolidColorBrush Hex(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
