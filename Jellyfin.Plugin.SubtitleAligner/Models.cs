namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>A stored result for one video/subtitle pair.</summary>
public sealed class ValidationRecord
{
    public string AlignmentMode { get; set; } = "Quick";

    public Guid LibraryId { get; set; }

    public string LibraryName { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string SubtitlePath { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public double? OffsetSeconds { get; set; }

    public double? DriftSecondsPerHour { get; set; }

    public double? LinearSlope { get; set; }

    public double? LinearInterceptSeconds { get; set; }

    public double? BreakSeconds { get; set; }

    public double? BeforeBreakOffsetSeconds { get; set; }

    public double? AfterBreakOffsetSeconds { get; set; }

    public string Confidence { get; set; } = string.Empty;

    public string SemanticConfidence { get; set; } = string.Empty;

    public string TimingConfidence { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int MatchedWords { get; set; }

    public double MedianMatchScore { get; set; }

    public double? P90ResidualSeconds { get; set; }

    public int Anchors { get; set; }

    public int StrictAnchors { get; set; }

    public int GuidedFuzzyAnchors { get; set; }

    public int Samples { get; set; }

    public int AlignedCueCount { get; set; }

    public int AdjustedCueCount { get; set; }

    public int VerifiedCueCount { get; set; }

    public int ProtectedCueCount { get; set; }

    public double CueAlignmentDeadBandSeconds { get; set; }

    public string TimingEvidence { get; set; } = string.Empty;

    public int SpeechActivitySamples { get; set; }

    public double? SpeechActivityOffsetSeconds { get; set; }

    public double? SpeechActivityCorrelation { get; set; }

    public double? SpeechActivityUniqueness { get; set; }

    public string CueAdjustmentStrategy { get; set; } = string.Empty;

    public bool IndividualCueAlignmentAvailable { get; set; }

    public bool IndividualCueAlignmentRecommended { get; set; }

    public bool WholeTrackShiftRecommended { get; set; }

    public int TotalCueCount { get; set; }

    public double AlignmentCoveragePercent { get; set; }

    public int LocalAlignmentSupportSeconds { get; set; }

    public double AnalysisElapsedSeconds { get; set; }

    public double MediaDurationSeconds { get; set; }

    public List<LocalTimingPoint> TimingPoints { get; set; } = [];

    public List<SubtitleCueTimingPoint> CueTimingPoints { get; set; } = [];

    public string Message { get; set; } = string.Empty;

    public string CorrectionStatus { get; set; } = string.Empty;

    public string CorrectionMessage { get; set; } = string.Empty;

    public string CorrectedSubtitlePath { get; set; } = string.Empty;

    public string CorrectedSubtitleSha256 { get; set; } = string.Empty;

    public string CorrectedWithMode { get; set; } = string.Empty;

    public DateTime? CorrectedUtc { get; set; }

    public DateTime AnalyzedUtc { get; set; }
}

/// <summary>Progress for one Jellyfin library's most recent subtitle sweep.</summary>
public sealed class LibraryScanRecord
{
    public Guid LibraryId { get; set; }

    public string LibraryName { get; set; } = string.Empty;

    public string State { get; set; } = "Not scanned";

    public int DiscoveredPairs { get; set; }

    public int ProcessedPairs { get; set; }

    public int ErrorPairs { get; set; }

    public DateTime? LastStartedUtc { get; set; }

    public DateTime? LastCompletedUtc { get; set; }
}

/// <summary>Current plugin activity and stored-result summary.</summary>
public sealed class ValidatorStatus
{
    public bool Running { get; set; }

    public string CurrentItem { get; set; } = string.Empty;

    public string LastMessage { get; set; } = string.Empty;

    public DateTime? LastRunUtc { get; set; }

    public int Total { get; set; }

    public int InSync { get; set; }

    public int Corrected { get; set; }

    public int NeedsAttention { get; set; }

    public int InsufficientEvidence { get; set; }

    public IReadOnlyList<LibraryScanRecord> Libraries { get; set; } = [];

    public IReadOnlyList<ValidationRecord> Recent { get; set; } = [];
}

/// <summary>Installation state for the plugin-managed Whisper model.</summary>
public sealed class WhisperModelStatus
{
    public string ModelName { get; set; } = string.Empty;

    public string ModelDisplayName { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public bool Valid { get; set; }

    public bool Busy { get; set; }

    public long SizeBytes { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>Capabilities relevant to local speech inference.</summary>
public sealed class HostCapabilities
{
    public string OperatingSystem { get; set; } = string.Empty;

    public string Architecture { get; set; } = string.Empty;

    public string Processor { get; set; } = string.Empty;

    public int LogicalProcessors { get; set; }

    public long TotalMemoryBytes { get; set; }

    public long AvailableMemoryBytes { get; set; }

    public IReadOnlyList<string> CpuFeatures { get; set; } = [];

    public bool GpuDeviceDetected { get; set; }

    public string InferenceBackend { get; set; } = string.Empty;
}

/// <summary>A managed model compatible with the detected host and engine.</summary>
public sealed class CompatibleModelOption
{
    public string ModelName { get; set; } = string.Empty;

    public string ModelDisplayName { get; set; } = string.Empty;

    public string QualityTier { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public long DownloadSizeBytes { get; set; }

    public long EstimatedWorkingMemoryBytes { get; set; }

    public bool Installed { get; set; }
}

/// <summary>One model's result on the optional throughput sample.</summary>
public sealed class ModelBenchmarkResult
{
    public string ModelName { get; set; } = string.Empty;

    public string ModelDisplayName { get; set; } = string.Empty;

    public long ModelSizeBytes { get; set; }

    public double LoadSeconds { get; set; }

    public double InferenceSeconds { get; set; }

    public double RealTimeFactor { get; set; }

    public int Runs { get; set; }

    public bool Successful { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>Host model compatibility advice and the most recent optional benchmarks.</summary>
public sealed class HostAssessmentStatus
{
    public bool Running { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public HostCapabilities Host { get; set; } = new();

    public IReadOnlyList<ModelBenchmarkResult> Results { get; set; } = [];

    public IReadOnlyList<CompatibleModelOption> CompatibleModels { get; set; } = [];

    public string RecommendedModelName { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    public DateTime? CompletedUtc { get; set; }
}

/// <summary>Requests an optional throughput benchmark for one compatible managed model.</summary>
public sealed class ModelBenchmarkRequest
{
    public string ModelName { get; set; } = string.Empty;
}

/// <summary>Identifies a video from the file name displayed by Jellyfin's subtitle editor.</summary>
public sealed class SyncByFileRequest
{
    public string FileName { get; set; } = string.Empty;

    public Guid? ItemId { get; set; }
}

/// <summary>Identifies a newly queued per-title alignment run.</summary>
public sealed class ItemSyncAccepted
{
    public Guid RunId { get; set; }

    public Guid ItemId { get; set; }
}

/// <summary>Live, title-scoped progress safe to show in the subtitle editor.</summary>
public sealed class ItemSyncStatus
{
    public string AlignmentMode { get; set; } = "Quick";

    public Guid RunId { get; set; }

    public Guid ItemId { get; set; }

    public string State { get; set; } = "Queued";

    public bool Complete { get; set; }

    public int ProgressPercent { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime RequestedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? AnalysisStartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public IReadOnlyList<ItemSubtitleResult> Results { get; set; } = [];
}

/// <summary>The latest persisted findings and any active run for one title.</summary>
public sealed class ItemResultSnapshot
{
    public Guid ItemId { get; set; }

    public Guid? ActiveRunId { get; set; }

    public DateTime? LastAnalyzedUtc { get; set; }

    public IReadOnlyList<ItemSubtitleResult> Results { get; set; } = [];
}

/// <summary>One subtitle result rendered in the title's subtitle editor.</summary>
public sealed class ItemSubtitleResult
{
    public string AlignmentMode { get; set; } = "Quick";

    public string SubtitleName { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Confidence { get; set; } = string.Empty;

    public string SemanticConfidence { get; set; } = string.Empty;

    public string TimingConfidence { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string CorrectionStatus { get; set; } = string.Empty;

    public string CorrectionMessage { get; set; } = string.Empty;

    public double? OffsetSeconds { get; set; }

    public double? DriftSecondsPerHour { get; set; }

    public double? BreakSeconds { get; set; }

    public int Anchors { get; set; }

    public int StrictAnchors { get; set; }

    public int GuidedFuzzyAnchors { get; set; }

    public int Samples { get; set; }

    public int MatchedWords { get; set; }

    public int AlignedCueCount { get; set; }

    public int AdjustedCueCount { get; set; }

    public int VerifiedCueCount { get; set; }

    public int ProtectedCueCount { get; set; }

    public double CueAlignmentDeadBandSeconds { get; set; }

    public string TimingEvidence { get; set; } = string.Empty;

    public int SpeechActivitySamples { get; set; }

    public double? SpeechActivityOffsetSeconds { get; set; }

    public double? SpeechActivityCorrelation { get; set; }

    public double? SpeechActivityUniqueness { get; set; }

    public string CueAdjustmentStrategy { get; set; } = string.Empty;

    public bool IndividualCueAlignmentAvailable { get; set; }

    public bool IndividualCueAlignmentRecommended { get; set; }

    public bool WholeTrackShiftRecommended { get; set; }

    public int TotalCueCount { get; set; }

    public double AlignmentCoveragePercent { get; set; }

    public double AnalysisElapsedSeconds { get; set; }

    public double MediaDurationSeconds { get; set; }

    public int LocalAlignmentSupportSeconds { get; set; }

    public IReadOnlyList<LocalTimingPoint> TimingPoints { get; set; } = [];

    public IReadOnlyList<SubtitleCueTimingPoint> CueTimingPoints { get; set; } = [];

    public double? P90ResidualSeconds { get; set; }

    public DateTime AnalyzedUtc { get; set; }
}

/// <summary>A searchable Jellyfin video that has supported text subtitles.</summary>
public sealed class VideoItemOption
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public int SubtitleCount { get; set; }
}

/// <summary>One point in a smoothed, local subtitle timing map.</summary>
public sealed class LocalTimingPoint
{
    public double SubtitleSeconds { get; set; }

    public double OffsetSeconds { get; set; }
}

/// <summary>One subtitle cue's position and whether direct speech evidence adjusted it.</summary>
public sealed class SubtitleCueTimingPoint
{
    /// <summary>
    /// Gets or sets the zero-based timing-row index in the source subtitle file.
    /// This remains stable when non-speech rows are excluded from matching.
    /// </summary>
    public int? SourceCueIndex { get; set; }

    public double SubtitleSeconds { get; set; }

    public double? OffsetSeconds { get; set; }

    public bool Adjusted { get; set; }

    public string MatchKind { get; set; } = string.Empty;

    public int MatchedWords { get; set; }

    public double MatchScore { get; set; }
}

/// <summary>Requests a connectivity check for a trusted external whisper-server.</summary>
public sealed class WhisperServerTestRequest
{
    public string Url { get; set; } = string.Empty;
}

/// <summary>Result of an external whisper-server connectivity check.</summary>
public sealed class WhisperServerTestResult
{
    public bool Reachable { get; set; }

    public string Message { get; set; } = string.Empty;
}

internal enum SubtitleCueRole
{
    Dialogue,
    Lyrics,
    Sign
}

internal sealed record SubtitleCue(
    double StartSeconds,
    double EndSeconds,
    string Text,
    SubtitleCueRole Role = SubtitleCueRole.Dialogue,
    int SourceCueIndex = -1);

internal sealed record SubtitleInput(
    Guid LibraryId,
    string LibraryName,
    Guid ItemId,
    string ItemName,
    string VideoPath,
    string SubtitlePath,
    string Language,
    string AudioLanguage,
    double MediaDurationSeconds,
    int? EmbeddedSubtitleOrdinal,
    string SubtitleExtension);

internal enum WhisperTimingGranularity
{
    Segment,
    Token,
    Word
}

internal sealed record WhisperSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    WhisperTimingGranularity TimingGranularity,
    double? AverageLogProbability = null,
    double? NoSpeechProbability = null,
    IReadOnlyList<double>? DecoderTokenProbabilities = null);

internal sealed record WhisperTranscript(
    IReadOnlyList<WhisperSegment> Segments,
    WhisperResponseMetadata Metadata,
    IReadOnlyList<WhisperSegment>? DecoderSegments = null);

internal sealed record WhisperResponseMetadata(
    bool HasExtensionMetadata,
    string ExtensionSchemaVersion,
    string RequestId,
    string Task,
    string RequestedModel,
    string SelectedModel,
    string Backend,
    string ServerVersion,
    string DetectedLanguage,
    double? DetectedLanguageProbability,
    string DetectedLanguageProbabilityBasis,
    string EffectiveLanguage,
    double? AudioDurationSeconds,
    double? PreparationSeconds,
    double? InferenceSeconds,
    double? RealtimeFactor,
    bool? CacheHit,
    bool? InferenceIncludesPreparation,
    int? DecodeCount,
    WhisperTimingProvenance? Timing,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<WhisperSpeechInterval> SpeechIntervals,
    bool RouteCapabilitiesKnown = false,
    IReadOnlyList<string>? RouteTimingGranularities = null,
    bool? RouteVadIntervals = null,
    string BuildIdentity = "",
    string RequestedLanguage = "",
    double? QueueSeconds = null,
    double? ColdStartSeconds = null,
    string CacheType = "");

internal sealed record WhisperTimingProvenance(
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Actual,
    string Basis,
    bool? Reliable,
    string Reliability,
    IReadOnlyDictionary<string, WhisperTimingChannelProvenance>? Channels = null);

internal sealed record WhisperTimingChannelProvenance(
    string Basis,
    bool? Reliable,
    string Reliability);

internal sealed record WhisperSpeechInterval(double StartSeconds, double EndSeconds, double? Confidence);

internal sealed record WhisperInferenceProgress(
    string Phase,
    double FractionCompleted,
    double? EtaSeconds,
    string State = "running",
    int? Attempt = null,
    int? MaximumAttempts = null);

internal sealed record WhisperRouteCapabilities(
    bool HasExtensionContract,
    string ObjectType,
    string ExtensionSchemaVersion,
    string Task,
    string RequestedModel,
    string SelectedModel,
    string Backend,
    bool? ModelFilesCached,
    IReadOnlyList<string> SupportedTasks,
    IReadOnlyList<string> TimingGranularities,
    bool? VadIntervals,
    IReadOnlyList<string> SupportedLanguages,
    bool? AutomaticLanguageDetection,
    string EffectiveLanguage,
    long? MaximumUploadBytes,
    double? MaximumAudioDurationSeconds,
    IReadOnlyList<string> Limitations,
    bool? ConfigurationCanChange,
    string BuildIdentity = "",
    IReadOnlyList<string>? TimingBases = null,
    IReadOnlyList<string>? ResponseFormats = null,
    bool? DualOutput = null,
    int? DualOutputDecodeCount = null,
    bool? Progress = null,
    bool? Cancellation = null,
    bool? WordProbability = null,
    bool? DecoderTokenProbabilities = null,
    bool? SegmentAverageLogProbability = null,
    bool? SegmentNoSpeechProbability = null,
    bool? DetectedLanguageProbability = null,
    IReadOnlyList<string>? ErrorCodes = null);

internal sealed class WhisperApiException : InvalidOperationException
{
    internal WhisperApiException(
        int statusCode,
        string code,
        string message,
        string details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    internal int StatusCode { get; }

    internal string Code { get; }

    internal string Details { get; }
}

/// <summary>Selects the fast sampled or complete local-timing workflow.</summary>
public enum SubtitleAlignmentMode
{
    Quick,
    Full
}

/// <summary>One scheduled or administrator-requested alignment pass.</summary>
public sealed record SubtitleRunRequest(
    bool IgnoreRunWindow,
    Guid? ItemId,
    bool ForceCorrection,
    SubtitleAlignmentMode Mode = SubtitleAlignmentMode.Quick,
    Guid? RunId = null,
    bool BypassTranscriptionCache = false);

/// <summary>Current disk usage and retention settings for cached speech analysis.</summary>
public sealed record TranscriptionCacheStatus(
    bool Enabled,
    int EntryCount,
    long SizeBytes,
    int RetentionDays,
    long MaximumBytes,
    DateTime? OldestAccessUtc,
    DateTime? NewestAccessUtc,
    long SessionHits,
    long SessionMisses);

/// <summary>Result of a transcription-cache cleanup pass.</summary>
public sealed record TranscriptionCacheCleanupResult(
    int RemovedEntries,
    long RemovedBytes,
    int RemainingEntries,
    long RemainingBytes);

internal sealed record TimingAnchor(
    double MediaSeconds,
    double SubtitleSeconds,
    double OffsetSeconds,
    double Similarity,
    double Uniqueness,
    int MatchedWords,
    double Weight);

internal sealed record DetailedTimingMatch(
    TimingAnchor Summary,
    IReadOnlyList<TimingAnchor> LocalAnchors);

internal sealed record TranscriptSection(
    double StartSeconds,
    double DurationSeconds,
    IReadOnlyList<WhisperSegment> Segments,
    WhisperResponseMetadata? Metadata,
    IReadOnlyList<WhisperSegment> DecoderSegments)
{
    public TranscriptSection(
        double startSeconds,
        double durationSeconds,
        IReadOnlyList<WhisperSegment> segments,
        WhisperResponseMetadata? metadata = null)
        : this(startSeconds, durationSeconds, segments, metadata, segments)
    {
    }
}

internal sealed record CueTimingMatch(
    int CueIndex,
    double SubtitleSeconds,
    double MediaSeconds,
    double MediaStartSeconds,
    double MediaEndSeconds,
    double OffsetSeconds,
    double Similarity,
    double Uniqueness,
    int MatchedWords,
    int TotalWords,
    bool Fuzzy,
    bool OnsetAnchored = true);

internal sealed record GuidedFuzzyMatchOptions(
    double MinimumPhraseScore,
    double MinimumUniqueness,
    int MinimumDistinctiveTokens,
    double MinimumTokenSimilarity,
    bool UseStemming,
    bool UsePhoneticMatching,
    double PhoneticWeight);

internal sealed record QuickAlignmentClassificationOptions(
    double ResidualToleranceSeconds = 0.50,
    double InSyncOffsetSeconds = 0.35,
    int ModelImprovementPercent = 40,
    double MinimumDriftAcrossTitleSeconds = 0.75,
    double MinimumTimingBreakSeconds = 1.0,
    int HighConfidenceMinAnchors = 5,
    int HighConfidenceMinWords = 60,
    int HighConfidenceMinCoveragePercent = 60,
    double HighConfidenceMinMatchScore = 0.72,
    int MediumConfidenceMinAnchors = 4,
    int MediumConfidenceMinWords = 35,
    int MediumConfidenceMinCoveragePercent = 40,
    double MediumConfidenceMinMatchScore = 0.62);

internal sealed record SpeechActivitySample(
    double WindowStartSeconds,
    SpeechActivityAlignment Alignment,
    string EvidenceType);

internal sealed class StoredResults
{
    public int SchemaVersion { get; set; } = 2;

    public List<ValidationRecord> Results { get; set; } = [];

    public List<LibraryScanRecord> Libraries { get; set; } = [];
}

internal sealed record LibraryDiscovery(Guid LibraryId, string LibraryName, IReadOnlyList<SubtitleInput> Inputs);

internal sealed record LibrarySweepPlan(
    Guid LibraryId,
    string LibraryName,
    int DiscoveredPairs,
    int CurrentPairs);
