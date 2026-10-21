using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace TvRemote.Tv
{
    // Os apps que o controle procura na TV e de onde vem o ícone oficial de cada um.
    internal static class AppCatalog
    {
        // A TV (2023+) não lista os apps instalados, mas responde por ID: estes são perguntados um a um.
        // Ao lado, o mesmo app na App Store, de onde vem o ícone oficial.
        // ponytail: lista fixa, app fora dela não aparece. Para incluir um, ache o ID e acrescente aqui.
        internal static readonly Entry[] Entries =
        {
            new Entry("3201907018807", "com.netflix.Netflix"),
            new Entry("111299001912", "com.google.ios.youtube"),
            new Entry("3201910019365", "com.amazon.aiv.AIVApp"),
            new Entry("3201901017640", "com.disney.disneyplus"),
            new Entry("3202301029760", "com.wbd.stream"),       // HBO Max
            new Entry("3201807016597", "com.apple.tv"),
            new Entry("3202110025305", "com.cbs.canada.app"),   // Paramount+
            new Entry("3201606009684", "com.spotify.client"),
            new Entry("3202203026841", "tv.twitch"),
            new Entry("3202010022079", null),                   // Internet: não existe na App Store
        };

        private static readonly HttpClient s_web = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly string s_iconFolder = Path.Combine(LocalStore.Folder, "icons");
        private static readonly HashSet<string> s_iconsTried = new HashSet<string>();

        internal static string IconFile(string appId)
        {
            string file = Path.Combine(s_iconFolder, appId + ".jpg");
            return File.Exists(file) ? file : null;
        }

        // Ícones oficiais pela busca pública da App Store (na loja do país da TV), guardados em %APPDATA%\TVRemote\icons.
        // Devolve true se baixou algum.
        internal static async Task<bool> DownloadMissingIconsAsync(IEnumerable<TvApp> installed, string country)
        {
            bool downloaded = false;
            foreach (Entry entry in Entries)
            {
                bool wanted = entry.StoreBundleId != null
                    && IconFile(entry.TvId) == null
                    && installed.Any(app => app.Id == entry.TvId)
                    && s_iconsTried.Add(entry.TvId);
                if (!wanted)
                {
                    continue;
                }

                try
                {
                    string json = await s_web.GetStringAsync(
                        "https://itunes.apple.com/lookup?bundleId=" + entry.StoreBundleId + "&country=" + (country ?? "us"));
                    string url = JsonField.Read(json, "artworkUrl512");
                    if (url == null)
                    {
                        continue;
                    }

                    byte[] image = await s_web.GetByteArrayAsync(url.Replace("512x512bb", "128x128bb"));
                    Directory.CreateDirectory(s_iconFolder);
                    File.WriteAllBytes(Path.Combine(s_iconFolder, entry.TvId + ".jpg"), image);
                    downloaded = true;
                }
                catch (Exception)
                {
                    // Sem internet ou a loja não respondeu: o ícone com a marca continua no lugar.
                }
            }

            return downloaded;
        }

        // Um app procurado na TV: o ID dele lá e o mesmo app na App Store.
        internal sealed class Entry
        {
            public Entry(string tvId, string storeBundleId)
            {
                TvId = tvId;
                StoreBundleId = storeBundleId;
            }

            public string TvId { get; private set; }

            public string StoreBundleId { get; private set; }
        }
    }
}
