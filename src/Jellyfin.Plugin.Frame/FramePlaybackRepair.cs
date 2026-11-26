using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Frame;

internal sealed record FramePlaybackIdentity(string Origin, string ExternalId, string Name)
{
    internal const string FileName = ".frame-playback.json";

    internal static bool Matches(string externalId, Guid frameId, bool series) =>
        long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) && numericId > 0 &&
        externalId == numericId.ToString(CultureInfo.InvariantCulture) &&
        frameId == (series ? BrowseClient.SeriesId(externalId) : BrowseClient.MovieId(externalId));
}

internal sealed record FramePlaybackTarget(string Folder, string Path, Guid FrameId, int? Season = null, int? Episode = null)
{
    internal bool IsSeries => Season.HasValue;
}

internal enum PlaybackRepairResult { Skipped, Repaired, Pending }

internal static class FramePlaybackRepair
{
    private static readonly EnumerationOptions Files = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
    private const int MaxSearchPages = 5;
    private const int MaxLinkBytes = 16 * 1024;

    internal static IEnumerable<FramePlaybackTarget> Targets(string root, bool series)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            yield break;

        root = Path.GetFullPath(root);
        SyncCatalogTask.RejectFolderLinks(root);
        foreach (var folder in Directory.EnumerateDirectories(root, "Frame-*", Files))
        {
            var name = Path.GetFileName(folder);
            if (!name.StartsWith("Frame-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.AsSpan(6), "D", out var id))
                continue;

            if (!series)
            {
                var path = Path.Combine(folder, "movie.strm");
                if (File.Exists(path))
                    yield return new(folder, path, id);
                continue;
            }

            foreach (var seasonFolder in Directory.EnumerateDirectories(folder, "Season *", Files))
            {
                var seasonName = Path.GetFileName(seasonFolder);
                if (!seasonName.StartsWith("Season ", StringComparison.Ordinal) ||
                    !int.TryParse(seasonName.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var season) ||
                    seasonName != SyncCatalogTask.SeasonFolderName(season))
                    continue;

                foreach (var path in Directory.EnumerateFiles(seasonFolder, "*.strm", Files))
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    var e = fileName.IndexOf('E');
                    if (e > 0 && int.TryParse(fileName.AsSpan(e + 1), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var episode) && episode > 0 &&
                        fileName == SyncCatalogTask.EpisodeFileName(season, episode))
                        yield return new(folder, path, id, season, episode);
                }
            }
        }
    }

    internal static async Task<PlaybackRepairResult> Repair(FramePlaybackTarget target, Uri origin, BrowseClient client,
        HttpClient media, HashSet<string> hosts, Func<bool> stillConfigured, Action<string> notify, CancellationToken ct)
    {
        var original = await ReadLink(target.Path, ct);
        if (AllowedUrl(original, hosts) is not { } current)
            return PlaybackRepairResult.Skipped;
        var currentStatus = await Probe(media, current, ct);
        if (currentStatus is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone))
            return PlaybackRepairResult.Skipped;

        var source = await ResolveIdentity(target, origin, client, ct);
        if (source is null)
            return PlaybackRepairResult.Pending;

        var sources = await client.RefreshPlayback(source.ExternalId, target.Season, target.Episode, ct);
        if (sources is null)
        {
            // A title imported only in Jellyfin may have expired out of FILMES-JR's browse cache.
            if (await LookUpTitle(target, source.Name, client, ct) is { } refreshed)
                sources = await client.RefreshPlayback(refreshed.ExternalId, target.Season, target.Episode, ct);
        }
        if (sources is null)
            return PlaybackRepairResult.Pending;

        var rejected = new HashSet<string>(StringComparer.Ordinal) { current };
        foreach (var raw in new[] { sources.VideoUrl, sources.BackupVideoUrl }.Distinct(StringComparer.Ordinal))
        {
            if (AllowedUrl(raw, hosts) is not { } candidate || rejected.Contains(candidate))
                continue;
            if (await Probe(media, candidate, ct) is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                rejected.Add(candidate);
                continue;
            }

            await SyncCatalogTask.SyncLock.WaitAsync(ct);
            try
            {
                if (!stillConfigured() || !string.Equals(original, await ReadLink(target.Path, ct), StringComparison.Ordinal))
                    return PlaybackRepairResult.Skipped;

                var stamp = new RepairStamp(Hash(candidate), rejected.Select(Hash).ToArray());
                await SyncCatalogTask.WriteRepairStamp(target.Path, StampName(target.Path), JsonSerializer.Serialize(stamp), ct);
                await SyncCatalogTask.UpdatePlaybackLink(target.Path, candidate, ct);
                notify(target.Path);
                var identityPath = Path.Combine(target.Folder, FramePlaybackIdentity.FileName);
                if (!File.Exists(identityPath))
                    await SyncCatalogTask.WritePlaybackIdentity(target.Folder, source, ct);
                return PlaybackRepairResult.Repaired;
            }
            finally
            {
                SyncCatalogTask.SyncLock.Release();
            }
        }
        return PlaybackRepairResult.Pending;
    }

    internal static async Task<bool> RetainRepairedLink(string path, string incoming, HashSet<string> hosts,
        CancellationToken ct)
    {
        var info = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, StampName(path)));
        SyncCatalogTask.RejectLink(info);
        if (!info.Exists || info.Length > MaxLinkBytes)
            return false;

        RepairStamp? stamp;
        try
        {
            using var input = info.OpenRead();
            stamp = await JsonSerializer.DeserializeAsync<RepairStamp>(input, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return false;
        }

        return stamp?.Rejected?.Contains(Hash(incoming), StringComparer.Ordinal) == true &&
            AllowedUrl(await ReadLink(path, ct), hosts) is { } current && Hash(current) == stamp.Replacement;
    }

    private static string StampName(string path) => ".frame-repaired-" + Path.GetFileNameWithoutExtension(path) + ".json";

    private static string Hash(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    private sealed record RepairStamp(string Replacement, string[] Rejected);

    private static async Task<string?> ReadLink(string path, CancellationToken ct)
    {
        SyncCatalogTask.RejectFolderLinks(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        SyncCatalogTask.RejectLink(info);
        return info.Exists && info.Length <= MaxLinkBytes ? (await File.ReadAllTextAsync(path, ct)).Trim() : null;
    }

    private static string? AllowedUrl(string? value, HashSet<string> hosts)
    {
        if (value is null)
            return null;
        try { return SyncCatalogTask.PlaybackUrl(value, hosts); }
        catch (InvalidDataException) { return null; }
    }

    private static async Task<HttpStatusCode?> Probe(HttpClient media, string url, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        try
        {
            using var response = await media.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // A 200 HTML error page isn't a playable replacement.
            return string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) &&
                response.IsSuccessStatusCode
                ? null : response.StatusCode;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<FramePlaybackIdentity?> ResolveIdentity(FramePlaybackTarget target, Uri origin,
        BrowseClient client, CancellationToken ct)
    {
        var identityFile = new FileInfo(Path.Combine(target.Folder, FramePlaybackIdentity.FileName));
        SyncCatalogTask.RejectLink(identityFile);
        if (identityFile.Exists)
        {
            if (identityFile.Length > MaxLinkBytes)
                return null;
            using var stream = identityFile.OpenRead();
            var stored = await JsonSerializer.DeserializeAsync<FramePlaybackIdentity>(stream, cancellationToken: ct);
            return stored is { Name.Length: > 0, ExternalId: not null } && stored.Origin == origin.AbsoluteUri &&
                FramePlaybackIdentity.Matches(stored.ExternalId, target.FrameId, target.IsSeries) ? stored : null;
        }

        var name = ReadManagedTitle(target);
        if (name is null || await LookUpTitle(target, name, client, ct) is not { } item)
            return null;
        return new(origin.AbsoluteUri, item.ExternalId, item.Name);
    }

    private static string? ReadManagedTitle(FramePlaybackTarget target)
    {
        var nfo = new FileInfo(Path.Combine(target.Folder, target.IsSeries ? "tvshow.nfo" : "movie.nfo"));
        SyncCatalogTask.RejectLink(nfo);
        if (!nfo.Exists)
            return null;
        using var input = nfo.OpenRead();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { MaxCharactersInDocument = 1024 * 1024 });
        var metadata = XElement.Load(reader);
        if (metadata.Name != (target.IsSeries ? "tvshow" : "movie") ||
            !metadata.Elements("uniqueid").Any(id => (string?)id.Attribute("type") == "frame" &&
                Guid.TryParse(id.Value, out var frameId) && frameId == target.FrameId))
            return null;
        var title = metadata.Element("title")?.Value.Trim();
        return title is { Length: > 0 and <= 512 } ? title : null;
    }

    private static async Task<FrameSearchResult?> LookUpTitle(FramePlaybackTarget target, string name,
        BrowseClient client, CancellationToken ct)
    {
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            var results = await client.Search(name, page, ct);
            var matching = results.Items.FirstOrDefault(item => item.IsSeries == target.IsSeries &&
                FramePlaybackIdentity.Matches(item.ExternalId, target.FrameId, target.IsSeries));
            if (matching is not null || !results.CanPageForward)
                return matching;
        }
        return null;
    }
}
