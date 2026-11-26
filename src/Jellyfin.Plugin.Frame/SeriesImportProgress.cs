using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Frame;

public sealed record FrameWebSeriesProgress(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("imported")] int Imported,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("pending")] int Pending,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

internal sealed class SeriesImportProgress(FrameWebSeriesProgress initial, IReadOnlySet<Guid>? retryPending = null,
    Action<FrameWebSeriesProgress>? publish = null)
{
    private readonly HashSet<Guid> pending = retryPending is null ? [] : [.. retryPending];
    private int total = initial.Total;
    private string? pendingReason;

    internal FrameWebSeriesProgress Current { get; private set; } = initial;

    internal int Pending => pending.Count;

    internal IReadOnlySet<Guid> Remaining => pending;

    internal FrameItem Plan(FrameItem item)
    {
        var seasons = item.Seasons ?? [];
        if (retryPending is null)
        {
            pending.UnionWith(seasons.SelectMany(season => season.Episodes ?? []).Select(episode => episode.Id));
            total = pending.Count;
        }
        Update("importing");
        return retryPending is null ? item : item with
        {
            Seasons = seasons.Select(season => season with
                { Episodes = (season.Episodes ?? []).Where(episode => pending.Contains(episode.Id)).ToArray() })
                .Where(season => season.Episodes is { Length: > 0 })
                .ToArray()
        };
    }

    internal void Saved(Guid id)
    {
        if (pending.Remove(id))
            Update("importing");
    }

    internal void ExplainPending(string? reason) => pendingReason = reason;

    internal void Finish(string? error)
    {
        var status = pending.Count > 0 && (error is null || total > pending.Count) ? "partial"
            : error is null ? "completed" : "failed";
        Update(status, error ?? pendingReason);
    }

    private void Update(string status, string? message = null)
    {
        Current = new(status, total - pending.Count, total, pending.Count, message);
        publish?.Invoke(Current);
    }
}
