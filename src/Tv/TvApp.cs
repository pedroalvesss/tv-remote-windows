namespace TvRemote.Tv
{
    // Um app instalado na TV, como ela informa.
    internal sealed class TvApp
    {
        public TvApp(string id, string name, bool isVisible)
        {
            Id = id;
            Name = name;
            IsVisible = isVisible;
        }

        public string Id { get; private set; }

        public string Name { get; private set; }

        // Aberto na tela agora.
        public bool IsVisible { get; private set; }
    }
}
