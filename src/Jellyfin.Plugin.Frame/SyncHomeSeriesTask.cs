using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class SyncHomeSeriesTask(IApplicationPaths paths, IDataProtectionProvider protection,
    ILibraryMonitor monitor, ILogger<SyncHomeSeriesTask> logger) : IScheduledTask
{
    public string Name => "Frame — Sincronizar séries da home";
    public string Key => "FrameSyncHomeSeries";
    public string Description => "Importa as séries da home FILMES-JR com todas as temporadas e episódios disponíveis.";
    public string Category => "Frame";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks }
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) =>
        HomeSync.Execute(paths, protection, monitor, logger, true, ImportHome, progress, ct);

    internal static async Task ImportHome(BrowseClient client, FrameConfiguration config, Func<bool> stillConfigured,
        Action<string> notify, ILogger logger, IProgress<double> progress, CancellationToken ct)
    {
        var home = await client.GetHome(ct);
        var cards = HomeSync.GetCards(home, series: true);
        var allowed = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        var artworkHosts = new HashSet<string>(allowed, allowed.Comparer) { BrowseTitleMetadata.ArtworkHost };
        var importedIds = new HashSet<Guid>();
        var episodeCount = 0;
        var pendingEpisodes = 0;
        var pendingSeries = 0;
        var nextSteps = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cards.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!stillConfigured())
                throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

            var card = cards[i];
            try
            {
                var show = await client.GetHomeTitle(card, ct);
                if (show is null)
                {
                    pendingSeries++;
                    logger.LogWarning("Série da home sem detalhes ou episódios disponíveis ({ExternalId}).",
                        card.Item.ExternalId);
                    continue;
                }
                if (importedIds.Contains(show.Id))
                    continue;

                var seasons = new List<FrameSeason>();
                var blocked = 0;
                foreach (var season in show.Seasons ?? [])
                {
                    var episodes = new List<FrameEpisode>();
                    foreach (var episode in season.Episodes ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            _ = SyncCatalogTask.PlaybackUrl(episode.VideoUrl, allowed);
                            episodes.Add(episode);
                        }
                        catch (InvalidDataException ex)
                        {
                            blocked++;
                            if (!string.IsNullOrWhiteSpace(episode.VideoUrl))
                                nextSteps.Add(ex.Message);
                        }
                    }
                    if (episodes.Count != 0)
                        seasons.Add(season with { Episodes = episodes.ToArray() });
                }
                pendingEpisodes += blocked;
                if (blocked != 0)
                    logger.LogWarning("Frame deixou {Count} episódio(s) pendente(s) na série da home {ExternalId}.",
                        blocked, card.Item.ExternalId);
                if (seasons.Count == 0)
                {
                    pendingSeries++;
                    continue;
                }

                var folder = Path.Combine(Path.GetFullPath(config.SeriesPath), "Frame-" + show.Id);
                var needPoster = !HomeSync.HasArtwork(folder, "poster");
                var needBackdrop = !HomeSync.HasArtwork(folder, "fanart");
                BrowseTitleMetadata? metadata = null;
                if (!File.Exists(Path.Combine(folder, "tvshow.nfo")) || needPoster || needBackdrop)
                    metadata = await HomeSync.LoadMetadata(client, card, logger, ct);
                if (metadata is not null)
                    show = metadata.ApplyTo(show);

                await SyncCatalogTask.SyncLock.WaitAsync(ct);
                try
                {
                    if (!stillConfigured())
                        throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

                    var item = show with { Seasons = seasons.ToArray(), Images = null };
                    var changed = await SyncCatalogTask.Materialize(item, config, allowed, ct, preserveExistingNfo: true);
                    importedIds.Add(show.Id);
                    episodeCount += seasons.Sum(season => season.Episodes!.Length);
                    try
                    {
                        await HomeSync.WriteMissingArtwork(folder, show.Images, metadata, artworkHosts, ct);
                    }
                    catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
                    {
                        logger.LogWarning("Frame sincronizou a série {ExternalId}, mas uma imagem falhou ({Type}).",
                            card.Item.ExternalId, ex.GetType().Name);
                    }
                    if (changed || needPoster && HomeSync.HasArtwork(folder, "poster") ||
                        needBackdrop && HomeSync.HasArtwork(folder, "fanart"))
                        notify(folder);
                }
                finally
                {
                    SyncCatalogTask.SyncLock.Release();
                }
            }
            catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
            {
                pendingSeries++;
                logger.LogWarning("O Frame não sincronizou a série da home {ExternalId} ({Type}).",
                    card.Item.ExternalId, ex.GetType().Name);
            }
            finally
            {
                progress.Report(100d * (i + 1) / cards.Length);
            }
        }

        progress.Report(100);
        logger.LogInformation("Séries da home FILMES-JR: {Series} séries e {Episodes} episódios sincronizados; " +
            "{PendingSeries} séries e {PendingEpisodes} episódios pendentes; resposta parcial {Partial}.",
            importedIds.Count, episodeCount, pendingSeries, pendingEpisodes, home.Partial);
        if (pendingSeries != 0 || pendingEpisodes != 0)
            throw new InvalidOperationException(
                $"Frame sincronizou {importedIds.Count} série(s) e {episodeCount} episódio(s). Ficaram pendentes " +
                $"{pendingSeries} série(s) e {pendingEpisodes} episódio(s). " +
                (nextSteps.Count > 0 ? string.Join(" ", nextSteps) : "Confira os Registros do Jellyfin."));
    }
}
