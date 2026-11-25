using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class HomeSyncTests
{
    [Fact]
    public async Task ImportsDistinctHomeMoviesWithPersistedFallbackAndPreservesPriorTitles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new FrameConfiguration { MoviesPath = root, MediaHosts = "media.example.org" };
        var calls = new List<string>();
        var notified = new List<string>();
        var url = "https://media.example.org/home.mp4";
        using var http = new HttpClient(new FakeHomeApi(request =>
        {
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return path switch
            {
                "/home" => Json("""
                    {"partial":true,"hero":{"externalId":"111","name":"Destaque","isSeries":false},
                     "rows":[{"items":[
                      {"item":{"externalId":"111","name":"Destaque","isSeries":false}},
                      {"item":{"externalId":"222","name":"Filme persistido","isSeries":false},
                       "persistedTitleId":"3cfc9f17-9393-47cb-b12c-d75b6536eb19"},
                      {"item":{"externalId":"333","name":"Série","isSeries":true}}
                     ]}]}
                    """),
                "/browse/titles/111" => Json(JsonSerializer.Serialize(new
                {
                    isSeries = false, movie = new { externalId = "111", name = "Destaque", videoUrl = url }
                })),
                "/catalog/titles/3cfc9f17-9393-47cb-b12c-d75b6536eb19" => Json("""
                    {"id":"3cfc9f17-9393-47cb-b12c-d75b6536eb19","externalId":"222","type":"Movie",
                     "name":"Filme persistido","videoUrl":"https://media.example.org/saved.mp4"}
                    """),
                _ => new(HttpStatusCode.NotFound)
            };
        }));

        try
        {
            var client = new BrowseClient(http, new Uri("https://frame.example/"), "test-token");
            var manual = Assert.IsType<FrameItem>(await client.GetTitleForImport(new("Destaque", 1, "111"), default));
            var outside = manual with { Id = Guid.NewGuid(), Name = "Filme fora da home" };
            var allowed = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
            await SyncCatalogTask.Materialize(manual with { Description = "Metadado existente" }, config, allowed, default);
            await SyncCatalogTask.Materialize(outside, config, allowed, default);
            var copied = Path.Combine(root, "Frame-" + manual.Id);
            var previousNfo = await File.ReadAllTextAsync(Path.Combine(copied, "movie.nfo"));
            File.SetLastWriteTimeUtc(Path.Combine(copied, "movie.strm"), DateTime.UtcNow.AddMinutes(-1));

            calls.Clear();
            url = "https://media.example.org/refreshed.mp4";
            await SyncHomeMoviesTask.ImportHome(client, config, () => true, notified.Add,
                NullLogger.Instance, new Progress<double>(), default);
            Assert.Equal(1, calls.Count(path => path == "/browse/titles/111"));
            Assert.DoesNotContain("/browse/titles/333", calls);
            Assert.Equal(3, Directory.GetDirectories(root).Length);
            Assert.Equal(url, (await File.ReadAllTextAsync(Path.Combine(copied, "movie.strm"))).Trim());
            Assert.Equal(previousNfo, await File.ReadAllTextAsync(Path.Combine(copied, "movie.nfo")));
            Assert.Equal(2, notified.Count);

            notified.Clear();
            await SyncHomeMoviesTask.ImportHome(client, config, () => true, notified.Add,
                NullLogger.Instance, new Progress<double>(), default);
            Assert.Equal(3, Directory.GetDirectories(root).Length);
            Assert.Empty(notified);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HomeSearchesAgainWhenAListedMovieHasNoCachedDetail()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new FakeHomeApi(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            return path switch
            {
                "/browse/titles/123" when requests.Count == 1 => new(HttpStatusCode.NotFound),
                "/browse/search/Filme?page=1" => Json("""
                    {"items":[{"externalId":"123","name":"Filme","year":2024,"isSeries":false}]}
                    """),
                "/browse/titles/123" => Json("""
                    {"isSeries":false,"movie":{"externalId":"123","name":"Filme",
                     "videoUrl":"https://media.example.org/example.mp4"}}
                    """),
                _ => throw new InvalidOperationException($"Requisição inesperada: {path}")
            };
        }));
        var client = new BrowseClient(http, new Uri("https://frame.example/"), "test-token");
        var movie = await client.GetHomeMovie(new(new("123", "Filme", 2024, false), null), default);
        Assert.Equal("https://media.example.org/example.mp4", movie?.VideoUrl);
        Assert.Equal(["/browse/titles/123", "/browse/search/Filme?page=1", "/browse/titles/123"], requests);
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHomeApi(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }
}
