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
        FrameJellyfinCompatibility.IntervalTrigger(TimeSpan.FromMinutes(30))
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) =>
        HomeSync.Execute(paths, protection, monitor, logger, false, ImportHome, progress, ct);


    internal static async Task ImportHome(BrowseClient client, FrameConfiguration config, Func<bool> stillConfigured,
        Action<string> notify, ILogger logger, IProgress<double> progress, CancellationToken ct, HomeSyncReport? report = null)
    {
        report ??= new HomeSyncReport();
        var home = await client.GetHome(ct);
        report.PartialHome = home.Partial;

        var cards = HomeSync.GetCards(home, series: false);

        var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        var artworkHosts = new HashSet<string>(hosts, hosts.Comparer) { BrowseTitleMetadata.ArtworkHost };
        var importedIds = new HashSet<Guid>();
        string? nextStep = null;
        for (var i = 0; i < cards.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!stillConfigured())
                throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

            var card = cards[i];
            string? playbackWarning = null;
            try
            {
                var movie = await client.GetHomeTitle(card, ct);
                if (movie is null)
                {
                    report.AddPending(card, "A API não disponibilizou uma URL de vídeo para este filme.");
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
                    playbackWarning = ex.Message;
                    nextStep ??= playbackWarning;
                    throw;
                }

                var folder = Path.Combine(Path.GetFullPath(config.MoviesPath), "Frame-" + movie.Id);
                var hasNfo = File.Exists(Path.Combine(folder, "movie.nfo"));
                var needPoster = !HomeSync.HasArtwork(folder, "poster");
                var needBackdrop = !HomeSync.HasArtwork(folder, "fanart");
                BrowseTitleMetadata? metadata = null;
                if (!hasNfo || needPoster || needBackdrop)
                    metadata = await HomeSync.LoadMetadata(client, card, logger, ct);
                if (metadata is not null)
                    movie = metadata.ApplyTo(movie);

                await SyncCatalogTask.SyncLock.WaitAsync(ct);
                try
                {
                    if (!stillConfigured())
                        throw new InvalidOperationException("A configuração do Frame mudou. Execute a tarefa novamente.");

                    var stream = Path.Combine(folder, "movie.strm");
                    var newMovie = !File.Exists(stream);
                    var changed = await SyncCatalogTask.Materialize(movie with { Images = null }, config, hosts, ct, preserveExistingNfo: true);
                    importedIds.Add(movie.Id);
                    report.Movies = importedIds.Count;
                    if (newMovie)
                        report.NewMovies++;
                    try
                    {
                        await HomeSync.WriteMissingArtwork(folder, movie.Images, metadata, artworkHosts, ct);
                    }
                    catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
                    {
                        logger.LogWarning("O Frame importou o filme {Id}, mas uma imagem falhou ({Type}).",
                            movie.Id, ex.GetType().Name);
                    }
                    if (changed ||
                        needPoster && HomeSync.HasArtwork(folder, "poster") || needBackdrop && HomeSync.HasArtwork(folder, "fanart"))
                        notify(stream);
                }
                finally
                {
                    SyncCatalogTask.SyncLock.Release();
                }
            }
            catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
            {
                report.AddPending(card, playbackWarning ?? HomeSync.DescribeItemFailure(ex));
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
            report.Movies, report.PendingTitles, home.Partial);
        if (report.PendingTitles > 0)
        {
            report.Status = "partial";
            report.Message = nextStep;
            throw new InvalidOperationException(
                $"Frame sincronizou {report.Movies} filme(s). {report.PendingTitles} título(s) ficaram pendentes. " +
                (nextStep ?? "Confira o resumo no painel do Frame."));
        }
    }

}
