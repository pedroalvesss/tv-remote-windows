# TV Remote para Windows

Controle de TV Samsung (Tizen) na bandeja do Windows, em WPF. `build.cmd` compila tudo em `src/` com o .NET Framework do próprio Windows. A estrutura de pastas e como funciona estão no README.

## Convenções

- **C# 5** (o compilador do .NET Framework): sem `?.`, `$""`, `nameof`, `=>` em membros nem filtros de exceção.
- **Nomes** (convenções da Microsoft para C#): PascalCase em tipos e membros públicos, `_campo` em campos privados, `s_campo` em campos estáticos privados, constantes em PascalCase. Sem abreviações.
- **Formato:** chaves no estilo Allman, uma instrução por linha, `var` só quando o tipo é óbvio pelo lado direito.
- **Comentários** em português, numa linha própria, explicando o porquê.
- **Um tipo por arquivo**, namespace igual à pasta (`TvRemote.Tv`, `TvRemote.Ui`, `TvRemote.Interop`). `Tv/` não conhece nada de tela; chamadas à API do Windows ficam só em `Interop/NativeMethods.cs` (regra CA1060).
- **Desempenho:** brushes e efeitos criados em código são congelados (`Freeze`); nada de `Effect` em elementos grandes que mudam (recalcula a cada passada do mouse). Para conferir, `tools/measure.ps1` mede memória, CPU e o tempo para o painel abrir.
- O `build.cmd` precisa de fim de linha CRLF (o `.gitattributes` garante).
- **Escapes `\uE...`:** as ferramentas de edição às vezes gravam o caractere em vez do escape. Depois de editar, confira se não ficou caractere invisível da faixa E000–F8FF nos `.cs`.

## Pendências

### Pular música no Spotify

Os botões ⏮ ⏭ do módulo "Na TV agora" mandam `KEY_REWIND` e `KEY_FF`. Na TV não existe tecla de próxima ou anterior faixa: no Spotify da TV elas voltam e avançam dentro da música, em vez de trocar de música. No YouTube e na Netflix esse comportamento está certo.

O caminho combinado, ainda não feito: usar a Web API do Spotify só quando o app aberto na TV for o Spotify.
- `POST /v1/me/player/next`, `/previous`, e `PUT /pause` e `/play`.
- `GET /v1/me/player/currently-playing` para mostrar a música, o artista e a capa no módulo.
- Login por OAuth com PKCE (sem segredo), com redirect em loopback (`http://127.0.0.1:<porta>/callback`). O refresh token fica em `%APPDATA%\TVRemote`, como o token da TV.

Depende do usuário: ter Spotify Premium (sem ele a API não aceita comandos), criar um app em developer.spotify.com e informar o Client ID. Em 21/10/2026 o dono preferiu deixar para depois.
