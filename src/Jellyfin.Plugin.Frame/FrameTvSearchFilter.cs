using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

internal sealed class FrameTvSearchFilter(IApplicationPaths paths, IDataProtectionProvider protection,
    IUserManager users, ILibraryManager library, IFileSystem fileSystem, ILibraryMonitor monitor,
    FrameWebSeriesImports seriesImports, ILogger<FrameTvSearchFilter> logger)
    : IAsyncActionFilter
{
    // Android TV can query film and series groups concurrently. Keep remote work bounded across sessions.
    private static readonly SemaphoreSlim Requests = new(2, 2);
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(6);
    private static readonly Regex YearSuffix = new(@"(?:\s+-\s+(?<year>[0-9]{4})|\s+\((?<year>[0-9]{4})\))$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var args = context.ActionArguments;
        var principal = context.HttpContext.User;
        if (Plugin.Instance?.Configuration is { TvSearchEnabled: true } config &&
            config.GetBrowseOrigin() is { } origin &&
            context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Items", ActionName: "GetItems" } &&
            context.ModelState.IsValid && principal.Identity?.IsAuthenticated == true &&
            string.Equals(principal.FindFirst("Jellyfin-Client")?.Value, "Jellyfin for Android TV", StringComparison.OrdinalIgnoreCase) &&
            args.TryGetValue("searchTerm", out var value) && value is string term && term.Trim().Length is >= 2 and <= 120 &&
            args.TryGetValue("recursive", out var recursive) && recursive is true &&
            (!args.TryGetValue("parentId", out var parent) || parent is null) &&
            (!args.TryGetValue("startIndex", out var start) || start is null or 0) &&
            args.TryGetValue("includeItemTypes", out var include) && include is BaseItemKind[] kinds &&
            kinds.Any(kind => kind is BaseItemKind.Movie or BaseItemKind.Series) &&
            FrameBrowseController.GetLibraryAccess(config, principal, users, library) is { } access &&
            (!args.TryGetValue("userId", out var userId) || userId is null || Equals(userId, access.User.Id)))
        {
            var movies = access.Movies && kinds.Contains(BaseItemKind.Movie);
            var series = access.Series && kinds.Contains(BaseItemKind.Series);
            if ((movies || series) && Requests.Wait(0))
            {
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
                    budget.CancelAfter(RequestBudget);
                    await IncludeFrameTitles(config, origin, access.User, term.Trim(), movies, series, budget.Token);
                }
                catch (OperationCanceledException)
                {
                    // Keep the native search usable when the remote catalog is slow.
                }
                catch (Exception ex)
                {
                    logger.LogDebug("A consulta do Frame para Android TV não pôde ser concluída ({Type}).", ex.GetType().Name);
                }
                finally
                {
                    Requests.Release();
                }
            }
        }

        await next();
    }

    private async Task IncludeFrameTitles(FrameConfiguration config, Uri origin, User user, string term, bool movies,
        bool series, CancellationToken ct)
    {
        var client = await BrowseAccountSession.CreateClient(origin, paths.PluginConfigurationsPath, protection, ct);
        var page = await client.Search(term, 1, ct);
        foreach (var match in ExactMatches(page.Items, term, movies, series))
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Plugin.Instance?.Configuration, config))
                return;

            var path = match.IsSeries
                ? FrameWebItemIndex.MediaFolder(config.SeriesPath, BrowseClient.SeriesId(match.ExternalId))
                : FrameWebItemIndex.MoviePath(config.MoviesPath, BrowseClient.MovieId(match.ExternalId));
            if (FrameWebItemIndex.FindIndexed(library, path, match.IsSeries) is not null)
                continue;

            var selection = new FrameImportRequest(term, 1, match.ExternalId, match.IsSeries, AllEpisodes: match.IsSeries);
            if (match.IsSeries)
            {
                if (seriesImports.Enqueue(config, selection) != FrameWebSeriesImports.QueueResult.Accepted)
                    continue;

                // Wait for the first indexed episode path to make the series visible to the native query.
                while (FrameWebItemIndex.Find(library, path, user, series: true) is null &&
                    seriesImports.Progress(config, match.ExternalId)?.Status is "queued" or "importing")
                    await Task.Delay(150, ct);
            }
            else
            {
                await ImportMovie(config, client, user, path, selection, ct);
            }
        }
    }

    private async Task ImportMovie(FrameConfiguration config, BrowseClient client, User user, string path,
        FrameImportRequest selection, CancellationToken ct)
    {
        if (await client.GetTitleForImport(selection, ct) is not { } movie || string.IsNullOrWhiteSpace(movie.VideoUrl))
            return;

        var hosts = SyncCatalogTask.ReadMediaAllowlist(config.MediaHosts);
        _ = SyncCatalogTask.PlaybackUrl(movie.VideoUrl, hosts);
        await SyncCatalogTask.SyncLock.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(Plugin.Instance?.Configuration, config) ||
                FrameWebItemIndex.FindIndexed(library, path, series: false) is not null)
                return;

            await SyncCatalogTask.Materialize(movie with { Images = null }, config, hosts, ct, preserveExistingNfo: true);
            try
            {
                if (movie.Images is { } images)
                {
                    using var artworkBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    artworkBudget.CancelAfter(TimeSpan.FromSeconds(1));
                    hosts.Add(BrowseTitleMetadata.ArtworkHost);
                    try
                    {
                        await HomeSync.WriteMissingArtwork(Path.GetDirectoryName(path)!, images, null, hosts, artworkBudget.Token);
                    }
                    catch (Exception ex) when (HomeSync.IsItemFailure(ex, ct))
                    {
                        logger.LogDebug("O Frame importou o filme, mas não conseguiu registrar sua imagem ({Type}).", ex.GetType().Name);
                    }
                }

                FrameWebItemIndex.Index(library, fileSystem, path, movie, user, ct);
            }
            finally
            {
                monitor.ReportFileSystemChanged(path);
            }
        }
        finally
        {
            SyncCatalogTask.SyncLock.Release();
        }
    }

    internal static IEnumerable<FrameSearchResult> ExactMatches(IEnumerable<FrameSearchResult> items, string term,
        bool movies, bool series)
    {
        var (query, year) = SearchTitle(term);
        if (query.Length == 0)
            return [];

        return items.Where(item =>
            {
                if (item.IsSeries ? !series : !movies)
                    return false;

                var (name, embeddedYear) = SearchTitle(item.Name);
                return name == query && (!year.HasValue || year == (item.Year > 0 ? item.Year : embeddedYear));
            })
            .GroupBy(item => item.IsSeries)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single());
    }

    private static (string Name, int? Year) SearchTitle(string title)
    {
        var clean = title.Trim();
        int? year = null;
        var suffix = YearSuffix.Match(clean);
        if (suffix.Success &&
            int.TryParse(suffix.Groups["year"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 1888 and <= 2099)
        {
            year = parsed;
            clean = clean[..suffix.Index];
        }

        var name = new StringBuilder();
        foreach (var character in clean.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            name.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return (string.Join(' ', name.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)), year);
    }
}
