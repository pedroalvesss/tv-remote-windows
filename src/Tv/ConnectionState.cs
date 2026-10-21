namespace TvRemote.Tv
{
    // Como está a conexão com a TV; a cor do ponto no cabeçalho vem daqui.
    internal enum ConnectionState
    {
        Offline,
        Waiting,
        Connected,
        Failed,
    }
}
