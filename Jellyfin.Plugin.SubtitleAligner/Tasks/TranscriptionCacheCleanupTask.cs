using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner.Tasks;

/// <summary>Removes expired and least-recently-used cached speech responses.</summary>
public sealed class TranscriptionCacheCleanupTask : IScheduledTask
{
    private readonly ILocalizationManager _localization;
    private readonly TranscriptionCache _cache;
    private readonly ILogger<TranscriptionCacheCleanupTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscriptionCacheCleanupTask"/> class.</summary>
    public TranscriptionCacheCleanupTask(
        ILocalizationManager localization,
        TranscriptionCache cache,
        ILogger<TranscriptionCacheCleanupTask> logger)
    {
        _localization = localization;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Clean Subtitle Aligner transcription cache";

    /// <inheritdoc />
    public string Key => "CleanSubtitleAlignerTranscriptionCache";

    /// <inheritdoc />
    public string Description => "Removes cached speech analysis that is expired or exceeds Subtitle Aligner's configured size limit.";

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        TranscriptionCacheCleanupResult result = await _cache.CleanupAsync(
                Plugin.Instance.Configuration,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Subtitle Aligner transcription-cache cleanup removed {RemovedEntries} entries ({RemovedBytes} bytes); {RemainingEntries} entries ({RemainingBytes} bytes) remain.",
            result.RemovedEntries,
            result.RemovedBytes,
            result.RemainingEntries,
            result.RemainingBytes);
    }
}
