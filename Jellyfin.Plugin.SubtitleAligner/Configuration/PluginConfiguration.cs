using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleAligner.Configuration;

/// <summary>Configuration for Subtitle Aligner.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class.</summary>
    public PluginConfiguration()
    {
        UseRunWindow = false;
        RunWindowStart = "02:00";
        RunWindowEnd = "06:00";
        WhisperServerPath = string.Empty;
        ExternalWhisperUrl = string.Empty;
        FallbackToLocalWhisper = true;
        WhisperModelPath = string.Empty;
        ManagedModelName = WhisperModelCatalog.Default.FileName;
        LastHostAssessmentSummary = string.Empty;
        FfmpegPath = string.Empty;
        SamplePositions = "10,30,50,70,90,20,40,60,80";
        InitialSamplesPerSubtitle = 5;
        SamplesPerSubtitle = 9;
        ClipDurationSeconds = 30;
        CueLeadSeconds = 5;
        MinimumMatchScore = 0.62;
        MinimumMatchUniqueness = 0.08;
        MinimumMatchedWords = 8;
        EnableGuidedFuzzyMatching = true;
        EnableCrossLanguageFuzzyMatching = true;
        CrossLanguageMinimumCueCoverage = 0.35;
        MaximumIndividualCueOffsetSeconds = 2.0;
        FuzzySearchRadiusSeconds = 60;
        MinimumFuzzyMatchScore = 0.55;
        MinimumFuzzyDistinctiveTokens = 5;
        MinimumFuzzyTokenSimilarity = 0.82;
        UseFuzzyStemming = true;
        UseFuzzyPhoneticMatching = true;
        FuzzyPhoneticWeight = 0.40;
        MinimumFuzzyMatchUniqueness = 0.08;
        CueAlignmentDeadBandSeconds = 0.10;
        CrossLanguageVadDeadBandSeconds = 0.75;
        LocalAlignmentSupportSeconds = 90;
        ResidualToleranceSeconds = 0.50;
        InSyncOffsetSeconds = 0.35;
        ModelImprovementPercent = 40;
        MinimumDriftAcrossTitleSeconds = 0.75;
        MinimumTimingBreakSeconds = 1.0;
        HighConfidenceMinAnchors = 5;
        HighConfidenceMinWords = 60;
        HighConfidenceMinCoveragePercent = 60;
        HighConfidenceMinMatchScore = 0.72;
        MediumConfidenceMinAnchors = 4;
        MediumConfidenceMinWords = 35;
        MediumConfidenceMinCoveragePercent = 40;
        MediumConfidenceMinMatchScore = 0.62;
        WhisperThreads = 0;
        EnableTranscriptionCache = true;
        TranscriptionCacheRetentionDays = 7;
        TranscriptionCacheMaximumMegabytes = 2048;
        CreateCorrectedSidecars = false;
    }

    /// <summary>Gets or sets a value indicating whether automatic runs are limited to the configured window.</summary>
    public bool UseRunWindow { get; set; }

    /// <summary>Gets or sets the local start time in HH:mm format.</summary>
    public string RunWindowStart { get; set; }

    /// <summary>Gets or sets the local end time in HH:mm format.</summary>
    public string RunWindowEnd { get; set; }

    /// <summary>Gets or sets an optional path to whisper-server.</summary>
    public string WhisperServerPath { get; set; }

    /// <summary>Gets or sets an optional trusted external whisper-server base URL.</summary>
    public string ExternalWhisperUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether an unavailable external server falls back to the local engine.</summary>
    public bool FallbackToLocalWhisper { get; set; }

    /// <summary>Gets or sets an optional path to the Whisper model.</summary>
    public string WhisperModelPath { get; set; }

    /// <summary>Gets or sets the selected plugin-managed model file name.</summary>
    public string ManagedModelName { get; set; }

    /// <summary>Gets or sets the completion time of the latest successful host assessment.</summary>
    public DateTime? LastHostAssessmentUtc { get; set; }

    /// <summary>Gets or sets a concise result from the latest successful host assessment.</summary>
    public string LastHostAssessmentSummary { get; set; }

    /// <summary>Gets or sets an optional path to FFmpeg.</summary>
    public string FfmpegPath { get; set; }

    /// <summary>Gets or sets the ordered percentage positions used for sampling.</summary>
    public string SamplePositions { get; set; }

    /// <summary>Gets or sets the sample count used before an adaptive early result.</summary>
    public int InitialSamplesPerSubtitle { get; set; }

    /// <summary>Gets or sets the maximum number of samples per subtitle.</summary>
    public int SamplesPerSubtitle { get; set; }

    /// <summary>Gets or sets each audio sample length in seconds.</summary>
    public int ClipDurationSeconds { get; set; }

    /// <summary>Gets or sets how many seconds before a selected cue a sample begins.</summary>
    public int CueLeadSeconds { get; set; }

    /// <summary>Gets or sets the minimum accepted local-alignment score.</summary>
    public double MinimumMatchScore { get; set; }

    /// <summary>Gets or sets the minimum score gap from the next-best match.</summary>
    public double MinimumMatchUniqueness { get; set; }

    /// <summary>Gets or sets the minimum matched words in one anchor.</summary>
    public int MinimumMatchedWords { get; set; }

    /// <summary>Gets or sets a value indicating whether failed strict sections receive a time-guided fuzzy pass.</summary>
    public bool EnableGuidedFuzzyMatching { get; set; }

    /// <summary>Gets or sets a value indicating whether translated subtitle text may use fuzzy cue matching.</summary>
    public bool EnableCrossLanguageFuzzyMatching { get; set; }

    /// <summary>Gets or sets the minimum fraction of subtitle cue words that a translated-text match must cover.</summary>
    public double CrossLanguageMinimumCueCoverage { get; set; }

    /// <summary>Gets or sets the largest absolute offset considered for an individual cue.</summary>
    public double MaximumIndividualCueOffsetSeconds { get; set; }

    /// <summary>Gets or sets how far around the predicted subtitle position the fuzzy pass searches.</summary>
    public int FuzzySearchRadiusSeconds { get; set; }

    /// <summary>Gets or sets the minimum weighted phrase-match score accepted by the fuzzy pass.</summary>
    public double MinimumFuzzyMatchScore { get; set; }

    /// <summary>Gets or sets the minimum distinctive matched tokens in a fuzzy anchor.</summary>
    public int MinimumFuzzyDistinctiveTokens { get; set; }

    /// <summary>Gets or sets the minimum normalized spelling similarity for a fuzzy token match.</summary>
    public double MinimumFuzzyTokenSimilarity { get; set; }

    /// <summary>Gets or sets a value indicating whether English suffix stemming is used by the fuzzy pass.</summary>
    public bool UseFuzzyStemming { get; set; }

    /// <summary>Gets or sets a value indicating whether low-weight English phonetic matches are used by the fuzzy pass.</summary>
    public bool UseFuzzyPhoneticMatching { get; set; }

    /// <summary>Gets or sets how much a phonetic token match contributes relative to an exact match.</summary>
    public double FuzzyPhoneticWeight { get; set; }

    /// <summary>Gets or sets the minimum fuzzy-score gap from the next-best candidate.</summary>
    public double MinimumFuzzyMatchUniqueness { get; set; }

    /// <summary>Gets or sets the largest absolute direct-cue offset treated as already aligned.</summary>
    public double CueAlignmentDeadBandSeconds { get; set; }

    /// <summary>Gets or sets the no-change band for source-linked translation segments supported by VAD.</summary>
    public double CrossLanguageVadDeadBandSeconds { get; set; }

    /// <summary>Gets or sets the support radius used to display and preserve results from legacy timing-map analyses.</summary>
    public int LocalAlignmentSupportSeconds { get; set; }

    /// <summary>Gets or sets the maximum p90 model residual in seconds.</summary>
    public double ResidualToleranceSeconds { get; set; }

    /// <summary>Gets or sets the largest absolute offset considered in sync.</summary>
    public double InSyncOffsetSeconds { get; set; }

    /// <summary>Gets or sets the required residual improvement for drift or break models.</summary>
    public int ModelImprovementPercent { get; set; }

    /// <summary>Gets or sets the minimum fitted drift across the title.</summary>
    public double MinimumDriftAcrossTitleSeconds { get; set; }

    /// <summary>Gets or sets the minimum jump for a timing-break result.</summary>
    public double MinimumTimingBreakSeconds { get; set; }

    /// <summary>Gets or sets the minimum anchors for high confidence.</summary>
    public int HighConfidenceMinAnchors { get; set; }

    /// <summary>Gets or sets the minimum matched words for high confidence.</summary>
    public int HighConfidenceMinWords { get; set; }

    /// <summary>Gets or sets the minimum title coverage percentage for high confidence.</summary>
    public int HighConfidenceMinCoveragePercent { get; set; }

    /// <summary>Gets or sets the minimum median match score for high confidence.</summary>
    public double HighConfidenceMinMatchScore { get; set; }

    /// <summary>Gets or sets the minimum anchors for medium confidence.</summary>
    public int MediumConfidenceMinAnchors { get; set; }

    /// <summary>Gets or sets the minimum matched words for medium confidence.</summary>
    public int MediumConfidenceMinWords { get; set; }

    /// <summary>Gets or sets the minimum title coverage percentage for medium confidence.</summary>
    public int MediumConfidenceMinCoveragePercent { get; set; }

    /// <summary>Gets or sets the minimum median match score for medium confidence.</summary>
    public double MediumConfidenceMinMatchScore { get; set; }

    /// <summary>Gets or sets the number of Whisper CPU threads.</summary>
    public int WhisperThreads { get; set; }

    /// <summary>Gets or sets a value indicating whether validated speech responses are cached on the Jellyfin host.</summary>
    public bool EnableTranscriptionCache { get; set; }

    /// <summary>Gets or sets how many days an unused cached speech response is retained.</summary>
    public int TranscriptionCacheRetentionDays { get; set; }

    /// <summary>Gets or sets the maximum on-disk transcription cache size in megabytes.</summary>
    public int TranscriptionCacheMaximumMegabytes { get; set; }

    /// <summary>Gets or sets a value indicating whether high-confidence corrections are written as new sidecar files.</summary>
    public bool CreateCorrectedSidecars { get; set; }
}
