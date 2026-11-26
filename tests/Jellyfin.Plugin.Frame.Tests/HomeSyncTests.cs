using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
            var report = new HomeSyncReport();
            await SyncHomeMoviesTask.ImportHome(client, config, () => true, notified.Add,
                NullLogger.Instance, new Progress<double>(), default, report);
            Assert.True(report.PartialHome);
            Assert.Equal(2, report.Movies);
            Assert.Equal(1, report.NewMovies);
            Assert.Equal(0, report.PendingTitles);
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
        var movie = await client.GetHomeTitle(new(new("123", "Filme", 2024, false), null), default);
        Assert.Equal("https://media.example.org/example.mp4", movie?.VideoUrl);
        Assert.Equal(["/browse/titles/123", "/browse/search/Filme?page=1", "/browse/titles/123"], requests);
    }
    [Fact]
    public async Task HomeSeriesAddsAllSeasonsAndKeepsExistingPilotEpisodeAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var moviesPath = Path.Combine(root, "movies");
        var seriesPath = Path.Combine(root, "series");
        var config = new FrameConfiguration { MoviesPath = moviesPath, SeriesPath = seriesPath, MediaHosts = "media.example.org" };
        const string detail = """
            {"isSeries":true,"series":{"externalId":"601","name":"Série da home","year":2024,
             "seasons":[
               {"number":0,"episodes":[{"number":1,"name":"Especial","videoUrl":"https://media.example.org/special.mp4"}]},
               {"number":1,"episodes":[
                 {"number":1,"name":"Piloto","videoUrl":"https://media.example.org/101.mp4"},
                 {"number":2,"name":"Segundo","backupVideoUrl":"http://video.example.org:8080/102.mp4"}
               ]},
               {"number":2,"episodes":[{"number":1,"name":"Terceiro","videoUrl":"https://media.example.org/201.mp4"}]}
             ]}}
            """;
        using var http = new HttpClient(new FakeHomeApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/home" => Json("""
                {"hero":{"externalId":"601","name":"Série da home","isSeries":true},
                 "rows":[{"items":[{"item":{"externalId":"601","name":"Série da home","isSeries":true}},
                 {"item":{"externalId":"111","name":"Filme","isSeries":false}}]}]}
                """),
            "/browse/titles/601" => Json(detail),
            "/browse/titles/601/metadata" => new(HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException("Apenas o detalhe da série deve ser consultado.")
        }));
        var client = new BrowseClient(http, new Uri("https://example.org/"), "test-token");
        var notified = new List<string>();
        try
        {
            var pilot = Assert.IsType<FrameItem>(await client.GetTitleForImport(new("Série da home", 1, "601", true), default));
            await SyncCatalogTask.Materialize(pilot, config, SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts), default);
            var folder = Assert.Single(Directory.GetDirectories(seriesPath));
            var nfo = Path.Combine(folder, "tvshow.nfo");
            const string identified = "<tvshow><title>Série identificada</title><tmdbid>42</tmdbid></tvshow>";
            await File.WriteAllTextAsync(nfo, identified);

            Task Sync(HomeSyncReport? result = null) => SyncHomeSeriesTask.ImportHome(client, config, () => true,
                notified.Add, NullLogger.Instance, new Progress<double>(), default, result);
            var report = new HomeSyncReport();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Sync(report));
            Assert.Contains("http://video.example.org:8080", error.Message);
            Assert.Equal("partial", report.Status);
            Assert.Equal(1, report.Series);
            Assert.Equal(3, report.Episodes);
            Assert.Equal(0, report.NewSeries);
            Assert.Equal(2, report.NewEpisodes);
            Assert.Equal(0, report.PendingTitles);
            Assert.Equal(1, report.PendingEpisodes);
            var pending = Assert.Single(report.Issues);
            Assert.Equal("Série da home", pending.Title);
            Assert.Equal("S01E02", pending.Episode);
            Assert.Contains("http://video.example.org:8080", pending.Reason);
            Assert.DoesNotContain("102.mp4", pending.Reason);
            Assert.Equal(3, Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories).Length);
            Assert.Equal(folder, Assert.Single(notified));
            Assert.Equal(identified, await File.ReadAllTextAsync(nfo));

            notified.Clear();
            config.MediaHosts += ", http://video.example.org:8080";
            await Sync();
            Assert.Equal(folder, Assert.Single(Directory.GetDirectories(seriesPath)));
            Assert.Equal(4, Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories).Length);
            Assert.True(File.Exists(Path.Combine(folder, "Season 00", "S00E01.strm")));
            Assert.True(File.Exists(Path.Combine(folder, "Season 01", "S01E02.strm")));
            Assert.True(File.Exists(Path.Combine(folder, "Season 02", "S02E01.strm")));
            Assert.False(Directory.Exists(moviesPath));
            Assert.Equal(folder, Assert.Single(notified));

            notified.Clear();
            var repeated = new HomeSyncReport();
            await Sync(repeated);
            Assert.Equal(1, repeated.Series);
            Assert.Equal(4, repeated.Episodes);
            Assert.Equal(0, repeated.NewSeries);
            Assert.Equal(0, repeated.NewEpisodes);
            Assert.Empty(notified);
            Assert.Equal(4, Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LatestReportsForMoviesAndSeriesAreStoredSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(await HomeSyncReportStore.Read(root, series: false, default));
            var finishedAt = DateTimeOffset.UtcNow;
            await HomeSyncReportStore.Write(root, series: false, new() { Movies = 10, FinishedAt = finishedAt });
            var series = new HomeSyncReport { Series = 2, Episodes = 5, Status = "partial" };
            series.AddPending(new(new("601", "Série", 0, true), null), "Sem vídeo.", "S01E02");
            await HomeSyncReportStore.Write(root, series: true, series);
            await HomeSyncReportStore.Write(root, series: false,
                new() { Movies = 28, Status = "completed", FinishedAt = finishedAt });

            var movies = Assert.IsType<HomeSyncReport>(await HomeSyncReportStore.Read(root, series: false, default));
            Assert.Equal(28, movies.Movies);
            Assert.Equal("completed", movies.Status);
            Assert.Equal(finishedAt, movies.FinishedAt);

            var storedSeries = Assert.IsType<HomeSyncReport>(await HomeSyncReportStore.Read(root, series: true, default));
            Assert.Equal(5, storedSeries.Episodes);
            Assert.Equal("partial", storedSeries.Status);
            Assert.Equal(1, storedSeries.PendingEpisodes);
            Assert.Equal(series.Issues, storedSeries.Issues);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HomeCompletesAnExistingSeriesWhenMetadataArrivesLater()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new FrameConfiguration { SeriesPath = root, MediaHosts = "media.example.org" };
        var metadataAvailable = false;
        var metadataCalls = 0;
        using var http = new HttpClient(new FakeHomeApi(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/home":
                    return Json("""{"hero":{"externalId":"601","name":"Mil Golpes - 2025","isSeries":true},"rows":[]}""");
                case "/browse/titles/601":
                    return Json("""
                        {"isSeries":true,"series":{"externalId":"601","name":"Mil Golpes - 2025","year":0,
                         "description":"Sinopse do catálogo","genreNames":["Drama"],
                         "seasons":[{"number":1,"episodes":[{"number":1,"name":"Piloto",
                         "videoUrl":"https://media.example.org/video.mp4"}]}]}}
                        """);
                case "/browse/titles/601/metadata":
                    metadataCalls++;
                    return metadataAvailable ? Json("""
                        {"id":42,"originalName":"A Thousand Blows","releaseDate":"2025-02-21",
                         "overview":"Sinopse alternativa","voteAverage":8,"cast":[{"name":"Ator","character":"Personagem"}]}
                        """) : new(HttpStatusCode.NoContent);
                default:
                    throw new InvalidOperationException("Rota inesperada no teste.");
            }
        }));
        var client = new BrowseClient(http, new Uri("https://example.org/"), "test-token");
        var notified = new List<string>();
        Task Sync() => SyncHomeSeriesTask.ImportHome(client, config, () => true, notified.Add,
            NullLogger.Instance, new Progress<double>(), default);

        try
        {
            await Sync();
            var folder = Assert.Single(Directory.GetDirectories(root));
            var nfo = Path.Combine(folder, "tvshow.nfo");
            Assert.Null(XElement.Load(nfo).Element("tmdbid"));
            var stream = Assert.Single(Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories));
            var initialStream = await File.ReadAllTextAsync(stream);
            await File.WriteAllTextAsync(Path.Combine(folder, "poster.jpg"), "Imagem já existente");
            await File.WriteAllTextAsync(Path.Combine(folder, "fanart.jpg"), "Imagem já existente");

            metadataAvailable = true;
            notified.Clear();
            await Sync();

            var enriched = XElement.Load(nfo);
            Assert.Equal(42, (int?)enriched.Element("tmdbid"));
            Assert.Equal(2025, (int?)enriched.Element("year"));
            Assert.Equal("2025-02-21", (string?)enriched.Element("premiered"));
            Assert.Equal("A Thousand Blows", (string?)enriched.Element("originaltitle"));
            Assert.Equal("Ator", (string?)enriched.Element("actor")?.Element("name"));
            Assert.Equal("Sinopse do catálogo", (string?)enriched.Element("plot"));
            Assert.Equal("Drama", (string?)enriched.Element("genre"));
            Assert.Equal(folder, Assert.Single(notified));
            Assert.Equal(folder, Assert.Single(Directory.GetDirectories(root)));
            Assert.Equal(stream, Assert.Single(Directory.GetFiles(folder, "*.strm", SearchOption.AllDirectories)));
            Assert.Equal(initialStream, await File.ReadAllTextAsync(stream));

            notified.Clear();
            await Sync();
            Assert.Empty(notified);
            Assert.Equal(2, metadataCalls);
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

    private sealed class FakeHomeApi(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }
}
