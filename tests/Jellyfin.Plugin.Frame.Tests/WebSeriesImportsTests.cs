using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class WebSeriesImportsTests
{
    private const string SeriesId = "543";
    private const string Detail = """
        {
          "isSeries": true,
          "series": {
            "externalId": "543",
            "name": "Série do Frame",
            "year": 2024,
            "seasons": [
              {"number": 2, "episodes": [
                {"number": 5, "name": "Quinto", "videoUrl": "https://media.example.org/s02e05.mp4"}]},
              {"number": 1, "episodes": [
                {"number": 1, "name": "Bloqueado", "videoUrl": "http://blocked.example.org/s01e01.mp4"},
                {"number": 2, "name": "Segundo", "videoUrl": "https://media.example.org/s01e02.mp4"},
                {"number": 3, "name": "Terceiro", "videoUrl": "https://media.example.org/s01e03.mp4"}]}
            ]
          }
        }
        """;

    [Fact]
    public async Task RegistersFirstAllowedEpisodeBeforeImportingOtherSeasonsAndCanBeRetried()
    {
        using var temporary = new SeriesDirectory();
        var root = temporary.Root;
        var config = new FrameConfiguration { SeriesPath = root.FullName, MediaHosts = "media.example.org" };
        var folder = FrameWebItemIndex.MediaFolder(config.SeriesPath, BrowseClient.SeriesId(SeriesId));
        var initialEpisode = Path.Combine("Season 01", "S01E02.strm");
        var expected = new[]
        {
            initialEpisode,
            Path.Combine("Season 01", "S01E03.strm"),
            Path.Combine("Season 02", "S02E05.strm")
        };
        var indexCalls = 0;
        var notifications = new List<string[]>();

        using var http = new HttpClient(new SeriesApi());
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);
        var selection = new FrameImportRequest("série", 1, SeriesId, IsSeries: true);

        string[] Files() => Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path)).Order(StringComparer.Ordinal).ToArray();
        void Index(FrameItem item, string path, CancellationToken _)
        {
            indexCalls++;
            Assert.Equal(folder, path);
            Assert.True(File.Exists(Path.Combine(path, "tvshow.nfo")));
            Assert.Equal(2, Assert.Single(Assert.Single(item.Seasons!).Episodes!).Number);
            if (indexCalls == 1)
                Assert.Equal([initialEpisode], Files());
        }
        void Notify(string path)
        {
            Assert.Equal(folder, path);
            notifications.Add(Files());
        }

        Assert.Null(await FrameWebSeriesImports.ImportSeries(client, config, selection, () => true,
            Index, Notify, NullLogger.Instance, default));
        Assert.Collection(notifications, first => Assert.Equal([initialEpisode], first), last => Assert.Equal(expected, last));

        Assert.Null(await FrameWebSeriesImports.ImportSeries(client, config, selection, () => true,
            Index, Notify, NullLogger.Instance, default));
        Assert.Equal(2, indexCalls);
        Assert.Equal(expected, Files());
    }

    [Fact]
    public async Task DoesNotWriteRemainingEpisodesWhenConfigurationChangesAfterIndexing()
    {
        using var temporary = new SeriesDirectory();
        var root = temporary.Root;
        var config = new FrameConfiguration { SeriesPath = root.FullName, MediaHosts = "media.example.org" };
        using var handler = new SeriesApi();
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);
        var stillConfigured = true;

        var error = await FrameWebSeriesImports.ImportSeries(client, config,
            new("série", 1, SeriesId, IsSeries: true), () => stillConfigured,
            (_, _, _) => stillConfigured = false, _ => { }, NullLogger.Instance, default);

        Assert.NotNull(error);
        Assert.Equal(1, handler.Calls);
        Assert.Single(root.EnumerateFiles("*.strm", SearchOption.AllDirectories));
    }

    [Fact]
    public void ConcurrentRequestsForTheSameSeriesShareABoundedQueueEntry()
    {
        using var queue = new FrameWebSeriesImports(null!, null!, null!, null!, null!,
            NullLogger<FrameWebSeriesImports>.Instance);
        var config = new FrameConfiguration { SeriesPath = Path.GetTempPath() };
        var selection = new FrameImportRequest("série", 1, SeriesId, IsSeries: true);

        Assert.Equal(FrameWebSeriesImports.QueueResult.NoPending,
            queue.Enqueue(config, selection with { RetryPending = true }));
        Parallel.For(0, 32, _ => Assert.Equal(FrameWebSeriesImports.QueueResult.Accepted, queue.Enqueue(config, selection)));
        Assert.Equal("queued", queue.Progress(new FrameConfiguration
            { SeriesPath = config.SeriesPath, MediaHosts = "example.org" }, selection.ExternalId)?.Status);
        for (var i = 0; i < 15; i++)
            Assert.Equal(FrameWebSeriesImports.QueueResult.Accepted,
                queue.Enqueue(config, selection with { ExternalId = $"other-{i}" }));

        Assert.Equal(FrameWebSeriesImports.QueueResult.Busy, queue.Enqueue(config, selection with { ExternalId = "overflow" }));
        Assert.Equal(FrameWebSeriesImports.QueueResult.Accepted, queue.Enqueue(config, selection));
    }

    [Fact]
    public async Task RejectsSeriesWithNoAllowedVideoBeforeCreatingFolders()
    {
        using var temporary = new SeriesDirectory();
        var config = new FrameConfiguration { SeriesPath = temporary.Root.FullName };
        using var http = new HttpClient(new SeriesApi());
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);

        var error = await FrameWebSeriesImports.ImportSeries(client, config,
            new("série", 1, SeriesId, IsSeries: true), () => true,
            (_, _, _) => throw new InvalidOperationException("A série não deve ser indexada."),
            _ => throw new InvalidOperationException("A biblioteca não deve ser notificada."), NullLogger.Instance, default);

        Assert.NotNull(error);
        Assert.Contains("http://blocked.example.org", error);
        Assert.Empty(temporary.Root.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task ReportsSavedEpisodesAndRetriesOnlyPendingWithFreshSearch()
    {
        using var temporary = new SeriesDirectory();
        var config = new FrameConfiguration { SeriesPath = temporary.Root.FullName, MediaHosts = "media.example.org" };
        using var handler = new SeriesApi();
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);
        var selection = new FrameImportRequest("série", 1, SeriesId, IsSeries: true);
        var counts = new List<int>();
        var first = new SeriesImportProgress(new("queued", 0, 0, 0), publish: progress => counts.Add(progress.Imported));

        var failure = await FrameWebSeriesImports.ImportSeries(client, config, selection, () => true,
            (_, _, _) => Assert.Equal(1, first.Current.Imported), _ => { }, NullLogger.Instance, default, first);
        first.Finish(failure);

        Assert.Null(failure);
        Assert.Equal(new[] { 0, 1, 2, 3, 3 }, counts);
        Assert.Equal("partial", first.Current.Status);
        Assert.Equal(4, first.Current.Total);
        Assert.Equal(1, first.Current.Pending);
        Assert.Contains("http://blocked.example.org", first.Current.Message);
        var folder = FrameWebItemIndex.MediaFolder(config.SeriesPath, BrowseClient.SeriesId(SeriesId));
        var savedPath = Path.Combine(folder, "Season 01", "S01E02.strm");
        Assert.Equal("https://media.example.org/s01e02.mp4", (await File.ReadAllTextAsync(savedPath)).Trim());

        handler.Response = Detail.Replace("http://blocked.example.org/s01e01.mp4", "https://media.example.org/s01e01.mp4")
            .Replace("https://media.example.org/s01e02.mp4", "https://media.example.org/replacement.mp4")
            .Replace("""{"number": 3, "name": "Terceiro", "videoUrl": "https://media.example.org/s01e03.mp4"}""",
                """
                {"number": 3, "name": "Terceiro", "videoUrl": "https://media.example.org/s01e03.mp4"},
                {"number": 4, "name": "Novo", "videoUrl": "https://media.example.org/s01e04.mp4"}
                """);
        var retry = new SeriesImportProgress(first.Current with { Status = "queued" }, first.Remaining);
        failure = await FrameWebSeriesImports.ImportSeries(client, config, selection with { RetryPending = true }, () => true,
            (_, _, _) => Assert.Equal(4, retry.Current.Imported), _ => { }, NullLogger.Instance, default, retry);
        retry.Finish(failure);

        Assert.Null(failure);
        Assert.Equal("completed", retry.Current.Status);
        Assert.Equal(4, retry.Current.Imported);
        Assert.Empty(retry.Remaining);
        Assert.Equal(1, handler.SearchCalls);
        Assert.Equal("https://media.example.org/s01e02.mp4", (await File.ReadAllTextAsync(savedPath)).Trim());
        Assert.True(File.Exists(Path.Combine(folder, "Season 01", "S01E01.strm")));
        Assert.False(File.Exists(Path.Combine(folder, "Season 01", "S01E04.strm")));
    }

    private sealed class SeriesDirectory : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("frame-series-");

        public void Dispose() => Root.Delete(recursive: true);
    }

    private sealed class SeriesApi : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int SearchCalls { get; private set; }
        internal string Response { get; set; } = Detail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/browse/search/", StringComparison.Ordinal))
            {
                SearchCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[{"externalId":"543","name":"Série do Frame","isSeries":true}]}""",
                        Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(path switch
            {
                "/browse/titles/543" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Response, Encoding.UTF8, "application/json")
                },
                "/browse/titles/543/metadata" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException($"Rota inesperada: {path}")
            });
        }
    }
}
