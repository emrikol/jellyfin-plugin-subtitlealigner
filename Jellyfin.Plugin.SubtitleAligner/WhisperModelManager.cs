using System.Buffers;
using System.Security.Cryptography;
using Jellyfin.Plugin.SubtitleAligner.Configuration;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Downloads, verifies, and installs pinned Whisper models.</summary>
public sealed class WhisperModelManager : IDisposable
{
    /// <summary>The named HTTP client used for model downloads.</summary>
    public const string HttpClientName = "SubtitleAlignerModel";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private bool _busy;
    private string _message = "The selected model is not installed.";
    private string _verifiedPath = string.Empty;
    private long _verifiedLength = -1;
    private DateTime _verifiedWriteUtc;
    private bool _verifiedValid;
    private string _identityPath = string.Empty;
    private long _identityLength = -1;
    private DateTime _identityWriteUtc;
    private string _identityHash = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="WhisperModelManager"/> class.</summary>
    public WhisperModelManager(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>Gets the path for a plugin-managed model.</summary>
    public static string GetManagedModelPath(WhisperModelDefinition definition)
        => Path.Combine(WhisperModelCatalog.ModelsDirectory, definition.FileName);

    /// <summary>Gets the path for the plugin-managed speech-boundary model.</summary>
    public static string GetManagedVadModelPath()
        => Path.Combine(WhisperModelCatalog.ModelsDirectory, WhisperModelCatalog.SileroVad.FileName);

    /// <summary>Gets the selected managed model definition.</summary>
    public static WhisperModelDefinition GetSelectedModel(PluginConfiguration configuration)
        => WhisperModelCatalog.Find(configuration.ManagedModelName);

    /// <summary>Gets a content identity for the managed or configured custom model.</summary>
    public async Task<string> GetIdentityAsync(string configuredPath, CancellationToken cancellationToken)
    {
        PluginConfiguration configuration = Plugin.Instance.Configuration;
        string path = string.IsNullOrWhiteSpace(configuredPath)
            ? GetManagedModelPath(GetSelectedModel(configuration))
            : configuredPath.Trim();
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return "missing";
        }

        lock (_stateGate)
        {
            if (string.Equals(_identityPath, file.FullName, StringComparison.Ordinal)
                && _identityLength == file.Length
                && _identityWriteUtc == file.LastWriteTimeUtc
                && _identityHash.Length > 0)
            {
                return _identityHash;
            }
        }

        string hash = await ComputeSha256Async(file.FullName, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            _identityPath = file.FullName;
            _identityLength = file.Length;
            _identityWriteUtc = file.LastWriteTimeUtc;
            _identityHash = hash;
        }

        return hash;
    }

    /// <summary>Returns current model installation state.</summary>
    public async Task<WhisperModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        WhisperModelDefinition definition = GetSelectedModel(Plugin.Instance.Configuration);
        FileInfo file = new(GetManagedModelPath(definition));
        bool valid = false;
        if (file.Exists)
        {
            bool needsVerification;
            lock (_stateGate)
            {
                needsVerification = !string.Equals(_verifiedPath, file.FullName, StringComparison.Ordinal)
                    || file.Length != _verifiedLength
                    || file.LastWriteTimeUtc != _verifiedWriteUtc;
                valid = _verifiedValid;
            }

            if (needsVerification)
            {
                valid = await IsVerifiedAsync(definition, file.FullName, cancellationToken).ConfigureAwait(false);
                RecordVerification(definition, file, valid);
            }
        }

        bool vadValid = await IsVerifiedAsync(
            WhisperModelCatalog.SileroVad,
            GetManagedVadModelPath(),
            cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            return new WhisperModelStatus
            {
                ModelName = definition.FileName,
                ModelDisplayName = definition.DisplayName,
                Installed = file.Exists,
                Valid = valid && vadValid,
                Busy = _busy,
                SizeBytes = file.Exists ? file.Length : 0,
                Message = file.Exists && valid && vadValid
                    ? $"{definition.DisplayName} and speech-boundary detection are ready."
                    : file.Exists && valid
                        ? "The speech model is ready, but the speech-boundary model must be downloaded."
                        : _message
            };
        }
    }

    /// <summary>Downloads and verifies the selected pinned model.</summary>
    public async Task<WhisperModelStatus> DownloadAsync(CancellationToken cancellationToken)
    {
        WhisperModelDefinition definition = GetSelectedModel(Plugin.Instance.Configuration);
        await BeginOperationAsync($"Downloading {definition.DisplayName}.", cancellationToken).ConfigureAwait(false);
        string destination = GetManagedModelPath(definition);
        string temporaryPath = destination + ".download";
        WhisperVadModelDefinition vadDefinition = WhisperModelCatalog.SileroVad;
        string vadDestination = GetManagedVadModelPath();
        string vadTemporaryPath = vadDestination + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await DownloadVerifiedAsync(definition, temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!await IsVerifiedAsync(vadDefinition, vadDestination, cancellationToken).ConfigureAwait(false))
            {
                await DownloadVerifiedAsync(vadDefinition, vadTemporaryPath, cancellationToken).ConfigureAwait(false);
                File.Move(vadTemporaryPath, vadDestination, overwrite: true);
                SetReadableMode(vadDestination);
            }

            InstallVerifiedFile(definition, temporaryPath);
            SetMessage($"Download complete. {definition.DisplayName} and speech-boundary detection are ready.");
        }
        catch
        {
            DeleteTemporary(temporaryPath);
            DeleteTemporary(vadTemporaryPath);
            SetMessage("The model download failed. Previously verified model files were left in place.");
            throw;
        }
        finally
        {
            EndOperation();
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads a pinned model to a caller-owned path and verifies its size and SHA-256.</summary>
    internal async Task DownloadVerifiedAsync(
        WhisperModelDefinition definition,
        string destination,
        CancellationToken cancellationToken)
        => await DownloadVerifiedAsync(
            definition.DownloadUri,
            definition.SizeBytes,
            definition.Sha256,
            destination,
            cancellationToken).ConfigureAwait(false);

    private async Task DownloadVerifiedAsync(
        WhisperVadModelDefinition definition,
        string destination,
        CancellationToken cancellationToken)
        => await DownloadVerifiedAsync(
            definition.DownloadUri,
            definition.SizeBytes,
            definition.Sha256,
            destination,
            cancellationToken).ConfigureAwait(false);

    private async Task DownloadVerifiedAsync(
        Uri downloadUri,
        long sizeBytes,
        string sha256,
        string destination,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != sizeBytes)
        {
            throw new InvalidDataException($"The download reported an unexpected size ({length} bytes).");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await WriteVerifiedAsync(source, destination, sizeBytes, sha256, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks a model file against its pinned identity.</summary>
    internal static async Task<bool> IsVerifiedAsync(
        WhisperModelDefinition definition,
        string path,
        CancellationToken cancellationToken)
        => await IsVerifiedAsync(
            definition.SizeBytes,
            definition.Sha256,
            path,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<bool> IsVerifiedAsync(
        WhisperVadModelDefinition definition,
        string path,
        CancellationToken cancellationToken)
        => await IsVerifiedAsync(
            definition.SizeBytes,
            definition.Sha256,
            path,
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> IsVerifiedAsync(
        long sizeBytes,
        string sha256,
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        return file.Exists
            && file.Length == sizeBytes
            && string.Equals(
                await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
                sha256,
                StringComparison.Ordinal);
    }

    private async Task BeginOperationAsync(string message, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A model operation is already running.");
        }

        lock (_stateGate)
        {
            _busy = true;
            _message = message;
        }
    }

    private void EndOperation()
    {
        lock (_stateGate)
        {
            _busy = false;
        }

        _gate.Release();
    }

    private static async Task WriteVerifiedAsync(
        Stream source,
        string destination,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            await using FileStream output = new(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                bytes += read;
                if (bytes > sizeBytes)
                {
                    throw new InvalidDataException("The model is larger than the pinned file.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (bytes != sizeBytes || !string.Equals(actual, sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The model size or SHA-256 does not match the pinned file.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void InstallVerifiedFile(WhisperModelDefinition definition, string temporaryPath)
    {
        string destination = GetManagedModelPath(definition);
        File.Move(temporaryPath, destination, overwrite: true);
        SetReadableMode(destination);
        RemoveOtherManagedModels(definition);
        RecordVerification(definition, new FileInfo(destination), valid: true);
    }

    private static void RemoveOtherManagedModels(WhisperModelDefinition selected)
    {
        string selectedPath = GetManagedModelPath(selected);
        foreach (WhisperModelDefinition candidate in WhisperModelCatalog.AssessmentCandidates)
        {
            string candidatePath = GetManagedModelPath(candidate);
            if (!string.Equals(candidatePath, selectedPath, StringComparison.Ordinal) && File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private void RecordVerification(WhisperModelDefinition definition, FileInfo file, bool valid)
    {
        lock (_stateGate)
        {
            _verifiedPath = file.FullName;
            _verifiedLength = file.Exists ? file.Length : -1;
            _verifiedWriteUtc = file.Exists ? file.LastWriteTimeUtc : default;
            _verifiedValid = valid;
            if (file.Exists && valid)
            {
                _identityPath = file.FullName;
                _identityLength = file.Length;
                _identityWriteUtc = file.LastWriteTimeUtc;
                _identityHash = definition.Sha256;
            }
            else if (file.Exists)
            {
                _message = "The installed file does not match the pinned model SHA-256.";
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void SetReadableMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void DeleteTemporary(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void SetMessage(string message)
    {
        lock (_stateGate)
        {
            _message = message;
        }
    }
}
