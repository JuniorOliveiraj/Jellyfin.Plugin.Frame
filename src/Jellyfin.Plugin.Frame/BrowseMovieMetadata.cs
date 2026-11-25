using System.Globalization;

namespace Jellyfin.Plugin.Frame;

internal sealed record BrowseMovieMetadata(int Id, string? OriginalName, string? Overview, string? PosterPath,
    string? BackdropPath, string? ReleaseDate, double? VoteAverage, int? RuntimeMinutes,
    string[]? Genres, string[]? Directors, FrameCastMember[]? Cast)
{
    internal const string ArtworkHost = "image.tmdb.org";

    internal FrameItem ApplyTo(FrameItem movie)
    {
        var date = DateOnly.TryParseExact(ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : (DateOnly?)null;
        return movie with
        {
            Description = string.IsNullOrWhiteSpace(movie.Description) ? Overview : movie.Description,
            Year = movie.Year ?? date?.Year,
            Rating = movie.Rating ?? VoteAverage,
            DurationMinutes = movie.DurationMinutes is > 0 ? movie.DurationMinutes :
                RuntimeMinutes is > 0 ? RuntimeMinutes : movie.DurationMinutes,
            Genres = movie.Genres is { Length: > 0 } ? movie.Genres : Genres,
            MovieMetadata = new(Id, OriginalName, date, Directors, Cast)
        };
    }

    internal FrameImages Artwork() => new(TmdbImage(PosterPath, "w500"), TmdbImage(BackdropPath, "w1280"), null);

    private static string? TmdbImage(string? path, string size)
    {
        if (path is null || path.Length < 2 || path[0] != '/' ||
            !path.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
            path[1..] is "." or "..")
            return null;
        return $"https://{ArtworkHost}/t/p/{size}{path}";
    }
}
