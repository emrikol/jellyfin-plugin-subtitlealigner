using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleAligner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Discovers subtitles, analyzes audio, and classifies timing.</summary>
public sealed class SubtitleValidationService : IDisposable
{
    private const double MinimumRetainedCueDurationSeconds = 1.0;
    private const int AnalysisVersion = 12;
    private const string MinorTimingVariationStatus = "Minor timing variation";
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt", ".ass", ".ssa"
    };

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<SubtitleValidationService> _logger;
    private readonly ResultStore _resultStore;
    private readonly WhisperServer _whisperServer;
    private readonly WhisperModelManager _modelManager;
    private readonly InferenceActivityGate _inferenceGate;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly object _requestGate = new();
    private readonly object _itemRunGate = new();
    private readonly Dictionary<Guid, ItemRunState> _itemRuns = [];
    private SubtitleRunRequest? _pendingRequest;
    private bool _running;
    private string _currentItem = string.Empty;
    private string _lastMessage = "Not run yet.";
    private DateTime? _lastRunUtc;

    /// <summary>Initializes a new instance of the <see cref="SubtitleValidationService"/> class.</summary>
    public SubtitleValidationService(
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        ILogger<SubtitleValidationService> logger,
        ResultStore resultStore,
        WhisperServer whisperServer,
        WhisperModelManager modelManager,
        InferenceActivityGate inferenceGate)
    {
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
        _resultStore = resultStore;
        _whisperServer = whisperServer;
        _modelManager = modelManager;
        _inferenceGate = inferenceGate;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runGate.Dispose();
    }

    /// <summary>Allows the next queued task to run outside its configured window.</summary>
    public void RequestManualRun()
    {
        lock (_requestGate)
        {
            _pendingRequest = new SubtitleRunRequest(true, null, false);
        }
    }

    /// <summary>Queues one selected title for an immediate correction attempt.</summary>
    public ItemSyncAccepted RequestItemRun(
        Guid itemId,
        SubtitleAlignmentMode mode = SubtitleAlignmentMode.Quick,
        bool bypassTranscriptionCache = false)
    {
        Guid runId = Guid.NewGuid();
        DateTime requestedUtc = DateTime.UtcNow;
        lock (_itemRunGate)
        {
            _itemRuns[runId] = new ItemRunState
            {
                RunId = runId,
                ItemId = itemId,
                AlignmentMode = mode.ToString(),
                RequestedUtc = requestedUtc,
                Message = "Waiting for Jellyfin's subtitle task."
            };
            PruneItemRuns();
        }

        SubtitleRunRequest? replaced;
        lock (_requestGate)
        {
            replaced = _pendingRequest;
            _pendingRequest = new SubtitleRunRequest(
                true,
                itemId,
                true,
                mode,
                runId,
                bypassTranscriptionCache);
        }

        if (replaced?.RunId is Guid replacedRunId)
        {
            CompleteItemRun(replacedRunId, "Superseded by a newer title request.", "Stopped");
        }

        return new ItemSyncAccepted { RunId = runId, ItemId = itemId };
    }

    /// <summary>Consumes a pending request or returns the normal scheduled-run request.</summary>
    public SubtitleRunRequest ConsumeRunRequest()
    {
        lock (_requestGate)
        {
            SubtitleRunRequest request = _pendingRequest ?? new SubtitleRunRequest(false, null, false);
            _pendingRequest = null;
            return request;
        }
    }

    /// <summary>Finds videos with supported external text subtitles.</summary>
    public IReadOnlyList<VideoItemOption> SearchItems(string searchTerm)
    {
        string queryText = searchTerm.Trim();
        if (queryText.Length < 2)
        {
            return [];
        }

        List<VideoItemOption> options = [];
        HashSet<Guid> seen = [];
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (options.Count >= 50 || !Guid.TryParse(folder.ItemId, out Guid libraryId))
            {
                continue;
            }

            string libraryName = string.IsNullOrWhiteSpace(folder.Name) ? "Unnamed library" : folder.Name;
            IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = libraryId,
                SearchTerm = queryText,
                Limit = 50
            });

            foreach (Video video in items.OfType<Video>())
            {
                if (options.Count >= 50 || !seen.Add(video.Id))
                {
                    continue;
                }

                List<SubtitleInput> inputs = DiscoverInputs(
                    libraryId,
                    libraryName,
                    [video],
                    new HashSet<string>(StringComparer.Ordinal),
                    includeEmbedded: true);
                if (inputs.Count == 0)
                {
                    continue;
                }

                options.Add(new VideoItemOption
                {
                    ItemId = video.Id,
                    Name = string.IsNullOrWhiteSpace(video.Name) ? "Untitled" : video.Name,
                    Kind = video.GetType().Name,
                    LibraryName = libraryName,
                    SubtitleCount = inputs.Count
                });
            }
        }

        return options
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Finds one video from the exact media file name shown in Jellyfin's subtitle editor.</summary>
    public Guid? FindUniqueItemByFileName(string fileName, Guid? candidateItemId = null)
    {
        string requestedName = Path.GetFileName(fileName.Trim());
        if (requestedName.Length == 0 || !string.Equals(requestedName, fileName.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        if (candidateItemId.HasValue
            && _libraryManager.GetItemById(candidateItemId.Value) is Video candidate
            && string.Equals(Path.GetFileName(candidate.Path), requestedName, StringComparison.OrdinalIgnoreCase))
        {
            return candidate.Id;
        }

        HashSet<Guid> matches = [];
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out Guid libraryId))
            {
                continue;
            }

            IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = libraryId
            });
            foreach (Video video in items.OfType<Video>())
            {
                if (string.Equals(Path.GetFileName(video.Path), requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(video.Id);
                    if (matches.Count > 1)
                    {
                        return null;
                    }
                }
            }
        }

        return matches.Count == 1 ? matches.Single() : null;
    }

    /// <summary>Returns current activity and recent results.</summary>
    public async Task<ValidatorStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        StoredResults stored = await _resultStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ValidationRecord> results = stored.Results;
        foreach (ValidationRecord result in results)
        {
            NormalizeLegacyMinorTimingVariation(result, Plugin.Instance.Configuration);
        }

        bool running;
        string currentItem;
        string lastMessage;
        DateTime? lastRunUtc;
        lock (_statusGate)
        {
            running = _running;
            currentItem = _currentItem;
            lastMessage = _lastMessage;
            lastRunUtc = _lastRunUtc;
        }

        return new ValidatorStatus
        {
            Running = running,
            CurrentItem = currentItem,
            LastMessage = lastMessage,
            LastRunUtc = lastRunUtc,
            Total = results.Count,
            InSync = results.Count(result => result.Status is "In sync" or MinorTimingVariationStatus),
            Corrected = results.Count(result => result.CorrectionStatus == "Created"),
            InsufficientEvidence = results.Count(result => result.Status == "Insufficient evidence"),
            NeedsAttention = results.Count(result => result.Status is not ("In sync" or MinorTimingVariationStatus or "Insufficient evidence")
                && result.CorrectionStatus != "Created"),
            Libraries = stored.Libraries
                .OrderBy(library => library.LibraryName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Recent = results.OrderByDescending(result => result.AnalyzedUtc).Take(50).ToArray()
        };
    }

    /// <summary>Returns live progress and safe, title-scoped results for one requested run.</summary>
    public ItemSyncStatus? GetItemRunStatus(Guid runId)
    {
        lock (_itemRunGate)
        {
            if (!_itemRuns.TryGetValue(runId, out ItemRunState? state))
            {
                return null;
            }

            return new ItemSyncStatus
            {
                AlignmentMode = state.AlignmentMode,
                RunId = state.RunId,
                ItemId = state.ItemId,
                State = state.State,
                Complete = state.Complete,
                ProgressPercent = state.ProgressPercent,
                Message = state.Message,
                RequestedUtc = state.RequestedUtc,
                StartedUtc = state.StartedUtc,
                AnalysisStartedUtc = state.AnalysisStartedUtc,
                CompletedUtc = state.CompletedUtc,
                Results = state.Results.ToArray()
            };
        }
    }

    /// <summary>Returns the latest persisted findings and any active run for one title.</summary>
    public async Task<ItemResultSnapshot> GetItemResultsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        Guid? activeRunId;
        lock (_itemRunGate)
        {
            activeRunId = _itemRuns.Values
                .Where(state => state.ItemId == itemId && !state.Complete)
                .OrderByDescending(state => state.RequestedUtc)
                .Select(state => (Guid?)state.RunId)
                .FirstOrDefault();
        }

        BaseItem? item = _libraryManager.GetItemById(itemId);
        double fallbackMediaDurationSeconds = item?.RunTimeTicks.GetValueOrDefault() > 0
            ? TimeSpan.FromTicks(item.RunTimeTicks.GetValueOrDefault()).TotalSeconds
            : 0;
        IReadOnlyList<ValidationRecord> storedResults = await _resultStore
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);
        storedResults = await ConsolidateLegacyEmbeddedResultsAsync(
                itemId,
                item,
                storedResults,
                cancellationToken)
            .ConfigureAwait(false);
        ValidationRecord[] records = storedResults
            .Where(result => result.ItemId == itemId)
            .OrderBy(result => result.SubtitlePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<ItemSubtitleResult> results = [];
        foreach (ValidationRecord result in records)
        {
            NormalizeLegacyMinorTimingVariation(result, Plugin.Instance.Configuration);
            IReadOnlyList<SubtitleCueTimingPoint> cueTimingPoints = await ResolveCueTimingPointsAsync(result, item, cancellationToken)
                .ConfigureAwait(false);
            results.Add(ToItemSubtitleResult(result, fallbackMediaDurationSeconds, cueTimingPoints));
        }

        return new ItemResultSnapshot
        {
            ItemId = itemId,
            ActiveRunId = activeRunId,
            LastAnalyzedUtc = results.Count == 0 ? null : results.Max(result => result.AnalyzedUtc),
            Results = results
        };
    }

    /// <summary>Runs one resumable validation pass.</summary>
    public async Task RunAsync(IProgress<double> progress, SubtitleRunRequest request, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.RunId.HasValue)
            {
                StartItemRun(
                    request.RunId.Value,
                    request.Mode == SubtitleAlignmentMode.Full
                        ? "Preparing a full local alignment for this title."
                        : "Discovering subtitle tracks for this title.");
            }

            PluginConfiguration configuration = Plugin.Instance.Configuration;
            if (!request.IgnoreRunWindow && !IsInsideRunWindow(configuration, DateTime.Now.TimeOfDay))
            {
                SetMessage("Outside the configured run window.");
                progress.Report(100);
                return;
            }

            SetRunning(true, string.Empty, "Discovering external text subtitles.");
            bool targeted = request.ItemId.HasValue;
            List<LibraryDiscovery> libraries = DiscoverLibraries(request.ItemId);
            if (targeted && libraries.Sum(library => library.Inputs.Count) == 0)
            {
                const string message = "The selected title has no supported text subtitle tracks.";
                SetCompleted(message);
                CompleteItemRun(request.RunId, message);
                progress.Report(100);
                return;
            }

            UpdateItemRun(request.RunId, 5, "Checking the selected speech engine.");
            string modelIdentity = string.IsNullOrWhiteSpace(configuration.ExternalWhisperUrl)
                ? await _modelManager.GetIdentityAsync(configuration.WhisperModelPath, cancellationToken).ConfigureAwait(false)
                : $"external:{configuration.ExternalWhisperUrl.Trim()}:{configuration.FallbackToLocalWhisper}";
            IReadOnlyList<ValidationRecord> existing = await _resultStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
            if (request.ItemId is Guid requestedItemId)
            {
                existing = await ConsolidateLegacyEmbeddedResultsAsync(
                        requestedItemId,
                        _libraryManager.GetItemById(requestedItemId),
                        existing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Dictionary<string, ValidationRecord> byKey = existing
                .GroupBy(ResultKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.AnalyzedUtc).First(), StringComparer.Ordinal);
            List<LibraryRun> runs = [];
            HashSet<string> discoveredKeys = new(StringComparer.Ordinal);
            int unchanged = 0;
            int totalPending = 0;

            foreach (LibraryDiscovery library in libraries)
            {
                List<PendingSubtitle> pending = [];
                int libraryCurrent = 0;
                foreach (SubtitleInput input in library.Inputs)
                {
                    string fingerprint = await CreateFingerprintAsync(
                        input,
                        configuration,
                        modelIdentity,
                        request.Mode,
                        cancellationToken).ConfigureAwait(false);
                    string key = ResultKey(input);
                    discoveredKeys.Add(key);
                    byKey.TryGetValue(key, out ValidationRecord? old);
                    if (!targeted
                        && old is not null
                        && await IsCurrentAsync(old, input, fingerprint, configuration, cancellationToken).ConfigureAwait(false))
                    {
                        unchanged++;
                        libraryCurrent++;
                    }
                    else
                    {
                        pending.Add(new PendingSubtitle(input, fingerprint, old));
                    }
                }

                totalPending += pending.Count;
                runs.Add(new LibraryRun(library.LibraryId, library.LibraryName, library.Inputs.Count, libraryCurrent, pending));
            }

            if (!targeted)
            {
                foreach (ValidationRecord embedded in existing.Where(result =>
                             IsEmbeddedSubtitlePath(result.SubtitlePath)
                             && _libraryManager.GetItemById(result.ItemId) is not null))
                {
                    discoveredKeys.Add(ResultKey(embedded));
                }

                await _resultStore.BeginSweepAsync(
                    runs.Select(run => new LibrarySweepPlan(run.LibraryId, run.LibraryName, run.DiscoveredPairs, run.CurrentPairs)).ToArray(),
                    discoveredKeys,
                    cancellationToken).ConfigureAwait(false);
            }

            if (totalPending == 0)
            {
                string message = $"Nothing changed. {unchanged} subtitle(s) already have current results.";
                SetCompleted(message);
                CompleteItemRun(request.RunId, message);
                progress.Report(100);
                return;
            }

            Directory.CreateDirectory(TemporaryDirectory);
            int completed = 0;
            int failures = 0;
            bool windowClosed = false;
            bool serverStarted = false;
            using IDisposable inferenceLease = await _inferenceGate.AcquireAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                UpdateItemRun(request.RunId, 10, "Connecting to the speech engine.");
                await _whisperServer.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
                serverStarted = true;
                BeginItemAnalysis(request.RunId);
                UpdateItemRun(
                    request.RunId,
                    15,
                    $"Connected to the {_whisperServer.BackendDescription}. Checking {totalPending} subtitle track(s).");
                foreach (LibraryRun run in runs)
                {
                    bool libraryFinished = true;
                    foreach (PendingSubtitle pending in run.Pending)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!request.IgnoreRunWindow && !IsInsideRunWindow(configuration, DateTime.Now.TimeOfDay))
                        {
                            windowClosed = true;
                            libraryFinished = false;
                            break;
                        }

                        SetCurrentItem($"{run.LibraryName} — {pending.Input.ItemName}");
                        string subtitleName = FriendlySubtitleDescription(pending.Input.Language);
                        UpdateItemRun(
                            request.RunId,
                            15 + (int)Math.Floor((completed * 80.0) / totalPending),
                            $"Checking {subtitleName} ({completed + 1} of {totalPending}).");
                        Stopwatch itemTimer = Stopwatch.StartNew();
                        ValidationRecord result;
                        try
                        {
                            result = await AnalyzeAsync(
                                pending.Input,
                                pending.Fingerprint,
                                configuration,
                                request.Mode,
                                request.BypassTranscriptionCache,
                                (sample, maximumSamples, inferenceProgress) =>
                                {
                                    double partialSubtitle = sample / (double)Math.Max(1, maximumSamples);
                                    int itemProgress = 15 + (int)Math.Floor(((completed + partialSubtitle) * 80.0) / totalPending);
                                    int currentStep = Math.Clamp((int)Math.Ceiling(sample), 1, maximumSamples);
                                    string progressMessage = inferenceProgress?.Phase switch
                                    {
                                        "receiving_result" => $"Receiving the completed speech result for {subtitleName}: section {currentStep} of {maximumSamples}.",
                                        "retrying_remote" => $"The external speech service did not return section {currentStep} of {maximumSamples}; retrying remotely (attempt {inferenceProgress.Attempt} of {inferenceProgress.MaximumAttempts}).",
                                        "retrying_local" => $"The external speech service did not return section {currentStep} of {maximumSamples}; retrying it on the NAS.",
                                        "cache_hit" => $"Reusing cached speech analysis for {subtitleName}: section {currentStep} of {maximumSamples}.",
                                        "matching_cues" => $"Matching every {subtitleName} cue against the completed speech analysis.",
                                        _ => sample == 0
                                        ? request.Mode == SubtitleAlignmentMode.Full
                                            ? $"Preparing {maximumSamples} sections for {subtitleName}."
                                            : $"Preparing up to {maximumSamples} samples for {subtitleName}."
                                        : request.Mode == SubtitleAlignmentMode.Full
                                            ? $"Aligning {subtitleName}: section {currentStep} of {maximumSamples}."
                                            : $"Checking {subtitleName}: sample {currentStep} of up to {maximumSamples}."
                                    };
                                    UpdateItemRun(request.RunId, itemProgress, progressMessage);
                                },
                                cancellationToken).ConfigureAwait(false);
                            itemTimer.Stop();
                            if (configuration.CreateCorrectedSidecars || request.ForceCorrection)
                            {
                                UpdateItemRun(request.RunId, null, $"Applying the result for {subtitleName}.");
                                await ApplyCorrectionAsync(
                                    result,
                                    pending.Previous,
                                    pending.Input,
                                    configuration,
                                    cancellationToken).ConfigureAwait(false);
                                if (result.CorrectionStatus == "Error")
                                {
                                    failures++;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            itemTimer.Stop();
                            failures++;
                            _logger.LogWarning(exception, "Subtitle validation failed for item {ItemId}", pending.Input.ItemId);
                            result = CreateFailure(pending.Input, pending.Fingerprint, request.Mode, exception.Message);
                        }

                        result.AnalysisElapsedSeconds = Math.Round(itemTimer.Elapsed.TotalSeconds, 3);
                        result.MediaDurationSeconds = Math.Round(pending.Input.MediaDurationSeconds, 3);

                        // Once analysis (and any atomic sidecar write) finishes, persist ownership and
                        // progress even if the task is cancelled between this item and the next one.
                        await _resultStore.UpsertAsync(result, !targeted, CancellationToken.None).ConfigureAwait(false);
                        RecordItemResult(request.RunId, result);
                        completed++;
                        UpdateItemRun(
                            request.RunId,
                            15 + (int)Math.Floor((completed * 80.0) / totalPending),
                            $"Finished {completed} of {totalPending} subtitle track(s).");
                        progress.Report((completed * 100.0) / totalPending);
                    }

                    if (!targeted && libraryFinished)
                    {
                        await _resultStore.CompleteLibraryAsync(run.LibraryId, cancellationToken).ConfigureAwait(false);
                    }

                    if (windowClosed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (serverStarted)
                {
                    await _whisperServer.StopAsync().ConfigureAwait(false);
                }

                if (!targeted)
                {
                    await _resultStore.PauseIncompleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (windowClosed)
            {
                string message = $"Run window closed after {completed} subtitle(s). Remaining work will resume next time.";
                SetCompleted(message);
                CompleteItemRun(request.RunId, message);
            }
            else
            {
                string scope = targeted ? "Selected title: " : string.Empty;
                string message = $"{scope}analyzed {completed} subtitle(s), skipped {unchanged} unchanged, and recorded {failures} error(s).";
                SetCompleted(message);
                CompleteItemRun(request.RunId, message);
            }

            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            CompleteItemRun(request.RunId, "The title check was stopped before it finished.", "Stopped");
            throw;
        }
        catch (Exception exception)
        {
            CompleteItemRun(request.RunId, $"The title check stopped: {exception.Message}", "Failed");
            throw;
        }
        finally
        {
            SetRunning(false, string.Empty, null);
            _runGate.Release();
        }
    }

    private string TemporaryDirectory => Path.Combine(_applicationPaths.TempDirectory, "subtitle-aligner");

    private List<LibraryDiscovery> DiscoverLibraries(Guid? itemId)
    {
        List<LibraryDiscovery> libraries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out Guid libraryId))
            {
                continue;
            }

            string libraryName = string.IsNullOrWhiteSpace(folder.Name) ? "Unnamed library" : folder.Name;
            InternalItemsQuery query = new()
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = libraryId
            };
            if (itemId.HasValue)
            {
                query.ItemIds = [itemId.Value];
            }

            IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);
            libraries.Add(new LibraryDiscovery(
                libraryId,
                libraryName,
                DiscoverInputs(libraryId, libraryName, items, seen, includeEmbedded: itemId.HasValue)));
        }

        return libraries.OrderBy(library => library.LibraryName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<SubtitleInput> DiscoverInputs(
        Guid libraryId,
        string libraryName,
        IReadOnlyList<BaseItem> items,
        HashSet<string> seen,
        bool includeEmbedded = false)
    {
        List<SubtitleInput> inputs = [];

        foreach (Video video in items.OfType<Video>().Where(video => !string.IsNullOrWhiteSpace(video.Path) && File.Exists(video.Path)))
        {
            IReadOnlyList<MediaStream> mediaStreams = _mediaSourceManager.GetMediaStreams(video.Id);
            double mediaDurationSeconds = video.RunTimeTicks.GetValueOrDefault() > 0
                ? TimeSpan.FromTicks(video.RunTimeTicks.GetValueOrDefault()).TotalSeconds
                : 0;
            string audioLanguage = mediaStreams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .OrderByDescending(stream => stream.IsDefault)
                .Select(stream => stream.Language ?? string.Empty)
                .FirstOrDefault() ?? string.Empty;
            Dictionary<string, string> subtitlePaths = new(StringComparer.Ordinal);
            foreach (MediaStream stream in mediaStreams)
            {
                if (stream.Type != MediaStreamType.Subtitle
                    || !stream.IsExternal
                    || string.IsNullOrWhiteSpace(stream.Path)
                    || IsGeneratedSidecar(stream.Path)
                    || !SupportedExtensions.Contains(Path.GetExtension(stream.Path))
                    || !File.Exists(stream.Path))
                {
                    continue;
                }

                subtitlePaths[stream.Path] = stream.Language ?? string.Empty;
            }

            string? directory = Path.GetDirectoryName(video.Path);
            if (directory is not null && Directory.Exists(directory))
            {
                string baseName = Path.GetFileNameWithoutExtension(video.Path);
                foreach (string path in Directory.EnumerateFiles(directory, baseName + ".*", SearchOption.TopDirectoryOnly)
                             .Where(path => !IsGeneratedSidecar(path) && SupportedExtensions.Contains(Path.GetExtension(path))))
                {
                    subtitlePaths.TryAdd(path, LanguageFromFileName(baseName, path));
                }
            }

            foreach ((string subtitlePath, string language) in subtitlePaths)
            {
                string key = $"{video.Id:N}|{subtitlePath}";
                if (!seen.Add(key))
                {
                    continue;
                }

                inputs.Add(new SubtitleInput(
                    libraryId,
                    libraryName,
                    video.Id,
                    string.IsNullOrWhiteSpace(video.Name) ? Path.GetFileNameWithoutExtension(video.Path) : video.Name,
                    video.Path,
                    subtitlePath,
                    language,
                    audioLanguage,
                    mediaDurationSeconds,
                    null,
                    Path.GetExtension(subtitlePath)));
            }

            if (!includeEmbedded)
            {
                continue;
            }

            int embeddedSubtitleOrdinal = 0;
            foreach (MediaStream stream in mediaStreams
                         .Where(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal)
                         .OrderBy(stream => stream.Index))
            {
                int currentSubtitleOrdinal = embeddedSubtitleOrdinal++;
                string extension = EmbeddedSubtitleExtension(stream.Codec ?? string.Empty);
                if (extension.Length == 0)
                {
                    continue;
                }

                string key = $"{video.Id:N}|embedded-subtitle:{currentSubtitleOrdinal}";
                if (!seen.Add(key))
                {
                    continue;
                }

                string language = stream.Language ?? string.Empty;
                inputs.Add(new SubtitleInput(
                    libraryId,
                    libraryName,
                    video.Id,
                    string.IsNullOrWhiteSpace(video.Name) ? Path.GetFileNameWithoutExtension(video.Path) : video.Name,
                    video.Path,
                    $"[embedded subtitle {currentSubtitleOrdinal + 1}: {(string.IsNullOrWhiteSpace(language) ? stream.Codec : language)}]",
                    language,
                    audioLanguage,
                    mediaDurationSeconds,
                    currentSubtitleOrdinal,
                    extension));
            }
        }

        return inputs.OrderBy(input => input.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(input => input.SubtitlePath, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<ValidationRecord> AnalyzeAsync(
        SubtitleInput input,
        string fingerprint,
        PluginConfiguration configuration,
        SubtitleAlignmentMode mode,
        bool bypassTranscriptionCache,
        Action<double, int, WhisperInferenceProgress?>? sampleProgress,
        CancellationToken cancellationToken)
    {
        string? temporarySubtitle = null;
        try
        {
            string readableSubtitlePath = input.SubtitlePath;
            if (input.EmbeddedSubtitleOrdinal.HasValue)
            {
                temporarySubtitle = await ExtractEmbeddedSubtitleAsync(input, configuration, cancellationToken).ConfigureAwait(false);
                readableSubtitlePath = temporarySubtitle;
            }

            return mode == SubtitleAlignmentMode.Full
                ? await AnalyzeFullMaterializedAsync(
                    input,
                    readableSubtitlePath,
                    fingerprint,
                    configuration,
                    bypassTranscriptionCache,
                    sampleProgress,
                    cancellationToken).ConfigureAwait(false)
                : await AnalyzeMaterializedAsync(
                    input,
                    readableSubtitlePath,
                    fingerprint,
                    configuration,
                    bypassTranscriptionCache,
                    sampleProgress,
                    cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporarySubtitle is not null && File.Exists(temporarySubtitle))
            {
                File.Delete(temporarySubtitle);
            }
        }
    }

    private async Task<ValidationRecord> AnalyzeMaterializedAsync(
        SubtitleInput input,
        string readableSubtitlePath,
        string fingerprint,
        PluginConfiguration configuration,
        bool bypassTranscriptionCache,
        Action<double, int, WhisperInferenceProgress?>? sampleProgress,
        CancellationToken cancellationToken)
    {
        string subtitleLanguage = NormalizeLanguage(input.Language);
        string audioLanguage = NormalizeLanguage(input.AudioLanguage);
        bool languagesDiffer = subtitleLanguage.Length > 0
            && audioLanguage.Length > 0
            && !string.Equals(subtitleLanguage, audioLanguage, StringComparison.Ordinal);
        bool translateToEnglish = subtitleLanguage == "en" && audioLanguage != "en";
        if (languagesDiffer && !translateToEnglish)
        {
            return CreateInsufficient(
                input,
                fingerprint,
                0,
                0,
                $"The audio is marked {input.AudioLanguage} and the subtitle is marked {input.Language}. Whisper can translate speech to English only; this language pair needs a general translation provider.");
        }

        IReadOnlyList<SubtitleCue> cues = await SubtitleParser.ParseAsync(readableSubtitlePath, cancellationToken).ConfigureAwait(false);
        if (cues.Count < 5)
        {
            return CreateInsufficient(input, fingerprint, 0, 0, "The subtitle contains too few usable dialogue cues.");
        }

        int maximumSamples = Math.Clamp(configuration.SamplesPerSubtitle, 3, 12);
        int initialSamples = Math.Clamp(configuration.InitialSamplesPerSubtitle, 3, maximumSamples);
        double clipDuration = Math.Clamp(configuration.ClipDurationSeconds, 10, 60);
        IReadOnlyList<double> windows = SelectWindows(
            cues,
            maximumSamples,
            configuration.SamplePositions,
            Math.Clamp(configuration.CueLeadSeconds, 0, 15));
        List<TimingAnchor> anchors = [];
        List<SpeechActivitySample> speechActivitySamples = [];
        int completedSamples = 0;
        bool individualCueAlignmentAvailable = false;
        string whisperLanguage = translateToEnglish
            ? audioLanguage
            : subtitleLanguage.Length > 0 ? subtitleLanguage : audioLanguage;

        sampleProgress?.Invoke(0, maximumSamples, null);

        foreach (double start in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string wavPath = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".wav");
            try
            {
                await ExtractAudioAsync(input.VideoPath, start, clipDuration, wavPath, configuration, cancellationToken).ConfigureAwait(false);
                WhisperTranscript transcript = await _whisperServer
                    .TranscribeAsync(
                        wavPath,
                        whisperLanguage,
                        translateToEnglish,
                        requireWordTimestamps: false,
                        requireVadIntervals: false,
                        bypassCache: bypassTranscriptionCache,
                        inferenceProgress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (transcript.Metadata.RouteCapabilitiesKnown)
                {
                    individualCueAlignmentAvailable = translateToEnglish
                        ? transcript.Metadata.RouteTimingGranularities?.Contains("segment", StringComparer.Ordinal) == true
                            && transcript.Metadata.RouteVadIntervals == true
                        : transcript.Metadata.RouteTimingGranularities?.Contains("word", StringComparer.Ordinal) == true;
                }

                IReadOnlyList<WhisperSpeechInterval> relativeSpeech = transcript.Metadata.SpeechIntervals.Count > 0
                    ? transcript.Metadata.SpeechIntervals
                    : transcript.Segments
                        .Where(segment => segment.EndSeconds > segment.StartSeconds)
                        .Select(segment => new WhisperSpeechInterval(
                            segment.StartSeconds,
                            segment.EndSeconds,
                            Confidence: null))
                        .ToArray();
                WhisperSpeechInterval[] absoluteSpeech = relativeSpeech
                    .Select(interval => new WhisperSpeechInterval(
                        start + interval.StartSeconds,
                        start + interval.EndSeconds,
                        interval.Confidence))
                    .ToArray();
                SpeechActivityAlignment? activity = SpeechActivityMatcher.Match(
                    absoluteSpeech,
                    cues,
                    start,
                    start + clipDuration,
                    maximumOffsetSeconds: Math.Min(10, clipDuration / 3));
                if (activity is not null)
                {
                    speechActivitySamples.Add(new SpeechActivitySample(
                        start,
                        activity,
                        transcript.Metadata.SpeechIntervals.Count > 0
                            ? "VAD"
                            : "decoder timing"));
                }

                TimingAnchor? anchor = TextMatcher.Match(
                    transcript.Segments,
                    cues,
                    start,
                    Math.Clamp(configuration.MinimumMatchScore, 0.30, 0.95),
                    Math.Clamp(configuration.MinimumMatchUniqueness, 0.01, 0.50),
                    Math.Clamp(configuration.MinimumMatchedWords, 3, 30));
                if (anchor is not null)
                {
                    anchors.Add(anchor);
                }

                completedSamples++;
                sampleProgress?.Invoke(completedSamples, maximumSamples, null);

                if (completedSamples >= initialSamples)
                {
                    ValidationRecord interim = Classify(input, fingerprint, anchors, completedSamples, cues[^1].EndSeconds, configuration);
                    ApplySpeechActivityCorroboration(
                        interim,
                        speechActivitySamples,
                        Math.Clamp(configuration.ResidualToleranceSeconds, 0.10, 3.0));
                    SetIndividualCueRecommendation(interim, individualCueAlignmentAvailable);
                    if (interim.Confidence is "High" or "Medium")
                    {
                        return interim;
                    }
                }
            }
            finally
            {
                if (File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }
        }

        ValidationRecord result = Classify(input, fingerprint, anchors, completedSamples, cues[^1].EndSeconds, configuration);
        ApplySpeechActivityCorroboration(
            result,
            speechActivitySamples,
            Math.Clamp(configuration.ResidualToleranceSeconds, 0.10, 3.0));
        SetIndividualCueRecommendation(result, individualCueAlignmentAvailable);
        return result;
    }

    private async Task<ValidationRecord> AnalyzeFullMaterializedAsync(
        SubtitleInput input,
        string readableSubtitlePath,
        string fingerprint,
        PluginConfiguration configuration,
        bool bypassTranscriptionCache,
        Action<double, int, WhisperInferenceProgress?>? sectionProgress,
        CancellationToken cancellationToken)
    {
        string subtitleLanguage = NormalizeLanguage(input.Language);
        string audioLanguage = NormalizeLanguage(input.AudioLanguage);
        bool languagesDiffer = subtitleLanguage.Length > 0
            && audioLanguage.Length > 0
            && !string.Equals(subtitleLanguage, audioLanguage, StringComparison.Ordinal);
        bool translateToEnglish = subtitleLanguage == "en" && audioLanguage != "en";
        if (languagesDiffer && !translateToEnglish)
        {
            ValidationRecord unsupported = CreateInsufficient(
                input,
                fingerprint,
                0,
                0,
                $"The audio is marked {input.AudioLanguage} and the subtitle is marked {input.Language}. Whisper can translate speech to English only; this language pair needs a general translation provider.");
            unsupported.AlignmentMode = "Full";
            return unsupported;
        }

        IReadOnlyList<SubtitleCue> cues = await SubtitleParser.ParseAsync(readableSubtitlePath, cancellationToken).ConfigureAwait(false);
        if (cues.Count < 5)
        {
            ValidationRecord tooShort = CreateInsufficient(
                input,
                fingerprint,
                0,
                0,
                "The subtitle contains too few usable dialogue cues.");
            tooShort.AlignmentMode = "Full";
            return tooShort;
        }

        const double sectionSeconds = 120;
        const double overlapSeconds = 15;
        double subtitleDuration = Math.Max(sectionSeconds, cues[^1].EndSeconds);
        List<double> starts = [];
        for (double start = 0; start < subtitleDuration; start += sectionSeconds - overlapSeconds)
        {
            starts.Add(start);
        }

        List<TranscriptSection> sections = [];
        int completedSections = 0;
        bool segmentTimingDetected = false;
        string whisperLanguage = translateToEnglish
            ? audioLanguage
            : subtitleLanguage.Length > 0 ? subtitleLanguage : audioLanguage;

        sectionProgress?.Invoke(0, starts.Count, null);
        foreach (double start in starts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string wavPath = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".wav");
            double duration = Math.Min(sectionSeconds, Math.Max(10, subtitleDuration - start));
            try
            {
                await ExtractAudioAsync(
                    input.VideoPath,
                    start,
                    duration,
                    wavPath,
                    configuration,
                    cancellationToken).ConfigureAwait(false);
                WhisperTranscript transcript = await _whisperServer
                    .TranscribeAsync(
                        wavPath,
                        whisperLanguage,
                        translateToEnglish,
                        requireWordTimestamps: !translateToEnglish,
                        requireVadIntervals: translateToEnglish,
                        bypassCache: bypassTranscriptionCache,
                        inferenceProgress: progress => sectionProgress?.Invoke(
                            completedSections + Math.Clamp(progress.FractionCompleted, 0, 0.999),
                            starts.Count,
                            progress),
                        cancellationToken)
                    .ConfigureAwait(false);
                sections.Add(new TranscriptSection(
                    start,
                    duration,
                    transcript.Segments,
                    transcript.Metadata,
                    transcript.DecoderSegments ?? []));
                segmentTimingDetected = !translateToEnglish && transcript.Segments.Any(segment =>
                    !string.IsNullOrWhiteSpace(segment.Text)
                    && segment.TimingGranularity == WhisperTimingGranularity.Segment);
            }
            finally
            {
                if (File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }

            completedSections++;
            sectionProgress?.Invoke(completedSections, starts.Count, null);
            if (segmentTimingDetected)
            {
                break;
            }
        }

        WhisperSegment[] timedSegments = sections
            .SelectMany(section => section.Segments)
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .ToArray();
        WhisperResponseMetadata[] responseMetadata = sections
            .Select(section => section.Metadata)
            .OfType<WhisperResponseMetadata>()
            .ToArray();
        if (translateToEnglish
            && responseMetadata.All(metadata => metadata.SpeechIntervals.Count == 0))
        {
            ValidationRecord noVad = CreateInsufficient(
                input,
                fingerprint,
                0,
                completedSections,
                "Translated text identified spoken regions, but the speech service did not return VAD intervals. No cues were moved because source-linked timing requires independent speech confirmation.");
            noVad.AlignmentMode = "Full";
            noVad.Model = responseMetadata
                .Select(metadata => metadata.SelectedModel)
                .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model)) ?? "Translated speech response";
            noVad.TimingEvidence = "Source-linked translated segments without VAD support";
            return noVad;
        }

        if (translateToEnglish
            && (sections.All(section => section.DecoderSegments.Count == 0)
                || responseMetadata.Any(metadata =>
                    metadata.Timing?.Channels?.TryGetValue(
                        "segment",
                        out WhisperTimingChannelProvenance? segmentChannel) != true
                    || segmentChannel?.Reliable != true
                    || !string.Equals(segmentChannel.Basis, "decoder_segment", StringComparison.Ordinal))))
        {
            ValidationRecord noDecoderCorroboration = CreateInsufficient(
                input,
                fingerprint,
                0,
                completedSections,
                "The speech service did not return reliable decoder-segment timing alongside the paired translation. No cues were moved because source-linked timing could not be independently corroborated.");
            noDecoderCorroboration.AlignmentMode = "Full";
            noDecoderCorroboration.Model = responseMetadata
                .Select(metadata => metadata.SelectedModel)
                .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model)) ?? "Translated speech response";
            noDecoderCorroboration.TimingEvidence = "Source-linked translated segments without decoder-segment corroboration";
            return noDecoderCorroboration;
        }

        if (!translateToEnglish
            && timedSegments.Any(segment => segment.TimingGranularity == WhisperTimingGranularity.Segment))
        {
            ValidationRecord segmentOnly = CreateInsufficient(
                input,
                fingerprint,
                0,
                completedSections,
                "Individual-cue alignment needs genuine word or token timestamps. The speech service returned segment-level timing, so no cues were moved. Shift entire track can still use this timing; update the speech service to return word timing before aligning individual cues.");
            segmentOnly.AlignmentMode = "Full";
            segmentOnly.Model = "Segment-timed speech response";
            segmentOnly.TimingEvidence = "Segment timestamps";
            return segmentOnly;
        }

        string timingEvidence = translateToEnglish
            ? "Source-linked translation onset + decoder-segment agreement + VAD support"
            : timedSegments.Length == 0
            ? "No speech timing returned"
            : timedSegments.All(segment => segment.TimingGranularity == WhisperTimingGranularity.Word)
                ? translateToEnglish ? "Translated word timestamps" : "Word timestamps"
                : timedSegments.All(segment => segment.TimingGranularity == WhisperTimingGranularity.Token)
                    ? translateToEnglish ? "Translated token timestamps" : "Token timestamps"
                    : translateToEnglish ? "Translated word and token timestamps" : "Word and token timestamps";

        sectionProgress?.Invoke(
            starts.Count,
            starts.Count,
            new WhisperInferenceProgress("matching_cues", 1, null));
        GuidedFuzzyMatchOptions fuzzyOptions = new(
            Math.Clamp(configuration.MinimumFuzzyMatchScore, 0.30, 0.95),
            Math.Clamp(configuration.MinimumFuzzyMatchUniqueness, 0.01, 0.50),
            Math.Clamp(configuration.MinimumFuzzyDistinctiveTokens, 3, 20),
            Math.Clamp(configuration.MinimumFuzzyTokenSimilarity, 0.60, 0.98),
            configuration.UseFuzzyStemming,
            configuration.UseFuzzyPhoneticMatching,
            Math.Clamp(configuration.FuzzyPhoneticWeight, 0.10, 0.80));
        IReadOnlyList<CueTimingMatch> cueMatches = translateToEnglish
            ? TextMatcher.MatchIndividualCuesWithVad(
                sections,
                cues,
                Math.Clamp(configuration.FuzzySearchRadiusSeconds, 15, 180),
                Math.Clamp(configuration.MinimumMatchScore, 0.30, 0.95),
                Math.Clamp(configuration.MinimumMatchUniqueness, 0.01, 0.50),
                ResolveFuzzyMatchingEnabled(configuration, crossLanguage: true),
                fuzzyOptions,
                ResolveCrossLanguageMinimumCueCoverage(configuration),
                ResolveMaximumIndividualCueOffsetSeconds(configuration))
            : TextMatcher.MatchIndividualCues(
                sections,
                cues,
                Math.Clamp(configuration.FuzzySearchRadiusSeconds, 15, 180),
                Math.Clamp(configuration.MinimumMatchScore, 0.30, 0.95),
                Math.Clamp(configuration.MinimumMatchUniqueness, 0.01, 0.50),
                ResolveFuzzyMatchingEnabled(configuration, crossLanguage: false),
                fuzzyOptions);
        double cueDeadBandSeconds = ResolveCueDeadBandSeconds(configuration, translateToEnglish);
        CueTimingMatch[] sharedBiasEvidence = cueMatches
            .Where(match => match.OnsetAnchored)
            .ToArray();
        bool sharedWholeTrackBias = TryDetectSharedCueBias(
            sharedBiasEvidence.Select(match => match.OffsetSeconds).ToArray(),
            cueDeadBandSeconds,
            out double sharedBiasSeconds,
            out int sharedBiasMatches);
        Dictionary<int, CueTimingMatch> matchesByCue = cueMatches.ToDictionary(match => match.CueIndex);
        List<SubtitleCueTimingPoint> cueTimingPoints = [];
        for (int cueIndex = 0; cueIndex < cues.Count; cueIndex++)
        {
            SubtitleCue cue = cues[cueIndex];
            if (matchesByCue.TryGetValue(cueIndex, out CueTimingMatch? match))
            {
                bool adjusted = IsEligibleCueAdjustment(match, cue, cueDeadBandSeconds);
                cueTimingPoints.Add(new SubtitleCueTimingPoint
                {
                    SourceCueIndex = cue.SourceCueIndex >= 0 ? cue.SourceCueIndex : null,
                    SubtitleSeconds = Math.Round(match.SubtitleSeconds, 3),
                    OffsetSeconds = Math.Round(match.OffsetSeconds, 3),
                    Adjusted = adjusted,
                    MatchKind = translateToEnglish
                        ? adjusted
                            ? match.Fuzzy ? "VAD fuzzy" : "VAD exact"
                            : "Verified with VAD"
                        : adjusted
                            ? match.Fuzzy ? "Fuzzy" : "Exact"
                            : "Verified",
                    MatchedWords = match.MatchedWords,
                    MatchScore = Math.Round(match.Similarity, 3)
                });
            }
            else
            {
                cueTimingPoints.Add(new SubtitleCueTimingPoint
                {
                    SourceCueIndex = cue.SourceCueIndex >= 0 ? cue.SourceCueIndex : null,
                    SubtitleSeconds = Math.Round((cue.StartSeconds + cue.EndSeconds) / 2, 3),
                    Adjusted = false,
                    MatchKind = "Unmatched"
                });
            }
        }

        List<LocalTimingPoint> timingPoints = cueMatches
            .Select(match => new LocalTimingPoint
            {
                SubtitleSeconds = Math.Round(match.SubtitleSeconds, 3),
                OffsetSeconds = Math.Round(match.OffsetSeconds, 3)
            })
            .ToList();
        int alignedCues = cueMatches.Count;
        int adjustedCues = cueTimingPoints.Count(point => point.Adjusted);
        int inBandCues = cueMatches.Count(match =>
            !IsMeaningfulCueAdjustment(match.OffsetSeconds, cueDeadBandSeconds));
        int protectedCues = alignedCues - adjustedCues - inBandCues;
        int verifiedCues = inBandCues;
        int strictMatches = cueMatches.Count(match => !match.Fuzzy);
        int fuzzyMatches = alignedCues - strictMatches;
        int matchedWords = cueMatches.Sum(match => match.MatchedWords);
        double coveragePercent = 100.0 * alignedCues / cues.Count;
        double medianScore = cueMatches.Count == 0 ? 0 : Median(cueMatches.Select(match => match.Similarity));
        string semanticConfidence = ClassifyDirectSemanticConfidence(
            alignedCues,
            matchedWords,
            medianScore,
            configuration);
        string timingConfidence = translateToEnglish
            ? ClassifyVadTimingConfidence(responseMetadata)
            : ClassifyTimingConfidence(timedSegments, responseMetadata);
        string confidence = MinimumConfidence(semanticConfidence, timingConfidence);
        double? medianOffset = cueMatches.Count == 0
            ? null
            : Math.Round(Median(cueMatches.Select(match => match.OffsetSeconds)), 3);
        string selectedModel = responseMetadata
            .Select(metadata => metadata.SelectedModel)
            .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model)) ?? "Direct per-cue speech matching";
        string message = sharedWholeTrackBias
            ? $"The individual cue evidence found a consistent {sharedBiasSeconds:+0.00;-0.00;0.00}s whole-track shift across {sharedBiasMatches} of {alignedCues} directly matched cues. No individual corrections were written. Use Shift entire track so the shared offset is checked and applied as one reversible change."
            : $"Directly matched {alignedCues} of {cues.Count} subtitle cues to their spoken audio. "
                + $"{inBandCues} {(inBandCues == 1 ? "was" : "were")} already aligned within the {cueDeadBandSeconds * 1000:0} ms no-change band; "
                + $"{protectedCues} had a text match but no safely anchored correction; "
                + $"{adjustedCues} {(adjustedCues == 1 ? "was" : "were")} eligible for an onset correction.";
        if (alignedCues < cues.Count)
        {
            message += $" The remaining {cues.Count - alignedCues} cues had no reliable individual match and were left unchanged.";
        }
        ValidationRecord result = new()
        {
            AlignmentMode = "Full",
            LibraryId = input.LibraryId,
            LibraryName = input.LibraryName,
            ItemId = input.ItemId,
            ItemName = input.ItemName,
            SubtitlePath = input.SubtitlePath,
            Language = input.Language,
            Fingerprint = fingerprint,
            Status = sharedWholeTrackBias || confidence == "Inconclusive" ? "Insufficient evidence" : "Locally aligned",
            OffsetSeconds = medianOffset,
            Confidence = confidence,
            SemanticConfidence = semanticConfidence,
            TimingConfidence = timingConfidence,
            Model = selectedModel,
            MatchedWords = matchedWords,
            MedianMatchScore = Math.Round(medianScore, 3),
            Anchors = alignedCues,
            StrictAnchors = strictMatches,
            GuidedFuzzyAnchors = fuzzyMatches,
            Samples = completedSections,
            AlignedCueCount = alignedCues,
            AdjustedCueCount = adjustedCues,
            VerifiedCueCount = verifiedCues,
            ProtectedCueCount = protectedCues,
            CueAlignmentDeadBandSeconds = cueDeadBandSeconds,
            TimingEvidence = timingEvidence,
            CueAdjustmentStrategy = "Direct",
            WholeTrackShiftRecommended = sharedWholeTrackBias,
            TotalCueCount = cues.Count,
            AlignmentCoveragePercent = Math.Round(coveragePercent, 1),
            TimingPoints = timingPoints,
            CueTimingPoints = cueTimingPoints,
            Message = sharedWholeTrackBias
                ? message
                : confidence == "Inconclusive"
                ? $"{message} The direct matches did not meet the configured confidence requirements, so no corrected sidecar was created."
                : message,
            AnalyzedUtc = DateTime.UtcNow
        };
        return result;
    }

    internal static string ClassifyTimingConfidence(
        IReadOnlyList<WhisperSegment> segments,
        IReadOnlyList<WhisperResponseMetadata> metadata)
    {
        if (segments.Count == 0
            || segments.Any(segment => segment.TimingGranularity == WhisperTimingGranularity.Segment)
            || metadata.Any(item => item.Timing?.Reliable == false
                || (item.Timing?.Channels?.TryGetValue(
                    "word",
                    out WhisperTimingChannelProvenance? wordChannel) == true
                    && wordChannel.Reliable == false)))
        {
            return "Inconclusive";
        }

        bool allWordTimed = segments.All(segment => segment.TimingGranularity == WhisperTimingGranularity.Word);
        bool everyResponseExplicitlyReliable = metadata.Count > 0
            && metadata.All(item =>
                item.Timing?.Channels?.TryGetValue(
                    "word",
                    out WhisperTimingChannelProvenance? wordChannel) == true
                && wordChannel.Reliable == true
                && wordChannel.Basis is "decoder_word" or "forced_alignment");
        return allWordTimed && everyResponseExplicitlyReliable ? "High" : "Medium";
    }

    internal static bool IsEligibleCueAdjustment(
        CueTimingMatch match,
        SubtitleCue cue,
        double deadBandSeconds)
    {
        if (!IsMeaningfulCueAdjustment(match.OffsetSeconds, deadBandSeconds)
            || !match.OnsetAnchored)
        {
            return false;
        }

        return match.OffsetSeconds <= 0
            || cue.EndSeconds - match.MediaStartSeconds >= MinimumRetainedCueDurationSeconds;
    }

    internal static string ClassifyDirectSemanticConfidence(
        int alignedCues,
        int matchedWords,
        double medianScore,
        PluginConfiguration configuration)
    {
        // Direct mode accepts or rejects every cue independently. Unmatched cues are
        // never interpolated, so title-wide coverage describes reach rather than the
        // safety of the cues that did match.
        if (alignedCues >= Math.Clamp(configuration.HighConfidenceMinAnchors, 3, 12)
            && matchedWords >= Math.Clamp(configuration.HighConfidenceMinWords, 10, 500)
            && medianScore >= Math.Clamp(configuration.HighConfidenceMinMatchScore, 0.30, 0.99))
        {
            return "High";
        }

        return alignedCues >= Math.Clamp(configuration.MediumConfidenceMinAnchors, 3, 12)
            && matchedWords >= Math.Clamp(configuration.MediumConfidenceMinWords, 10, 500)
            && medianScore >= Math.Clamp(configuration.MediumConfidenceMinMatchScore, 0.30, 0.99)
            ? "Medium"
            : "Inconclusive";
    }

    internal static string ClassifyVadTimingConfidence(IReadOnlyList<WhisperResponseMetadata> metadata) =>
        metadata.Count > 0
            && metadata.Any(item => item.SpeechIntervals.Count > 0)
            && metadata.All(item =>
                item.Timing?.Channels?.TryGetValue(
                    "vad",
                    out WhisperTimingChannelProvenance? vadChannel) == true
                && string.Equals(vadChannel.Basis, "vad", StringComparison.Ordinal)
                && vadChannel.Reliable == true
                && item.Timing.Channels.TryGetValue(
                    "segment",
                    out WhisperTimingChannelProvenance? segmentChannel)
                && string.Equals(segmentChannel.Basis, "decoder_segment", StringComparison.Ordinal)
                && segmentChannel.Reliable == true)
            ? "Medium"
            : "Inconclusive";

    private static string MinimumConfidence(string semanticConfidence, string timingConfidence)
    {
        static int Rank(string value) => value switch
        {
            "High" => 2,
            "Medium" => 1,
            _ => 0
        };

        return Rank(semanticConfidence) <= Rank(timingConfidence)
            ? semanticConfidence
            : timingConfidence;
    }

    private static List<SubtitleCueTimingPoint> BuildCueTimingPoints(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<LocalTimingPoint> timingPoints,
        int supportSeconds,
        bool timingsAlreadyCorrected = false)
    {
        List<SubtitleCueTimingPoint> points = [];
        foreach (SubtitleCue cue in cues)
        {
            double displayedMidpoint = (cue.StartSeconds + cue.EndSeconds) / 2;
            double sourceMidpoint = displayedMidpoint;
            if (timingsAlreadyCorrected)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    if (!SubtitleCorrector.TryGetLocalOffset(timingPoints, sourceMidpoint, out double appliedOffset, supportSeconds))
                    {
                        break;
                    }

                    sourceMidpoint = Math.Max(0, displayedMidpoint - appliedOffset);
                }
            }

            bool adjusted = SubtitleCorrector.TryGetLocalOffset(
                timingPoints,
                sourceMidpoint,
                out double offsetSeconds,
                supportSeconds);
            points.Add(new SubtitleCueTimingPoint
            {
                SourceCueIndex = cue.SourceCueIndex >= 0 ? cue.SourceCueIndex : null,
                SubtitleSeconds = Math.Round(sourceMidpoint, 3),
                OffsetSeconds = adjusted ? Math.Round(offsetSeconds, 3) : null,
                Adjusted = adjusted
            });
        }

        return points;
    }

    private async Task<IReadOnlyList<SubtitleCueTimingPoint>> ResolveCueTimingPointsAsync(
        ValidationRecord result,
        BaseItem? item,
        CancellationToken cancellationToken)
    {
        if (result.CueTimingPoints.Count > 0)
        {
            return result.CueTimingPoints;
        }

        if (!string.Equals(result.Status, "Locally aligned", StringComparison.Ordinal)
            || result.TimingPoints.Count < 2)
        {
            return [];
        }

        bool embeddedSource = IsEmbeddedSubtitlePath(result.SubtitlePath);
        string readablePath = !embeddedSource && File.Exists(result.SubtitlePath) ? result.SubtitlePath : string.Empty;
        string? temporarySubtitle = null;
        try
        {
            if (embeddedSource && item is Video)
            {
                SubtitleInput[] candidates = DiscoverInputs(
                        result.LibraryId,
                        result.LibraryName,
                        [item],
                        new HashSet<string>(StringComparer.Ordinal),
                        includeEmbedded: true)
                    .Where(input => input.EmbeddedSubtitleOrdinal.HasValue
                        && string.Equals(NormalizeLanguage(input.Language), NormalizeLanguage(result.Language), StringComparison.Ordinal))
                    .ToArray();
                if (candidates.Length == 1)
                {
                    try
                    {
                        temporarySubtitle = await ExtractEmbeddedSubtitleAsync(
                                candidates[0],
                                Plugin.Instance.Configuration,
                                cancellationToken)
                            .ConfigureAwait(false);
                        readablePath = temporarySubtitle;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                    {
                        _logger.LogDebug(exception, "Could not extract original subtitle timing for item {ItemId}; using its corrected sidecar", result.ItemId);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(readablePath))
            {
                readablePath = result.CorrectedSubtitlePath;
            }

            if (string.IsNullOrWhiteSpace(readablePath) || !File.Exists(readablePath))
            {
                return [];
            }

            IReadOnlyList<SubtitleCue> cues = await SubtitleParser.ParseAsync(readablePath, cancellationToken)
                .ConfigureAwait(false);
            int supportSeconds = result.LocalAlignmentSupportSeconds > 0
                ? result.LocalAlignmentSupportSeconds
                : (int)SubtitleCorrector.LocalAlignmentSupportSeconds;
            bool usingCorrectedSidecar = string.Equals(readablePath, result.CorrectedSubtitlePath, StringComparison.Ordinal);
            List<SubtitleCueTimingPoint> points = BuildCueTimingPoints(cues, result.TimingPoints, supportSeconds, usingCorrectedSidecar);
            if (!usingCorrectedSidecar || points.Count(point => point.Adjusted) == result.AlignedCueCount)
            {
                result.CueTimingPoints = points;
                await _resultStore.UpsertAsync(result, advanceLibraryProgress: false, CancellationToken.None).ConfigureAwait(false);
            }

            return points;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Could not reconstruct cue-level timing evidence for item {ItemId}", result.ItemId);
            return [];
        }
        finally
        {
            if (temporarySubtitle is not null && File.Exists(temporarySubtitle))
            {
                File.Delete(temporarySubtitle);
            }
        }
    }

    private async Task<IReadOnlyList<ValidationRecord>> ConsolidateLegacyEmbeddedResultsAsync(
        Guid itemId,
        BaseItem? item,
        IReadOnlyList<ValidationRecord> storedResults,
        CancellationToken cancellationToken)
    {
        ValidationRecord[] itemResults = storedResults.Where(result => result.ItemId == itemId).ToArray();
        if (item is not Video || itemResults.Length == 0)
        {
            return storedResults;
        }

        MediaStream[] embeddedStreams = _mediaSourceManager.GetMediaStreams(itemId)
            .Where(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal)
            .OrderBy(stream => stream.Index)
            .ToArray();
        List<(ValidationRecord Result, bool WasLegacy)> candidates = [];
        bool identityChanged = false;
        foreach (ValidationRecord result in itemResults)
        {
            bool wasLegacy = TryGetLegacyEmbeddedStreamIndex(result, out int legacyStreamIndex);
            if (wasLegacy)
            {
                int ordinal = Array.FindIndex(embeddedStreams, stream => stream.Index == legacyStreamIndex);
                if (ordinal >= 0)
                {
                    MediaStream stream = embeddedStreams[ordinal];
                    string language = string.IsNullOrWhiteSpace(stream.Language) ? stream.Codec ?? string.Empty : stream.Language;
                    string stablePath = $"[embedded subtitle {ordinal + 1}: {language}]";
                    if (!string.Equals(result.SubtitlePath, stablePath, StringComparison.Ordinal))
                    {
                        result.SubtitlePath = stablePath;
                        identityChanged = true;
                    }
                }
            }

            candidates.Add((result, wasLegacy));
        }

        ValidationRecord[] consolidated = candidates
            .GroupBy(candidate => ResultKey(candidate.Result), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => ResultStatusRank(candidate.Result))
                .ThenByDescending(candidate => candidate.Result.AlignmentCoveragePercent)
                .ThenByDescending(candidate => candidate.Result.Anchors)
                .ThenByDescending(candidate => !candidate.WasLegacy)
                .ThenByDescending(candidate => candidate.Result.AnalyzedUtc)
                .First()
                .Result)
            .ToArray();
        if (!identityChanged && consolidated.Length == itemResults.Length)
        {
            return storedResults;
        }

        await _resultStore.ReplaceItemResultsAsync(itemId, consolidated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Reconciled {PreviousCount} stored subtitle result(s) to {CurrentCount} stable result(s) for item {ItemId}",
            itemResults.Length,
            consolidated.Length,
            itemId);
        return storedResults.Where(result => result.ItemId != itemId).Concat(consolidated).ToArray();
    }

    private static bool TryGetLegacyEmbeddedStreamIndex(ValidationRecord result, out int streamIndex)
    {
        streamIndex = -1;
        if (!IsEmbeddedSubtitlePath(result.SubtitlePath)
            || string.IsNullOrWhiteSpace(result.CorrectedSubtitlePath))
        {
            return false;
        }

        string fileName = Path.GetFileName(result.CorrectedSubtitlePath);
        int marker = fileName.LastIndexOf(".track", StringComparison.OrdinalIgnoreCase);
        int suffix = marker < 0
            ? -1
            : fileName.IndexOf(".subalign", marker + 6, StringComparison.OrdinalIgnoreCase);
        return marker >= 0
            && suffix > marker + 6
            && int.TryParse(
                fileName.AsSpan(marker + 6, suffix - marker - 6),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out streamIndex);
    }

    private static int ResultStatusRank(ValidationRecord result)
    {
        int status = result.Status switch
        {
            "Locally aligned" => 60,
            "Constant offset" => 50,
            "In sync" or MinorTimingVariationStatus => 40,
            "Progressive drift" or "Timing break" or "Inconsistent" => 30,
            "Insufficient evidence" => 20,
            "Error" => 0,
            _ => 10
        };
        int confidence = result.Confidence switch
        {
            "High" => 3,
            "Medium" => 2,
            "Low" => 1,
            _ => 0
        };
        return (status * 10) + confidence;
    }

    private async Task ExtractAudioAsync(
        string videoPath,
        double startSeconds,
        double durationSeconds,
        string outputPath,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string ffmpeg = string.IsNullOrWhiteSpace(configuration.FfmpegPath)
            ? _mediaEncoder.EncoderPath
            : configuration.FfmpegPath.Trim();
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        string[] arguments =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", outputPath
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg did not start.");
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            string conciseError = error.Length > 600 ? error[..600] : error;
            throw new InvalidOperationException($"FFmpeg could not extract the audio sample: {conciseError}");
        }
    }

    private async Task<string> ExtractEmbeddedSubtitleAsync(
        SubtitleInput input,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        int subtitleOrdinal = input.EmbeddedSubtitleOrdinal
            ?? throw new InvalidOperationException("The embedded subtitle has no FFmpeg subtitle ordinal.");
        string outputPath = Path.Combine(
            TemporaryDirectory,
            $"{Guid.NewGuid():N}{input.SubtitleExtension}");
        string ffmpeg = string.IsNullOrWhiteSpace(configuration.FfmpegPath)
            ? _mediaEncoder.EncoderPath
            : configuration.FfmpegPath.Trim();
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        string[] arguments =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-i", input.VideoPath,
            "-map", $"0:s:{subtitleOrdinal}",
            "-vn", "-an", "-dn",
            "-c:s", SubtitleEncoderForExtension(input.SubtitleExtension),
            "-y", outputPath
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg did not start while extracting the embedded subtitle.");
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            string conciseError = error.Length > 600 ? error[..600] : error;
            throw new InvalidOperationException($"FFmpeg could not extract the embedded subtitle: {conciseError}");
        }

        return outputPath;
    }

    private static IReadOnlyList<double> SelectWindows(
        IReadOnlyList<SubtitleCue> cues,
        int count,
        string configuredPositions,
        int cueLeadSeconds)
    {
        List<SubtitleCue> useful = cues.Where(cue => cue.Text.Count(char.IsLetterOrDigit) >= 8).ToList();
        if (useful.Count == 0)
        {
            return [];
        }

        IReadOnlyList<double> fractions = ParseSamplePositions(configuredPositions);
        List<double> windows = [];
        foreach (double fraction in fractions.Take(count))
        {
            int cueIndex = (int)Math.Round(fraction * (useful.Count - 1), MidpointRounding.AwayFromZero);
            double start = Math.Max(0, useful[cueIndex].StartSeconds - cueLeadSeconds);
            if (windows.All(existing => Math.Abs(existing - start) >= 15))
            {
                windows.Add(start);
            }
        }

        return windows;
    }

    private static IReadOnlyList<double> ParseSamplePositions(string configuredPositions)
    {
        double[] defaults = [10, 30, 50, 70, 90, 20, 40, 60, 80];
        List<double> percentages = [];
        foreach (string part in configuredPositions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percentage)
                && percentage > 0
                && percentage < 100
                && percentages.All(existing => Math.Abs(existing - percentage) >= 0.1))
            {
                percentages.Add(percentage);
            }
        }

        foreach (double percentage in defaults)
        {
            if (percentages.All(existing => Math.Abs(existing - percentage) >= 0.1))
            {
                percentages.Add(percentage);
            }
        }

        return percentages.Select(percentage => percentage / 100).ToArray();
    }

    private static ValidationRecord Classify(
        SubtitleInput input,
        string fingerprint,
        IReadOnlyList<TimingAnchor> rawAnchors,
        int samples,
        double subtitleDuration,
        PluginConfiguration configuration)
    {
        QuickAlignmentClassificationOptions options = new(
            configuration.ResidualToleranceSeconds,
            configuration.InSyncOffsetSeconds,
            configuration.ModelImprovementPercent,
            configuration.MinimumDriftAcrossTitleSeconds,
            configuration.MinimumTimingBreakSeconds,
            configuration.HighConfidenceMinAnchors,
            configuration.HighConfidenceMinWords,
            configuration.HighConfidenceMinCoveragePercent,
            configuration.HighConfidenceMinMatchScore,
            configuration.MediumConfidenceMinAnchors,
            configuration.MediumConfidenceMinWords,
            configuration.MediumConfidenceMinCoveragePercent,
            configuration.MediumConfidenceMinMatchScore);
        return Classify(input, fingerprint, rawAnchors, samples, subtitleDuration, options);
    }

    internal static ValidationRecord Classify(
        SubtitleInput input,
        string fingerprint,
        IReadOnlyList<TimingAnchor> rawAnchors,
        int samples,
        double subtitleDuration,
        QuickAlignmentClassificationOptions configuration)
    {
        List<TimingAnchor> anchors = EnforceMonotonic(rawAnchors);
        if (anchors.Count < 3)
        {
            return CreateInsufficient(input, fingerprint, anchors.Count, samples, "Whisper found too few reliable, unique text matches.");
        }

        double constantOffset = WeightedMedian(anchors, anchor => anchor.OffsetSeconds);
        double[] constantResiduals = anchors.Select(anchor => Math.Abs(anchor.OffsetSeconds - constantOffset)).ToArray();
        double constantMedianResidual = Median(constantResiduals);
        double constantP90 = Percentile90(constantResiduals);

        LinearFit linear = FitLinear(anchors);
        double[] linearResiduals = anchors
            .Select(anchor => Math.Abs(anchor.OffsetSeconds - ((linear.Slope * anchor.MediaSeconds) + linear.Intercept)))
            .ToArray();
        double linearMedianResidual = Median(linearResiduals);
        double linearP90 = Percentile90(linearResiduals);

        double timeSpan = anchors[^1].MediaSeconds - anchors[0].MediaSeconds;
        double spanCoverage = timeSpan / Math.Max(1, subtitleDuration);
        double driftAcrossSpan = linear.Slope * timeSpan;
        double driftPerHour = linear.Slope * 3600;
        StepFit? step = FindBestStep(anchors);
        double bestGlobalP90 = Math.Min(constantP90, linearP90);
        double residualTolerance = Math.Clamp(configuration.ResidualToleranceSeconds, 0.10, 3.0);
        double inSyncOffset = Math.Clamp(configuration.InSyncOffsetSeconds, 0.05, 3.0);
        double improvementFactor = 1 - (Math.Clamp(configuration.ModelImprovementPercent, 1, 90) / 100.0);
        double minimumDrift = Math.Clamp(configuration.MinimumDriftAcrossTitleSeconds, 0.10, 10.0);
        double minimumBreak = Math.Clamp(configuration.MinimumTimingBreakSeconds, 0.10, 20.0);
        double minorVariationLimit = Math.Min(1.0, Math.Max(residualTolerance, minimumBreak));

        int matchedWords = anchors.Sum(anchor => anchor.MatchedWords);
        double medianScore = Median(anchors.Select(anchor => anchor.Similarity));
        string evidence = anchors.Count >= Math.Clamp(configuration.HighConfidenceMinAnchors, 3, 12)
            && matchedWords >= Math.Clamp(configuration.HighConfidenceMinWords, 10, 500)
            && spanCoverage >= Math.Clamp(configuration.HighConfidenceMinCoveragePercent, 5, 100) / 100.0
            && medianScore >= Math.Clamp(configuration.HighConfidenceMinMatchScore, 0.30, 0.99)
            ? "High"
            : anchors.Count >= Math.Clamp(configuration.MediumConfidenceMinAnchors, 3, 12)
                && matchedWords >= Math.Clamp(configuration.MediumConfidenceMinWords, 10, 500)
                && spanCoverage >= Math.Clamp(configuration.MediumConfidenceMinCoveragePercent, 5, 100) / 100.0
                && medianScore >= Math.Clamp(configuration.MediumConfidenceMinMatchScore, 0.30, 0.99)
                ? "Medium"
                : "Inconclusive";

        string status;
        string model;
        string message;
        double selectedP90;
        bool stepFits = step is not null
            && bestGlobalP90 > 0
            && step.P90Residual <= bestGlobalP90 * improvementFactor
            && Math.Abs(step.Jump) >= minimumBreak
            && step.P90Residual <= residualTolerance;
        bool driftFits = constantMedianResidual > 0
            && linearMedianResidual <= constantMedianResidual * improvementFactor
            && Math.Abs(driftAcrossSpan) >= minimumDrift
            && linearP90 <= residualTolerance;

        if (stepFits)
        {
            status = "Timing break";
            model = "Single discontinuity";
            selectedP90 = step!.P90Residual;
            message = $"Timing changes by about {step.Jump:+0.00;-0.00;0.00} seconds near {FormatTimestamp(step.SplitSeconds)}.";
        }
        else if (driftFits)
        {
            status = "Progressive drift";
            model = "Linear drift";
            selectedP90 = linearP90;
            message = $"Timing changes by about {driftPerHour:+0.0;-0.0;0.0} seconds per hour.";
        }
        else if (constantP90 <= residualTolerance && Math.Abs(constantOffset) >= inSyncOffset)
        {
            status = "Constant offset";
            model = "Constant offset";
            selectedP90 = constantP90;
            message = $"The subtitle appears to need an offset of {constantOffset:+0.00;-0.00;0.00} seconds.";
        }
        else if (constantP90 <= residualTolerance)
        {
            status = "In sync";
            model = "Constant offset";
            selectedP90 = constantP90;
            message = $"Median timing difference is {constantOffset:+0.00;-0.00;0.00} seconds.";
        }
        else if (Math.Abs(constantOffset) < inSyncOffset && constantP90 <= minorVariationLimit)
        {
            status = MinorTimingVariationStatus;
            model = "Constant offset with local variation";
            selectedP90 = constantP90;
            message = MinorTimingVariationMessage(constantOffset);
        }
        else
        {
            status = "Inconsistent";
            model = "No reliable model";
            selectedP90 = Math.Min(constantP90, linearP90);
            message = "Reliable text matches disagree; this may be a different cut or have multiple timing changes.";
        }

        if (evidence == "Inconclusive" && status != "Inconsistent")
        {
            status = "Insufficient evidence";
            message = "A possible timing pattern was found, but there are not enough strong matches across the video to report it safely.";
        }

        return new ValidationRecord
        {
            LibraryId = input.LibraryId,
            LibraryName = input.LibraryName,
            ItemId = input.ItemId,
            ItemName = input.ItemName,
            SubtitlePath = input.SubtitlePath,
            Language = input.Language,
            Fingerprint = fingerprint,
            Status = status,
            OffsetSeconds = Math.Round(constantOffset, 3),
            DriftSecondsPerHour = Math.Round(driftPerHour, 3),
            LinearSlope = linear.Slope,
            LinearInterceptSeconds = linear.Intercept,
            BreakSeconds = step?.SplitSeconds,
            BeforeBreakOffsetSeconds = step?.LeftOffset,
            AfterBreakOffsetSeconds = step?.RightOffset,
            Confidence = status == "Inconsistent" ? "Inconclusive" : evidence,
            Model = model,
            MatchedWords = matchedWords,
            MedianMatchScore = Math.Round(medianScore, 3),
            P90ResidualSeconds = Math.Round(selectedP90, 3),
            Anchors = anchors.Count,
            Samples = samples,
            Message = message,
            AnalyzedUtc = DateTime.UtcNow
        };
    }

    internal static void SetIndividualCueRecommendation(
        ValidationRecord result,
        bool backendSupportsIndividualCueAlignment)
    {
        result.IndividualCueAlignmentAvailable = backendSupportsIndividualCueAlignment;
        result.IndividualCueAlignmentRecommended = backendSupportsIndividualCueAlignment
            && string.Equals(result.AlignmentMode, "Quick", StringComparison.Ordinal)
            && result.Status is "Progressive drift" or "Timing break" or "Inconsistent";
    }

    internal static void ApplySpeechActivityCorroboration(
        ValidationRecord result,
        IReadOnlyList<SpeechActivitySample> samples,
        double residualToleranceSeconds)
    {
        const double minimumCorrelation = 0.35;
        const double minimumUniqueness = 0.03;
        SpeechActivitySample[] reliable = samples
            .Where(sample => sample.Alignment.Correlation >= minimumCorrelation
                && sample.Alignment.Uniqueness >= minimumUniqueness)
            .OrderBy(sample => sample.WindowStartSeconds)
            .ToArray();
        result.SpeechActivitySamples = reliable.Length;
        if (reliable.Length == 0)
        {
            if (result.Status == "Constant offset")
            {
                RefuseUncorroboratedShift(result, "Speech activity did not provide a reliable independent timing match.");
            }

            return;
        }

        double activityOffset = Median(reliable.Select(sample => sample.Alignment.OffsetSeconds));
        double activityP90 = Percentile90(reliable.Select(sample => Math.Abs(sample.Alignment.OffsetSeconds - activityOffset)).ToArray());
        result.SpeechActivityOffsetSeconds = Math.Round(activityOffset, 3);
        result.SpeechActivityCorrelation = Math.Round(Median(reliable.Select(sample => sample.Alignment.Correlation)), 3);
        result.SpeechActivityUniqueness = Math.Round(Median(reliable.Select(sample => sample.Alignment.Uniqueness)), 3);
        result.TimingEvidence = reliable.All(sample => sample.EvidenceType == "VAD")
            ? "Global speech activity (VAD) + text matches"
            : "Global speech activity + text matches";
        result.SemanticConfidence = result.Confidence;
        result.TimingConfidence = reliable.Length >= 5 && activityP90 <= residualToleranceSeconds
            ? "High"
            : reliable.Length >= 3 && activityP90 <= residualToleranceSeconds
                ? "Medium"
                : "Inconclusive";

        if (result.Status != "Constant offset")
        {
            return;
        }

        double disagreement = result.OffsetSeconds is double textOffset
            ? Math.Abs(textOffset - activityOffset)
            : double.PositiveInfinity;
        if (reliable.Length < 3
            || activityP90 > residualToleranceSeconds
            || disagreement > residualToleranceSeconds)
        {
            RefuseUncorroboratedShift(
                result,
                $"Text and speech-activity timing did not agree within {residualToleranceSeconds:0.00} seconds.");
            return;
        }

        if (result.TimingConfidence != "High")
        {
            RefuseUncorroboratedShift(result, "More independent speech-activity windows are needed before shifting the complete track.");
            return;
        }

        result.Message += $" Independent speech activity corroborated the shift at {activityOffset:+0.00;-0.00;0.00} seconds across {reliable.Length} windows.";
    }

    private static void RefuseUncorroboratedShift(ValidationRecord result, string reason)
    {
        result.Status = "Insufficient evidence";
        result.Confidence = "Inconclusive";
        result.TimingConfidence = "Inconclusive";
        result.Message = $"A possible whole-track shift was found from text matches, but it was not applied. {reason}";
    }

    private static ValidationRecord CreateInsufficient(
        SubtitleInput input,
        string fingerprint,
        int anchors,
        int samples,
        string message)
    {
        return new ValidationRecord
        {
            LibraryId = input.LibraryId,
            LibraryName = input.LibraryName,
            ItemId = input.ItemId,
            ItemName = input.ItemName,
            SubtitlePath = input.SubtitlePath,
            Language = input.Language,
            Fingerprint = fingerprint,
            Status = "Insufficient evidence",
            Confidence = "Inconclusive",
            Anchors = anchors,
            Samples = samples,
            Message = message,
            AnalyzedUtc = DateTime.UtcNow
        };
    }

    private static ValidationRecord CreateFailure(
        SubtitleInput input,
        string fingerprint,
        SubtitleAlignmentMode mode,
        string message)
    {
        return new ValidationRecord
        {
            AlignmentMode = mode.ToString(),
            LibraryId = input.LibraryId,
            LibraryName = input.LibraryName,
            ItemId = input.ItemId,
            ItemName = input.ItemName,
            SubtitlePath = input.SubtitlePath,
            Language = input.Language,
            Fingerprint = fingerprint,
            Status = "Error",
            Confidence = "Inconclusive",
            Message = message,
            AnalyzedUtc = DateTime.UtcNow
        };
    }

    private static List<TimingAnchor> EnforceMonotonic(IReadOnlyList<TimingAnchor> rawAnchors)
    {
        List<TimingAnchor> anchors = rawAnchors.OrderBy(anchor => anchor.MediaSeconds).ToList();
        for (int index = 1; index < anchors.Count;)
        {
            if (anchors[index].SubtitleSeconds > anchors[index - 1].SubtitleSeconds)
            {
                index++;
                continue;
            }

            int remove = anchors[index].Weight < anchors[index - 1].Weight ? index : index - 1;
            anchors.RemoveAt(remove);
            index = Math.Max(1, remove);
        }

        return anchors;
    }

    private static LinearFit FitLinear(IReadOnlyList<TimingAnchor> anchors)
    {
        double totalWeight = anchors.Sum(anchor => Math.Max(anchor.Weight, 0.001));
        double meanTime = anchors.Sum(anchor => anchor.MediaSeconds * Math.Max(anchor.Weight, 0.001)) / totalWeight;
        double meanOffset = anchors.Sum(anchor => anchor.OffsetSeconds * Math.Max(anchor.Weight, 0.001)) / totalWeight;
        double denominator = anchors.Sum(anchor => Math.Max(anchor.Weight, 0.001) * Math.Pow(anchor.MediaSeconds - meanTime, 2));
        double slope = denominator <= double.Epsilon
            ? 0
            : anchors.Sum(anchor => Math.Max(anchor.Weight, 0.001)
                * (anchor.MediaSeconds - meanTime)
                * (anchor.OffsetSeconds - meanOffset)) / denominator;
        return new LinearFit(slope, meanOffset - (slope * meanTime));
    }

    private static StepFit? FindBestStep(IReadOnlyList<TimingAnchor> anchors)
    {
        if (anchors.Count < 4)
        {
            return null;
        }

        double span = anchors[^1].MediaSeconds - anchors[0].MediaSeconds;
        StepFit? best = null;
        for (int split = 2; split <= anchors.Count - 2; split++)
        {
            double splitSeconds = (anchors[split - 1].MediaSeconds + anchors[split].MediaSeconds) / 2;
            double location = (splitSeconds - anchors[0].MediaSeconds) / Math.Max(1, span);
            if (location < 0.15 || location > 0.85)
            {
                continue;
            }

            List<TimingAnchor> left = anchors.Take(split).ToList();
            List<TimingAnchor> right = anchors.Skip(split).ToList();
            double leftOffset = WeightedMedian(left, anchor => anchor.OffsetSeconds);
            double rightOffset = WeightedMedian(right, anchor => anchor.OffsetSeconds);
            double[] residuals = left.Select(anchor => Math.Abs(anchor.OffsetSeconds - leftOffset))
                .Concat(right.Select(anchor => Math.Abs(anchor.OffsetSeconds - rightOffset)))
                .ToArray();
            StepFit candidate = new(
                splitSeconds,
                leftOffset,
                rightOffset,
                rightOffset - leftOffset,
                Median(residuals),
                Percentile90(residuals));
            if (best is null
                || candidate.MedianResidual < best.MedianResidual
                || (candidate.MedianResidual == best.MedianResidual && candidate.P90Residual < best.P90Residual))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static double WeightedMedian(
        IReadOnlyList<TimingAnchor> anchors,
        Func<TimingAnchor, double> selector)
    {
        List<(double Value, double Weight)> values = anchors
            .Select(anchor => (selector(anchor), Math.Max(anchor.Weight, 0.001)))
            .OrderBy(value => value.Item1)
            .ToList();
        double half = values.Sum(value => value.Weight) / 2;
        double cumulative = 0;
        foreach ((double value, double weight) in values)
        {
            cumulative += weight;
            if (cumulative >= half)
            {
                return value;
            }
        }

        return values[^1].Value;
    }

    private static double Percentile90(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.90) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string FormatTimestamp(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
            : time.ToString("m\\:ss", CultureInfo.InvariantCulture);
    }

    private static bool IsInsideRunWindow(PluginConfiguration configuration, TimeSpan now)
    {
        if (!configuration.UseRunWindow)
        {
            return true;
        }

        if (!TimeSpan.TryParseExact(configuration.RunWindowStart, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan start)
            || !TimeSpan.TryParseExact(configuration.RunWindowEnd, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan end))
        {
            return false;
        }

        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private static async Task<string> CreateFingerprintAsync(
        SubtitleInput input,
        PluginConfiguration configuration,
        string modelIdentity,
        SubtitleAlignmentMode mode,
        CancellationToken cancellationToken)
    {
        FileInfo video = new(input.VideoPath);
        long subtitleLength = 0;
        long subtitleModifiedTicks = 0;
        string subtitleHash;
        if (input.EmbeddedSubtitleOrdinal.HasValue)
        {
            subtitleHash = $"embedded-subtitle:{input.EmbeddedSubtitleOrdinal}:{input.SubtitleExtension}";
        }
        else
        {
            FileInfo subtitle = new(input.SubtitlePath);
            subtitleLength = subtitle.Length;
            subtitleModifiedTicks = subtitle.LastWriteTimeUtc.Ticks;
            subtitleHash = await ComputeFileHashAsync(input.SubtitlePath, cancellationToken).ConfigureAwait(false);
        }

        string material = string.Join('|',
            AnalysisVersion,
            mode,
            video.Length,
            video.LastWriteTimeUtc.Ticks,
            subtitleLength,
            subtitleModifiedTicks,
            subtitleHash,
            configuration.SamplePositions,
            configuration.InitialSamplesPerSubtitle,
            configuration.SamplesPerSubtitle,
            configuration.ClipDurationSeconds,
            configuration.CueLeadSeconds,
            configuration.MinimumMatchScore,
            configuration.MinimumMatchUniqueness,
            configuration.MinimumMatchedWords,
            configuration.EnableGuidedFuzzyMatching,
            configuration.EnableCrossLanguageFuzzyMatching,
            configuration.CrossLanguageMinimumCueCoverage,
            configuration.MaximumIndividualCueOffsetSeconds,
            configuration.FuzzySearchRadiusSeconds,
            configuration.MinimumFuzzyMatchScore,
            configuration.MinimumFuzzyDistinctiveTokens,
            configuration.MinimumFuzzyTokenSimilarity,
            configuration.UseFuzzyStemming,
            configuration.UseFuzzyPhoneticMatching,
            configuration.FuzzyPhoneticWeight,
            configuration.MinimumFuzzyMatchUniqueness,
            configuration.CueAlignmentDeadBandSeconds,
            configuration.CrossLanguageVadDeadBandSeconds,
            configuration.LocalAlignmentSupportSeconds,
            configuration.ResidualToleranceSeconds,
            configuration.InSyncOffsetSeconds,
            configuration.ModelImprovementPercent,
            configuration.MinimumDriftAcrossTitleSeconds,
            configuration.MinimumTimingBreakSeconds,
            configuration.HighConfidenceMinAnchors,
            configuration.HighConfidenceMinWords,
            configuration.HighConfidenceMinCoveragePercent,
            configuration.HighConfidenceMinMatchScore,
            configuration.MediumConfidenceMinAnchors,
            configuration.MediumConfidenceMinWords,
            configuration.MediumConfidenceMinCoveragePercent,
            configuration.MediumConfidenceMinMatchScore,
            configuration.WhisperModelPath,
            modelIdentity,
            input.Language,
            input.AudioLanguage,
            input.EmbeddedSubtitleOrdinal,
            input.SubtitleExtension);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private async Task<bool> IsCurrentAsync(
        ValidationRecord old,
        SubtitleInput input,
        string fingerprint,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (old.LibraryId != input.LibraryId
            || old.Status == "Error"
            || !string.Equals(old.Fingerprint, fingerprint, StringComparison.Ordinal)
            || (string.Equals(old.AlignmentMode, "Quick", StringComparison.Ordinal)
                && string.Equals(old.CorrectionStatus, "Created", StringComparison.Ordinal)
                && old.Status is "Progressive drift" or "Timing break"))
        {
            return false;
        }

        if (!configuration.CreateCorrectedSidecars || !IsCorrectable(old))
        {
            return true;
        }

        if (!string.Equals(old.CorrectionStatus, "Created", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(old.CorrectedSubtitlePath)
            || string.IsNullOrWhiteSpace(old.CorrectedSubtitleSha256)
            || !File.Exists(old.CorrectedSubtitlePath))
        {
            return false;
        }

        string currentHash = await ComputeFileHashAsync(old.CorrectedSubtitlePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(currentHash, old.CorrectedSubtitleSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyCorrectionAsync(
        ValidationRecord result,
        ValidationRecord? previous,
        SubtitleInput input,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string? temporarySubtitle = null;
        try
        {
            bool preserveFullAlignment = previous is not null
                && (string.Equals(previous.CorrectedWithMode, "Full", StringComparison.Ordinal)
                    || (string.IsNullOrWhiteSpace(previous.CorrectedWithMode)
                        && string.Equals(previous.AlignmentMode, "Full", StringComparison.Ordinal)))
                && string.Equals(previous.CorrectionStatus, "Created", StringComparison.Ordinal)
                && string.Equals(result.AlignmentMode, "Quick", StringComparison.Ordinal);
            if (preserveFullAlignment
                && !string.IsNullOrWhiteSpace(previous!.CorrectedSubtitlePath)
                && !string.IsNullOrWhiteSpace(previous.CorrectedSubtitleSha256)
                && File.Exists(previous.CorrectedSubtitlePath))
            {
                string existingHash = await ComputeFileHashAsync(previous.CorrectedSubtitlePath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingHash, previous.CorrectedSubtitleSha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.CorrectionStatus = "Created";
                    result.CorrectedSubtitlePath = previous.CorrectedSubtitlePath;
                    result.CorrectedSubtitleSha256 = previous.CorrectedSubtitleSha256;
                    result.CorrectedWithMode = "Full";
                    result.CorrectedUtc = previous.CorrectedUtc;
                    result.CorrectionMessage = "Kept the existing full-alignment sidecar; this run did not replace it with a less complete result.";
                    return;
                }
            }

            if (!IsCorrectable(result))
            {
                if (previous is not null
                    && !string.IsNullOrWhiteSpace(previous.CorrectedSubtitlePath)
                    && !string.IsNullOrWhiteSpace(previous.CorrectedSubtitleSha256)
                    && File.Exists(previous.CorrectedSubtitlePath))
                {
                    // Preserve the ownership evidence even when the on-disk hash no longer
                    // matches. Without it, the blocked result would overwrite the only
                    // record that identifies the file the plugin previously created.
                    result.CorrectedSubtitlePath = previous.CorrectedSubtitlePath;
                    result.CorrectedSubtitleSha256 = previous.CorrectedSubtitleSha256;
                    string previousHash = await ComputeFileHashAsync(previous.CorrectedSubtitlePath, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(previousHash, previous.CorrectedSubtitleSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(previous.CorrectionStatus, "Reset", StringComparison.Ordinal))
                        {
                            result.CorrectionStatus = "Reset";
                            result.CorrectionMessage = "The plugin-owned sidecar remains at the original timings because the media directory does not permit deletion.";
                            return;
                        }

                        try
                        {
                            File.Delete(previous.CorrectedSubtitlePath);
                            result.CorrectionStatus = "Removed";
                            result.CorrectedSubtitlePath = string.Empty;
                            result.CorrectedSubtitleSha256 = string.Empty;
                            result.CorrectionMessage = $"Removed obsolete plugin-owned {Path.GetFileName(previous.CorrectedSubtitlePath)}.";
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogInformation(
                                exception,
                                "Could not delete obsolete plugin-owned subtitle for item {ItemId}; restoring original timings in place",
                                result.ItemId);
                            await RestoreOriginalTimingsAsync(
                                    result,
                                    previous.CorrectedSubtitlePath,
                                    input,
                                    configuration,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await RefreshItemAsync(result.ItemId, result, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    bool matchesOriginalSubtitle = false;
                    try
                    {
                        string originalSubtitlePath = input.SubtitlePath;
                        if (input.EmbeddedSubtitleOrdinal.HasValue)
                        {
                            temporarySubtitle = await ExtractEmbeddedSubtitleAsync(input, configuration, cancellationToken)
                                .ConfigureAwait(false);
                            originalSubtitlePath = temporarySubtitle;
                        }

                        string originalSubtitleHash = await ComputeFileHashAsync(originalSubtitlePath, cancellationToken)
                            .ConfigureAwait(false);
                        matchesOriginalSubtitle = IsRecoverableOriginalTimingSidecar(
                                previous.CorrectedSubtitleSha256,
                                previousHash,
                                originalSubtitleHash)
                            || await AreEquivalentOriginalSubtitleFilesAsync(
                                    previous.CorrectedSubtitlePath,
                                    originalSubtitlePath,
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                    {
                        _logger.LogInformation(
                            exception,
                            "Could not verify whether stale sidecar {SubtitlePath} contains the original timings",
                            previous.CorrectedSubtitlePath);
                    }

                    if (matchesOriginalSubtitle)
                    {
                        try
                        {
                            File.Delete(previous.CorrectedSubtitlePath);
                            result.CorrectionStatus = "Removed";
                            result.CorrectedSubtitlePath = string.Empty;
                            result.CorrectedSubtitleSha256 = string.Empty;
                            result.CorrectedWithMode = string.Empty;
                            result.CorrectedUtc = DateTime.UtcNow;
                            result.CorrectionMessage = $"Removed obsolete original-timing duplicate {Path.GetFileName(previous.CorrectedSubtitlePath)}.";
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogInformation(
                                exception,
                                "Could not delete recovered original-timing duplicate for item {ItemId}",
                                result.ItemId);
                            result.CorrectionStatus = "Reset";
                            result.CorrectedSubtitleSha256 = previousHash;
                            result.CorrectedWithMode = string.Empty;
                            result.CorrectedUtc = DateTime.UtcNow;
                            result.CorrectionMessage = "Recovered the plugin-owned sidecar at the original timings. It remains as a duplicate external track because the media directory does not permit deletion.";
                        }

                        await RefreshItemAsync(result.ItemId, result, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    result.CorrectionStatus = "Blocked";
                    result.CorrectionMessage = $"The older {Path.GetFileName(previous.CorrectedSubtitlePath)} no longer matches the version created by Subtitle Aligner, so it was left untouched. Review or remove it manually if it is no longer needed.";
                    return;
                }

                result.CorrectionStatus = result.Status is "In sync" or MinorTimingVariationStatus
                    ? "Not needed"
                    : "Not eligible";
                return;
            }

            string sourcePath = input.SubtitlePath;
            if (input.EmbeddedSubtitleOrdinal.HasValue)
            {
                temporarySubtitle = await ExtractEmbeddedSubtitleAsync(input, configuration, cancellationToken).ConfigureAwait(false);
                sourcePath = temporarySubtitle;
            }

            string outputPath = CorrectedSidecarPath(input);
            if (!File.Exists(outputPath)
                && previous is not null
                && IsLegacyEmbeddedSidecar(previous.CorrectedSubtitlePath)
                && !string.IsNullOrWhiteSpace(previous.CorrectedSubtitleSha256)
                && File.Exists(previous.CorrectedSubtitlePath)
                && string.Equals(
                    await ComputeFileHashAsync(previous.CorrectedSubtitlePath, cancellationToken).ConfigureAwait(false),
                    previous.CorrectedSubtitleSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                outputPath = previous.CorrectedSubtitlePath;
            }

            bool overwrite = false;
            if (File.Exists(outputPath))
            {
                bool previouslyOwned = previous is not null
                    && string.Equals(previous.CorrectedSubtitlePath, outputPath, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(previous.CorrectedSubtitleSha256)
                    && string.Equals(
                        await ComputeFileHashAsync(outputPath, cancellationToken).ConfigureAwait(false),
                        previous.CorrectedSubtitleSha256,
                        StringComparison.OrdinalIgnoreCase);
                if (!previouslyOwned)
                {
                    result.CorrectionStatus = "Blocked";
                    result.CorrectionMessage = $"{Path.GetFileName(outputPath)} already exists and is not owned by Subtitle Aligner. Rename or remove it before trying again.";
                    return;
                }

                overwrite = true;
            }

            await SubtitleCorrector.WriteCorrectedCopyAsync(
                sourcePath,
                outputPath,
                result,
                overwrite,
                cancellationToken).ConfigureAwait(false);
            result.CorrectionStatus = "Created";
            result.CorrectedSubtitlePath = outputPath;
            result.CorrectedSubtitleSha256 = await ComputeFileHashAsync(outputPath, CancellationToken.None).ConfigureAwait(false);
            result.CorrectedWithMode = result.AlignmentMode;
            result.CorrectedUtc = DateTime.UtcNow;
            result.CorrectionMessage = $"Created {Path.GetFileName(outputPath)}; the original was not changed.";

            await RefreshItemAsync(result.ItemId, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not write a corrected subtitle for item {ItemId}", result.ItemId);
            result.CorrectionStatus = "Error";
            result.CorrectionMessage = exception.Message;
        }
        finally
        {
            if (temporarySubtitle is not null && File.Exists(temporarySubtitle))
            {
                File.Delete(temporarySubtitle);
            }
        }
    }

    private async Task RestoreOriginalTimingsAsync(
        ValidationRecord result,
        string destinationPath,
        SubtitleInput input,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string sourcePath = input.SubtitlePath;
        string? temporarySubtitle = null;
        try
        {
            if (input.EmbeddedSubtitleOrdinal.HasValue)
            {
                temporarySubtitle = await ExtractEmbeddedSubtitleAsync(input, configuration, cancellationToken)
                    .ConfigureAwait(false);
                sourcePath = temporarySubtitle;
            }

            await OverwriteFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);

            result.CorrectionStatus = "Reset";
            result.CorrectedSubtitlePath = destinationPath;
            result.CorrectedSubtitleSha256 = await ComputeFileHashAsync(destinationPath, CancellationToken.None)
                .ConfigureAwait(false);
            result.CorrectedWithMode = string.Empty;
            result.CorrectedUtc = DateTime.UtcNow;
            result.CorrectionMessage = "Restored the plugin-owned sidecar to the original timings because the media directory does not permit deletion; it remains as a duplicate external track.";
        }
        finally
        {
            if (temporarySubtitle is not null && File.Exists(temporarySubtitle))
            {
                File.Delete(temporarySubtitle);
            }
        }
    }

    internal static async Task OverwriteFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using (FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream destination = await OpenForOverwriteWithRetryAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<FileStream> OpenForOverwriteWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int attempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshItemAsync(Guid itemId, ValidationRecord result, CancellationToken cancellationToken)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        try
        {
            await item.RefreshMetadata(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Subtitle output changed but item {ItemId} could not be refreshed", itemId);
            result.CorrectionMessage += " Jellyfin could not refresh the item immediately; its next library scan will pick up the change.";
        }
    }

    internal static bool IsCorrectable(ValidationRecord result)
    {
        if (result.Status == "Locally aligned")
        {
            bool enoughCueEvidence = string.Equals(result.CueAdjustmentStrategy, "Direct", StringComparison.Ordinal)
                ? result.CueTimingPoints.Any(point => point.Adjusted)
                : result.TimingPoints.Count >= 3;
            return result.Confidence is "High" or "Medium" && enoughCueEvidence;
        }

        return result.Confidence == "High" && result.Status == "Constant offset";
    }

    internal static bool IsMeaningfulCueAdjustment(double offsetSeconds, double deadBandSeconds) =>
        Math.Abs(offsetSeconds) > NormalizeCueDeadBandSeconds(deadBandSeconds);

    internal static bool IsRecoverableOriginalTimingSidecar(
        string recordedPluginHash,
        string currentSidecarHash,
        string? originalSubtitleHash) =>
        !string.IsNullOrWhiteSpace(recordedPluginHash)
        && !string.IsNullOrWhiteSpace(currentSidecarHash)
        && !string.IsNullOrWhiteSpace(originalSubtitleHash)
        && !string.Equals(currentSidecarHash, recordedPluginHash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(currentSidecarHash, originalSubtitleHash, StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> AreEquivalentOriginalSubtitleFilesAsync(
        string currentSidecarPath,
        string originalSubtitlePath,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(originalSubtitlePath);
        if (!extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string currentText = await File.ReadAllTextAsync(currentSidecarPath, cancellationToken).ConfigureAwait(false);
        string originalText = await File.ReadAllTextAsync(originalSubtitlePath, cancellationToken).ConfigureAwait(false);
        string[] currentLines = NormalizeSubtitleLineEndings(currentText).Split('\n');
        string[] originalLines = NormalizeSubtitleLineEndings(originalText).Split('\n');
        if (currentLines.Length != originalLines.Length)
        {
            return false;
        }

        for (int index = 0; index < currentLines.Length; index++)
        {
            if (string.Equals(currentLines[index], originalLines[index], StringComparison.Ordinal))
            {
                continue;
            }

            if (!AreEquivalentAssDialogueLines(currentLines[index], originalLines[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeSubtitleLineEndings(string text) =>
        text.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static bool AreEquivalentAssDialogueLines(string currentLine, string originalLine)
    {
        if (!currentLine.StartsWith("Dialogue:", StringComparison.Ordinal)
            || !originalLine.StartsWith("Dialogue:", StringComparison.Ordinal))
        {
            return false;
        }

        string[] currentFields = currentLine.Split(',', 10);
        string[] originalFields = originalLine.Split(',', 10);
        if (currentFields.Length != 10 || originalFields.Length != 10)
        {
            return false;
        }

        for (int index = 0; index < currentFields.Length; index++)
        {
            if (index is 1 or 2)
            {
                continue;
            }

            if (!string.Equals(currentFields[index], originalFields[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return TryParseAssTimestamp(currentFields[1], out double currentStart)
            && TryParseAssTimestamp(originalFields[1], out double originalStart)
            && TryParseAssTimestamp(currentFields[2], out double currentEnd)
            && TryParseAssTimestamp(originalFields[2], out double originalEnd)
            && Math.Abs(currentStart - originalStart) <= 0.011
            && Math.Abs(currentEnd - originalEnd) <= 0.011;
    }

    private static bool TryParseAssTimestamp(string value, out double seconds)
    {
        if (TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out TimeSpan timestamp))
        {
            seconds = timestamp.TotalSeconds;
            return true;
        }

        seconds = 0;
        return false;
    }

    internal static bool TryDetectSharedCueBias(
        IReadOnlyList<double> offsets,
        double deadBandSeconds,
        out double medianOffset,
        out int agreeingMatches)
    {
        double[] meaningfulOffsets = offsets
            .Where(offset => IsMeaningfulCueAdjustment(offset, deadBandSeconds))
            .ToArray();
        medianOffset = meaningfulOffsets.Length == 0 ? 0 : Median(meaningfulOffsets);
        agreeingMatches = 0;
        if (meaningfulOffsets.Length < 4)
        {
            return false;
        }

        // Individual-cue alignment must not recreate a coherent global shift as
        // many local edits. The tolerance is deliberately narrower than the
        // cross-language no-change band and requires a strong supermajority.
        double agreementToleranceSeconds = Math.Clamp(deadBandSeconds, 0.15, 0.50);
        double centerOffset = medianOffset;
        agreeingMatches = meaningfulOffsets.Count(offset =>
            Math.Abs(offset - centerOffset) <= agreementToleranceSeconds);
        return agreeingMatches >= 4
            && agreeingMatches >= (int)Math.Ceiling(meaningfulOffsets.Length * 0.75);
    }

    internal static double ResolveCueDeadBandSeconds(
        PluginConfiguration configuration,
        bool crossLanguageVad) => NormalizeCueDeadBandSeconds(
            crossLanguageVad
                ? configuration.CrossLanguageVadDeadBandSeconds <= 0
                    ? 0.75
                    : configuration.CrossLanguageVadDeadBandSeconds
                : configuration.CueAlignmentDeadBandSeconds);

    internal static bool ResolveFuzzyMatchingEnabled(
        PluginConfiguration configuration,
        bool crossLanguage) => configuration.EnableGuidedFuzzyMatching
        && (!crossLanguage || configuration.EnableCrossLanguageFuzzyMatching);

    internal static double ResolveCrossLanguageMinimumCueCoverage(
        PluginConfiguration configuration) => configuration.CrossLanguageMinimumCueCoverage <= 0
        ? 0.35
        : Math.Clamp(configuration.CrossLanguageMinimumCueCoverage, 0.25, 1.0);

    internal static double ResolveMaximumIndividualCueOffsetSeconds(
        PluginConfiguration configuration) => configuration.MaximumIndividualCueOffsetSeconds <= 0
        ? 2.0
        : Math.Clamp(configuration.MaximumIndividualCueOffsetSeconds, 0.25, 10.0);

    private static double NormalizeCueDeadBandSeconds(double deadBandSeconds) =>
        deadBandSeconds <= 0 ? 0.10 : Math.Clamp(deadBandSeconds, 0.05, 1.0);

    private static void NormalizeLegacyMinorTimingVariation(
        ValidationRecord result,
        PluginConfiguration configuration)
    {
        if (!string.Equals(result.Status, "Inconsistent", StringComparison.Ordinal)
            || result.OffsetSeconds is not double offset
            || result.P90ResidualSeconds is not double residual)
        {
            return;
        }

        double inSyncOffset = Math.Clamp(configuration.InSyncOffsetSeconds, 0.05, 3.0);
        double residualTolerance = Math.Clamp(configuration.ResidualToleranceSeconds, 0.10, 3.0);
        double minimumBreak = Math.Clamp(configuration.MinimumTimingBreakSeconds, 0.10, 20.0);
        double minorVariationLimit = Math.Min(1.0, Math.Max(residualTolerance, minimumBreak));
        if (Math.Abs(offset) >= inSyncOffset || residual > minorVariationLimit)
        {
            return;
        }

        result.Status = MinorTimingVariationStatus;
        result.Model = "Constant offset with local variation";
        result.Message = MinorTimingVariationMessage(offset);
        if (string.Equals(result.CorrectionStatus, "Not eligible", StringComparison.Ordinal))
        {
            result.CorrectionStatus = "Not needed";
        }
    }

    private static string MinorTimingVariationMessage(double offsetSeconds)
    {
        string description = Math.Abs(offsetSeconds) <= 0.05
            ? "essentially perfect"
            : "within the in-sync range";
        string offset = offsetSeconds
            .ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)
            .Replace("-", "−", StringComparison.Ordinal);
        return $"The overall median is {description} at {offset}s. Small local variations were detected, so no correction is needed.";
    }

    private static string CorrectedSidecarPath(SubtitleInput input)
    {
        if (input.EmbeddedSubtitleOrdinal.HasValue)
        {
            string videoDirectory = Path.GetDirectoryName(input.VideoPath)
                ?? throw new InvalidOperationException("The video path has no parent directory.");
            string language = NormalizeLanguage(input.Language);
            string languageToken = language.Length == 0 ? string.Empty : $".{language}";
            return Path.Combine(
                videoDirectory,
                $"{Path.GetFileNameWithoutExtension(input.VideoPath)}{languageToken}.subtitle{input.EmbeddedSubtitleOrdinal.Value + 1}.subalign{input.SubtitleExtension}");
        }

        string directory = Path.GetDirectoryName(input.SubtitlePath)
            ?? throw new InvalidOperationException("The source subtitle path has no parent directory.");
        return Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(input.SubtitlePath) + ".subalign" + Path.GetExtension(input.SubtitlePath));
    }

    private static bool IsGeneratedSidecar(string path)
    {
        return Path.GetFileNameWithoutExtension(path).EndsWith(".subalign", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyEmbeddedSidecar(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.Contains(".track", StringComparison.OrdinalIgnoreCase)
            && fileName.Contains(".subalign", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmbeddedSubtitlePath(string path)
    {
        return path.StartsWith("[embedded subtitle ", StringComparison.Ordinal);
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResultKey(ValidationRecord result) => $"{result.ItemId:N}|{result.SubtitlePath}";

    private static string ResultKey(SubtitleInput input) => $"{input.ItemId:N}|{input.SubtitlePath}";

    private static string LanguageFromFileName(string videoBaseName, string subtitlePath)
    {
        string subtitleName = Path.GetFileNameWithoutExtension(subtitlePath);
        if (!subtitleName.StartsWith(videoBaseName + ".", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string suffix = subtitleName[(videoBaseName.Length + 1)..];
        return suffix.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private static string EmbeddedSubtitleExtension(string codec)
    {
        return codec.Trim().ToLowerInvariant() switch
        {
            "ass" or "ssa" => ".ass",
            "subrip" or "srt" or "mov_text" => ".srt",
            "webvtt" => ".vtt",
            _ => string.Empty
        };
    }

    private static string SubtitleEncoderForExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => "ass",
            ".vtt" => "webvtt",
            _ => "srt"
        };
    }

    private static string NormalizeLanguage(string language)
    {
        string normalized = language.Trim().ToLowerInvariant().Split('-', '_')[0];
        return normalized switch
        {
            "eng" or "english" => "en",
            "spa" or "spanish" => "es",
            "fra" or "fre" or "french" => "fr",
            "deu" or "ger" or "german" => "de",
            "ita" or "italian" => "it",
            "por" or "portuguese" => "pt",
            "nld" or "dut" or "dutch" => "nl",
            "jpn" or "japanese" => "ja",
            "kor" or "korean" => "ko",
            "zho" or "chi" or "chinese" => "zh",
            "rus" or "russian" => "ru",
            _ when normalized.Length == 2 => normalized,
            _ => string.Empty
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private sealed record LinearFit(double Slope, double Intercept);

    private sealed record StepFit(
        double SplitSeconds,
        double LeftOffset,
        double RightOffset,
        double Jump,
        double MedianResidual,
        double P90Residual);

    private sealed record PendingSubtitle(SubtitleInput Input, string Fingerprint, ValidationRecord? Previous);

    private sealed record LibraryRun(
        Guid LibraryId,
        string LibraryName,
        int DiscoveredPairs,
        int CurrentPairs,
        IReadOnlyList<PendingSubtitle> Pending);

    private static ItemSubtitleResult ToItemSubtitleResult(
        ValidationRecord result,
        double fallbackMediaDurationSeconds = 0,
        IReadOnlyList<SubtitleCueTimingPoint>? cueTimingPoints = null)
    {
        int adjustedCueCount = result.AdjustedCueCount;
        int verifiedCueCount = result.VerifiedCueCount;
        if (string.Equals(result.CueAdjustmentStrategy, "Direct", StringComparison.Ordinal)
            && result.AlignedCueCount > 0
            && adjustedCueCount == 0
            && verifiedCueCount == 0)
        {
            adjustedCueCount = result.CueTimingPoints.Count > 0
                ? result.CueTimingPoints.Count(point => point.Adjusted)
                : result.AlignedCueCount;
            verifiedCueCount = Math.Max(0, result.AlignedCueCount - adjustedCueCount);
        }

        return new ItemSubtitleResult
        {
            AlignmentMode = result.AlignmentMode,
            SubtitleName = DisplaySubtitleName(result.SubtitlePath),
            Language = result.Language,
            Status = result.Status,
            Confidence = result.Confidence,
            SemanticConfidence = result.SemanticConfidence,
            TimingConfidence = result.TimingConfidence,
            Message = result.Message,
            CorrectionStatus = result.CorrectionStatus,
            CorrectionMessage = result.CorrectionMessage,
            OffsetSeconds = result.Status is "In sync" or "Constant offset" or "Locally aligned" or MinorTimingVariationStatus
                ? result.OffsetSeconds
                : null,
            DriftSecondsPerHour = result.Status == "Progressive drift" ? result.DriftSecondsPerHour : null,
            BreakSeconds = result.Status == "Timing break" ? result.BreakSeconds : null,
            Anchors = result.Anchors,
            StrictAnchors = result.StrictAnchors,
            GuidedFuzzyAnchors = result.GuidedFuzzyAnchors,
            Samples = result.Samples,
            MatchedWords = result.MatchedWords,
            AlignedCueCount = result.AlignedCueCount,
            AdjustedCueCount = adjustedCueCount,
            VerifiedCueCount = verifiedCueCount,
            ProtectedCueCount = result.ProtectedCueCount,
            CueAlignmentDeadBandSeconds = result.CueAlignmentDeadBandSeconds,
            TimingEvidence = result.TimingEvidence,
            SpeechActivitySamples = result.SpeechActivitySamples,
            SpeechActivityOffsetSeconds = result.SpeechActivityOffsetSeconds,
            SpeechActivityCorrelation = result.SpeechActivityCorrelation,
            SpeechActivityUniqueness = result.SpeechActivityUniqueness,
            CueAdjustmentStrategy = result.CueAdjustmentStrategy,
            IndividualCueAlignmentAvailable = result.IndividualCueAlignmentAvailable,
            IndividualCueAlignmentRecommended = result.IndividualCueAlignmentRecommended,
            WholeTrackShiftRecommended = result.WholeTrackShiftRecommended,
            TotalCueCount = result.TotalCueCount,
            AlignmentCoveragePercent = result.AlignmentCoveragePercent,
            AnalysisElapsedSeconds = result.AnalysisElapsedSeconds,
            MediaDurationSeconds = result.MediaDurationSeconds > 0
                ? result.MediaDurationSeconds
                : fallbackMediaDurationSeconds,
            LocalAlignmentSupportSeconds = result.LocalAlignmentSupportSeconds,
            TimingPoints = result.TimingPoints,
            CueTimingPoints = cueTimingPoints ?? result.CueTimingPoints,
            P90ResidualSeconds = result.P90ResidualSeconds,
            AnalyzedUtc = result.AnalyzedUtc
        };
    }

    private static string DisplaySubtitleName(string path)
    {
        return IsEmbeddedSubtitlePath(path) ? path : Path.GetFileName(path);
    }

    private static string FriendlySubtitleDescription(string language)
    {
        string languageName = NormalizeLanguage(language) switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "ar" => "Arabic",
            "ru" => "Russian",
            _ => string.Empty
        };
        return languageName.Length > 0 ? $"{languageName} subtitles" : "the subtitle track";
    }

    private void StartItemRun(Guid runId, string message)
    {
        lock (_itemRunGate)
        {
            if (_itemRuns.TryGetValue(runId, out ItemRunState? state) && !state.Complete)
            {
                state.State = "Running";
                state.StartedUtc = DateTime.UtcNow;
                state.ProgressPercent = 1;
                state.Message = message;
            }
        }
    }

    private void UpdateItemRun(Guid? runId, int? progressPercent, string message)
    {
        if (!runId.HasValue)
        {
            return;
        }

        lock (_itemRunGate)
        {
            if (_itemRuns.TryGetValue(runId.Value, out ItemRunState? state) && !state.Complete)
            {
                state.State = "Running";
                if (progressPercent.HasValue)
                {
                    state.ProgressPercent = Math.Clamp(progressPercent.Value, state.ProgressPercent, 99);
                }

                state.Message = message;
            }
        }
    }

    private void BeginItemAnalysis(Guid? runId)
    {
        if (!runId.HasValue)
        {
            return;
        }

        lock (_itemRunGate)
        {
            if (_itemRuns.TryGetValue(runId.Value, out ItemRunState? state) && !state.Complete)
            {
                state.AnalysisStartedUtc ??= DateTime.UtcNow;
            }
        }
    }

    private void CompleteItemRun(Guid? runId, string message, string stateName = "Complete")
    {
        if (!runId.HasValue)
        {
            return;
        }

        CompleteItemRun(runId.Value, message, stateName);
    }

    private void RecordItemResult(Guid? runId, ValidationRecord result)
    {
        if (!runId.HasValue)
        {
            return;
        }

        lock (_itemRunGate)
        {
            if (_itemRuns.TryGetValue(runId.Value, out ItemRunState? state) && !state.Complete)
            {
                state.Results.Add(ToItemSubtitleResult(result));
                state.Results.Sort((left, right) => string.Compare(left.SubtitleName, right.SubtitleName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private void CompleteItemRun(Guid runId, string message, string stateName = "Complete")
    {
        lock (_itemRunGate)
        {
            if (_itemRuns.TryGetValue(runId, out ItemRunState? state))
            {
                state.State = stateName;
                state.Complete = true;
                state.ProgressPercent = 100;
                state.Message = message;
                state.CompletedUtc = DateTime.UtcNow;
            }
        }
    }

    private void PruneItemRuns()
    {
        const int retainedRuns = 30;
        foreach (Guid runId in _itemRuns.Values
                     .Where(state => state.Complete)
                     .OrderByDescending(state => state.CompletedUtc)
                     .Skip(retainedRuns)
                     .Select(state => state.RunId)
                     .ToArray())
        {
            _itemRuns.Remove(runId);
        }
    }

    private sealed class ItemRunState
    {
        public string AlignmentMode { get; init; } = "Quick";

        public Guid RunId { get; init; }

        public Guid ItemId { get; init; }

        public string State { get; set; } = "Queued";

        public bool Complete { get; set; }

        public int ProgressPercent { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTime RequestedUtc { get; init; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? AnalysisStartedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        public List<ItemSubtitleResult> Results { get; } = [];
    }

    private void SetRunning(bool running, string currentItem, string? message)
    {
        lock (_statusGate)
        {
            _running = running;
            _currentItem = currentItem;
            if (message is not null)
            {
                _lastMessage = message;
            }
        }
    }

    private void SetCurrentItem(string item)
    {
        lock (_statusGate)
        {
            _currentItem = item;
        }
    }

    private void SetMessage(string message)
    {
        lock (_statusGate)
        {
            _lastMessage = message;
        }
    }

    private void SetCompleted(string message)
    {
        lock (_statusGate)
        {
            _lastMessage = message;
            _lastRunUtc = DateTime.UtcNow;
            _currentItem = string.Empty;
        }
    }
}
