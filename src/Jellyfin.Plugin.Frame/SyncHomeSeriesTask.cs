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
        FrameJellyfinCompatibility.IntervalTrigger(TimeSpan.FromMinutes(30))
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) =>
        HomeSync.Execute(paths, protection, monitor, logger, true, ImportHome, progress, ct);

    internal static async Task ImportHome(BrowseClient client, FrameConfiguration config, Func<bool> stillConfigured,
        Action<string> notify, ILogger logger, IProgress<double> progress, CancellationToken ct, HomeSyncReport? report = null)
    {
        report ??= new HomeSyncReport();
        var home = await client.GetHome(ct);
        report.PartialHome = home.Partial;
        var cards = HomeSync.GetCards(home, series: true);
        var allowed = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        var artworkHosts = new HashSet<string>(allowed, allowed.Comparer) { BrowseTitleMetadata.ArtworkHost };
        var importedIds = new HashSet<Guid>();
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
                    report.AddPending(card, "A API não disponibilizou o detalhe ou os episódios desta série.");
                    logger.LogWarning("Série da home sem detalhes ou episódios disponíveis ({ExternalId}).",
                        card.Item.ExternalId);
                    continue;
                }
                if (importedIds.Contains(show.Id))
                    continue;

                var episodes = SyncCatalogTask.SelectSeriesEpisodes(show, allowed, ct,
                    (episode, reason) => report.AddPending(card, reason, episode));
                nextSteps.UnionWith(episodes.Guidance);
                if (episodes.Pending != 0)
                    logger.LogWarning("Frame deixou {Count} episódio(s) pendente(s) na série da home {ExternalId}.",
                        episodes.Pending, card.Item.ExternalId);
                if (episodes.Imported == 0)
                {
                    report.AddPending(card, "Nenhum episódio da série pôde ser importado.");
                    continue;
                }

                var folder = Path.Combine(Path.GetFullPath(config.SeriesPath), "Frame-" + show.Id);
                var needPoster = !HomeSync.HasArtwork(folder, "poster");
                var needBackdrop = !HomeSync.HasArtwork(folder, "fanart");
                BrowseTitleMetadata? metadata = null;
                if (needPoster || needBackdrop || SyncCatalogTask.NeedsSeriesMetadata(folder, show.Id))
                    metadata = await HomeSync.LoadMetadata(client, card, logger, ct);
                if (metadata is not null)
                    show = metadata.ApplyTo(show);

                await SyncCatalogTask.SyncLock.WaitAsync(ct);
                try
                {
                    if (!stillConfigured())
                        throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

                    var item = show with { Seasons = episodes.Seasons, Images = null };
                    var newSeries = !File.Exists(Path.Combine(folder, "tvshow.nfo"));
                    var newEpisodes = episodes.Seasons.Sum(season =>
                    {
                        var seasonPath = Path.Combine(folder, SyncCatalogTask.SeasonFolderName(season.Number));
                        return season.Episodes!.Count(episode =>
                            !File.Exists(Path.Combine(seasonPath, SyncCatalogTask.EpisodeFileName(season.Number, episode.Number) + ".strm")));
                    });
                    var changed = await SyncCatalogTask.Materialize(item, config, allowed, ct, preserveExistingNfo: true);
                    importedIds.Add(show.Id);
                    report.Series = importedIds.Count;
                    report.Episodes += episodes.Imported;
                    report.NewEpisodes += newEpisodes;
                    if (newSeries)
                        report.NewSeries++;
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
                report.AddPending(card, HomeSync.DescribeItemFailure(ex));
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
            report.Series, report.Episodes, report.PendingTitles, report.PendingEpisodes, home.Partial);
        if (report.PendingTitles != 0 || report.PendingEpisodes != 0)
        {
            report.Status = "partial";
            report.Message = nextSteps.Count > 0 ? string.Join(" ", nextSteps) : null;
            throw new InvalidOperationException(
                $"Frame sincronizou {report.Series} série(s) e {report.Episodes} episódio(s). Ficaram pendentes " +
                $"{report.PendingTitles} série(s) e {report.PendingEpisodes} episódio(s). " +
                (report.Message ?? "Confira o resumo no painel do Frame."));
        }
    }
}
