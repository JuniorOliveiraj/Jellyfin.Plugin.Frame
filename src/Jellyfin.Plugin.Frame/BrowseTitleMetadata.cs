using System.Globalization;

namespace Jellyfin.Plugin.Frame;

internal sealed record BrowseTitleMetadata(int Id, string? OriginalName, string? Overview, string? PosterPath,
    string? BackdropPath, string? ReleaseDate, double? VoteAverage, int? RuntimeMinutes,
    string[]? Genres, string[]? Directors, FrameCastMember[]? Cast)
{
    internal const string ArtworkHost = "image.tmdb.org";

    internal FrameItem ApplyTo(FrameItem item)
    {
        var date = DateOnly.TryParseExact(ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : (DateOnly?)null;
        return item with
        {
            Description = string.IsNullOrWhiteSpace(item.Description) ? Overview : item.Description,
            Year = item.Year ?? date?.Year,
            Rating = item.Rating ?? VoteAverage,
            DurationMinutes = item.DurationMinutes is > 0 ? item.DurationMinutes :
                RuntimeMinutes is > 0 ? RuntimeMinutes : item.DurationMinutes,
            Genres = item.Genres is { Length: > 0 } ? item.Genres : Genres,
            TitleMetadata = new(Id, OriginalName, date, Directors, Cast)
        };
    }

    internal FrameImages Artwork() => new(PosterUrl(PosterPath), TmdbImage(BackdropPath, "w1280"), null);

    internal static string? PosterUrl(string? path) => TmdbImage(path, "w500");

    private static string? TmdbImage(string? path, string size)
    {
        if (path is null || path.Length < 2 || path[0] != '/' ||
            !path.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
            path[1..] is "." or "..")
            return null;
        return $"https://{ArtworkHost}/t/p/{size}{path}";
    }
}
