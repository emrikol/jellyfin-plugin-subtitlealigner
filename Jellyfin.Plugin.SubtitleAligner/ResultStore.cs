using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Stores compact resumable results in the plugin data directory.</summary>
public sealed class ResultStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<ResultStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="ResultStore"/> class.</summary>
    public ResultStore(ILogger<ResultStore> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>Returns all stored results.</summary>
    public async Task<IReadOnlyList<ValidationRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false)).Results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the complete persisted scan state.</summary>
    internal async Task<StoredResults> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a new library sweep and removes records that no longer exist in Jellyfin.</summary>
    internal async Task BeginSweepAsync(
        IReadOnlyList<LibrarySweepPlan> plans,
        IReadOnlySet<string> discoveredKeys,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredResults stored = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            stored.Results.RemoveAll(result => !discoveredKeys.Contains(ResultKey(result)));

            Dictionary<Guid, LibraryScanRecord> oldLibraries = stored.Libraries
                .ToDictionary(library => library.LibraryId);
            DateTime now = DateTime.UtcNow;
            stored.Libraries = plans.Select(plan =>
            {
                oldLibraries.TryGetValue(plan.LibraryId, out LibraryScanRecord? old);
                bool current = plan.CurrentPairs >= plan.DiscoveredPairs;
                return new LibraryScanRecord
                {
                    LibraryId = plan.LibraryId,
                    LibraryName = plan.LibraryName,
                    State = current ? "Current" : "Pending",
                    DiscoveredPairs = plan.DiscoveredPairs,
                    ProcessedPairs = plan.CurrentPairs,
                    ErrorPairs = 0,
                    LastStartedUtc = now,
                    LastCompletedUtc = current ? now : old?.LastCompletedUtc
                };
            }).ToList();
            await SaveUnlockedAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds or replaces one result and persists it immediately.</summary>
    public async Task UpsertAsync(
        ValidationRecord record,
        bool advanceLibraryProgress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredResults stored = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            int index = stored.Results.FindIndex(item =>
                item.ItemId == record.ItemId
                && string.Equals(item.SubtitlePath, record.SubtitlePath, StringComparison.Ordinal));

            if (index >= 0)
            {
                stored.Results[index] = record;
            }
            else
            {
                stored.Results.Add(record);
            }

            LibraryScanRecord? library = stored.Libraries.FirstOrDefault(item => item.LibraryId == record.LibraryId);
            if (advanceLibraryProgress && library is not null)
            {
                library.ProcessedPairs = Math.Min(library.DiscoveredPairs, library.ProcessedPairs + 1);
                if (record.Status == "Error" || record.CorrectionStatus == "Error")
                {
                    library.ErrorPairs++;
                }

                library.State = "Scanning";
            }

            await SaveUnlockedAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces every stored result for one item as one atomic reconciliation.</summary>
    internal async Task ReplaceItemResultsAsync(
        Guid itemId,
        IReadOnlyList<ValidationRecord> results,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredResults stored = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            stored.Results.RemoveAll(result => result.ItemId == itemId);
            stored.Results.AddRange(results);
            await SaveUnlockedAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks one library's attempted sweep complete.</summary>
    public async Task CompleteLibraryAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredResults stored = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            LibraryScanRecord? library = stored.Libraries.FirstOrDefault(item => item.LibraryId == libraryId);
            if (library is not null)
            {
                library.LastCompletedUtc = DateTime.UtcNow;
                library.State = library.ErrorPairs > 0 ? "Completed with errors" : "Current";
            }

            await SaveUnlockedAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks unfinished libraries resumable after cancellation or a closed run window.</summary>
    public async Task PauseIncompleteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredResults stored = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            foreach (LibraryScanRecord library in stored.Libraries.Where(item => item.ProcessedPairs < item.DiscoveredPairs))
            {
                library.State = "Paused";
            }

            await SaveUnlockedAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StorePath => Path.Combine(WhisperModelCatalog.DataDirectory, "results.json");

    private static string ResultKey(ValidationRecord record) => $"{record.ItemId:N}|{record.SubtitlePath}";

    private async Task<StoredResults> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath))
        {
            return new StoredResults();
        }

        try
        {
            await using FileStream stream = File.OpenRead(StorePath);
            return await JsonSerializer.DeserializeAsync<StoredResults>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new StoredResults();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Could not read Subtitle Aligner results; starting with an empty result set");
            return new StoredResults();
        }
    }

    private static async Task SaveUnlockedAsync(StoredResults stored, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WhisperModelCatalog.DataDirectory);
        string temporaryPath = StorePath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, StorePath, true);
    }
}
