using System.Net;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using System.Xml;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

[ApiController]
[Route("Frame/Browse")]
[Authorize(Policy = "RequiresElevation")]
public sealed class FrameBrowseController(ILibraryMonitor monitor, ILogger<FrameBrowseController> logger,
    IApplicationPaths paths, IDataProtectionProvider protection)
    : ControllerBase
{
    [HttpGet("Icon")]
    public IActionResult GetIcon()
    {
        var icon = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Frame.Frame-icon.png");
        return icon is null ? NotFound() : File(icon, "image/png");
    }

    [HttpPost("Session")]
    public async Task<IActionResult> SignIn([FromBody] FrameLoginRequest request, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || GetOrigin(config) is not { } origin ||
            !string.Equals(request.ApiUrl, config.BrowseApiUrl, StringComparison.Ordinal))
            return BadRequest(new { message = "Salve a URL base HTTPS da API antes de entrar." });

        var email = request.Email?.Trim();
        if (email is not { Length: > 0 and <= 254 } || request.Password is not { Length: > 0 and <= 1024 } password)
            return BadRequest(new { message = "Informe o e-mail e a senha da sua conta FILMES-JR." });

        try
        {
            await AccountSession(origin).SignIn(email, password, ct);
            return Ok(new { message = "Conta FILMES-JR conectada." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return BadRequest(new { message = "E-mail ou senha incorretos." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return StatusCode(403, new { message = "A conta FILMES-JR está inativa ou sem acesso." });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "A API FILMES-JR demorou para responder." });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException or CryptographicException or JsonException)
        {
            logger.LogWarning("Frame não conseguiu conectar a conta ({Type}).", ex.GetType().Name);
            return StatusCode(502, new { message = "Não foi possível conectar a conta. Confira a API e o armazenamento do Jellyfin." });
        }
    }

    [HttpGet("Session")]
    public async Task<IActionResult> GetSession(CancellationToken ct)
    {
        if (GetOrigin() is not { } origin)
            return Ok(new { connected = false });
        try
        {
            return Ok(new { connected = await AccountSession(origin).HasSession(ct) });
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            return Ok(new { connected = false });
        }
    }

    [HttpPut("Token")]
    public async Task<IActionResult> SaveToken([FromBody] FrameTokenRequest request, CancellationToken ct)
    {
        if (GetOrigin() is not { } origin)
            return BadRequest(new { message = "Salve a URL base HTTPS da API antes de configurar o token." });
        var token = request.Token?.Trim();
        if (token is not { Length: > 0 and <= 8192 } || token.Any(c => c is < '!' or > '~'))
            return BadRequest(new { message = "Informe um token válido, sem espaços." });
        await BrowseAccountSession.Gate.WaitAsync(ct);
        try
        {
            await BrowseTokenStore.Write(TokenPath, origin, token, protection, ct);
            return Ok(new { message = "Token salvo." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning("Frame não conseguiu salvar a credencial ({Type}).", ex.GetType().Name);
            return StatusCode(500, new { message = "Não foi possível proteger ou gravar o token no servidor." });
        }
        finally
        {
            BrowseAccountSession.Gate.Release();
        }
    }

    [HttpDelete("Token")]
    public async Task<IActionResult> RemoveToken(CancellationToken ct)
    {
        try
        {
            if (GetOrigin() is not { } origin)
                return BadRequest(new { message = "Salve a URL base HTTPS da API primeiro." });
            var revoked = await AccountSession(origin).SignOut(ct);
            return Ok(new { message = revoked
                ? "Credenciais removidas. A variável de ambiente será usada se estiver configurada."
                : "Credenciais removidas do Jellyfin. A API não confirmou o encerramento da sessão." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { message = "Não foi possível remover as credenciais do servidor." });
        }
    }

    private BrowseAccountSession AccountSession(Uri origin) =>
        new(BrowseClient.SharedHttp, origin, TokenPath, protection);

    private string TokenPath => Path.Combine(paths.PluginConfigurationsPath, BrowseAccountSession.CredentialsFileName);

    private static Uri? GetOrigin(FrameConfiguration? config = null) =>
        (config ?? Plugin.Instance?.Configuration)?.GetBrowseOrigin();

    [HttpGet("Search")]
    public async Task<IActionResult> Search([FromQuery] string term, [FromQuery] int page = 1,
        CancellationToken ct = default)
    {
        if (!ValidSearch(term, page))
            return BadRequest(new { message = "Digite de 2 a 120 caracteres para buscar." });

        return await Run(async (client, _) => Ok(await client.Search(term, page, ct)), ct);
    }

    [HttpPost("Import")]
    public async Task<IActionResult> Import([FromBody] FrameImportRequest selection, CancellationToken ct)
    {
        if (!ValidSearch(selection.Term, selection.Page) || !BrowseClient.IsValidId(selection.ExternalId))
            return BadRequest(new { message = "Seleção inválida." });

        return await Run(async (client, config) =>
        {
            var item = await client.GetTitleForImport(selection, ct);
            if (item is null)
                return NotFound(new { message = "Título indisponível. Faça uma nova busca." });

            var isSeries = item.Type == "series";
            var mediaPath = isSeries ? config.SeriesPath : config.MoviesPath;
            if (!Path.IsPathFullyQualified(mediaPath))
                return BadRequest(new { message = isSeries
                    ? "Salve a pasta absoluta dedicada a séries antes de importar."
                    : "Salve a pasta absoluta de filmes." });
            if (isSeries && Path.IsPathFullyQualified(config.MoviesPath) &&
                (ContainsPath(config.MoviesPath, mediaPath) || ContainsPath(mediaPath, config.MoviesPath)))
                return BadRequest(new { message = "Use pastas separadas para filmes e séries, sem colocar uma dentro da outra." });

            var season = item.Seasons?.SingleOrDefault();
            var episode = season?.Episodes?.SingleOrDefault();
            var videoUrl = isSeries ? episode?.VideoUrl : item.VideoUrl;
            if (string.IsNullOrWhiteSpace(videoUrl))
                return UnprocessableEntity(new { message = isSeries
                    ? "A API não retornou um episódio com URL de vídeo para esta série."
                    : "A API não retornou uma URL de vídeo para este filme." });

            var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
            try
            {
                _ = SyncCatalogTask.PlaybackUrl(videoUrl, hosts);
            }
            catch (InvalidDataException ex)
            {
                return UnprocessableEntity(new { message = ex.Message });
            }

            BrowseTitleMetadata? metadata = null;
            using (var metadataBudget = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                metadataBudget.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    metadata = await client.GetTitleMetadata(selection.ExternalId, metadataBudget.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException ||
                    ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("O Frame não conseguiu enriquecer os metadados do título {Id} ({Type}).",
                        item.Id, ex.GetType().Name);
                }
            }

            if (metadata is not null)
                item = metadata.ApplyTo(item);

            string? artworkWarning = null;
            var artworkHosts = new HashSet<string>(hosts, hosts.Comparer) { BrowseTitleMetadata.ArtworkHost };
            var fallback = item.Images is null ? null : new FrameImages(
                AllowedArtwork(item.Images.PosterUrl, artworkHosts),
                AllowedArtwork(item.Images.BackdropUrl, artworkHosts),
                AllowedArtwork(item.Images.LogoUrl, artworkHosts),
                AllowedArtwork(item.Images.LogoBigUrl, artworkHosts));

            await SyncCatalogTask.SyncLock.WaitAsync(ct);
            try
            {
                if (Plugin.Instance?.Configuration is not { OnDemand: true } current || !ReferenceEquals(current, config))
                    return Conflict(new { message = "A configuração mudou. Faça uma nova busca." });

                await SyncCatalogTask.Materialize(item with { Images = null }, config, hosts, ct);
                var folder = Path.Combine(Path.GetFullPath(mediaPath), "Frame-" + item.Id);
                try
                {
                    if (!await SyncCatalogTask.ImportArtwork(folder, metadata?.Artwork(), artworkHosts, ct, fallback))
                        artworkWarning = " Nenhuma capa disponível nas origens de imagem permitidas.";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException ||
                    ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Frame importou o título {Id}, mas uma imagem falhou ({Type}).", item.Id, ex.GetType().Name);
                    artworkWarning = " Parte do material visual não pôde ser importada.";
                }

                var mediaFile = isSeries
                    ? Path.Combine(folder, SyncCatalogTask.SeasonFolderName(season!.Number),
                        SyncCatalogTask.EpisodeFileName(season.Number, episode!.Number) + ".strm")
                    : Path.Combine(folder, "movie.strm");
                monitor.ReportFileSystemChanged(mediaFile);
            }
            finally
            {
                SyncCatalogTask.SyncLock.Release();
            }

            var message = isSeries
                ? $"Episódio {SyncCatalogTask.EpisodeFileName(season!.Number, episode!.Number)} importado. Aguarde a atualização da biblioteca."
                : "Filme importado. Aguarde a atualização da biblioteca.";
            return Ok(new { message = message + artworkWarning });
        }, ct);
    }

    private static bool ContainsPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private async Task<IActionResult> Run(Func<BrowseClient, FrameConfiguration, Task<IActionResult>> action,
        CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || GetOrigin(config) is not { } baseUri)
            return BadRequest(new { message = "Ative o modo sob demanda e salve a URL base HTTPS da API." });
        try
        {
            var token = await AccountSession(baseUri).GetAccessToken(ct)
                ?? Environment.GetEnvironmentVariable("FRAME_API_TOKEN");
            return await action(new BrowseClient(BrowseClient.SharedHttp, baseUri, token), config);
        }
        catch (CryptographicException)
        {
            return Conflict(new { message = "A credencial salva não pode ser usada com esta URL da API. Entre novamente ou remova a credencial." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return StatusCode(502, new { message = "A API recusou o acesso. Entre novamente ou confira a permissão da conta FILMES-JR." });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Frame recebeu erro da API ({Status}).", (int?)ex.StatusCode);
            return StatusCode(502, new { message = "A API do Frame não respondeu com sucesso." });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "A API do Frame demorou para responder." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or XmlException)
        {
            logger.LogWarning("Frame não concluiu a operação ({Type}).", ex.GetType().Name);
            return StatusCode(500, new { message = "Confira a resposta da API e a permissão de escrita na pasta de filmes." });
        }
    }

    private static bool ValidSearch(string? term, int page) =>
        page is >= 1 and <= 100 && term?.Trim() is { Length: >= 2 and <= 120 } trimmed && !trimmed.Any(char.IsControl);

    private static string? AllowedArtwork(string? url, HashSet<string> hosts) =>
        SyncCatalogTask.MediaUrlAllowed(url, hosts) ? url : null;
}

public sealed record FrameTokenRequest(string? Token);

public sealed class FrameLoginRequest
{
    public string? ApiUrl { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
}
