using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Frame;

public sealed class FrameConfiguration : BasePluginConfiguration
{
    public bool OnDemand { get; set; }
    public bool WebSearchEnabled { get; set; }
    public bool SyncHomeMovies { get; set; }
    public bool SyncHomeSeries { get; set; }
    public bool SyncHomeCollections { get; set; }
    public string BrowseApiUrl { get; set; } = "";
    public string CatalogUrl { get; set; } = "";
    public string MoviesPath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public string MediaHosts { get; set; } = "";

    internal Uri? GetBrowseOrigin()
    {
        if (!OnDemand || !Uri.TryCreate(BrowseApiUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return null;
        return new Uri(origin.AbsoluteUri.TrimEnd('/') + "/");
    }
}

public sealed class Plugin : BasePlugin<FrameConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
    }

    internal static Plugin? Instance { get; private set; }
    public override string Name => "Frame";
    public override Guid Id => new("bb053bff-fcf8-4e86-b3b6-83b83866063e");

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = Name, EmbeddedResourcePath = "Jellyfin.Plugin.Frame.Configuration.html" }
    ];
}
