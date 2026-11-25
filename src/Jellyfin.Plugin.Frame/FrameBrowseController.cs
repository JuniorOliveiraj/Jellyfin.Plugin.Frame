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

    [HttpPut("Token")]
    public async Task<IActionResult> SaveToken([FromBody] FrameTokenRequest request, CancellationToken ct)
    {
        if (GetOrigin() is not { } origin)
            return BadRequest(new { message = "Salve a URL base HTTPS da API antes de configurar o token." });
        var token = request.Token?.Trim();
        if (token is not { Length: > 0 and <= 8192 } || token.Any(c => c is < '!' or > '~'))
            return BadRequest(new { message = "Informe um token válido, sem espaços." });
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
    }

    [HttpDelete("Token")]
    public IActionResult RemoveToken()
    {
        try
        {
            if (System.IO.File.Exists(TokenPath))
                System.IO.File.Delete(TokenPath);
            return Ok(new { message = "Token do formulário removido. A variável de ambiente será usada se estiver configurada." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { message = "Não foi possível remover o token do servidor." });
        }
    }

    private string TokenPath => Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.Frame.browse-token");

    private static Uri? GetOrigin(FrameConfiguration? config = null)
    {
        config ??= Plugin.Instance?.Configuration;
        if (config is not { OnDemand: true } || !Uri.TryCreate(config.BrowseApiUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return null;
        return new Uri(origin.AbsoluteUri.TrimEnd('/') + "/");
    }

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
            var hosts = config.MediaHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (hosts.Count == 0 || !Path.IsPathFullyQualified(config.MoviesPath))
                return BadRequest(new { message = "Salve a pasta absoluta de filmes e os hosts de mídia permitidos." });

            var movie = await client.GetMovie(selection, ct);
            if (movie is null)
                return NotFound(new { message = "Filme indisponível. Faça uma nova busca." });
            if (string.IsNullOrWhiteSpace(movie.VideoUrl))
                return UnprocessableEntity(new { message = "A API não retornou uma URL de vídeo para este filme." });
            if (!SyncCatalogTask.MediaUrlAllowed(movie.VideoUrl, hosts))
                return UnprocessableEntity(new { message = "A URL de vídeo precisa usar HTTPS e um host de mídia permitido." });

            string? artworkWarning = null;
            var images = movie.Images is null ? null : new FrameImages(
                AllowedArtwork(movie.Images.PosterUrl, hosts),
                AllowedArtwork(movie.Images.BackdropUrl, hosts),
                AllowedArtwork(movie.Images.LogoUrl, hosts));

            await SyncCatalogTask.SyncLock.WaitAsync(ct);
            try
            {
                if (Plugin.Instance?.Configuration is not { OnDemand: true } current || !ReferenceEquals(current, config))
                    return Conflict(new { message = "A configuração mudou. Faça uma nova busca." });

                await SyncCatalogTask.Materialize(movie with { Images = null }, config, hosts, ct);
                var folder = Path.Combine(Path.GetFullPath(config.MoviesPath), "Frame-" + movie.Id);
                try
                {
                    await SyncCatalogTask.ImportArtwork(folder, images, hosts, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException ||
                    ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Frame importou o filme {Id}, mas uma imagem falhou ({Type}).", movie.Id, ex.GetType().Name);
                    artworkWarning = " Uma imagem não pôde ser importada.";
                }

                monitor.ReportFileSystemChanged(Path.Combine(folder, "movie.strm"));
            }
            finally
            {
                SyncCatalogTask.SyncLock.Release();
            }

            return Ok(new { message = "Filme importado. Aguarde a atualização da biblioteca." + artworkWarning });
        }, ct);
    }

    private async Task<IActionResult> Run(Func<BrowseClient, FrameConfiguration, Task<IActionResult>> action,
        CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || GetOrigin(config) is not { } baseUri)
            return BadRequest(new { message = "Ative o modo sob demanda e salve a URL base HTTPS da API." });
        try
        {
            var token = await BrowseTokenStore.Read(TokenPath, baseUri, protection, ct)
                ?? Environment.GetEnvironmentVariable("FRAME_API_TOKEN");
            return await action(new BrowseClient(BrowseClient.SharedHttp, baseUri, token), config);
        }
        catch (CryptographicException)
        {
            return Conflict(new { message = "O token salvo não pode ser usado com esta URL da API. Salve um token novo ou remova o anterior." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return StatusCode(502, new { message = "A API recusou o acesso. Confira o token e a permissão da conta do Frame." });
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
