using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class HomeCollectionsTests
{
    [Fact]
    public void ReadsDiscoverySectionsByStableIdAndKeepsTheirNames()
    {
        var home = JsonSerializer.Deserialize<BrowseHome>(
            """
            {
              "rows": [
                {"id":"movies","type":"discovery","title":"Filmes para descobrir","items":[]},
                {"id":"series","type":"discovery","title":"Séries para descobrir","items":[]},
                {"id":"movies","type":"discovery","title":"Repetida","items":[]},
                {"id":"continue","type":"continue","title":"Continuar assistindo","items":[]},
                {"id":"for-you","type":"personalized","title":"Escolhidos para você","items":[]},
                {"id":"library","type":"library","title":"Da sua lista","items":[]},
                {"id":"bad/row","type":"discovery","title":"Nome inválido","items":[]},
                {"id":"empty-title","type":"discovery","title":" ","items":[]}
              ],
              "partial": false
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var rows = SyncHomeCollectionsTask.DiscoveryRows(home!);

        Assert.Collection(rows,
            movie => Assert.Equal(("movies", "Filmes para descobrir"), (movie.Id, movie.Title)),
            series => Assert.Equal(("series", "Séries para descobrir"), (series.Id, series.Title)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedSynchronizationAddsOnlyNewLinksAndRemovesOnlyWithCompleteness(bool complete)
    {
        var common = Guid.NewGuid();
        var old = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var current = new HashSet<Guid> { common, old };
        var desired = new HashSet<Guid> { common, latest };

        var first = SyncHomeCollectionsTask.MembershipChanges(current, desired, complete);
        Assert.Equal(latest, Assert.Single(first.Add));
        Assert.Equal(complete ? [old] : Array.Empty<Guid>(), first.Remove);

        current.UnionWith(first.Add);
        current.ExceptWith(first.Remove);
        var repeated = SyncHomeCollectionsTask.MembershipChanges(current, desired, complete);
        Assert.Empty(repeated.Add);
        Assert.Empty(repeated.Remove);
    }
}
