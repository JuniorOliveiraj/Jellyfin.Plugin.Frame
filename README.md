# Jellyfin.Plugin.Frame

Plugin experimental para Jellyfin **10.10.7** (.NET 8) e **10.11.x** (.NET 9). Sincroniza um catálogo HTTPS em arquivos STRM e NFO. A release inclui um pacote para cada linha do servidor. O Jellyfin usa o mesmo manifesto para selecionar a versão compatível.

## Publicação e instalação pelo catálogo

1. Envie este projeto para um **repositório público** no GitHub, com GitHub Actions habilitado.
2. Envie o commit para a main. Após os testes, o GitHub Actions publica uma release se a versão no .csproj for mais nova que as já publicadas. A pipeline cria a tag automaticamente. Pull requests rodam os testes. Só pushes na main geram o pacote, o manifesto e a release.
3. Confira a release no GitHub. O workflow publica o ZIP, o SHA-256 e o `manifest.json` com a URL e o checksum do ZIP.
4. Em **Painel → Plugins → Repositórios → Novo repositório**, informe o nome **Frame** e a URL `https://github.com/USUARIO/REPOSITORIO/releases/latest/download/manifest.json`. Substitua `USUARIO/REPOSITORIO` pelos dados do seu GitHub.
5. Em **Catálogo**, instale **Frame** e reinicie o Jellyfin Server.

Para um build local, instale o SDK .NET 9 e os runtimes ASP.NET Core 8 e 9. Use `dotnet test tests/Jellyfin.Plugin.Frame.Tests -c Release` e `pwsh -File scripts/New-Release.ps1 -Repository USUARIO/REPOSITORIO`. Os arquivos são gerados em `artifacts/release`. O manifesto local aponta para a release correspondente.

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

## Buscar pela lupa do Jellyfin para Android TV

No painel do Frame, com a conta FILMES-JR conectada e o modo sob demanda configurado, marque **Buscar filmes e séries pela lupa do Jellyfin para Android TV** e salve. Use as pastas dedicadas já cadastradas nas bibliotecas Filme e Séries do Jellyfin, com monitoramento em tempo real.

A lupa mostra os resultados da biblioteca e pode adicionar um filme ou uma série inédita quando o título completo encontra uma única correspondência desse tipo na primeira página do FILMES-JR. A importação adiciona o item à biblioteca compartilhada e respeita as permissões da conta Jellyfin e os hosts de mídia cadastrados. Consultas parciais ou ambíguas continuam exibindo os resultados locais. Para distinguir versões, use o nome completo exibido no FILMES-JR. As temporadas e episódios de uma série continuam chegando depois que ela é registrada.

## Buscar pela lupa do Jellyfin Web

1. Mantenha o plugin [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) ativo e reinicie o servidor após instalar os plugins.
2. No painel do Frame, com o modo sob demanda configurado, marque **Buscar filmes e séries pela lupa do Jellyfin Web** e salve. Cadastre as pastas dedicadas nas respectivas bibliotecas do Jellyfin. Ative o monitoramento em tempo real para que o servidor reconheça alterações nos arquivos.
3. Use a lupa global e selecione **Todos**, **Filmes** ou **Séries**. Os resultados locais ficam acima da seção **Resultados do Frame**, que carrega novas páginas conforme você rola. Escolha um cartão do Frame para importar o título. A ficha abre assim que o Jellyfin reconhece o item. Para séries, o plugin grava primeiro um episódio disponível e continua a importação das outras temporadas e episódios em segundo plano, mesmo após sair da busca.

O cartão da série mostra quantos episódios já foram importados. Se sobrarem pendências, use **Tentar episódios pendentes** para consultar a API novamente e gravar só esses episódios. O servidor guarda o último progresso em memória por até 24 horas.

A lupa consulta a conta FILMES-JR do servidor e mostra resultados dos tipos de biblioteca a que o usuário tem acesso. A integração remota fica desativada para usuários com filtros por classificação, tags ou itens sem classificação. Se um cartão visível tiver capa ausente ou quebrada, o plugin tenta usar a capa já armazenada pelo FILMES-JR. Isso requer o endpoint de metadados em cache na API.

## Sincronização de filmes e séries da home

Ative **Sincronizar automaticamente os filmes da home FILMES-JR** no painel do Frame, mantenha o modo sob demanda e salve. Use uma conta FILMES-JR conectada, uma pasta de filmes cadastrada como biblioteca do Jellyfin e os hosts de reprodução permitidos.

Execute **Frame — Sincronizar filmes da home** nas tarefas do Jellyfin para a primeira execução. O intervalo padrão é de 30 minutos e pode ser ajustado na tarefa. A sincronização consulta /home, importa os filmes do destaque e das listas da conta e atualiza suas referências de reprodução. Filmes que saem da home permanecem na biblioteca.

Para séries, ative **Sincronizar automaticamente as séries da home FILMES-JR e todos os episódios**, mantenha a pasta de séries separada e execute **Frame — Sincronizar séries da home**. A tarefa inclui todas as temporadas e os episódios disponíveis, incluindo especiais. Uma origem de vídeo bloqueada deixa apenas os episódios afetados pendentes para uma nova execução.

Na seção **Últimas sincronizações da home**, clique em **Atualizar resumo** após a execução. O painel mostra o último resultado de cada tarefa, os totais sincronizados, os novos itens da biblioteca e os motivos das pendências. Quando falta permissão para uma origem de vídeo, a mensagem informa o domínio e a porta a cadastrar.

Cada filme autorizado é escrito na mesma pasta da importação manual. A tarefa preenche imagens locais ausentes quando disponíveis e mantém NFOs já existentes. Títulos indisponíveis ou com origem de mídia bloqueada ficam pendentes e são registrados individualmente.


## Coleções da home no Jellyfin

No modo sob demanda, marque **Criar coleções das seções de descoberta da home** no painel do Frame. Primeiro aguarde os filmes e as séries aparecerem nas bibliotecas. Depois, execute **Frame — Sincronizar coleções da home** em **Tarefas agendadas**. O intervalo padrão é de 30 minutos.

As coleções nativas usam os nomes da API, como **Frame — Filmes para descobrir** e **Frame — Séries para descobrir**. A tarefa usa os títulos já indexados. Quando a seção muda, ela ajusta os vínculos da coleção e mantém os títulos na biblioteca. Se a home vier parcial ou algum título ainda estiver aguardando indexação, a tarefa mantém os vínculos existentes. Seções pessoais, como **Continuar assistindo**, ficam fora da sincronização.

## Recuperação de links de reprodução

No modo sob demanda, a tarefa **Frame — Recuperar links de reprodução** verifica lotes de até 60 arquivos STRM a cada 30 minutos. Também pode ser executada pelo painel de tarefas do Jellyfin. Ela percorre filmes e episódios importados pelo Frame e registra os totais nos logs do servidor.

Quando o endereço atual responde com um erro de acesso ou de disponibilidade, a tarefa solicita um novo link ao FILMES-JR e testa a origem principal e a reserva permitidas. Ela atualiza o STRM no mesmo caminho para preservar a ficha e o histórico do Jellyfin. Enquanto o link recuperado estiver gravado, sincronizações que ainda recebam o endereço rejeitado preservam o reparo. Falhas temporárias e redirecionamentos deixam o STRM como estava. Para uma importação antiga, a tarefa tenta identificar o título pelo NFO; se ele não puder ser associado ao catálogo, reimporte o título pelo painel do Frame.

## Contrato experimental da sincronização geral

A URL configurada deve retornar `{ "items": [...], "nextCursor": null }`. Se houver mais páginas, preencha `nextCursor`; a próxima requisição inclui `cursor` codificado. O exemplo em `teste/catalog.json` contém um filme.

- Filmes com `type: "movie"` usam UUID em `id`, `name`, `videoUrl` e os campos opcionais `description`, `year`, `rating`, `durationMinutes` e `genres`.
- Séries com `type: "series"` usam UUID em `id`, `name` e `seasons`. Cada temporada tem `number` e `episodes`. Cada episódio tem UUID em `id`, `number`, `name`, `videoUrl` e, opcionalmente, `description` e `durationMinutes`.
- Filmes e séries podem fornecer images com posterUrl, backdropUrl e logoUrl. A origem precisa constar nos hosts de mídia e servir PNG ou JPEG sem redirecionamentos.
- Use referências de vídeo estáveis e autorizadas que o Jellyfin possa abrir diretamente. URLs HTTP exigem autorização explícita da origem. As URLs são gravadas nos STRM. Cadastre somente origens de mídia confiáveis.

A tarefa consulta o catálogo completo e só regrava arquivos com conteúdo alterado. Itens removidos do catálogo permanecem no disco. O cursor de alterações e a renovação automática das URLs de reprodução ainda estão pendentes.
