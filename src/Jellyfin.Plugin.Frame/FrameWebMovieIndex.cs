using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Frame;

internal static class FrameWebMovieIndex
{
    internal static string MoviePath(string root, Guid frameId) =>
        Path.Combine(Path.GetFullPath(root), "Frame-" + frameId, "movie.strm");

    internal static Guid? Find(ILibraryManager library, string path, User user) =>
        File.Exists(path) ? VisibleMovieId(library.FindByPath(path, false), user) : null;

    // Call under the Frame sync lock after the STRM, NFO and artwork have been saved.
    internal static Guid? Index(ILibraryManager library, IFileSystem fileSystem, string path, FrameItem item,
        User user, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        if (library.FindByPath(path, false) is { } existing)
            return VisibleMovieId(existing, user);
        if (library.IsScanRunning)
            return null;

        var folder = Path.GetDirectoryName(path)!;
        var root = Path.GetDirectoryName(folder)!;
        if (library.FindByPath(root, true) is not Folder parent ||
            parent is AggregateFolder or CollectionFolder or UserRootFolder || parent.Id == Guid.Empty)
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directoryService = new DirectoryService(fileSystem);
        var directory = fileSystem.GetDirectoryInfo(folder);
        var movie = library.ResolvePaths([directory], directoryService, parent, library.GetLibraryOptions(parent),
                library.GetContentType(parent))
            .OfType<Movie>().FirstOrDefault(candidate => string.Equals(candidate.Path, path, comparison));
        if (movie is null || movie.Id == Guid.Empty)
            return null;

        // A native scan may have just indexed this path while the resolver was running.
        if (library.GetItemById(movie.Id) is { } indexed)
            return VisibleMovieId(indexed, user);
        if (library.IsScanRunning)
            return null;

        movie.SetParent(parent);
        movie.Name = item.Name!;
        movie.ProductionYear = item.Year;
        movie.Overview = item.Description;
        ct.ThrowIfCancellationRequested();
        library.CreateItems([movie], parent, ct);
        movie.ChangedExternally();
        return VisibleMovieId(movie, user);
    }

    private static Guid? VisibleMovieId(BaseItem? item, User user) =>
        item is Movie movie && movie.Id != Guid.Empty && movie.IsVisibleStandalone(user) ? movie.Id : null;
}
