namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Serializes access to the plugin's single local inference process.</summary>
public sealed class InferenceActivityGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for exclusive access to whisper-server.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
