using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class MovieMetadataTests
{
    [Fact]
    public async Task ReimportAddsMetadataAndArtworkToTheExistingMovie()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        const string baseUrl = "https://frame.example/";
        const string fallbackPoster = "https://media.example.org/cover.png";
        const string poster = "https://image.tmdb.org/t/p/w500/movie.jpg";
        const string backdrop = "https://image.tmdb.org/t/p/w1280/backdrop.jpg";
        var movie = new FrameItem(Guid.NewGuid(), "Filme - 2023", "movie", 2023, 8.0, "Sinopse original", ["Drama"],
            null, "https://media.example.org/test.mp4", new(null, null, null, fallbackPoster), null);
        var config = new FrameConfiguration { MoviesPath = root };
        var allowed = SyncCatalogTask.ReadMediaAllowlist("media.example.org");
        var artHosts = new HashSet<string>(allowed, allowed.Comparer) { BrowseTitleMetadata.ArtworkHost };
        var imageRequests = new List<string>();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nX0AAAAASUVORK5CYII=");
        using var api = new HttpClient(new FakeHttp(request =>
        {
            Assert.Equal(baseUrl + "browse/titles/123/metadata", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":42,"originalName":"Nome original","posterPath":"/movie.jpg","backdropPath":"/backdrop.jpg",
                     "releaseDate":"2023-07-19","runtimeMinutes":180,"directors":["Diretora"],
                     "cast":[{"name":"Atriz","character":"Personagem"}]}
                    """, Encoding.UTF8, "application/json")
            };
        }));
        using var images = new HttpClient(new FakeHttp(request =>
        {
            Assert.Null(request.Headers.Authorization);
            var url = request.RequestUri!.AbsoluteUri;
            imageRequests.Add(url);
            if (url == poster)
                return new(HttpStatusCode.NotFound);
            Assert.Contains(url, new[] { backdrop, fallbackPoster });
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new(HttpStatusCode.OK) { Content = content };
        }));

        try
        {
            await SyncCatalogTask.Materialize(movie with { Images = null }, config, allowed, default);
            var folder = Assert.Single(Directory.GetDirectories(root));
            var originalStrm = await File.ReadAllTextAsync(Path.Combine(folder, "movie.strm"));
            var metadata = Assert.IsType<BrowseTitleMetadata>(await new BrowseClient(api, new Uri(baseUrl), "test-token")
                .GetTitleMetadata("123", default));
            var updated = metadata.ApplyTo(movie);
            await SyncCatalogTask.Materialize(updated with { Images = null }, config, allowed, default);
            Assert.True(await SyncCatalogTask.ImportArtwork(folder, metadata.Artwork(), artHosts, default, updated.Images, images));

            Assert.Equal(movie.Id, updated.Id);
            Assert.Equal(folder, Assert.Single(Directory.GetDirectories(root)));
            Assert.Equal(originalStrm, await File.ReadAllTextAsync(Path.Combine(folder, "movie.strm")));
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(folder, "poster.png")));
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(folder, "fanart.png")));
            Assert.Equal([poster, fallbackPoster, backdrop], imageRequests);

            var nfo = XElement.Load(Path.Combine(folder, "movie.nfo"));
            Assert.Equal("Sinopse original", (string?)nfo.Element("plot"));
            Assert.Equal("Nome original", (string?)nfo.Element("originaltitle"));
            Assert.Equal("2023-07-19", (string?)nfo.Element("premiered"));
            Assert.Equal(42, (int?)nfo.Element("tmdbid"));
            Assert.Equal(180, (int?)nfo.Element("runtime"));
            Assert.Equal("Diretora", (string?)nfo.Element("director"));
            Assert.Equal("Atriz", (string?)nfo.Element("actor")?.Element("name"));
            Assert.Equal("Personagem", (string?)nfo.Element("actor")?.Element("role"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task MissingMetadataIsOptional(HttpStatusCode status)
    {
        using var http = new HttpClient(new FakeHttp(_ => new(status)));
        Assert.Null(await new BrowseClient(http, new Uri("https://frame.example/"), "test")
            .GetTitleMetadata("123", default));
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
