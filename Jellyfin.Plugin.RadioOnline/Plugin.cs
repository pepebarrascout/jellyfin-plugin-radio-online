using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.RadioOnline.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RadioOnline;

/// <summary>
/// Main entry point for the Jellyfin Radio Online plugin.
/// Provides automated online radio streaming to Icecast servers with
/// weekly playlist scheduling capabilities.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The unique identifier for this plugin.
    /// </summary>
    public static readonly Guid PluginGuid = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <summary>
    /// Singleton instance of the plugin for access by services.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths service.</param>
    /// <param name="xmlSerializer">The XML serialization service.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Radio Online";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description =>
        "Automated online radio plugin that streams audio to Icecast servers " +
        "with weekly playlist scheduling. Supports m4a and ogg audio formats.";



    /// <summary>
    /// Gets the plugin web pages (configuration dashboard).
    /// </summary>
    /// <returns>An enumerable of plugin page info objects.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.html",
                    GetType().Namespace),
            },
        ];
    }
}
