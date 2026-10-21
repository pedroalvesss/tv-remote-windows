# TV Remote para Windows

Controle de TV Samsung (Tizen) na bandeja do Windows. `TVRemote.cs` (conexão e painel) e `TVRemote.xaml` (visual), compilados por `build.cmd` com o .NET Framework do próprio Windows (C# 5: sem `?.`, `$""` nem `nameof`). Detalhes de como funciona estão no README.

## Pendências

### Pular música no Spotify

Os botões ⏮ ⏭ do módulo "Na TV agora" mandam `KEY_REWIND` e `KEY_FF`. Na TV não existe tecla de próxima ou anterior faixa: no Spotify da TV elas voltam e avançam dentro da música, em vez de trocar de música. No YouTube e na Netflix esse comportamento está certo.

O caminho combinado, ainda não feito: usar a Web API do Spotify só quando o app aberto na TV for o Spotify.
- `POST /v1/me/player/next`, `/previous`, e `PUT /pause` e `/play`.
- `GET /v1/me/player/currently-playing` para mostrar a música, o artista e a capa no módulo.
- Login por OAuth com PKCE (sem segredo), com redirect em loopback (`http://127.0.0.1:<porta>/callback`). O refresh token fica em `%APPDATA%\TVRemote`, como o token da TV.

Depende do usuário: ter Spotify Premium (sem ele a API não aceita comandos), criar um app em developer.spotify.com e informar o Client ID. Em 21/10/2026 o dono preferiu deixar para depois.
