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

    [Fact]
    public async Task FirstImportEnrichesAWholeSeriesWithoutACatalogYear()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        const string posterUrl = "https://image.tmdb.org/t/p/w500/series.jpg";
        var poster = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nX0AAAAASUVORK5CYII=");
        using var api = new HttpClient(new FakeHttp(request =>
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/browse/titles/601" => """
                    {"isSeries":true,"series":{"externalId":"601","name":"Série sem ano","year":0,"seasons":[
                        {"number":1,"episodes":[{"number":1,"videoUrl":"https://media.example.org/pilot.mp4"}]},
                        {"number":2,"episodes":[{"number":1,"videoUrl":"https://media.example.org/next.mp4"}]}
                    ]}}
                    """,
                "/browse/titles/601/metadata" => """
                    {"id":42,"originalName":"Original","releaseDate":"2008-01-20","posterPath":"/series.jpg",
                     "overview":"Sinopse do backend"}
                    """,
                _ => throw new InvalidOperationException("Rota inesperada.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
        using var images = new HttpClient(new FakeHttp(request =>
        {
            Assert.Equal(posterUrl, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var content = new ByteArrayContent(poster);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new(HttpStatusCode.OK) { Content = content };
        }));

        try
        {
            var client = new BrowseClient(api, new Uri("https://frame.example/"), "test-token");
            var show = Assert.IsType<FrameItem>(await client.GetTitleForImport(new("Série sem ano", 1, "601", true, true), default));
            Assert.Null(show.Year);
            var metadata = Assert.IsType<BrowseTitleMetadata>(await client.GetTitleMetadata("601", default));
            var config = new FrameConfiguration { SeriesPath = root };
            var hosts = SyncCatalogTask.ReadMediaAllowlist("media.example.org");
            var allowed = SyncCatalogTask.SelectSeriesEpisodes(show, hosts, default);
            var enriched = metadata.ApplyTo(show) with { Seasons = allowed.Seasons, Images = null };
            Assert.True(await SyncCatalogTask.Materialize(enriched, config, hosts, default, preserveExistingNfo: true));
            var folder = Assert.Single(Directory.GetDirectories(root));
            var artworkHosts = new HashSet<string>(hosts, hosts.Comparer) { BrowseTitleMetadata.ArtworkHost };
            Assert.True(await SyncCatalogTask.ImportArtwork(folder, metadata.Artwork(), artworkHosts, default, imageClient: images));

            var nfo = XElement.Load(Path.Combine(folder, "tvshow.nfo"));
            Assert.Equal(42, (int?)nfo.Element("tmdbid"));
            Assert.Equal(2008, (int?)nfo.Element("year"));
            Assert.Equal("2008-01-20", (string?)nfo.Element("premiered"));
            Assert.Equal("Sinopse do backend", (string?)nfo.Element("plot"));
            Assert.Equal(poster, await File.ReadAllBytesAsync(Path.Combine(folder, "poster.png")));
            Assert.Equal("https://media.example.org/next.mp4",
                (await File.ReadAllTextAsync(Path.Combine(folder, "Season 02", "S02E01.strm"))).Trim());
            Assert.False(await SyncCatalogTask.Materialize(enriched, config, hosts, default, preserveExistingNfo: true));
            Assert.Equal(folder, Assert.Single(Directory.GetDirectories(root)));
            Assert.Equal(2, Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories).Length);
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

    [Fact]
    public async Task FetchingAMissingBackdropKeepsAnExistingPoster()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        const string backdrop = "https://media.example.org/backdrop.png";
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nX0AAAAASUVORK5CYII=");
        using var images = new HttpClient(new FakeHttp(request =>
        {
            Assert.Equal(backdrop, request.RequestUri!.AbsoluteUri);
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new(HttpStatusCode.OK) { Content = content };
        }));
        Directory.CreateDirectory(root);
        try
        {
            var poster = Path.Combine(root, "poster.png");
            await File.WriteAllTextAsync(poster, "Capa existente");
            var hosts = SyncCatalogTask.ReadMediaAllowlist("media.example.org");

            await SyncCatalogTask.ImportArtwork(root, null, hosts, default,
                new(null, backdrop, null), images, includePoster: false);

            Assert.Equal("Capa existente", await File.ReadAllTextAsync(poster));
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(root, "fanart.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("<lockdata>true</lockdata>")]
    [InlineData("<uniqueid type=\"tmdb\">99</uniqueid>")]
    public async Task ReimportPreservesLockedMetadataOrAManualTmdbChoice(string manualMetadata)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var show = new FrameItem(Guid.NewGuid(), "Série", "series", null, null, null, null, null, null,
            null, [new(1, [new(Guid.NewGuid(), 1, "Piloto", null, null, "https://media.example.org/video.mp4")])]);
        var config = new FrameConfiguration { SeriesPath = root };
        var hosts = SyncCatalogTask.ReadMediaAllowlist("media.example.org");
        try
        {
            await SyncCatalogTask.Materialize(show, config, hosts, default);
            var folder = Assert.Single(Directory.GetDirectories(root));
            var nfo = Path.Combine(folder, "tvshow.nfo");
            var manual = $"<tvshow><title>Série</title><uniqueid type=\"frame\">{show.Id}</uniqueid>{manualMetadata}</tvshow>";
            await File.WriteAllTextAsync(nfo, manual);
            var updated = show with { TitleMetadata = new(42, "Nome original", new DateOnly(2025, 2, 21), null, null) };

            Assert.False(await SyncCatalogTask.Materialize(updated, config, hosts, default, preserveExistingNfo: true));
            Assert.Equal(manual, await File.ReadAllTextAsync(nfo));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
