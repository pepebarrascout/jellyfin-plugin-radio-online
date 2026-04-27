using Jellyfin.Plugin.RadioOnline.ScheduledTasks;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RadioOnline;

/// <summary>
/// Registers services for the Radio Online plugin with Jellyfin's DI container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers all plugin services, scheduled tasks, and hosted services.
    /// </summary>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Radio streaming background service (runs continuously)
        serviceCollection.AddHostedService<RadioStreamingHostedService>();

        // Liquidsoap Telnet client
        serviceCollection.AddSingleton<LiquidsoapClient>();

        // Schedule manager
        serviceCollection.AddSingleton<ScheduleManagerService>();

        // Audio provider (retrieves playlists from Jellyfin library)
        serviceCollection.AddSingleton<AudioProviderService>();

        // Scheduled task (dashboard visibility)
        serviceCollection.AddSingleton<IScheduledTask, RadioSchedulerTask>();
    }
}
