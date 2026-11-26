using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class FrameTvSearchTests
{
    private static readonly FrameSearchResult[] Titles =
    [
        new("1", "Ação no Céu - 2020", 2020),
        new("2", "Ação no Céu - 2024", 2024),
        new("3", "Ação no Céu (2022)", 2022, IsSeries: true),
        new("4", "Blade Runner 2049", 2017),
        new("5", "Blade Runner", 1982)
    ];

    [Theory]
    [InlineData("acao no ceu", true, true, new[] { "3" })]
    [InlineData("Ação no Céu - 2020", true, true, new[] { "1" })]
    [InlineData("ação no céu", true, false, new string[0])]
    [InlineData("ação", true, true, new string[0])]
    [InlineData("Blade Runner", true, false, new[] { "5" })]
    [InlineData("Blade Runner 2049", true, false, new[] { "4" })]
    public void FindsOnlyOneExactMatchPerPermittedType(string query, bool movies, bool series, string[] expected) =>
        Assert.Equal(expected, FrameTvSearchFilter.ExactMatches(Titles, query, movies, series).Select(item => item.ExternalId));
}
