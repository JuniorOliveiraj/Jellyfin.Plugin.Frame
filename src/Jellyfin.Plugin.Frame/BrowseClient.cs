using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Frame;

internal sealed record BrowseTitle(string ExternalId, string Name, int Year, bool IsSeries);
internal sealed record BrowsePage(BrowseTitle[] Items);
internal sealed record BrowseHome(BrowseTitle? Hero, BrowseHomeRow[]? Rows, bool Partial);
internal sealed record BrowseHomeRow(BrowseHomeItem[]? Items);
internal sealed record BrowseHomeItem(BrowseTitle Item, Guid? PersistedTitleId);
internal sealed record PersistedBrowseTitle(Guid Id, string Type, string? ExternalId, string Name, int Year,
    double? Rating, string? Description, string[]? Genres, int? DurationMinutes, string? VideoUrl,
    FrameImages? Images, BrowseSeason[]? Seasons);
internal sealed record BrowseDetail(bool IsSeries, BrowseMovie? Movie, BrowseSeries? Series);
internal sealed record BrowseMovie(string ExternalId, string Name, int Year, double? Rating, string? Description,
    string[]? GenreNames, int? DurationMinutes, string? VideoUrl, FrameImages? Images);
internal sealed record BrowseSeries(string ExternalId, string Name, int Year, double? Rating, string? Description,
    string[]? GenreNames, FrameImages? Images, BrowseSeason[]? Seasons);
internal sealed record BrowseSeason(int Number, BrowseEpisode[]? Episodes);
internal sealed record BrowseEpisode(int Number, string? Name, string? VideoUrl, int? DurationMinutes, string? BackupVideoUrl)
{
    internal string? PlaybackUrl => string.IsNullOrWhiteSpace(VideoUrl) ? BackupVideoUrl : VideoUrl;
}

public sealed record FrameSearchResult(
    [property: JsonPropertyName("externalId")] string ExternalId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("isSeries")] bool IsSeries = false);

public sealed record FrameSearchPage(
    [property: JsonPropertyName("items")] FrameSearchResult[] Items,
    [property: JsonPropertyName("canPageForward")] bool CanPageForward);

public sealed record FrameImportRequest(
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("externalId")] string ExternalId,
    [property: JsonPropertyName("isSeries")] bool? IsSeries = null);

internal sealed class BrowseClient(HttpClient http, Uri origin, string? token)
{
    internal static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(25),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    };

    internal async Task<FrameSearchPage> Search(string term, int page, CancellationToken ct)
    {
        var result = await ReadSearch(term, page, ct);
        var titles = result.Items
            .Where(item => item is not null && IsValidId(item.ExternalId) && !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => (item.ExternalId, item.IsSeries))
            .Select(item => new FrameSearchResult(item.ExternalId, item.Name, item.Year, item.IsSeries))
            .ToArray();

        // The upstream search counts only the current page. An empty raw page ends pagination.
        return new(titles, result.Items.Length > 0 && page < 100);
    }

    internal async Task<FrameItem?> GetTitleForImport(FrameImportRequest selection, CancellationToken ct)
    {
        var path = "browse/titles/" + Uri.EscapeDataString(selection.ExternalId);
        var detail = await Read<BrowseDetail>(path, ct, allowMissing: true);
        detail ??= await ReloadTitleFromSearch(selection, path, ct);

        if (detail is null || selection.IsSeries is bool expectedSeries && detail.IsSeries != expectedSeries)
            return null;

        if (detail is { IsSeries: true, Series: { } series } && series.ExternalId == selection.ExternalId)
            return MapSeries(series);
        if (detail is not { IsSeries: false, Movie: { } movie } || movie.ExternalId != selection.ExternalId)
            return null;

        return MapMovie(movie);
    }

    internal async Task<BrowseHome> GetHome(CancellationToken ct) =>
        await Read<BrowseHome>("home", ct) ?? throw new InvalidDataException("Resposta da home vazia.");

    internal async Task<FrameItem?> GetHomeTitle(BrowseHomeItem card, CancellationToken ct)
    {
        var externalId = card.Item.ExternalId;
        var isSeries = card.Item.IsSeries;
        var path = IsValidId(externalId) ? "browse/titles/" + Uri.EscapeDataString(externalId) : null;
        if (path is not null &&
            MapLiveHomeTitle(await Read<BrowseDetail>(path, ct, allowMissing: true), externalId, isSeries) is { } live)
            return live;

        if (card.PersistedTitleId is Guid localId)
        {
            var persisted = await Read<PersistedBrowseTitle>($"catalog/titles/{localId:D}", ct, allowMissing: true);
            if (persisted is not null && persisted.Id == localId &&
                string.Equals(persisted.Type, isSeries ? "series" : "movie", StringComparison.OrdinalIgnoreCase))
            {
                var identity = IsValidId(persisted.ExternalId) ? persisted.ExternalId! : localId.ToString("D");
                if (isSeries)
                {
                    var series = MapSeries(new(identity, persisted.Name, persisted.Year, persisted.Rating,
                        persisted.Description, persisted.Genres, persisted.Images, persisted.Seasons), allEpisodes: true);
                    if (HasEpisodeLinks(series))
                        return series;
                }
                else if (!string.IsNullOrWhiteSpace(persisted.VideoUrl))
                {
                    return MapMovie(new(identity, persisted.Name, persisted.Year, persisted.Rating, persisted.Description,
                        persisted.Genres, persisted.DurationMinutes, persisted.VideoUrl, persisted.Images));
                }
            }
        }
        if (path is null || string.IsNullOrWhiteSpace(card.Item.Name))
            return null;
        var selection = new FrameImportRequest(card.Item.Name, 1, externalId, IsSeries: isSeries);
        return MapLiveHomeTitle(await ReloadTitleFromSearch(selection, path, ct), externalId, isSeries);
    }

    private static FrameItem? MapLiveHomeTitle(BrowseDetail? detail, string externalId, bool isSeries)
    {
        if (isSeries && detail is { IsSeries: true, Series: { } series } && series.ExternalId == externalId)
        {
            var item = MapSeries(series, allEpisodes: true);
            return HasEpisodeLinks(item) ? item : null;
        }

        return !isSeries && detail is { IsSeries: false, Movie: { } movie } &&
            movie.ExternalId == externalId && !string.IsNullOrWhiteSpace(movie.VideoUrl) ? MapMovie(movie) : null;
    }

    private static bool HasEpisodeLinks(FrameItem series) =>
        (series.Seasons ?? []).Any(season => (season.Episodes ?? []).Any(episode => !string.IsNullOrWhiteSpace(episode.VideoUrl)));

    private async Task<BrowseDetail?> ReloadTitleFromSearch(FrameImportRequest selection, string path, CancellationToken ct)
    {
        var result = await ReadSearch(selection.Term, selection.Page, ct);
        if (!result.Items.Any(item => item is not null && item.ExternalId == selection.ExternalId &&
            (selection.IsSeries is null || item.IsSeries == selection.IsSeries)))
            return null;

        return await Read<BrowseDetail>(path, ct, allowMissing: true);
    }

    private static FrameItem MapMovie(BrowseMovie movie) =>
        new(StableId("movie:" + movie.ExternalId), movie.Name, "movie",
            movie.Year > 0 ? movie.Year : null, movie.Rating, movie.Description, movie.GenreNames,
            movie.DurationMinutes, movie.VideoUrl, movie.Images, null);

    private static FrameItem MapSeries(BrowseSeries series, bool allEpisodes = false)
    {
        var seasons = (series.Seasons ?? [])
            .Where(season => season is { Number: >= 0 })
            .GroupBy(season => season.Number)
            .OrderBy(group => group.Key == 0)
            .ThenBy(group => group.Key)
            .Select(group => new FrameSeason(group.Key, group.SelectMany(season => season.Episodes ?? [])
                .Where(episode => episode is { Number: > 0 })
                .GroupBy(episode => episode.Number)
                .OrderBy(episodes => episodes.Key)
                .Select(episodes => episodes.FirstOrDefault(episode => !string.IsNullOrWhiteSpace(episode.PlaybackUrl))
                    ?? episodes.First())
                .Select(episode => MapEpisode(series.ExternalId, group.Key, episode))
                .ToArray()))
            .Where(season => season.Episodes!.Length > 0)
            .ToArray();

        if (!allEpisodes)
        {
            var first = seasons.SelectMany(season => season.Episodes!
                .Select(episode => (Season: season.Number, Episode: episode)))
                .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Episode.VideoUrl));
            seasons = first.Episode is { } episode ? [new(first.Season, [episode])] : [];
        }

        return new(StableId("series:" + series.ExternalId), series.Name, "series",
            series.Year > 0 ? series.Year : null, series.Rating, series.Description, series.GenreNames,
            null, null, series.Images, seasons);
    }

    private static FrameEpisode MapEpisode(string seriesId, int seasonNumber, BrowseEpisode episode)
    {
        var key = FormattableString.Invariant($"episode:{seriesId}:{seasonNumber}:{episode.Number}");
        var name = string.IsNullOrWhiteSpace(episode.Name) ? $"Episódio {episode.Number}" : episode.Name;
        return new(StableId(key), episode.Number, name, null, episode.DurationMinutes, episode.PlaybackUrl);
    }

    private static Guid StableId(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("filmes-jr:" + key));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    internal Task<BrowseTitleMetadata?> GetTitleMetadata(string externalId, CancellationToken ct) =>
        Read<BrowseTitleMetadata>("browse/titles/" + Uri.EscapeDataString(externalId) + "/metadata", ct,
            allowMissing: true);

    internal Task<BrowseTitleMetadata?> GetPersistedTitleMetadata(Guid id, CancellationToken ct) =>
        Read<BrowseTitleMetadata>($"catalog/titles/{id:D}/metadata", ct, allowMissing: true);

    internal static bool IsValidId(string? id) => id is { Length: > 0 and <= 128 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task<BrowsePage> ReadSearch(string term, int page, CancellationToken ct)
    {
        var result = await Read<BrowsePage>($"browse/search/{Uri.EscapeDataString(term.Trim())}?page={page}", ct);
        return result is { Items: not null } ? result : throw new InvalidDataException("Resposta de busca inválida.");
    }

    private async Task<T?> Read<T>(string path, CancellationToken ct, bool allowMissing = false) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Resposta vazia.");
    }
}
