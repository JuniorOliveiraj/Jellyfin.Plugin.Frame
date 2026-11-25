using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task ImportsHttpVideoOnlyWhenItsExactOriginIsAllowed()
    {
        const string url = "http://media.example.org:8080/clip.mp4?reference=sample";
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new FrameConfiguration { MoviesPath = root };
        var movie = new FrameItem(Guid.NewGuid(), "Teste HTTP", "movie", null, null, null, null, null, url, null, null);
        try
        {
            var hostsOnly = SyncCatalogTask.ReadMediaAllowlist("media.example.org, http://media.example.org:8081");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                SyncCatalogTask.Materialize(movie, config, hostsOnly, default));
            Assert.Contains("http://media.example.org:8080", error.Message);
            Assert.DoesNotContain("clip.mp4", error.Message);
            Assert.DoesNotContain("reference", error.Message);
            Assert.False(Directory.Exists(root));

            var allowed = SyncCatalogTask.ReadMediaAllowlist("media.example.org, http://media.example.org:8080/");
            await SyncCatalogTask.Materialize(movie, config, allowed, default);
            var file = Assert.Single(Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories));
            Assert.Equal(url + "\n", await File.ReadAllTextAsync(file));

            Assert.False(SyncCatalogTask.MediaUrlAllowed(url, allowed));
            Assert.True(SyncCatalogTask.MediaUrlAllowed("https://media.example.org/poster.jpg", allowed));
            Assert.Throws<InvalidDataException>(() =>
                SyncCatalogTask.PlaybackUrl("http://example-user@example.org/clip.mp4", allowed));
            var defaultPort = SyncCatalogTask.ReadMediaAllowlist("http://media.example.org:80");
            Assert.Equal("http://media.example.org/clip.mp4",
                SyncCatalogTask.PlaybackUrl("http://media.example.org/clip.mp4", defaultPort));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task ImportsOneMovieAndKeepsItsPathOnTitleChange()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
            var page = JsonSerializer.Deserialize<CatalogPage>(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var movie = Assert.Single(page.Items!);
            var config = new FrameConfiguration { MoviesPath = root };
            var hosts = new HashSet<string> { "interactive-examples.mdn.mozilla.net" };

            await SyncCatalogTask.Materialize(movie, config, hosts, default);
            var folder = Assert.Single(Directory.GetDirectories(root));
            var strm = Path.Combine(folder, "movie.strm");
            var nfo = Path.Combine(folder, "movie.nfo");
            Assert.Equal(movie.VideoUrl + "\n", await File.ReadAllTextAsync(strm));
            await SyncCatalogTask.Materialize(movie with { Name = "Nome & <novo>" }, config, hosts, default);
            Assert.Equal(folder, Assert.Single(Directory.GetDirectories(root)));
            Assert.Equal("Nome & <novo>", (string?)XElement.Load(nfo).Element("title"));
            Assert.Equal(2, Directory.GetFiles(folder).Length);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SyncCatalogTask.Materialize(movie with { VideoUrl = "https://example.invalid/clip.mp4" }, config, hosts, default));
            Assert.Equal(movie.VideoUrl + "\n", await File.ReadAllTextAsync(strm));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportsSeriesWithSpecialsAndEpisodeMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            const string url = "https://media.example.org/demo.mp4";
            var episode = new FrameEpisode(Guid.NewGuid(), 1, "Especial", "Sinopse", 10, url);
            var series = new FrameItem(Guid.NewGuid(), "Série", "series", null, null, null, null,
                null, null, null, [new FrameSeason(0, [episode])]);
            await SyncCatalogTask.Materialize(series, new FrameConfiguration { SeriesPath = root },
                new HashSet<string> { "media.example.org" }, default);
            var show = Assert.Single(Directory.GetDirectories(root));
            Assert.Equal("Série", (string?)XElement.Load(Path.Combine(show, "tvshow.nfo")).Element("title"));
            var season = Path.Combine(show, "Season 00");
            Assert.Equal(url, (await File.ReadAllTextAsync(Path.Combine(season, "S00E01.strm"))).Trim());
            var nfo = XElement.Load(Path.Combine(season, "S00E01.nfo"));
            Assert.Equal(0, (int?)nfo.Element("season"));
            Assert.Equal(1, (int?)nfo.Element("episode"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
