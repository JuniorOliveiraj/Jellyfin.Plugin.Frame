# Jellyfin.Plugin.Frame

Plugin experimental para Jellyfin **10.11.x**, compilado com .NET 9 contra Jellyfin 10.11.0. Sincroniza um catálogo HTTPS em arquivos STRM e NFO. A versão e o comportamento no Homedroid ainda precisam ser verificados.

## Publicação e instalação pelo catálogo

1. Envie este projeto para um **repositório público** no GitHub, com GitHub Actions habilitado.
2. Envie o commit para a main. Após os testes, o GitHub Actions publica uma release se a versão no .csproj for mais nova que as já publicadas. A pipeline cria a tag automaticamente. Pull requests rodam os testes. Só pushes na main geram o pacote, o manifesto e a release.
3. Confira a release no GitHub. O workflow publica o ZIP, o SHA-256 e o `manifest.json` com a URL e o checksum do ZIP.
4. Em **Painel → Plugins → Repositórios → Novo repositório**, informe o nome **Frame** e a URL `https://github.com/USUARIO/REPOSITORIO/releases/latest/download/manifest.json`. Substitua `USUARIO/REPOSITORIO` pelos dados do seu GitHub.
5. Em **Catálogo**, instale **Frame** e reinicie o Jellyfin Server.

Para um build local, use `dotnet test tests/Jellyfin.Plugin.Frame.Tests -c Release` e `pwsh -File scripts/New-Release.ps1 -Repository USUARIO/REPOSITORIO`. Os arquivos são gerados em `artifacts/release`. O manifesto local aponta para a release correspondente.

## Configuração e primeiro teste

- Configure o Frame em **Painel → Plugins**. Para o teste pelo GitHub, use `https://raw.githubusercontent.com/USUARIO/REPOSITORIO/main/teste/catalog.json` como URL do catálogo e `interactive-examples.mdn.mozilla.net` como host de mídia.
- Use pastas dedicadas e graváveis pelo serviço Jellyfin. Cadastre as mesmas pastas como bibliotecas dos tipos **Filmes** e **Séries**, conforme necessário. Os caminhos são do servidor que executa o Jellyfin.
- Para a API real, configure `FRAME_API_TOKEN` no ambiente do serviço se o catálogo exigir Bearer. Use uma URL de catálogo confiável. O token é destinado ao catálogo e redirecionamentos HTTP de catálogo ficam desabilitados.
- Execute **Frame — Sincronizar catálogo** em **Tarefas agendadas** e atualize a biblioteca pelo Jellyfin. O item de teste deve aparecer uma vez. Teste a reprodução e repita a sincronização. O intervalo padrão é de 30 minutos e pode ser ajustado na tarefa.
- `New-FrameTestMovie.ps1` continua disponível para testar STRM/NFO diretamente com PowerShell em uma pasta separada.

## Busca e importação sob demanda

1. Na página do Frame, marque **Importar filmes sob demanda** e informe a URL base HTTPS da instalação FILMES-JR. Use somente a base, sem acrescentar /browse no final. Mantenha a pasta de filmes já cadastrada na biblioteca e informe os hosts autorizados usados nas URLs de vídeo e imagens. Se a API usar gateway de mídia, inclua o host público desse gateway.
2. Salve a configuração. Em **Conta FILMES-JR**, entre com o e-mail e a senha de uma conta com acesso ao catálogo. O Jellyfin guarda os tokens criptografados e renova a sessão quando necessário. O token manual e a variável FRAME_API_TOKEN continuam disponíveis como alternativas.
3. Busque pelo título e clique em **Importar** no filme escolhido. O plugin consulta o detalhe, grava o STRM e o NFO e notifica o monitor do Jellyfin. Se o item demorar a aparecer, execute **Rastrear Todas as Bibliotecas**.
4. Reproduza o filme e importe o mesmo resultado novamente para verificar que mantém a mesma pasta. O importador usa um UUID derivado do identificador externo. A tarefa de sincronização geral ignora a execução enquanto esse modo estiver ativo.

Se a conta usar URLs temporárias do gateway, teste a reprodução logo após importar. O gateway FILMES-JR emite URLs com validade de oito horas quando a proteção de mídia está ativa. A renovação automática ainda está pendente. Para este teste, reimporte o filme se o URL de reprodução expirar. Se a sessão da conta expirar, entre novamente. Se estiver usando um JWT manual e ele expirar, atualize-o no painel ou na variável de ambiente.

## Contrato experimental da sincronização geral

A URL configurada deve retornar `{ "items": [...], "nextCursor": null }`. Se houver mais páginas, preencha `nextCursor`; a próxima requisição inclui `cursor` codificado. O exemplo em `teste/catalog.json` contém um filme.

- Filmes com `type: "movie"` usam UUID em `id`, `name`, `videoUrl` e os campos opcionais `description`, `year`, `rating`, `durationMinutes` e `genres`.
- Séries com `type: "series"` usam UUID em `id`, `name` e `seasons`. Cada temporada tem `number` e `episodes`. Cada episódio tem UUID em `id`, `number`, `name`, `videoUrl` e, opcionalmente, `description` e `durationMinutes`.
- Filmes e séries podem fornecer images com posterUrl, backdropUrl e logoUrl. A origem precisa constar nos hosts de mídia e servir PNG ou JPEG sem redirecionamentos.
- Use referências HTTPS estáveis e autorizadas que o Jellyfin possa abrir diretamente. As URLs são gravadas nos STRM. Cadastre somente origens de mídia confiáveis.

A tarefa consulta o catálogo completo e só regrava arquivos com conteúdo alterado. Itens removidos do catálogo permanecem no disco. O cursor de alterações e a renovação automática das URLs de reprodução ainda estão pendentes.
