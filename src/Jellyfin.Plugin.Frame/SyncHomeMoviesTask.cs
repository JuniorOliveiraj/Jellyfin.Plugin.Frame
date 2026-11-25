using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class SyncHomeMoviesTask(IApplicationPaths paths, IDataProtectionProvider protection,
    ILibraryMonitor monitor, ILogger<SyncHomeMoviesTask> logger) : IScheduledTask
{
    public string Name => "Frame — Sincronizar filmes da home";
    public string Key => "FrameSyncHomeMovies";
    public string Description => "Importa os filmes exibidos na home da conta FILMES-JR conectada.";
    public string Category => "Frame";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { OnDemand: true, SyncHomeMovies: true })
            return;
        var origin = config.GetBrowseOrigin();
        if (origin is null || !Path.IsPathFullyQualified(config.MoviesPath))
            throw new InvalidOperationException("Salve a URL HTTPS da API e a pasta dedicada a filmes no Frame.");

        try
        {
            var tokenPath = Path.Combine(paths.PluginConfigurationsPath, BrowseAccountSession.CredentialsFileName);
            var session = new BrowseAccountSession(BrowseClient.SharedHttp, origin, tokenPath, protection);
            var token = await session.GetAccessToken(ct) ?? Environment.GetEnvironmentVariable("FRAME_API_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Entre na sua conta FILMES-JR no painel do Frame.");

            var existingConfig = () => ReferenceEquals(Plugin.Instance?.Configuration, config);
            var client = new BrowseClient(BrowseClient.SharedHttp, origin, token);
            await ImportHome(client, config, existingConfig, monitor.ReportFileSystemChanged, logger, progress, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("A API recusou a sessão. Entre novamente no Frame e confira o acesso da conta.");
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("A credencial salva é de outra URL da API. Entre novamente no Frame.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("O Frame não conseguiu consultar a home FILMES-JR ({Status}).", (int?)ex.StatusCode);
            throw new InvalidOperationException("A home do FILMES-JR não respondeu. Confira a API e os Registros.");
        }
        catch (Exception ex) when (IsItemFailure(ex, ct))
        {
            logger.LogWarning("O Frame não conseguiu iniciar a sincronização da home ({Type}).", ex.GetType().Name);
            throw new InvalidOperationException("Confira a resposta da home e as permissões da pasta de filmes.");
        }
    }

    internal static async Task ImportHome(BrowseClient client, FrameConfiguration config, Func<bool> stillConfigured,
        Action<string> notify, ILogger logger, IProgress<double> progress, CancellationToken ct)
    {
        var home = await client.GetHome(ct);
        if (home.Rows is null)
            throw new InvalidDataException("A home FILMES-JR não retornou as listas de títulos.");

        var homeItems = home.Rows.SelectMany(row => row?.Items ?? []);
        if (home.Hero is { } hero)
            homeItems = homeItems.Prepend(new(hero, null));
        var cards = homeItems
            .OfType<BrowseHomeItem>()
            .Where(card => card.Item is { IsSeries: false } item &&
                (BrowseClient.IsValidId(item.ExternalId) || card.PersistedTitleId is not null))
            .GroupBy(card => BrowseClient.IsValidId(card.Item.ExternalId)
                ? card.Item.ExternalId : card.PersistedTitleId!.Value.ToString("D"), StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(card => card.PersistedTitleId is not null) ?? group.First())
            .ToArray();

        var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        var artworkHosts = new HashSet<string>(hosts, hosts.Comparer) { BrowseTitleMetadata.ArtworkHost };
        var importedIds = new HashSet<Guid>();
        var failures = 0;
        string? nextStep = null;
        for (var i = 0; i < cards.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!stillConfigured())
                throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

            var card = cards[i];
            try
            {
                var movie = await client.GetHomeMovie(card, ct);
                if (movie is null)
                {
                    failures++;
                    logger.LogWarning("Filme da home sem vídeo disponível ({ExternalId}).", card.Item.ExternalId);
                    continue;
                }
                if (importedIds.Contains(movie.Id))
                    continue;

                try
                {
                    _ = SyncCatalogTask.PlaybackUrl(movie.VideoUrl, hosts);
                }
                catch (InvalidDataException ex)
                {
                    nextStep ??= ex.Message;
                    throw;
                }

                var folder = Path.Combine(Path.GetFullPath(config.MoviesPath), "Frame-" + movie.Id);
                var hasNfo = File.Exists(Path.Combine(folder, "movie.nfo"));
                var needPoster = !HasArtwork(folder, "poster");
                var needBackdrop = !HasArtwork(folder, "fanart");
                BrowseTitleMetadata? metadata = null;
                if (!hasNfo || needPoster || needBackdrop)
                    metadata = await LoadMetadata(client, card, logger, ct);
                if (metadata is not null)
                    movie = metadata.ApplyTo(movie);

                await SyncCatalogTask.SyncLock.WaitAsync(ct);
                try
                {
                    if (!stillConfigured())
                        throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

                    var stream = Path.Combine(folder, "movie.strm");
                    var previousVersion = File.GetLastWriteTimeUtc(stream);
                    await SyncCatalogTask.Materialize(movie with { Images = null }, config, hosts, ct, preserveMovieNfo: true);
                    importedIds.Add(movie.Id);
                    try
                    {
                        await WriteMissingArtwork(folder, movie.Images, metadata, artworkHosts, ct);
                    }
                    catch (Exception ex) when (IsItemFailure(ex, ct))
                    {
                        logger.LogWarning("O Frame importou o filme {Id}, mas uma imagem falhou ({Type}).",
                            movie.Id, ex.GetType().Name);
                    }
                    if (!hasNfo || previousVersion != File.GetLastWriteTimeUtc(stream) ||
                        needPoster && HasArtwork(folder, "poster") || needBackdrop && HasArtwork(folder, "fanart"))
                        notify(stream);
                }
                finally
                {
                    SyncCatalogTask.SyncLock.Release();
                }
            }
            catch (Exception ex) when (IsItemFailure(ex, ct))
            {
                failures++;
                logger.LogWarning("O Frame não sincronizou o filme da home {ExternalId} ({Type}).",
                    card.Item.ExternalId, ex.GetType().Name);
            }
            finally
            {
                progress.Report(100d * (i + 1) / cards.Length);
            }
        }

        progress.Report(100);
        logger.LogInformation("Home FILMES-JR: {Imported} sincronizados; {Failures} falhas; resposta parcial {Partial}.",
            importedIds.Count, failures, home.Partial);
        if (failures > 0)
            throw new InvalidOperationException(
                $"Frame sincronizou {importedIds.Count} filme(s). {failures} título(s) ficaram pendentes. " +
                (nextStep ?? "Confira os Registros do Jellyfin."));
    }

    private static async Task<BrowseTitleMetadata?> LoadMetadata(BrowseClient client, BrowseHomeItem card,
        ILogger logger, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var metadata = BrowseClient.IsValidId(card.Item.ExternalId)
                ? await client.GetTitleMetadata(card.Item.ExternalId, budget.Token) : null;
            return metadata ?? (card.PersistedTitleId is Guid id
                ? await client.GetPersistedTitleMetadata(id, budget.Token) : null);
        }
        catch (Exception ex) when (IsItemFailure(ex, ct))
        {
            logger.LogWarning("Metadados da home indisponíveis ({Type}).", ex.GetType().Name);
            return null;
        }
    }

    private static async Task WriteMissingArtwork(string folder, FrameImages? source, BrowseTitleMetadata? metadata,
        HashSet<string> hosts, CancellationToken ct)
    {
        var poster = !HasArtwork(folder, "poster");
        var backdrop = !HasArtwork(folder, "fanart");
        if (!poster && !backdrop)
            return;
        var art = metadata?.Artwork();
        string? Allowed(string? url) => SyncCatalogTask.MediaUrlAllowed(url, hosts) ? url : null;
        await SyncCatalogTask.ImportArtwork(folder,
            new FrameImages(poster ? art?.PosterUrl : null, backdrop ? art?.BackdropUrl : null, null),
            hosts, ct, new FrameImages(poster ? Allowed(source?.PosterUrl) : null,
                backdrop ? Allowed(source?.BackdropUrl) : null, null,
                poster ? Allowed(source?.LogoBigUrl) : null));
    }

    private static bool HasArtwork(string folder, string name) =>
        File.Exists(Path.Combine(folder, name + ".jpg")) || File.Exists(Path.Combine(folder, name + ".png"));

    private static bool IsItemFailure(Exception ex, CancellationToken ct) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or XmlException or
            HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested;
}
