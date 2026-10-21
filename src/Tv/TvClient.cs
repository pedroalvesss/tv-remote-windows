using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TvRemote.Tv
{
    // A conexão com a TV: acha na rede, pareia, manda teclas, vê e abre apps.
    // Os eventos chegam na thread da interface, porque Run começa nela.
    internal sealed class TvClient
    {
        private const string PowerKey = "KEY_POWER";
        private const string ClientName = "Controle do PC";

        // Sem resposta em 30 s, desiste e pede de novo: o pedido na TV não expira sozinho.
        private const int PairingTimeoutMs = 30000;

        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly Regex s_visibleField = new Regex("\"visible\"\\s*:\\s*true");

        private readonly SemaphoreSlim _sending = new SemaphoreSlim(1);
        private ClientWebSocket _socket;
        private bool _isPaired;
        private bool _isRescanRequested;
        private bool _isRefreshing;
        private List<TvApp> _apps = new List<TvApp>();

        public TvClient()
        {
            Ip = LocalStore.Load("ip");
            Name = "TV";
            Status = "Procurando a TV…";
            State = ConnectionState.Offline;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // A TV usa certificado autoassinado: aceita só o dela.
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                errors == SslPolicyErrors.None
                || (sender is HttpWebRequest && ((HttpWebRequest)sender).Address.Host == Ip);
        }

        public event Action Changed = delegate { };

        public event Action AppsChanged = delegate { };

        // Teclado aberto e fechado na tela da TV.
        public event Action KeyboardOpened = delegate { };

        public event Action KeyboardClosed = delegate { };

        public string Ip { get; private set; }

        public string Name { get; private set; }

        public string Country { get; private set; }

        public string Status { get; private set; }

        public ConnectionState State { get; private set; }

        // Tela ligada; em espera a TV continua na rede, mas com a tela apagada.
        public bool IsOn { get; private set; }

        public IReadOnlyList<TvApp> Apps
        {
            get { return _apps; }
        }

        public bool IsConnected
        {
            get { return _isPaired && _socket != null && _socket.State == WebSocketState.Open; }
        }

        public async void Run()
        {
            while (true)
            {
                if (Ip == null || _isRescanRequested)
                {
                    _isRescanRequested = false;
                    SetStatus("Procurando a TV na rede…", ConnectionState.Offline);
                    string found = await DiscoverAsync();
                    if (found != null)
                    {
                        Ip = found;
                        LocalStore.Save("ip", found);
                    }
                    else if (Ip == null)
                    {
                        SetStatus("Nenhuma TV Samsung na rede", ConnectionState.Offline);
                        await Task.Delay(10000);
                        continue;
                    }
                }

                if (!await ReadInfoAsync())
                {
                    IsOn = false;
                    SetStatus("TV desligada", ConnectionState.Offline);
                    await Task.Delay(5000);
                    continue;
                }

                Task refresh = RefreshAppsAsync();
                try
                {
                    await RunSessionAsync();
                }
                catch (Exception e)
                {
                    // Queda de rede, TV desligada no meio ou pareamento sem resposta: tenta de novo daqui a pouco.
                    SetStatus("Sem conexão (" + e.GetBaseException().Message + ")", ConnectionState.Failed);
                }

                await refresh;
                _isPaired = false;
                await Task.Delay(3000);
            }
        }

        public void Rescan()
        {
            _isRescanRequested = true;
            if (_socket != null)
            {
                _socket.Abort();
            }
        }

        public async void Press(string key)
        {
            if (!IsConnected)
            {
                if (key == PowerKey)
                {
                    Wake();
                }

                return;
            }

            await SendAsync("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"Click\",\"DataOfCmd\":\"" + key
                + "\",\"Option\":\"false\",\"TypeOfRemote\":\"SendRemoteKey\"}}");
        }

        public async void Launch(string appId)
        {
            if (Ip == null)
            {
                return;
            }

            bool opened = false;
            try
            {
                HttpResponseMessage reply = await s_http.PostAsync(ApiUrl("applications/" + appId), null);
                opened = reply.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                // A TV não respondeu pelo HTTP: tenta abaixo pela conexão do controle.
            }

            if (!opened)
            {
                await SendAsync("{\"method\":\"ms.channel.emit\",\"params\":{\"event\":\"ed.apps.launch\",\"to\":\"host\",\"data\":{\"appId\":\""
                    + appId + "\",\"action_type\":\"DEEP_LINK\"}}}");
            }

            await Task.Delay(2500);
            await RefreshAppsAsync();
        }

        // Texto inteiro de uma vez no teclado aberto na tela da TV; o fim confirma, como o Enter do teclado dela.
        public async void Type(string text)
        {
            await SendAsync("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\""
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                + "\",\"DataOfCmd\":\"base64\",\"TypeOfRemote\":\"SendInputString\"}}");
            await SendAsync("{\"method\":\"ms.remote.control\",\"params\":{\"TypeOfRemote\":\"SendInputEnd\"}}");
        }

        // Abre um vídeo no YouTube da TV pelo DIAL (porta 8080), o mesmo caminho do "transmitir" do celular.
        public async Task<bool> PlayYouTubeAsync(string videoId)
        {
            if (Ip == null)
            {
                return false;
            }

            try
            {
                var body = new StringContent("v=" + videoId, Encoding.UTF8, "text/plain");
                HttpResponseMessage reply = await s_http.PostAsync("http://" + Ip + ":8080/ws/apps/YouTube", body);
                return reply.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task RefreshAppsAsync()
        {
            if (Ip == null || _isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            try
            {
                Task<string> info = FetchAsync(ApiUrl(""));
                string[] replies = await Task.WhenAll(
                    AppCatalog.Entries.Select(entry => ProbeAsync(ApiUrl("applications/" + entry.TvId))));
                string infoJson = await info;
                bool isOn = infoJson != null && JsonField.Read(infoJson, "PowerState") == "on";
                if (isOn != IsOn)
                {
                    IsOn = isOn;
                    Changed();
                }

                var apps = new List<TvApp>();
                for (int i = 0; i < AppCatalog.Entries.Length; i++)
                {
                    string id = AppCatalog.Entries[i].TvId;
                    if (replies[i] == null)
                    {
                        // A TV demorou para responder: mantém o app como estava, para a lista não piscar.
                        TvApp known = _apps.FirstOrDefault(app => app.Id == id);
                        if (known != null)
                        {
                            apps.Add(known);
                        }

                        continue;
                    }

                    string name = JsonField.Read(replies[i], "name");
                    if (name == null)
                    {
                        // Não instalado.
                        continue;
                    }

                    string shortName = name.Split(new[] { " - " }, StringSplitOptions.None)[0];
                    apps.Add(new TvApp(id, shortName, s_visibleField.IsMatch(replies[i])));
                }

                if (Signature(apps) != Signature(_apps))
                {
                    _apps = apps;
                    AppsChanged();
                    DownloadIcons();
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private static string Signature(IEnumerable<TvApp> apps)
        {
            return string.Join(",", apps.Select(app => app.Id + app.IsVisible));
        }

        private async void DownloadIcons()
        {
            if (await AppCatalog.DownloadMissingIconsAsync(_apps, Country))
            {
                AppsChanged();
            }
        }

        private async Task RunSessionAsync()
        {
            string token = LocalStore.Load("token");
            string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientName));
            string url = "wss://" + Ip + ":8002/api/v2/channels/samsung.remote.control?name=" + Uri.EscapeDataString(name)
                + (token == null ? "" : "&token=" + token);
            _socket = new ClientWebSocket();
            var pairing = new CancellationTokenSource(PairingTimeoutMs);
            SetStatus("Conectando…", ConnectionState.Waiting);
            await _socket.ConnectAsync(new Uri(url), pairing.Token);
            SetStatus("Permita o acesso na tela da TV", ConnectionState.Waiting);

            var buffer = new byte[65536];
            while (_socket.State == WebSocketState.Open)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult received;
                do
                {
                    CancellationToken cancel = _isPaired ? CancellationToken.None : pairing.Token;
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                    text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                string message = text.ToString();
                switch (JsonField.Read(message, "event"))
                {
                    case "ms.channel.connect":
                        string newToken = JsonField.Read(message, "token");
                        if (newToken != null)
                        {
                            LocalStore.Save("token", newToken);
                        }

                        _isPaired = true;
                        SetStatus("Conectada", ConnectionState.Connected);
                        break;
                    case "ms.channel.unauthorized":
                        SetStatus("A TV recusou o acesso", ConnectionState.Failed);
                        return;
                    case "ms.remote.imeStart":
                        KeyboardOpened();
                        break;
                    case "ms.remote.imeEnd":
                        KeyboardClosed();
                        break;
                }
            }
        }

        private async Task SendAsync(string json)
        {
            if (!IsConnected)
            {
                return;
            }

            await _sending.WaitAsync();
            try
            {
                var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception)
            {
                // Conexão caiu: derruba de vez para Run reconectar.
                _socket.Abort();
            }
            finally
            {
                _sending.Release();
            }
        }

        private async Task<bool> ReadInfoAsync()
        {
            string json = await FetchAsync(ApiUrl(""));
            if (json == null || !json.Contains("Tizen"))
            {
                return false;
            }

            Name = JsonField.Read(json, "name") ?? "TV";
            Country = JsonField.Read(json, "countryCode");
            IsOn = JsonField.Read(json, "PowerState") == "on";
            string mac = JsonField.Read(json, "wifiMac");
            if (mac != null)
            {
                LocalStore.Save("mac", mac);
            }

            return true;
        }

        // Liga pela rede (Wake-on-LAN). Precisa de "Ligar com celular" ativo na TV.
        private void Wake()
        {
            string mac = LocalStore.Load("mac");
            if (mac == null)
            {
                return;
            }

            byte[] address = mac.Split(':', '-').Select(part => Convert.ToByte(part, 16)).ToArray();
            byte[] packet = Enumerable.Repeat((byte)0xFF, 6)
                .Concat(Enumerable.Repeat(address, 16).SelectMany(part => part))
                .ToArray();
            using (var udp = new UdpClient { EnableBroadcast = true })
            {
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            }

            SetStatus("Ligando a TV…", ConnectionState.Waiting);
        }

        private static async Task<string> DiscoverAsync()
        {
            // ponytail: varre só a /24 de cada placa com gateway; rede maior → ponha o IP em %APPDATA%\TVRemote\ip.txt.
            List<string> hosts = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(unicast => unicast.Address.ToString())
                .Select(address => address.Substring(0, address.LastIndexOf('.') + 1))
                .Distinct()
                .SelectMany(prefix => Enumerable.Range(1, 254).Select(host => prefix + host))
                .ToList();
            string[] replies = await Task.WhenAll(hosts.Select(host => FetchAsync("http://" + host + ":8001/api/v2/")));
            int found = Array.FindIndex(replies, reply => reply != null && reply.Contains("Tizen"));
            return found < 0 ? null : hosts[found];
        }

        private string ApiUrl(string path)
        {
            return "http://" + Ip + ":8001/api/v2/" + path;
        }

        private static async Task<string> FetchAsync(string url)
        {
            try
            {
                return await s_http.GetStringAsync(url);
            }
            catch (Exception)
            {
                // Sem resposta (desligada, fora da rede, demorou): vale como "não achei".
                return null;
            }
        }

        // Como FetchAsync, mas separa "não existe" (404 → texto vazio) de "não respondeu" (null).
        private static async Task<string> ProbeAsync(string url)
        {
            try
            {
                HttpResponseMessage reply = await s_http.GetAsync(url);
                if (reply.StatusCode == HttpStatusCode.NotFound)
                {
                    return "";
                }

                return reply.IsSuccessStatusCode ? await reply.Content.ReadAsStringAsync() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SetStatus(string status, ConnectionState state)
        {
            Status = status;
            State = state;
            Changed();
        }
    }
}
