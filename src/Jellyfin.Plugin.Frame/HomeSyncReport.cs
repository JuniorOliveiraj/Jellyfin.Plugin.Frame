using System.Text.Json;

namespace Jellyfin.Plugin.Frame;

internal sealed record HomeSyncIssue(string Title, string? ExternalId, string? Episode, string Reason);

internal sealed class HomeSyncReport
{
    private const int MaxIssueDetails = 200;

    public DateTimeOffset FinishedAt { get; set; }
    public string Status { get; set; } = "failed";
    public string? Message { get; set; }
    public bool PartialHome { get; set; }
    public int Movies { get; set; }
    public int Series { get; set; }
    public int Episodes { get; set; }
    public int NewMovies { get; set; }
    public int NewSeries { get; set; }
    public int NewEpisodes { get; set; }
    public int PendingTitles { get; set; }
    public int PendingEpisodes { get; set; }
    public int OmittedIssues { get; set; }
    public List<HomeSyncIssue> Issues { get; init; } = [];

    internal void AddPending(BrowseHomeItem card, string reason, string? episode = null)
    {
        if (episode is null)
            PendingTitles++;
        else
            PendingEpisodes++;

        if (Issues.Count >= MaxIssueDetails)
        {
            OmittedIssues++;
            return;
        }

        var id = BrowseClient.IsValidId(card.Item.ExternalId) ? card.Item.ExternalId : null;
        var title = string.IsNullOrWhiteSpace(card.Item.Name) ? "Título sem nome" : card.Item.Name;
        Issues.Add(new(title, id, episode, reason));
    }
}

internal static class HomeSyncReportStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<HomeSyncReport?> Read(string directory, bool series, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(ReportPath(directory, series), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<HomeSyncReport>(stream, Json, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static async Task Write(string directory, bool series, HomeSyncReport report)
    {
        Directory.CreateDirectory(directory);
        var path = ReportPath(directory, series);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(report, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string ReportPath(string directory, bool series) =>
        Path.Combine(directory, series ? "Jellyfin.Plugin.Frame.home-series.json" : "Jellyfin.Plugin.Frame.home-movies.json");
}
