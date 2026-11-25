using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class SyncCatalogTask(ILogger<SyncCatalogTask> logger) : IScheduledTask
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    };
    internal static readonly SemaphoreSlim SyncLock = new(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxPages = 100;

    public string Name => "Frame — Sincronizar catálogo";
    public string Key => "FrameSyncCatalog";
    public string Description => "Atualiza os arquivos STRM e NFO das bibliotecas dedicadas.";
    public string Category => "Frame";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await SyncLock.WaitAsync(0, cancellationToken))
            return;
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || config.OnDemand || string.IsNullOrWhiteSpace(config.CatalogUrl))
                return;

            var catalog = ParseHttps(config.CatalogUrl);
            var hosts = ReadMediaAllowlist(config.MediaHosts);
            if (hosts.Count == 0 || !Path.IsPathFullyQualified(config.MoviesPath))
                throw new InvalidDataException("Configure a pasta absoluta de filmes e os hosts de mídia.");
            if (!string.IsNullOrWhiteSpace(config.SeriesPath) &&
                (!Path.IsPathFullyQualified(config.SeriesPath) ||
                 Path.GetFullPath(config.SeriesPath).TrimEnd('/', '\\').Equals(
                     Path.GetFullPath(config.MoviesPath).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Configure uma pasta absoluta e distinta para séries.");

            var items = await ReadCatalog(catalog, cancellationToken);
            var failures = 0;
            var processed = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await Materialize(item, config, hosts, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or HttpRequestException or System.Xml.XmlException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    logger.LogWarning("Frame não processou o item {ItemId} ({Reason}).", item?.Id, exception.GetType().Name);
                }
                progress.Report(100d * ++processed / items.Count);
            }
            progress.Report(100);
            if (failures > 0)
                throw new InvalidOperationException($"Frame encontrou falhas em {failures} item(ns).");
            logger.LogInformation("Frame consultou {Count} item(ns).", items.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Sincronização Frame interrompida ({Reason}).", exception.GetType().Name);
            throw new InvalidOperationException("Confira a configuração, o acesso ao catálogo e as permissões das bibliotecas Frame.");
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static async Task<List<FrameItem>> ReadCatalog(Uri catalog, CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("FRAME_API_TOKEN");
        var items = new List<FrameItem>();
        var seenIds = new HashSet<Guid>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var address = new UriBuilder(catalog);
            if (cursor is not null)
                address.Query = string.Join("&", new[] { catalog.Query.TrimStart('?'), "cursor=" + Uri.EscapeDataString(cursor) }
                    .Where(part => part.Length > 0));
            using var request = new HttpRequestMessage(HttpMethod.Get, address.Uri);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<CatalogPage>(body, Json, cancellationToken);
            if (result?.Items is null || items.Count + result.Items.Length > 50_000)
                throw new InvalidDataException("Resposta de catálogo inválida.");
            foreach (var item in result.Items)
            {
                if (item is null || item.Id == Guid.Empty || !seenIds.Add(item.Id))
                    throw new InvalidDataException("Identificadores de catálogo inválidos ou repetidos.");
                items.Add(item);
            }
            cursor = result.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                return items;
            if (cursor.Length > 2048 || !cursors.Add(cursor))
                throw new InvalidDataException("Cursor inválido.");
        }
        throw new InvalidDataException("Limite de páginas atingido.");
    }

    internal static async Task Materialize(FrameItem item, FrameConfiguration config, HashSet<string> hosts,
        CancellationToken cancellationToken, bool preserveMovieNfo = false)
    {
        if (string.Equals(item.Type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            var nfo = Metadata("movie", item.Id, item.Name, item.Description, item.DurationMinutes);
            nfo.Add(item.Year is int year ? new XElement("year", year) : null,
                item.Rating is double rating ? new XElement("rating", rating) : null,
                (item.Genres ?? []).Select(genre => new XElement("genre", genre)));
            AddTitleMetadata(nfo, item.TitleMetadata);
            var url = PlaybackUrl(item.VideoUrl, hosts);
            var folder = OpenFolder(config.MoviesPath, "Frame-" + item.Id);
            if (!preserveMovieNfo || !File.Exists(Path.Combine(folder, "movie.nfo")))
                await Write(folder, "movie.nfo", nfo.ToString(), cancellationToken);
            await Write(folder, "movie.strm", url, cancellationToken);
            await ImportArtwork(folder, item.Images, hosts, cancellationToken);
            return;
        }

        if (!string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(config.SeriesPath))
            return;

        var show = Metadata("tvshow", item.Id, item.Name, item.Description, null);
        show.Add(item.Year is int startYear ? new XElement("year", startYear) : null,
            item.Rating is double showRating ? new XElement("rating", showRating) : null,
            (item.Genres ?? []).Select(genre => new XElement("genre", genre)));
        AddTitleMetadata(show, item.TitleMetadata);
        var seasons = item.Seasons ?? throw new InvalidDataException("Temporadas ausentes.");
        var episodes = new List<(int Season, string Name, string Url, XElement Nfo)>();
        var episodeIds = new HashSet<Guid>();
        var slots = new HashSet<(int, int)>();
        foreach (var season in seasons)
        {
            if (season is null || season.Number < 0 || season.Episodes is null)
                throw new InvalidDataException("Temporada inválida.");
            foreach (var episode in season.Episodes)
            {
                if (episode is null || episode.Number <= 0 || !episodeIds.Add(episode.Id) ||
                    !slots.Add((season.Number, episode.Number)))
                    throw new InvalidDataException("Episódio inválido ou repetido.");
                var nfo = Metadata("episodedetails", episode.Id, episode.Name, episode.Description, episode.DurationMinutes);
                nfo.Add(new XElement("season", season.Number), new XElement("episode", episode.Number));
                episodes.Add((season.Number, EpisodeFileName(season.Number, episode.Number),
                    PlaybackUrl(episode.VideoUrl, hosts), nfo));
            }
        }
        var series = OpenFolder(config.SeriesPath, "Frame-" + item.Id);
        await Write(series, "tvshow.nfo", show.ToString(), cancellationToken);
        foreach (var episode in episodes)
        {
            var folder = OpenFolder(series, SeasonFolderName(episode.Season));
            await Write(folder, episode.Name + ".nfo", episode.Nfo.ToString(), cancellationToken);
            await Write(folder, episode.Name + ".strm", episode.Url, cancellationToken);
        }
        await ImportArtwork(series, item.Images, hosts, cancellationToken);
    }

    internal static string EpisodeFileName(int season, int episode) => FormattableString.Invariant($"S{season:D2}E{episode:D2}");

    internal static string SeasonFolderName(int season) => FormattableString.Invariant($"Season {season:D2}");

    internal static async Task<bool> ImportArtwork(string folder, FrameImages? images, HashSet<string> hosts,
        CancellationToken cancellationToken, FrameImages? fallback = null, HttpClient? imageClient = null)
    {
        if (images is null && fallback is null)
            return false;

        imageClient ??= Http;
        Exception? firstFailure = null;
        var posterSaved = false;
        foreach (var (name, variants) in new[]
        {
            ("poster", new[] { images?.PosterUrl, images?.LogoBigUrl, fallback?.PosterUrl, fallback?.LogoBigUrl, fallback?.BackdropUrl }),
            ("fanart", new[] { images?.BackdropUrl, fallback?.BackdropUrl }),
            ("logo", new[] { images?.LogoUrl, fallback?.LogoUrl })
        })
        {
            Exception? failure = null;
            foreach (var value in variants.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    using var response = await imageClient.GetAsync(MediaUrl(value, hosts), cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var extension = response.Content.Headers.ContentType?.MediaType switch
                    {
                        "image/jpeg" when bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }) => ".jpg",
                        "image/png" when bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) => ".png",
                        _ => throw new InvalidDataException("Formato de imagem inválido.")
                    };

                    await WriteBytes(folder, name + extension, bytes, cancellationToken);
                    posterSaved |= name == "poster";
                    failure = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                    UnauthorizedAccessException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    failure = ex;
                }
            }
            firstFailure ??= failure;
        }
        if (firstFailure is not null)
            throw new IOException("Não foi possível importar todo o material visual.", firstFailure);
        return posterSaved;
    }

    private static void AddTitleMetadata(XElement nfo, FrameTitleMetadata? metadata)
    {
        if (metadata is null)
            return;

        nfo.Add(
            metadata.TmdbId > 0 ? new XElement("tmdbid", metadata.TmdbId) : null,
            !string.IsNullOrWhiteSpace(metadata.OriginalTitle) ? new XElement("originaltitle", metadata.OriginalTitle) : null,
            metadata.ReleaseDate is { } date ? new XElement("premiered",
                date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) : null,
            (metadata.Directors ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct()
                .Select(name => new XElement("director", name)),
            (metadata.Cast ?? []).Where(person => !string.IsNullOrWhiteSpace(person?.Name)).Take(10)
                .Select(person => new XElement("actor", new XElement("name", person.Name),
                    !string.IsNullOrWhiteSpace(person.Character) ? new XElement("role", person.Character) : null)));
    }

    private static XElement Metadata(string type, Guid id, string? title, string? plot, int? duration)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(title) || duration < 0)
            throw new InvalidDataException("Metadados inválidos.");
        return new XElement(type, new XElement("title", title), new XElement("plot", plot ?? ""),
            new XElement("uniqueid", new XAttribute("type", "frame"), id),
            duration is int minutes ? new XElement("runtime", minutes) : null);
    }

    private static Uri ParseHttps(string? value) =>
        ParseWebUrl(value) is { Scheme: "https" } uri ? uri : throw new InvalidDataException("URL HTTPS inválida.");

    private static Uri? ParseWebUrl(string? value)
    {
        if (value is null || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return null;
        return uri;
    }

    internal static HashSet<string> ReadMediaAllowlist(string entries)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.CheckHostName(entry) != UriHostNameType.Unknown)
                allowed.Add(entry);
            else if (ParseWebUrl(entry) is { Scheme: "http", AbsolutePath: "/", Query.Length: 0 } origin)
                allowed.Add(origin.GetLeftPart(UriPartial.Authority));
        }
        return allowed;
    }

    internal static string PlaybackUrl(string? value, HashSet<string> allowed)
    {
        var uri = ParseWebUrl(value) ?? throw new InvalidDataException("A API devolveu uma URL de vídeo inválida.");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!allowed.Contains(origin))
                throw new InvalidDataException($"Para permitir este vídeo HTTP, adicione {origin} em Hosts de mídia permitidos.");
        }
        else if (!IsAllowedHttpsMedia(uri, allowed))
        {
            throw new InvalidDataException(uri.IsDefaultPort
                ? $"Adicione {uri.IdnHost} em Hosts de mídia permitidos."
                : "A URL HTTPS de vídeo precisa usar a porta padrão.");
        }
        return uri.AbsoluteUri;
    }

    internal static bool MediaUrlAllowed(string? value, HashSet<string> hosts) =>
        ParseWebUrl(value) is { } uri && IsAllowedHttpsMedia(uri, hosts);

    private static bool IsAllowedHttpsMedia(Uri uri, HashSet<string> hosts) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && hosts.Contains(uri.IdnHost);

    private static string MediaUrl(string? value, HashSet<string> hosts)
    {
        var uri = ParseHttps(value);
        if (!IsAllowedHttpsMedia(uri, hosts))
            throw new InvalidDataException("Origem de mídia não permitida.");
        return uri.AbsoluteUri;
    }

    private static string OpenFolder(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            RejectLink(current);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("Links não são permitidos na biblioteca gerenciada.");
    }

    private static Task Write(string folder, string name, string content, CancellationToken cancellationToken) =>
        WriteBytes(folder, name, System.Text.Encoding.UTF8.GetBytes(content + "\n"), cancellationToken);

    private static async Task WriteBytes(string folder, string name, byte[] content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, name);
        RejectLink(new FileInfo(path));
        if (File.Exists(path) && new FileInfo(path).Length == content.Length)
        {
            var previous = await File.ReadAllBytesAsync(path, cancellationToken);
            if (previous.AsSpan().SequenceEqual(content))
                return;
        }
        var temp = Path.Combine(folder, $".frame-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
