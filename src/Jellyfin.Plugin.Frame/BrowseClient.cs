using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Frame;

internal sealed record BrowseTitle(string ExternalId, string Name, int Year, bool IsSeries);
internal sealed record BrowsePage(BrowseTitle[] Items);
internal sealed record BrowseDetail(bool IsSeries, BrowseMovie? Movie);
internal sealed record BrowseMovie(string ExternalId, string Name, int Year, double? Rating, string? Description,
    string[]? GenreNames, int? DurationMinutes, string? VideoUrl, FrameImages? Images);
public sealed record FrameSearchResult(
    [property: JsonPropertyName("externalId")] string ExternalId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int Year);

public sealed record FrameSearchPage(
    [property: JsonPropertyName("items")] FrameSearchResult[] Items,
    [property: JsonPropertyName("canPageForward")] bool CanPageForward);

public sealed record FrameImportRequest(
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("externalId")] string ExternalId);

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
        var films = result.Items
            .Where(item => item is { IsSeries: false } && IsValidId(item.ExternalId) && !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => item.ExternalId)
            .Select(item => new FrameSearchResult(item.ExternalId, item.Name, item.Year))
            .ToArray();

        // The upstream search counts only the current page. An empty raw page ends pagination.
        return new(films, result.Items.Length > 0 && page < 100);
    }

    internal async Task<FrameItem?> GetMovie(FrameImportRequest selection, CancellationToken ct)
    {
        var path = "browse/titles/" + Uri.EscapeDataString(selection.ExternalId);
        var detail = await Read<BrowseDetail>(path, ct, allowMissing: true);
        if (detail is null)
        {
            var result = await ReadSearch(selection.Term, selection.Page, ct);
            if (!result.Items.Any(item => item is { IsSeries: false } && item.ExternalId == selection.ExternalId))
                return null;
            detail = await Read<BrowseDetail>(path, ct, allowMissing: true);
        }

        if (detail is not { IsSeries: false, Movie: { } movie } || movie.ExternalId != selection.ExternalId)
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("filmes-jr:movie:" + movie.ExternalId));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new(new Guid(digest.AsSpan(0, 16), bigEndian: true), movie.Name, "movie",
            movie.Year > 0 ? movie.Year : null, movie.Rating, movie.Description, movie.GenreNames,
            movie.DurationMinutes, movie.VideoUrl, movie.Images, null);
    }

    internal Task<BrowseMovieMetadata?> GetMovieMetadata(string externalId, CancellationToken ct) =>
        Read<BrowseMovieMetadata>("browse/titles/" + Uri.EscapeDataString(externalId) + "/metadata", ct,
            allowMissing: true);

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
