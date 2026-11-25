namespace Jellyfin.Plugin.Frame;

internal sealed record CatalogPage(FrameItem[]? Items, string? NextCursor);
internal sealed record FrameItem(Guid Id, string? Name, string? Type, int? Year, double? Rating,
    string? Description, string[]? Genres, int? DurationMinutes, string? VideoUrl, FrameImages? Images, FrameSeason[]? Seasons, FrameMovieMetadata? MovieMetadata = null);
internal sealed record FrameSeason(int Number, FrameEpisode[]? Episodes);
internal sealed record FrameEpisode(Guid Id, int Number, string? Name, string? Description,
    int? DurationMinutes, string? VideoUrl);
internal sealed record FrameImages(string? PosterUrl, string? BackdropUrl, string? LogoUrl, string? LogoBigUrl = null);
internal sealed record FrameMovieMetadata(int TmdbId, string? OriginalTitle, DateOnly? ReleaseDate,
    string[]? Directors, FrameCastMember[]? Cast);
internal sealed record FrameCastMember(string? Name, string? Character);
