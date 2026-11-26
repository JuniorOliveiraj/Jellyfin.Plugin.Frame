using System.Reflection;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class WebItemIndexTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task RegistersOnlyTheResolvedItemAndReusesItsJellyfinId(bool series, bool scanRunning, bool samePath)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var frameId = Guid.NewGuid();
        var folder = FrameWebItemIndex.MediaFolder(root, frameId);
        var path = series ? folder : FrameWebItemIndex.MoviePath(root, frameId);
        var candidate = series ? (BaseItem)new TestSeries() : new TestMovie();
        candidate.Id = Guid.NewGuid();
        candidate.Path = samePath ? path : Path.Combine(folder, "other");
        bool RefreshRequested() => candidate is TestSeries show ? show.RefreshRequested :
            ((TestMovie)candidate).RefreshRequested;
        var parent = new Folder { Id = Guid.NewGuid(), Path = root };
        var user = new User("viewer", "auth", "reset");
        var item = new FrameItem(frameId, "Título do Frame", series ? "series" : "movie", 2025, null, "Sinopse do catálogo", null,
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
                    {
                        Assert.Equal(series, args[1]);
                        return saved.SingleOrDefault();
                    }
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
                    return new BaseItem[] { candidate };
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
            await File.WriteAllTextAsync(series ? Path.Combine(folder, "tvshow.nfo") : path, item.VideoUrl);
            var artworkPath = Path.Combine(folder, "poster.png");
            await File.WriteAllBytesAsync(artworkPath, [1]);
            Guid? Index()
            {
                if (!series)
                    return FrameWebItemIndex.Index(library, fileSystem, path, item, user, default);

                FrameWebItemIndex.IndexSeries(library, fileSystem, path, item, default);
                return FrameWebItemIndex.Find(library, path, user, series: true);
            }
            var result = Index();

            if (scanRunning || !samePath)
            {
                Assert.Null(result);
                Assert.Empty(saved);
                Assert.Equal(scanRunning ? 0 : 1, resolveCalls);
                Assert.False(RefreshRequested());
                return;
            }

            Assert.Equal(candidate.Id, result);
            Assert.Same(candidate, Assert.Single(saved));
            Assert.Equal(parent.Id, candidate.ParentId);
            Assert.Equal(item.Name, candidate.Name);
            Assert.Equal(item.Year, candidate.ProductionYear);
            Assert.Equal(item.Description, candidate.Overview);
            Assert.True(RefreshRequested());
            Assert.Equal(artworkPath, candidate.GetImageInfo(ImageType.Primary, 0).Path);

            Assert.Equal(candidate.Id, Index());
            Assert.Single(saved);
            Assert.Equal(1, resolveCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddsMissingArtworkAndKeepsAnExistingCover()
    {
        var directory = Directory.CreateTempSubdirectory("frame-artwork-");
        try
        {
            var item = new Movie();
            item.AddImage(new ItemImageInfo { Type = ImageType.Primary, Path = "custom-poster.jpg" });
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "poster.png"), [1]);
            var backdrop = Path.Combine(directory.FullName, "fanart.jpg");
            await File.WriteAllBytesAsync(backdrop, [2]);

            Assert.True(FrameWebItemIndex.AttachLocalArtwork(item, directory.FullName));
            Assert.Equal("custom-poster.jpg", item.GetImageInfo(ImageType.Primary, 0).Path);
            Assert.Equal(backdrop, item.GetImageInfo(ImageType.Backdrop, 0).Path);
            Assert.False(FrameWebItemIndex.AttachLocalArtwork(item, directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
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

    private sealed class TestSeries : Series
    {
        public bool RefreshRequested { get; private set; }

        public override bool IsVisibleStandalone(User user) => true;

        public override void ChangedExternally() => RefreshRequested = true;
    }

    private sealed class TestMovie : Movie
    {
        public bool RefreshRequested { get; private set; }

        public override bool IsVisibleStandalone(User user) => true;

        public override void ChangedExternally() => RefreshRequested = true;
    }
}
