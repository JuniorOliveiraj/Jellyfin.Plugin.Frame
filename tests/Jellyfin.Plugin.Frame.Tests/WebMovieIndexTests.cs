using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class WebMovieIndexTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RegistersOnlyTheResolvedMovieAndReusesItsJellyfinId(bool scanRunning, bool samePath)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var frameId = Guid.NewGuid();
        var path = FrameWebMovieIndex.MoviePath(root, frameId);
        var folder = Path.GetDirectoryName(path)!;
        var movie = new TestMovie { Id = Guid.NewGuid(), Path = samePath ? path : Path.Combine(folder, "other.strm") };
        var parent = new Folder { Id = Guid.NewGuid(), Path = root };
        var user = new User("viewer", "auth", "reset");
        var item = new FrameItem(frameId, "Filme do Frame", "movie", 2025, null, "Sinopse do catálogo", null,
            null, "https://media.example.org/movie.mp4", null, null);
        var resolveCalls = 0;
        var saved = new List<BaseItem>();

        var library = CreateProxy<ILibraryManager>((method, args) =>
        {
            switch (method)
            {
                case "get_IsScanRunning":
                    return scanRunning;
                case "FindByPath":
                    if (Equals(path, args[0]))
                        return saved.SingleOrDefault();
                    Assert.Equal(root, args[0]);
                    return parent;
                case "GetLibraryOptions":
                    return new LibraryOptions();
                case "GetContentType":
                case "GetItemById":
                    return null;
                case "ResolvePaths":
                    resolveCalls++;
                    Assert.Same(parent, args[2]);
                    var directory = Assert.Single(Assert.IsAssignableFrom<IEnumerable<FileSystemMetadata>>(args[0]));
                    Assert.Equal(folder, directory.FullName);
                    return new BaseItem[] { movie };
                case "CreateItems":
                    Assert.Same(parent, args[1]);
                    saved.AddRange(Assert.IsAssignableFrom<IReadOnlyList<BaseItem>>(args[0]));
                    return null;
                default:
                    throw new InvalidOperationException($"Método inesperado: {method}");
            }
        });
        var fileSystem = CreateProxy<IFileSystem>((method, args) => method == "GetDirectoryInfo"
            ? new FileSystemMetadata { FullName = Assert.IsType<string>(args[0]), IsDirectory = true }
            : throw new InvalidOperationException($"Método inesperado: {method}"));

        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(path, item.VideoUrl);
            var result = FrameWebMovieIndex.Index(library, fileSystem, path, item, user, default);

            if (scanRunning || !samePath)
            {
                Assert.Null(result);
                Assert.Empty(saved);
                Assert.Equal(scanRunning ? 0 : 1, resolveCalls);
                Assert.False(movie.RefreshRequested);
                return;
            }

            Assert.Equal(movie.Id, result);
            Assert.Same(movie, Assert.Single(saved));
            Assert.Equal(parent.Id, movie.ParentId);
            Assert.Equal(item.Name, movie.Name);
            Assert.Equal(item.Year, movie.ProductionYear);
            Assert.Equal(item.Description, movie.Overview);
            Assert.True(movie.RefreshRequested);

            Assert.Equal(movie.Id, FrameWebMovieIndex.Index(library, fileSystem, path, item, user, default));
            Assert.Single(saved);
            Assert.Equal(1, resolveCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static T CreateProxy<T>(Func<string, object?[], object?> handle) where T : class
    {
        var proxy = DispatchProxy.Create<T, LibraryCallProxy>();
        ((LibraryCallProxy)(object)proxy).Handle = handle;
        return proxy;
    }

    public class LibraryCallProxy : DispatchProxy
    {
        internal Func<string, object?[], object?> Handle { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handle(targetMethod!.Name, args ?? []);
    }

    private sealed class TestMovie : Movie
    {
        public bool RefreshRequested { get; private set; }

        public override bool IsVisibleStandalone(User user) => true;

        public override void ChangedExternally() => RefreshRequested = true;
    }
}
