using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

internal static class HomeSync
{
    internal static async Task Execute(IApplicationPaths paths, IDataProtectionProvider protection, ILibraryMonitor monitor,
        ILogger logger, bool series, Func<BrowseClient, FrameConfiguration, Func<bool>, Action<string>, ILogger,
            IProgress<double>, CancellationToken, Task> import, IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { OnDemand: true } || !(series ? config.SyncHomeSeries : config.SyncHomeMovies))
            return;
        var origin = config.GetBrowseOrigin();
        var directory = series ? config.SeriesPath : config.MoviesPath;
        var description = series ? "séries" : "filmes";
        if (origin is null || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException($"Salve a URL HTTPS da API e a pasta dedicada a {description} no Frame.");
        if (series && Path.IsPathFullyQualified(config.MoviesPath) &&
            (ContainsPath(config.MoviesPath, directory) || ContainsPath(directory, config.MoviesPath)))
            throw new InvalidOperationException("Use pastas separadas para filmes e séries, sem colocar uma dentro da outra.");

        try
        {
            var tokenPath = Path.Combine(paths.PluginConfigurationsPath, BrowseAccountSession.CredentialsFileName);
            var session = new BrowseAccountSession(BrowseClient.SharedHttp, origin, tokenPath, protection);
            var token = await session.GetAccessToken(ct) ?? Environment.GetEnvironmentVariable("FRAME_API_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Entre na sua conta FILMES-JR no painel do Frame.");

            var existingConfig = () => ReferenceEquals(Plugin.Instance?.Configuration, config);
            var client = new BrowseClient(BrowseClient.SharedHttp, origin, token);
            await import(client, config, existingConfig, monitor.ReportFileSystemChanged, logger, progress, ct);
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
            throw new InvalidOperationException($"Confira a resposta da home e as permissões da pasta de {description}.");
        }
    }

    internal static BrowseHomeItem[] GetCards(BrowseHome home, bool series)
    {
        if (home.Rows is null)
            throw new InvalidDataException("A home FILMES-JR não retornou as listas de títulos.");

        var homeItems = home.Rows.SelectMany(row => row?.Items ?? []);
        if (home.Hero is { } hero)
            homeItems = homeItems.Prepend(new(hero, null));
        return homeItems
            .OfType<BrowseHomeItem>()
            .Where(card => card.Item is { } item && item.IsSeries == series &&
                (BrowseClient.IsValidId(item.ExternalId) || card.PersistedTitleId is not null))
            .GroupBy(card => BrowseClient.IsValidId(card.Item.ExternalId)
                ? card.Item.ExternalId : card.PersistedTitleId!.Value.ToString("D"), StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(card => card.PersistedTitleId is not null) ?? group.First())
            .ToArray();

    }

    internal static bool ContainsPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }


    internal static async Task<BrowseTitleMetadata?> LoadMetadata(BrowseClient client, BrowseHomeItem card,
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

    internal static async Task WriteMissingArtwork(string folder, FrameImages? source, BrowseTitleMetadata? metadata,
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

    internal static bool HasArtwork(string folder, string name) =>
        File.Exists(Path.Combine(folder, name + ".jpg")) || File.Exists(Path.Combine(folder, name + ".png"));

    internal static bool IsItemFailure(Exception ex, CancellationToken ct) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or XmlException or
            HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested;
}
