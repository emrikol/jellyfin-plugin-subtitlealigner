using System.Globalization;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>
/// Validates subtitle timing with small Whisper samples.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable plugin identifier.</summary>
    public static readonly Guid PluginId = Guid.Parse("8f7de91f-ddab-4402-9c9d-f15a36fa119e");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the active plugin instance.</summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <inheritdoc />
    public override string Name => "Subtitle Aligner";

    /// <inheritdoc />
    public override string Description => "Checks text subtitles against spoken audio and writes conservative corrected sidecars without changing source files.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "subtitlealigner",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.subtitlealigner.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "subtitlealignerjs",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.subtitlealigner.js",
                    GetType().Namespace)
            }
        ];
    }
}
