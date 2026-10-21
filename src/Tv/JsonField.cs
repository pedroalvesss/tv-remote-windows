using System.Text.RegularExpressions;

namespace TvRemote.Tv
{
    // Lê um campo de texto de um JSON simples, sem biblioteca de JSON (o .NET Framework não traz uma pronta).
    internal static class JsonField
    {
        internal static string Read(string json, string name)
        {
            Match match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
