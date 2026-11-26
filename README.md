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

1. Na página do Frame, marque **Importar filmes e séries sob demanda** e informe a URL base HTTPS da instalação FILMES-JR. Use somente a base, sem acrescentar /browse no final. Mantenha a pasta de filmes já cadastrada e, para séries, indique uma pasta separada em uma biblioteca do tipo **Séries**. Informe os hosts autorizados usados nas URLs de vídeo e imagens. Se a API usar gateway de mídia HTTPS, inclua o domínio público desse gateway. Para permitir vídeo HTTP de uma origem conhecida, informe explicitamente http://dominio:porta no mesmo campo. A autorização vale somente para vídeo e exige correspondência de domínio e porta.
2. Salve a configuração. Em **Conta FILMES-JR**, entre com o e-mail e a senha de uma conta com acesso ao catálogo. O Jellyfin guarda os tokens criptografados e renova a sessão quando necessário. O token manual e a variável FRAME_API_TOKEN continuam disponíveis como alternativas.
3. Busque pelo título. Os resultados mostram **Filme** ou **Série**. Clique em **Importar** para filmes ou **Importar série completa** para todas as temporadas e episódios disponíveis de uma série. Para testar só um episódio, use **Importar primeiro episódio**. O plugin consulta o detalhe, grava o STRM e o NFO e notifica o monitor do Jellyfin. Se o item demorar a aparecer, execute **Rastrear Todas as Bibliotecas**.
4. O Frame usa a rota de metadados FILMES-JR para obter capa, fundo, elenco e direção quando o TMDB estiver disponível. Se faltar a capa do TMDB, tenta as imagens HTTPS do catálogo em hosts permitidos.
5. Reproduza o filme e importe o mesmo resultado novamente para verificar que mantém a mesma pasta. O importador usa um UUID derivado do identificador externo. A tarefa de sincronização geral ignora a execução enquanto esse modo estiver ativo.

Se a conta usar URLs temporárias do gateway, teste a reprodução logo após importar. O gateway FILMES-JR emite URLs com validade de oito horas quando a proteção de mídia está ativa. A renovação automática ainda está pendente. Para este teste, reimporte o filme se o URL de reprodução expirar. Se a sessão da conta expirar, entre novamente. Se estiver usando um JWT manual e ele expirar, atualize-o no painel ou na variável de ambiente.

## Buscar pela lupa do Jellyfin Web

1. Mantenha o plugin [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) ativo e reinicie o servidor após instalar os plugins.
2. No painel do Frame, com o modo sob demanda configurado, marque **Buscar filmes pela lupa do Jellyfin Web** e salve. A pasta dedicada de filmes deve corresponder a uma pasta cadastrada na biblioteca. O monitoramento em tempo real ajuda o Jellyfin a reconhecer novos arquivos quando já existe um rastreamento em andamento.
3. Recarregue o Jellyfin Web e pesquise um filme pela lupa global. Os filmes da biblioteca aparecem primeiro. A seção **Resultados do Frame** carrega mais filmes conforme você rola a grade. O idioma aparece junto do ano quando estiver indicado no título. Ao clicar, o cartão acompanha a importação e mostra como tentar novamente se houver erro. O plugin importa e registra apenas o título escolhido, então abre a ficha assim que o Jellyfin disponibiliza o item. Se a atualização demorar, use **Abrir no Jellyfin** novamente depois que a biblioteca reconhecer o título.

A busca consulta a conta FILMES-JR conectada no servidor. Ela está disponível para usuários com acesso à biblioteca de filmes do Frame e sem filtros de conteúdo por classificação, tags ou itens sem classificação. Nesta versão, a integração da lupa funciona no Jellyfin Web e retorna filmes. Para cartões visíveis com capa ausente ou quebrada, o plugin tenta usar a capa já armazenada pelo FILMES-JR. Esse recurso requer o endpoint de metadados em cache na API.

## Sincronização de filmes e séries da home

Ative **Sincronizar automaticamente os filmes da home FILMES-JR** no painel do Frame, mantenha o modo sob demanda e salve. Use uma conta FILMES-JR conectada, uma pasta de filmes cadastrada como biblioteca do Jellyfin e os hosts de reprodução permitidos.

Execute **Frame — Sincronizar filmes da home** nas tarefas do Jellyfin para a primeira execução. O intervalo padrão é de 30 minutos e pode ser ajustado na tarefa. A sincronização consulta /home, importa os filmes do destaque e das listas da conta e atualiza suas referências de reprodução. Filmes que saem da home permanecem na biblioteca.

Para séries, ative **Sincronizar automaticamente as séries da home FILMES-JR e todos os episódios**, mantenha a pasta de séries separada e execute **Frame — Sincronizar séries da home**. A tarefa inclui todas as temporadas e os episódios disponíveis, incluindo especiais. Uma origem de vídeo bloqueada deixa apenas os episódios afetados pendentes para uma nova execução.

Na seção **Últimas sincronizações da home**, clique em **Atualizar resumo** após a execução. O painel mostra o último resultado de cada tarefa, os totais sincronizados, os novos itens da biblioteca e os motivos das pendências. Quando falta permissão para uma origem de vídeo, a mensagem informa o domínio e a porta a cadastrar.

Cada filme autorizado é escrito na mesma pasta da importação manual. A tarefa preenche imagens locais ausentes quando disponíveis e mantém NFOs já existentes. Títulos indisponíveis ou com origem de mídia bloqueada ficam pendentes e são registrados individualmente.


## Contrato experimental da sincronização geral

A URL configurada deve retornar `{ "items": [...], "nextCursor": null }`. Se houver mais páginas, preencha `nextCursor`; a próxima requisição inclui `cursor` codificado. O exemplo em `teste/catalog.json` contém um filme.

- Filmes com `type: "movie"` usam UUID em `id`, `name`, `videoUrl` e os campos opcionais `description`, `year`, `rating`, `durationMinutes` e `genres`.
- Séries com `type: "series"` usam UUID em `id`, `name` e `seasons`. Cada temporada tem `number` e `episodes`. Cada episódio tem UUID em `id`, `number`, `name`, `videoUrl` e, opcionalmente, `description` e `durationMinutes`.
- Filmes e séries podem fornecer images com posterUrl, backdropUrl e logoUrl. A origem precisa constar nos hosts de mídia e servir PNG ou JPEG sem redirecionamentos.
- Use referências de vídeo estáveis e autorizadas que o Jellyfin possa abrir diretamente. URLs HTTP exigem autorização explícita da origem. As URLs são gravadas nos STRM. Cadastre somente origens de mídia confiáveis.

A tarefa consulta o catálogo completo e só regrava arquivos com conteúdo alterado. Itens removidos do catálogo permanecem no disco. O cursor de alterações e a renovação automática das URLs de reprodução ainda estão pendentes.
