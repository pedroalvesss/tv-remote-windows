using System;
using System.IO;

namespace TvRemote.Tv
{
    // Arquivos do app em %APPDATA%\TVRemote: IP, MAC e token da TV, e os ícones baixados.
    internal static class LocalStore
    {
        internal static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TVRemote");

        internal static string Load(string name)
        {
            string file = Path.Combine(Folder, name + ".txt");
            string value = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            return value == "" ? null : value;
        }

        internal static void Save(string name, string value)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, name + ".txt"), value);
        }
    }
}
