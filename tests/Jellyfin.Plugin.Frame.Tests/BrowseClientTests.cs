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
        Assert.True(response.RootElement.GetProperty("canPageForward").GetBoolean());

        var request = JsonSerializer.Deserialize<FrameImportRequest>(
            """{"term":"oppenheimer","page":1,"externalId":"123"}""");
        Assert.Equal(new FrameImportRequest("oppenheimer", 1, "123"), request);
    }


    [Fact]
    public async Task SearchesOnePageAndOnlyReturnsMovies()
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
        Assert.Equal("123", Assert.Single(page.Items).ExternalId);
        Assert.True(page.CanPageForward);
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
        var movie = Assert.IsType<FrameItem>(await client.GetMovie(selected, default));
        Assert.Equal("Ação", Assert.Single(movie.Genres!));
        Assert.Equal("https://media.example.org/test.mp4", movie.VideoUrl);
        Assert.Equal(movie.Id, (await client.GetMovie(selected, default))?.Id);
        Assert.Equal(4, handler.Calls);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeApi(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, ++Calls));
    }
}
