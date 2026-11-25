using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Frame;

public sealed class FrameConfiguration : BasePluginConfiguration
{
    public string CatalogUrl { get; set; } = "";
    public string MoviesPath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public string MediaHosts { get; set; } = "";
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
