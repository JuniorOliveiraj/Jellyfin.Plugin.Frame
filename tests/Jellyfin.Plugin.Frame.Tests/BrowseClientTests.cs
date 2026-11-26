using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class BrowseClientTests
{
    private const string SearchJson = """
        {"items":[{"externalId":"123","name":"Filme","year":2024,"isSeries":false},
        {"externalId":"456","name":"Série","year":2024,"isSeries":true}],"page":2,"pageSize":2,"totalCount":2}
        """;

    [Fact]
    public void SearchAndImportUseThePanelJsonContractWithoutACamelCasePolicy()
    {
        var payload = new FrameSearchPage([new("123", "Oppenheimer", 2023)], true);
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var film = response.RootElement.GetProperty("items")[0];
        Assert.Equal("123", film.GetProperty("externalId").GetString());
        Assert.Equal("Oppenheimer", film.GetProperty("name").GetString());
        Assert.Equal(2023, film.GetProperty("year").GetInt32());
        Assert.False(film.GetProperty("isSeries").GetBoolean());
        Assert.True(response.RootElement.GetProperty("canPageForward").GetBoolean());

        var request = JsonSerializer.Deserialize<FrameImportRequest>(
            """{"term":"oppenheimer","page":1,"externalId":"123"}""");
        Assert.Equal(new FrameImportRequest("oppenheimer", 1, "123"), request);
    }


    [Fact]
    public async Task SearchesOnePageAndReturnsMoviesAndSeries()
    {
        using var handler = new FakeApi((request, _) =>
        {
            Assert.Equal("/api/browse/search/Filme%20%26%20A%C3%A7%C3%A3o?page=2", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return Json(SearchJson);
        });
        using var http = new HttpClient(handler);
        var page = await new BrowseClient(http, new Uri("https://example.org/api/"), "test-token")
            .Search("Filme & Ação", 2, default);
        Assert.Collection(page.Items,
            film => { Assert.Equal("123", film.ExternalId); Assert.False(film.IsSeries); },
            series => { Assert.Equal("456", series.ExternalId); Assert.True(series.IsSeries); });
        Assert.True(page.CanPageForward);
    }

    [Fact]
    public async Task WebResultsUseTheSearchPosterWithoutRequestingTheMovieDetails()
    {
        using var handler = new FakeApi((request, _) =>
        {
            Assert.Equal("/browse/search/Chefinho?page=1", request.RequestUri!.PathAndQuery);
            return Json("""
                {"items":[
                  {"externalId":"123","name":"O Poderoso Chefinho","year":2017,
                   "images":{"logoBigUrl":"https://image.tmdb.org/t/p/w500/poster.jpg",
                             "backdropUrl":"https://image.tmdb.org/t/p/w1280/backdrop.jpg"}},
                  {"externalId":"456","name":"Outro filme","images":{"logoBigUrl":"http://image.tmdb.org/poster.jpg",
                   "backdropUrl":"https://images.example.org/backdrop.jpg"}},
                  {"externalId":"789","name":"Sem capa autorizada","images":{"posterUrl":"https://unlisted.example.org/other.jpg"}}
                ]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new BrowseClient(http, new Uri("https://example.org/"), "test-token");
        var hosts = SyncCatalogTask.ReadMediaAllowlist("image.tmdb.org, images.example.org");

        var page = await client.Search("Chefinho", 1, default, hosts);

        Assert.Collection(page.Items,
            film => Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", film.PosterUrl),
            film => Assert.Equal("https://images.example.org/backdrop.jpg", film.PosterUrl),
            film => Assert.Null(film.PosterUrl));
        Assert.Equal(1, handler.Calls);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.Equal(page.Items[0].PosterUrl, json.RootElement.GetProperty("items")[0].GetProperty("posterUrl").GetString());
    }

    [Fact]
    public void WebPageSkipsLocalMoviesAndSeriesWithoutLosingItsUpstreamIdentity()
    {
        var page = new FrameSearchPage([
            new("100", "Filme da biblioteca", 2025),
            new("200", "Série", 2026, true),
            new("300", "Filme do Frame", 2026)
        ], true);

        var webPage = page.ForWeb(id => id == "100");
        Assert.Equal("300", Assert.Single(webPage.Items).ExternalId);
        Assert.True(webPage.CanPageForward);
        Assert.NotNull(webPage.PageKey);

        var repeated = (page with { Items = page.Items.Reverse().ToArray() }).ForWeb(_ => true);
        Assert.Empty(repeated.Items);
        Assert.True(repeated.CanPageForward);
        Assert.Equal(webPage.PageKey, repeated.PageKey);
        Assert.NotEqual(webPage.PageKey, new FrameSearchPage([new("401", "Outro", 2026)], true)
            .ForWeb(_ => false).PageKey);
    }

    [Fact]
    public async Task AcceptsPagesBeyondOneHundredAndStopsAtTheEmptyPage()
    {
        using var handler = new FakeApi((request, _) => request.RequestUri!.Query switch
        {
            "?page=101" => Json(SearchJson),
            "?page=102" => Json("""{"items":[]}"""),
            _ => throw new InvalidOperationException("Página inesperada.")
        });
        using var http = new HttpClient(handler);
        var client = new BrowseClient(http, new Uri("https://example.org/"), "test-token");

        Assert.True((await client.Search("Filme", 101, default)).CanPageForward);
        var last = await client.Search("Filme", 102, default);
        Assert.False(last.CanPageForward);
        Assert.Empty(last.Items);
    }

    [Fact]
    public async Task RepeatsSearchOnceAfterCacheMissAndUsesStableId()
    {
        using var handler = new FakeApi((request, call) =>
        {
            if (call == 2)
            {
                Assert.Equal("/browse/search/Filme?page=2", request.RequestUri!.PathAndQuery);
                return Json(SearchJson);
            }

            Assert.Equal("/browse/titles/123", request.RequestUri!.AbsolutePath);
            return call == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("""
                    {"isSeries":false,"movie":{"externalId":"123","name":"Filme","year":2024,
                    "genreNames":["Ação"],"videoUrl":"https://media.example.org/test.mp4"}}
                    """);
        });
        using var http = new HttpClient(handler);
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);
        var selected = new FrameImportRequest("Filme", 2, "123");
        var movie = Assert.IsType<FrameItem>(await client.GetTitleForImport(selected, default));
        Assert.Equal("Ação", Assert.Single(movie.Genres!));
        Assert.Equal("https://media.example.org/test.mp4", movie.VideoUrl);
        Assert.Equal(movie.Id, (await client.GetTitleForImport(selected, default))?.Id);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task ImportsOnlyTheFirstAvailableRegularEpisodeFromSeriesDetail()
    {
        using var handler = new FakeApi((request, _) =>
        {
            Assert.Equal("/browse/titles/456", request.RequestUri!.AbsolutePath);
            return Json("""
                {"isSeries":true,"series":{"externalId":"456","name":"Série de teste","year":2024,
                 "seasons":[
                   {"number":2,"episodes":[{"number":1,"name":"Outra temporada","videoUrl":"https://media.example.org/201.mp4"}]},
                   {"number":0,"episodes":[{"number":1,"name":"Especial","videoUrl":"https://media.example.org/001.mp4"}]},
                   {"number":1,"episodes":[
                     {"number":3,"name":"Terceiro","videoUrl":"https://media.example.org/103.mp4"},
                     {"number":1,"name":"Sem vídeo"},
                     {"number":2,"name":"Segundo","videoUrl":"https://media.example.org/102.mp4","durationMinutes":50}
                   ]}
                 ]}}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new BrowseClient(http, new Uri("https://example.org/"), null);
        var selection = new FrameImportRequest("Série", 1, "456", IsSeries: true);
        var item = Assert.IsType<FrameItem>(await client.GetTitleForImport(selection, default));
        var season = Assert.Single(item.Seasons!);
        var episode = Assert.Single(season.Episodes!);
        Assert.Equal(1, season.Number);
        Assert.Equal(2, episode.Number);
        Assert.Equal(50, episode.DurationMinutes);
        var again = Assert.IsType<FrameItem>(await client.GetTitleForImport(selection, default));
        Assert.Equal(item.Id, again.Id);
        Assert.Equal(episode.Id, Assert.Single(Assert.Single(again.Seasons!).Episodes!).Id);
        Assert.Null(await client.GetTitleForImport(selection with { IsSeries = false }, default));

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new FrameConfiguration
            {
                SeriesPath = Path.Combine(root, "series"),
                MoviesPath = Path.Combine(root, "movies")
            };
            var allowed = SyncCatalogTask.ReadMediaAllowlist("media.example.org");
            await SyncCatalogTask.Materialize(item, config, allowed, default);
            await SyncCatalogTask.Materialize(again, config, allowed, default);
            var show = Assert.Single(Directory.GetDirectories(config.SeriesPath));
            Assert.True(File.Exists(Path.Combine(show, "tvshow.nfo")));
            var stream = Assert.Single(Directory.GetFiles(show, "*.strm", SearchOption.AllDirectories));
            Assert.Equal(Path.Combine(show, "Season 01", "S01E02.strm"), stream);
            Assert.Equal(episode.VideoUrl, (await File.ReadAllTextAsync(stream)).Trim());
            Assert.True(File.Exists(Path.ChangeExtension(stream, ".nfo")));
            var fullRequest = JsonSerializer.Deserialize<FrameImportRequest>(
                """{"term":"Série","page":1,"externalId":"456","isSeries":true,"allEpisodes":true}""");
            var full = Assert.IsType<FrameItem>(await client.GetTitleForImport(fullRequest!, default));
            Assert.Equal(again.Id, full.Id);

            var selected = SyncCatalogTask.SelectSeriesEpisodes(full, allowed, default);
            Assert.Equal(4, selected.Imported);
            Assert.Equal(1, selected.Pending);
            Assert.Empty(selected.Guidance);
            var completeSeries = full with { Seasons = selected.Seasons };
            await SyncCatalogTask.Materialize(completeSeries, config, allowed, default, preserveExistingNfo: true);
            Assert.False(await SyncCatalogTask.Materialize(completeSeries, config, allowed, default, preserveExistingNfo: true));
            Assert.Equal(show, Assert.Single(Directory.GetDirectories(config.SeriesPath)));
            Assert.Equal(4, Directory.GetFiles(show, "*.strm", SearchOption.AllDirectories).Length);
            Assert.True(File.Exists(stream));
            Assert.False(Directory.Exists(config.MoviesPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"posterPath":"/poster.jpg"}""", "https://image.tmdb.org/t/p/w500/poster.jpg")]
    [InlineData(HttpStatusCode.OK, """{"posterPath":"//evil.example/image.jpg"}""", null)]
    [InlineData(HttpStatusCode.NoContent, "", null)]
    [InlineData(HttpStatusCode.NotFound, "", null)]
    public async Task CachedPosterUsesOnlyDedicatedEndpointAndAcceptsMissingArtwork(
        HttpStatusCode status, string json, string? expected)
    {
        using var http = new HttpClient(new FakeApi((request, _) =>
        {
            Assert.Equal("/api/browse/titles/123/metadata/cached", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        }));
        var client = new BrowseClient(http, new Uri("https://example.org/api/"), "test-token");

        Assert.Equal(expected, await client.GetCachedPoster("123", default));
    }

    private sealed class FakeApi(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, ++Calls));
    }
}
