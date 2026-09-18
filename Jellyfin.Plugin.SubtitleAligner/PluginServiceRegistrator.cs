using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Registers the plugin services.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(WhisperServer.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(15);
        });
        serviceCollection.AddHttpClient(WhisperModelManager.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
        });
        serviceCollection.AddSingleton<ResultStore>();
        serviceCollection.AddSingleton<TranscriptionCache>();
        serviceCollection.AddSingleton<InferenceActivityGate>();
        serviceCollection.AddSingleton<WhisperModelManager>();
        serviceCollection.AddSingleton<WhisperServer>();
        serviceCollection.AddSingleton<HostAssessmentService>();
        serviceCollection.AddSingleton<SubtitleValidationService>();
        serviceCollection.AddSingleton<IStartupFilter, ClientScriptInjectionFilter>();
    }
}
