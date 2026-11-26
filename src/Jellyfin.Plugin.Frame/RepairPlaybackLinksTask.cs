using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class RepairPlaybackLinksTask(IApplicationPaths paths, IDataProtectionProvider protection,
    ILibraryMonitor monitor, ILogger<RepairPlaybackLinksTask> logger) : IScheduledTask
{
    private static readonly HttpClient Media = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxResponseDrainSize = 0
    });
    private const int FilesPerRun = 60;
    private const int RenewalsPerRun = 12;

    private string? nextAfter;
    private FrameConfiguration? previousConfig;

    public string Name => "Frame — Recuperar links de reprodução";
    public string Key => "FrameRepairPlaybackLinks";
    public string Description => "Confere links de filmes e episódios do Frame e corrige os que têm um substituto disponível.";
    public string Category => "Frame";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.GetBrowseOrigin() is not { } origin)
            return;

        if (!ReferenceEquals(previousConfig, config))
        {
            previousConfig = config;
            nextAfter = null;
        }

        var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        if (hosts.Count == 0)
            return;

        var candidates = FramePlaybackRepair.Targets(config.MoviesPath, false);
        if (Path.IsPathFullyQualified(config.SeriesPath) &&
            (!Path.IsPathFullyQualified(config.MoviesPath) ||
                !HomeSync.ContainsPath(config.MoviesPath, config.SeriesPath) &&
                !HomeSync.ContainsPath(config.SeriesPath, config.MoviesPath)))
            candidates = candidates.Concat(FramePlaybackRepair.Targets(config.SeriesPath, true));

        var files = candidates.OrderBy(target => target.Path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            return;

        var start = nextAfter is null ? 0 : Array.FindIndex(files, file => StringComparer.Ordinal.Compare(file.Path, nextAfter) > 0);
        if (start < 0)
            start = 0;

        var checkedFiles = 0;
        var repaired = 0;
        var pending = 0;
        var count = Math.Min(FilesPerRun, files.Length);
        var client = await BrowseAccountSession.CreateClient(origin, paths.PluginConfigurationsPath, protection, ct);
        bool StillConfigured() => ReferenceEquals(Plugin.Instance?.Configuration, config);
        for (var offset = 0; offset < count && repaired + pending < RenewalsPerRun && StillConfigured(); offset++)
        {
            ct.ThrowIfCancellationRequested();
            var target = files[(start + offset) % files.Length];
            nextAfter = target.Path;
            checkedFiles++;
            try
            {
                var result = await FramePlaybackRepair.Repair(target, origin, client, Media, hosts,
                    StillConfigured, monitor.ReportFileSystemChanged, ct);
                if (result == PlaybackRepairResult.Repaired) repaired++;
                if (result == PlaybackRepairResult.Pending) pending++;
            }
            catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
            {
                pending++;
                logger.LogWarning("Não foi possível recuperar o link do título {Id} ({Type}).", target.FrameId, ex.GetType().Name);
            }
            progress.Report(100d * checkedFiles / count);
        }

        progress.Report(100);
        logger.LogInformation("Links do Frame: {Checked} verificados, {Repaired} recuperados e {Pending} pendentes.",
            checkedFiles, repaired, pending);
    }
}
