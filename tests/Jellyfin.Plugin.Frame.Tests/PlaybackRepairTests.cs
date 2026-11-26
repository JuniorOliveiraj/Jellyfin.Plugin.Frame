using System.Net;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class PlaybackRepairTests
{
    private static readonly Uri Origin = new("https://api.example.org/");
    private const string Old = "https://media.example.org/expired.mp4";
    private const string Fresh = "https://media.example.org/fresh.mp4";

    [Fact]
    public async Task RepairsAStoredMovieWithoutChangingItsPathOrNfo()
    {
        using var library = new LibraryFiles();
        var target = await library.AddMovie();
        var nfo = Path.Combine(target.Folder, "movie.nfo");
        var before = await File.ReadAllTextAsync(nfo);
        var notifications = new List<string>();
        using var api = new HttpClient(new Stub(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/browse/titles/123/playback/refresh", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer secret-for-api", request.Headers.Authorization?.ToString());
            return Sources(Fresh, null);
        }));
        using var media = new HttpClient(new Stub(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            return new HttpResponseMessage(request.RequestUri!.AbsoluteUri == Old ? HttpStatusCode.Gone : HttpStatusCode.PartialContent);
        }));

        var result = await Repair(target, api, media, notifications.Add);

        Assert.Equal(PlaybackRepairResult.Repaired, result);
        Assert.Equal(Fresh, (await File.ReadAllTextAsync(target.Path)).Trim());
        Assert.Equal(before, await File.ReadAllTextAsync(nfo));
        Assert.Equal(target.Path, Assert.Single(notifications));
    }

    [Fact]
    public async Task RepairsLegacyEpisodeByStableIdentityAndPicksAnAllowedBackup()
    {
        using var library = new LibraryFiles();
        var target = await library.AddEpisode();
        var paths = new List<string>();
        using var api = new HttpClient(new Stub(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            return request.Method == HttpMethod.Get
                ? Json("""
                    {"items":[
                        {"externalId":"987","name":"Catálogo","isSeries":true},
                        {"externalId":"123","name":"Catálogo","isSeries":true}
                    ]}
                    """)
                : Sources("https://unlisted.example.org/blocked.mp4", Fresh);
        }));
        using var media = new HttpClient(new Stub(request =>
        {
            Assert.Equal("media.example.org", request.RequestUri!.Host);
            return new HttpResponseMessage(request.RequestUri.AbsoluteUri == Old ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        }));

        var result = await Repair(target, api, media);

        Assert.Equal(PlaybackRepairResult.Repaired, result);
        Assert.Equal(new[] { "/browse/search/Cat%C3%A1logo?page=1", "/browse/titles/123/playback/refresh?season=0&episode=12" }, paths);
        Assert.True(File.Exists(Path.Combine(target.Folder, FramePlaybackIdentity.FileName)));
        Assert.Equal(Fresh, (await File.ReadAllTextAsync(target.Path)).Trim());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PreservesWorkingLinksAndInconclusiveResponses(HttpStatusCode status)
    {
        using var library = new LibraryFiles();
        var target = await library.AddMovie();
        using var api = new HttpClient(new Stub(_ => throw new InvalidOperationException("A API não deve ser consultada.")));
        using var media = new HttpClient(new Stub(_ => new HttpResponseMessage(status)));

        Assert.Equal(PlaybackRepairResult.Skipped, await Repair(target, api, media));
        Assert.Equal(Old, await File.ReadAllTextAsync(target.Path));
    }

    [Fact]
    public async Task SyncKeepsTheRepairedLinkWhenHomeStillReturnsTheExpiredOne()
    {
        using var library = new LibraryFiles();
        var target = await library.AddMovie();
        using var api = new HttpClient(new Stub(_ => Sources(Fresh, null)));
        using var media = new HttpClient(new Stub(request =>
            new HttpResponseMessage(request.RequestUri!.AbsoluteUri == Old ? HttpStatusCode.Gone : HttpStatusCode.OK)));

        Assert.Equal(PlaybackRepairResult.Repaired, await Repair(target, api, media));
        var config = new FrameConfiguration
        {
            MoviesPath = Path.GetDirectoryName(target.Folder)!,
            OnDemand = true,
            BrowseApiUrl = Origin.AbsoluteUri,
            MediaHosts = "media.example.org"
        };
        var movie = new FrameItem(target.FrameId, "Catálogo", "movie", null, null, null, null, null, Old, null,
            null, ExternalId: "123");
        var allowed = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        await SyncCatalogTask.Materialize(movie, config, allowed, default, preserveExistingNfo: true);

        Assert.Equal(Fresh, (await File.ReadAllTextAsync(target.Path)).Trim());

        const string newer = "https://media.example.org/newer.mp4";
        await SyncCatalogTask.Materialize(movie with { VideoUrl = newer }, config, allowed, default, preserveExistingNfo: true);

        Assert.Equal(newer, (await File.ReadAllTextAsync(target.Path)).Trim());
    }

    [Fact]
    public async Task DoesNotOverwriteAnImportThatChangedTheStrmDuringRenewal()
    {
        using var library = new LibraryFiles();
        var target = await library.AddMovie();
        using var api = new HttpClient(new Stub(_ =>
        {
            File.WriteAllText(target.Path, "https://media.example.org/newer-import.mp4");
            return Sources(Fresh, null);
        }));
        using var media = new HttpClient(new Stub(request =>
            new HttpResponseMessage(request.RequestUri!.AbsoluteUri == Old ? HttpStatusCode.NotFound : HttpStatusCode.OK)));

        Assert.Equal(PlaybackRepairResult.Skipped, await Repair(target, api, media,
            _ => throw new InvalidOperationException("A biblioteca não deve ser notificada.")));
        Assert.Equal("https://media.example.org/newer-import.mp4", await File.ReadAllTextAsync(target.Path));
    }

    [Fact]
    public async Task WarmsTheBrowseCacheAndRejectsAnHtmlReplacement()
    {
        using var library = new LibraryFiles();
        var target = await library.AddMovie();
        var apiCalls = 0;
        using var api = new HttpClient(new Stub(request =>
        {
            apiCalls++;
            if (request.Method == HttpMethod.Get)
                return Json("""{"items":[{"externalId":"123","name":"Catálogo"}]}""");
            return apiCalls == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Sources(Fresh, null);
        }));
        using var media = new HttpClient(new Stub(request => request.RequestUri!.AbsoluteUri == Old
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Login</html>", Encoding.UTF8, "text/html") }));

        Assert.Equal(PlaybackRepairResult.Pending, await Repair(target, api, media));
        Assert.Equal(3, apiCalls);
        Assert.Equal(Old, await File.ReadAllTextAsync(target.Path));
    }

    private static Task<PlaybackRepairResult> Repair(FramePlaybackTarget target, HttpClient api, HttpClient media,
        Action<string>? notify = null) =>
        FramePlaybackRepair.Repair(target, Origin, new BrowseClient(api, Origin, "secret-for-api"), media,
            SyncCatalogTask.ReadMediaAllowlist("media.example.org"), () => true, notify ?? (_ => { }), default);

    private static HttpResponseMessage Sources(string? primary, string? backup) =>
        Json(System.Text.Json.JsonSerializer.Serialize(new { videoUrl = primary, backupVideoUrl = backup }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response(request));
    }

    private sealed class LibraryFiles : IDisposable
    {
        private DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("frame-repair-");

        internal async Task<FramePlaybackTarget> AddMovie()
        {
            var id = BrowseClient.MovieId("123");
            var folder = FrameWebItemIndex.MediaFolder(Root.FullName, id);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "movie.strm"), Old);
            await File.WriteAllTextAsync(Path.Combine(folder, "movie.nfo"), "<movie><title>Catálogo</title></movie>");
            await SyncCatalogTask.WritePlaybackIdentity(folder, new(Origin.AbsoluteUri, "123", "Catálogo"), default);
            Directory.CreateDirectory(Path.Combine(Root.FullName, "Outro"));
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "Outro", "movie.strm"), Old);
            return Assert.Single(FramePlaybackRepair.Targets(Root.FullName, false));
        }

        internal async Task<FramePlaybackTarget> AddEpisode()
        {
            var id = BrowseClient.SeriesId("123");
            var folder = FrameWebItemIndex.MediaFolder(Root.FullName, id);
            var season = Directory.CreateDirectory(Path.Combine(folder, "Season 00")).FullName;
            await File.WriteAllTextAsync(Path.Combine(season, "S00E12.strm"), Old);
            await File.WriteAllTextAsync(Path.Combine(season, "S01E14.strm"), Old);
            await File.WriteAllTextAsync(Path.Combine(folder, "tvshow.nfo"),
                $"<tvshow><title>Catálogo</title><uniqueid type=\"frame\">{id}</uniqueid></tvshow>");
            return Assert.Single(FramePlaybackRepair.Targets(Root.FullName, true));
        }

        public void Dispose() => Root.Delete(recursive: true);
    }
}
