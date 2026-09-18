using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SubtitleAligner.Tasks;

/// <summary>Scheduled subtitle validation pass.</summary>
public sealed class SubtitleValidationTask : IScheduledTask
{
    private readonly ILocalizationManager _localization;
    private readonly SubtitleValidationService _service;

    /// <summary>Initializes a new instance of the <see cref="SubtitleValidationTask"/> class.</summary>
    public SubtitleValidationTask(ILocalizationManager localization, SubtitleValidationService service)
    {
        _localization = localization;
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Align Subtitle Timing";

    /// <inheritdoc />
    public string Key => "AlignSubtitleTiming";

    /// <inheritdoc />
    public string Description => "Samples external text subtitles with Whisper and can create corrected copies for high-confidence timing problems.";

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
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _service.RunAsync(progress, _service.ConsumeRunRequest(), cancellationToken);
    }
}
