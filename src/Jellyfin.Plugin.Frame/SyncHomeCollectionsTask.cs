using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class SyncHomeCollectionsTask(IApplicationPaths paths, IDataProtectionProvider protection, ILibraryManager library,
    ICollectionManager collections, IFileSystem fileSystem, ILogger<SyncHomeCollectionsTask> logger) : IScheduledTask
{
    private const string SourceProvider = "FrameHome";

    public string Name => "Frame — Sincronizar coleções da home";
    public string Key => "FrameSyncHomeCollections";
    public string Description => "Organiza as seções de descoberta da home FILMES-JR em coleções do Jellyfin.";
    public string Category => "Frame";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        FrameJellyfinCompatibility.IntervalTrigger(TimeSpan.FromMinutes(30))
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { SyncHomeCollections: true } || config.GetBrowseOrigin() is not { } origin)
            return;

        var client = await BrowseAccountSession.CreateClient(origin, paths.PluginConfigurationsPath, protection, ct);
        var home = await client.GetHome(ct);
        var rows = DiscoveryRows(home);
        var folder = await collections.GetCollectionsFolder(false);
        var created = 0;
        var added = 0;
        var removed = 0;

        for (var i = 0; i < rows.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Plugin.Instance?.Configuration, config))
                return;

            var row = rows[i];
            var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri + "\n" + row.Id)));
            var desired = new HashSet<Guid>();
            var missing = row.Items is null;
            foreach (var card in row.Items ?? [])
            {
                if (FindItem(library, config, card) is Guid itemId)
                    desired.Add(itemId);
                else
                    missing = true;
            }

            var existing = folder?.Children.OfType<BoxSet>()
                .FirstOrDefault(boxSet => boxSet.ProviderIds.TryGetValue(SourceProvider, out var id) && id == sourceId);
            if (existing is not null)
            {
                var changes = MembershipChanges(existing.GetLinkedChildren().Select(item => item.Id), desired,
                    complete: !home.Partial && !missing);
                if (changes.Add.Length != 0)
                    await collections.AddToCollectionAsync(existing.Id, changes.Add);
                if (changes.Remove.Length != 0)
                    await collections.RemoveFromCollectionAsync(existing.Id, changes.Remove);
                added += changes.Add.Length;
                removed += changes.Remove.Length;
            }
            else if (desired.Count != 0)
            {
                folder ??= await collections.GetCollectionsFolder(true);
                if (folder is null)
                    throw new InvalidOperationException("O Jellyfin não conseguiu criar a biblioteca Coleções.");

                var name = "Frame — " + row.Title!.Trim();
                var target = Path.Combine(folder.Path, fileSystem.GetValidFilename(name) + " [boxset]");
                if (Directory.Exists(target) || File.Exists(target) || library.FindByPath(target, true) is not null)
                {
                    logger.LogWarning("O Frame encontrou uma coleção com o mesmo nome da seção {RowId}.", row.Id);
                }
                else
                {
                    await collections.CreateCollectionAsync(new CollectionCreationOptions
                    {
                        Name = name,
                        IsLocked = true,
                        ProviderIds = new(StringComparer.OrdinalIgnoreCase) { [SourceProvider] = sourceId },
                        ItemIdList = desired.Select(id => id.ToString("D")).ToArray()
                    });
                    created++;
                    added += desired.Count;
                }
            }
            progress.Report(100d * (i + 1) / rows.Length);
        }

        progress.Report(100);
        logger.LogInformation("Coleções do Frame: {Created} criadas, {Added} títulos adicionados e {Removed} títulos removidos.",
            created, added, removed);
    }

    internal static BrowseHomeRow[] DiscoveryRows(BrowseHome home)
    {
        if (home.Rows is null)
            throw new InvalidDataException("A home FILMES-JR não retornou as listas de títulos.");

        return home.Rows.OfType<BrowseHomeRow>()
            .Where(row => row.Type == "discovery" && BrowseClient.IsValidId(row.Id) &&
                row.Title?.Trim() is { Length: > 0 and <= 100 } title && !title.Any(char.IsControl))
            .DistinctBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static Guid? FindItem(ILibraryManager library, FrameConfiguration config, BrowseHomeItem? card)
    {
        if (card?.Item is not { } title)
            return null;

        var root = title.IsSeries ? config.SeriesPath : config.MoviesPath;
        if (!Path.IsPathFullyQualified(root))
            return null;
        if (title.IsSeries && Path.IsPathFullyQualified(config.MoviesPath) &&
            (HomeSync.ContainsPath(config.MoviesPath, root) || HomeSync.ContainsPath(root, config.MoviesPath)))
            return null;

        foreach (var externalId in new[] { title.ExternalId, card.PersistedTitleId?.ToString("D") }.Distinct(StringComparer.Ordinal))
        {
            if (!BrowseClient.IsValidId(externalId))
                continue;

            var path = title.IsSeries
                ? FrameWebItemIndex.MediaFolder(root, BrowseClient.SeriesId(externalId!))
                : FrameWebItemIndex.MoviePath(root, BrowseClient.MovieId(externalId!));
            if (FrameWebItemIndex.FindIndexed(library, path, title.IsSeries) is { } item)
                return item.Id;
        }
        return null;
    }

    internal static (Guid[] Add, Guid[] Remove) MembershipChanges(IEnumerable<Guid> current, HashSet<Guid> desired, bool complete)
    {
        var present = current.ToHashSet();
        return (desired.Except(present).ToArray(), complete ? present.Except(desired).ToArray() : []);
    }
}
