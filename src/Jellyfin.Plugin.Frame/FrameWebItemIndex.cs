using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Frame;

internal static class FrameWebItemIndex
{
    internal static string MediaFolder(string root, Guid frameId) =>
        Path.Combine(Path.GetFullPath(root), "Frame-" + frameId);

    internal static string MoviePath(string root, Guid frameId) =>
        Path.Combine(MediaFolder(root, frameId), "movie.strm");

    internal static Guid? Find(ILibraryManager library, string path, User user, bool series = false) =>
        VisibleItemId(FindIndexed(library, path, series), user, series);

    internal static BaseItem? FindIndexed(ILibraryManager library, string path, bool series) =>
        Exists(path, series) && library.FindByPath(path, series) is { } item && MatchesType(item, series) &&
        item.Id != Guid.Empty ? item : null;

    // Call under the Frame sync lock after writing the files required by the resolver.
    internal static Guid? Index(ILibraryManager library, IFileSystem fileSystem, string path, FrameItem item,
        User user, CancellationToken ct) =>
        VisibleItemId(IndexItem(library, fileSystem, path, item, series: false, ct), user, series: false);

    internal static void IndexSeries(ILibraryManager library, IFileSystem fileSystem, string folder, FrameItem item,
        CancellationToken ct) =>
        IndexItem(library, fileSystem, folder, item, series: true, ct);

    private static BaseItem? IndexItem(ILibraryManager library, IFileSystem fileSystem, string path, FrameItem item,
        bool series, CancellationToken ct)
    {
        if (!Exists(path, series))
            return null;
        if (library.FindByPath(path, series) is { } existing)
            return existing;
        if (library.IsScanRunning)
            return null;

        var folder = series ? path : Path.GetDirectoryName(path)!;
        var root = Path.GetDirectoryName(folder)!;
        if (library.FindByPath(root, true) is not Folder parent ||
            parent is AggregateFolder or CollectionFolder or UserRootFolder || parent.Id == Guid.Empty)
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directoryService = new DirectoryService(fileSystem);
        var directory = fileSystem.GetDirectoryInfo(folder);
        var resolved = library.ResolvePaths([directory], directoryService, parent, library.GetLibraryOptions(parent),
                library.GetContentType(parent))
            .FirstOrDefault(candidate => MatchesType(candidate, series) && string.Equals(candidate.Path, path, comparison));
        if (resolved is null || resolved.Id == Guid.Empty)
            return null;

        // A native scan may have just indexed this path while the resolver was running.
        if (library.GetItemById(resolved.Id) is { } indexed)
            return indexed;
        if (library.IsScanRunning)
            return null;

        resolved.SetParent(parent);
        resolved.Name = item.Name!;
        resolved.ProductionYear = item.Year;
        resolved.Overview = item.Description;
        ct.ThrowIfCancellationRequested();
        library.CreateItems([resolved], parent, ct);
        resolved.ChangedExternally();
        return resolved;
    }

    private static bool Exists(string path, bool series) =>
        File.Exists(series ? Path.Combine(path, "tvshow.nfo") : path);

    private static bool MatchesType(BaseItem item, bool series) => series ? item is Series : item is Movie;

    private static Guid? VisibleItemId(BaseItem? item, User user, bool series) =>
        item is not null && MatchesType(item, series) && item.Id != Guid.Empty && item.IsVisibleStandalone(user)
            ? item.Id : null;
}
