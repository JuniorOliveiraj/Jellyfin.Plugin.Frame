namespace Jellyfin.Plugin.Frame;

internal sealed record CatalogPage(FrameItem[]? Items, string? NextCursor);
internal sealed record FrameItem(Guid Id, string? Name, string? Type, int? Year, double? Rating,
    string? Description, string[]? Genres, int? DurationMinutes, string? VideoUrl, FrameImages? Images, FrameSeason[]? Seasons);
internal sealed record FrameSeason(int Number, FrameEpisode[]? Episodes);
internal sealed record FrameEpisode(Guid Id, int Number, string? Name, string? Description,
    int? DurationMinutes, string? VideoUrl);
internal sealed record FrameImages(string? PosterUrl, string? BackdropUrl, string? LogoUrl);
