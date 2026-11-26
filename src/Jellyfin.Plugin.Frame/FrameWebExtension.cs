using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Frame;

public sealed class FrameWebServices : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddScoped<FrameTvSearchFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<FrameTvSearchFilter>());
        serviceCollection.AddSingleton<FrameWebSeriesImports>();
        serviceCollection.AddHostedService(services => services.GetRequiredService<FrameWebSeriesImports>());
        serviceCollection.AddHostedService<FrameWebExtension>();
    }
}

public sealed class FrameWebExtension(ILogger<FrameWebExtension> logger) : IHostedService
{
    private const string TransformationId = "f7c79ec7-1b8f-414b-962a-50af95986cda";
    private const string ScriptPath = "../Frame/Browse/WebScript";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var pluginInterface = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly => assembly.GetName().Name == "Jellyfin.Plugin.FileTransformation")
            ?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterface is null)
        {
            logger.LogInformation("A busca do Frame na lupa requer File Transformation.");
            return Task.CompletedTask;
        }

        try
        {
            // File Transformation receives its own Newtonsoft JObject across the plugin load contexts.
            var register = pluginInterface.GetMethod("RegisterTransformation",
                BindingFlags.Public | BindingFlags.Static);
            var parameters = register?.GetParameters();
            if (parameters is not { Length: 1 })
                throw new InvalidOperationException("Versão incompatível do File Transformation.");

            var parse = parameters[0].ParameterType.GetMethod("Parse", [typeof(string)]);
            var payload = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = "index.html",
                callbackAssembly = typeof(FrameWebExtension).Assembly.FullName,
                callbackClass = typeof(FrameWebExtension).FullName,
                callbackMethod = nameof(TransformIndex)
            });
            var argument = parse?.Invoke(null, [payload])
                ?? throw new InvalidOperationException("Formato incompatível do File Transformation.");
            register!.Invoke(null, [argument]);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException or AmbiguousMatchException)
        {
            logger.LogWarning("O Frame não conseguiu registrar a busca no File Transformation ({Type}).",
                ex.GetBaseException().GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string TransformIndex(FrameWebHtml payload)
    {
        var html = payload.Contents;
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0 || html.Contains(ScriptPath, StringComparison.Ordinal))
            return html;

        var version = typeof(FrameWebExtension).Assembly.GetName().Version;
        return html.Insert(bodyEnd, $"<script defer src=\"{ScriptPath}?v={version}\"></script>");
    }
}

public sealed class FrameWebHtml
{
    public string Contents { get; set; } = string.Empty;
}