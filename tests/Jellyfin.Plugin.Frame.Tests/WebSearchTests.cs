using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class WebSearchTests
{
    [Fact]
    public void RegistersOneSameServerScriptForAWebClientWithOrWithoutABaseUrl()
    {
        const string html = "<html><body><main></main></body></html>";
        var transformed = FrameWebExtension.TransformIndex(new() { Contents = html });
        var script = $"../Frame/Browse/WebScript?v={typeof(FrameWebExtension).Assembly.GetName().Version}";
        Assert.Contains($"<script defer src=\"{script}\"></script></body>", transformed);
        Assert.Equal(transformed, FrameWebExtension.TransformIndex(new() { Contents = transformed }));
        Assert.Equal("/Frame/Browse/WebScript", new Uri(new Uri("https://example.org/web/index.html"), script).AbsolutePath);
        Assert.Equal("/jellyfin/Frame/Browse/WebScript",
            new Uri(new Uri("https://example.org/jellyfin/web/"), script).AbsolutePath);
        Assert.Equal("invalid html", FrameWebExtension.TransformIndex(new() { Contents = "invalid html" }));
    }

    [Fact]
    public void AdminRoutesKeepElevationAndOnlyTheStaticScriptIsAnonymous()
    {
        var controller = typeof(FrameBrowseController);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());

        var routes = controller.GetMethods()
            .Select(method => (Method: method, Route: method.GetCustomAttribute<HttpMethodAttribute>()?.Template))
            .Where(entry => entry.Route is not null);
        foreach (var (method, route) in routes)
        {
            var anonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
            switch (route)
            {
                case "WebScript":
                    Assert.NotNull(anonymous);
                    break;
                case "WebSearch":
                case "WebMovie":
                case "WebSeries":
                case "WebPoster":
                case "WebImport":
                    Assert.Null(anonymous);
                    Assert.Null(authorize);
                    break;
                default:
                    Assert.Null(anonymous);
                    Assert.Equal("RequiresElevation", authorize?.Policy);
                    break;
            }
        }
    }

    [Fact]
    public void SerializesTheItemIdWithTheNameExpectedByTheWebClient()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"{{\"itemId\":\"{id}\"}}", JsonSerializer.Serialize(new FrameWebItemResult(id)));
    }
}