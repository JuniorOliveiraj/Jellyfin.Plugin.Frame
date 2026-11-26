using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Frame;

public sealed class FrameWebSeriesImports(IApplicationPaths paths, IDataProtectionProvider protection,
    ILibraryManager library, IFileSystem fileSystem, ILibraryMonitor monitor, ILogger<FrameWebSeriesImports> logger)
    : BackgroundService
{
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(16) { SingleReader = true });
    private readonly Dictionary<ImportKey, Job> active = [];
    private readonly MemoryCache history = new(new MemoryCacheOptions { SizeLimit = 64 });

    internal enum QueueResult { Accepted, Busy, NoPending }

    internal QueueResult Enqueue(FrameConfiguration config, FrameImportRequest selection)
    {
        var key = Key(config, selection.ExternalId);
        lock (active)
        {
            if (active.ContainsKey(key))
                return QueueResult.Accepted;

            var previous = selection.RetryPending ? history.Get<Report>(key) : null;
            if (selection.RetryPending && previous is not { Remaining.Count: > 0 })
                return QueueResult.NoPending;

            var progress = previous is null ? new FrameWebSeriesProgress("queued", 0, 0, 0) :
                previous.Progress with { Status = "queued", Message = null };
            var job = new Job(config, selection, key, progress, previous?.Remaining);
            if (!queue.Writer.TryWrite(job))
                return QueueResult.Busy;

            active.Add(key, job);
            history.Remove(key);
            return QueueResult.Accepted;
        }
    }

    internal FrameWebSeriesProgress? Progress(FrameConfiguration config, string externalId)
    {
        var key = Key(config, externalId);
        lock (active)
        {
            return active.TryGetValue(key, out var job) ? job.Progress : history.Get<Report>(key)?.Progress;
        }
    }

    private static ImportKey Key(FrameConfiguration config, string externalId)
    {
        // Keep pending episodes available when an administrator updates the media allowlist.
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SeriesPath));
        return new(config.GetBrowseOrigin()?.AbsoluteUri ?? "", OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path, externalId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var progress = new SeriesImportProgress(job.Progress, job.RetryPending,
                snapshot => job.Progress = snapshot);
            string? failure = null;
            try
            {
                bool StillConfigured() => ReferenceEquals(Plugin.Instance?.Configuration, job.Config);
                if (!StillConfigured() || job.Config.GetBrowseOrigin() is not { } origin)
                {
                    failure = "A configuração do Frame mudou. Faça uma nova busca.";
                    continue;
                }

                var client = await BrowseAccountSession.CreateClient(origin, paths.PluginConfigurationsPath, protection, stoppingToken);
                failure = await ImportSeries(client, job.Config, job.Selection, StillConfigured,
                    IndexSeries, monitor.ReportFileSystemChanged, logger, stoppingToken, progress);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex is CryptographicException ? "Entre novamente na sua conta FILMES-JR no painel do Frame."
                    : HomeSync.DescribeItemFailure(ex);
                logger.LogWarning("A importação em segundo plano da série {ExternalId} falhou ({Type}).",
                    job.Selection.ExternalId, ex.GetType().Name);
            }
            finally
            {
                lock (active)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        progress.Finish(failure);
                        history.Set(job.Key, new Report(progress.Current, progress.Remaining), new MemoryCacheEntryOptions
                        {
                            Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                        });
                    }
                    active.Remove(job.Key);
                }
            }
        }
    }

    private void IndexSeries(FrameItem series, string folder, CancellationToken ct)
    {
        try
        {
            FrameWebItemIndex.IndexSeries(library, fileSystem, folder, series, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("O Jellyfin não indexou imediatamente a série do Frame {Id} ({Type}).",
                series.Id, ex.GetType().Name);
        }
    }

    internal static async Task<string?> ImportSeries(BrowseClient client, FrameConfiguration config,
        FrameImportRequest selection, Func<bool> stillConfigured, Action<FrameItem, string, CancellationToken> index,
        Action<string> notify, ILogger logger, CancellationToken ct, SeriesImportProgress? progress = null)
    {
        selection = selection with { IsSeries = true, AllEpisodes = true };
        var item = await client.GetTitleForImport(selection, ct);
        if (item is null)
            return "Série indisponível. Faça uma nova busca.";

        progress ??= new(new("queued", 0, 0, 0));
        var planned = progress.Plan(item);
        var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        var episodes = SyncCatalogTask.SelectSeriesEpisodes(planned, hosts, ct);
        var guidance = string.Join(" ", episodes.Guidance.Order(StringComparer.Ordinal));
        if (episodes.Imported < progress.Pending)
            progress.ExplainPending(guidance.Length != 0 ? guidance :
                "Alguns episódios ficaram sem uma URL de vídeo disponível.");
        if (episodes.Imported == 0)
            return guidance.Length != 0 ? guidance : "A API não retornou um episódio com URL de vídeo para esta série.";

        var firstSeason = episodes.Seasons[0];
        var firstEpisode = firstSeason.Episodes![0];
        var folder = FrameWebItemIndex.MediaFolder(config.SeriesPath, item.Id);
        var seed = item with { Seasons = [firstSeason with { Episodes = [firstEpisode] }], Images = null };

        await SyncCatalogTask.SyncLock.WaitAsync(ct);
        try
        {
            if (!stillConfigured())
                return "A configuração do Frame mudou. Faça uma nova busca.";

            await SyncCatalogTask.Materialize(seed, config, hosts, ct, preserveExistingNfo: true, episodeSaved: progress.Saved);
            index(seed, folder, ct);
            notify(folder);
        }
        finally
        {
            SyncCatalogTask.SyncLock.Release();
        }

        if (!stillConfigured())
            return "A configuração do Frame mudou. Faça uma nova busca.";

        var card = new BrowseHomeItem(new(selection.ExternalId, item.Name!, item.Year ?? 0, true, item.Images), null);
        var metadata = await HomeSync.LoadMetadata(client, card, logger, ct);
        var remaining = episodes.Seasons.Select(season => season with
            { Episodes = season.Episodes!.Where(episode => episode.Id != firstEpisode.Id).ToArray() })
            .Where(season => season.Episodes is { Length: > 0 })
            .ToArray();
        var series = (metadata?.ApplyTo(item) ?? item) with { Seasons = remaining, Images = null };
        await SyncCatalogTask.SyncLock.WaitAsync(ct);
        try
        {
            if (!stillConfigured())
                return "A configuração do Frame mudou. Faça uma nova busca.";

            // Notify Jellyfin even if a write fails after saving part of the remaining episodes.
            try
            {
                await SyncCatalogTask.Materialize(series, config, hosts, ct, preserveExistingNfo: true, episodeSaved: progress.Saved);
                hosts.Add(BrowseTitleMetadata.ArtworkHost);
                try
                {
                    await HomeSync.WriteMissingArtwork(folder, item.Images, metadata, hosts, ct);
                }
                catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
                {
                    logger.LogWarning("Uma imagem da série do Frame {Id} falhou ({Type}).", item.Id, ex.GetType().Name);
                }
            }
            finally
            {
                notify(folder);
            }
        }
        finally
        {
            SyncCatalogTask.SyncLock.Release();
        }

        if (progress.Pending != 0)
            logger.LogWarning("Frame registrou {Imported} episódio(s) da série {ExternalId} e deixou {Pending} pendente(s).",
                progress.Current.Imported, selection.ExternalId, progress.Pending);

        return null;
    }

    public override void Dispose()
    {
        base.Dispose();
        history.Dispose();
    }

    private readonly record struct ImportKey(string Origin, string SeriesPath, string ExternalId);

    private sealed record Report(FrameWebSeriesProgress Progress, IReadOnlySet<Guid> Remaining);

    private sealed class Job(FrameConfiguration config, FrameImportRequest selection, ImportKey key,
        FrameWebSeriesProgress progress, IReadOnlySet<Guid>? retryPending)
    {
        internal FrameConfiguration Config { get; } = config;
        internal FrameImportRequest Selection { get; } = selection;
        internal ImportKey Key { get; } = key;
        internal IReadOnlySet<Guid>? RetryPending { get; } = retryPending;
        internal volatile FrameWebSeriesProgress Progress = progress;
    }
}
