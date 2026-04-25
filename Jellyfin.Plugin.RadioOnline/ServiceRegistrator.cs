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
        // FFmpeg streaming service (uses Jellyfin's bundled FFmpeg)
        serviceCollection.AddSingleton<IcecastStreamingService>();

        // Schedule manager
        serviceCollection.AddSingleton<ScheduleManagerService>();

        // Audio provider (retrieves playlists from Jellyfin library)
        serviceCollection.AddSingleton<AudioProviderService>();

        // Radio streaming background service - register as Singleton first so the
        // controller can inject it, then register as HostedService using the same instance
        serviceCollection.AddSingleton<RadioStreamingHostedService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RadioStreamingHostedService>());

        // Scheduled task (dashboard visibility)
        serviceCollection.AddSingleton<IScheduledTask, RadioSchedulerTask>();
    }
}
