using Jellyfin.Plugin.RadioOnline.ScheduledTasks;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RadioOnline;

/// <summary>
/// Registers services for the Radio Online plugin with Jellyfin's DI container.
/// This class is automatically discovered by Jellyfin via reflection.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers all plugin services, scheduled tasks, and hosted services.
    /// </summary>
    /// <param name="serviceCollection">The service collection to register services with.</param>
    /// <param name="applicationHost">The server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register the radio streaming background service (runs continuously)
        serviceCollection.AddHostedService<RadioStreamingHostedService>();

        // Register the FFmpeg streaming service (works inside Docker containers)
        serviceCollection.AddSingleton<IcecastStreamingService>();

        // Register the Liquidsoap streaming service (requires Liquidsoap installed in container)
        serviceCollection.AddSingleton<LiquidsoapStreamingService>();

        // Register the schedule manager service
        serviceCollection.AddSingleton<ScheduleManagerService>();

        // Register the audio provider service
        serviceCollection.AddSingleton<AudioProviderService>();

        // Register the radio scheduler task (for dashboard visibility and manual triggers)
        serviceCollection.AddSingleton<IScheduledTask, RadioSchedulerTask>();
    }
}
