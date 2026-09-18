namespace Jellyfin.Plugin.SubtitleAligner.Tests;

using System.Net;
using Xunit;

public sealed class WhisperFallbackTests
{
    [Fact]
    public async Task ExecuteWithRetriesAsync_RetriesTransientRemoteFailuresTwice()
    {
        int calls = 0;
        List<int> retryAttempts = [];

        string result = await WhisperServer.ExecuteWithRetriesAsync(
            attempt =>
            {
                calls++;
                return attempt < 3
                    ? Task.FromException<string>(new HttpRequestException("Synthetic transport failure"))
                    : Task.FromResult("remote-result");
            },
            exception => WhisperServer.ShouldFallbackToLocal(exception, CancellationToken.None),
            maximumAttempts: 3,
            (nextAttempt, exception) =>
            {
                Assert.IsType<HttpRequestException>(exception);
                retryAttempts.Add(nextAttempt);
                return Task.CompletedTask;
            });

        Assert.Equal("remote-result", result);
        Assert.Equal(3, calls);
        Assert.Equal([2, 3], retryAttempts);
    }

    [Fact]
    public async Task ExecuteWithFallbackAsync_RetriesCurrentOperationExactlyOnce()
    {
        int calls = 0;
        bool localActive = false;

        string result = await WhisperServer.ExecuteWithFallbackAsync(
            () =>
            {
                calls++;
                return localActive
                    ? Task.FromResult("nas-result")
                    : Task.FromException<string>(new HttpRequestException("Synthetic transport failure"));
            },
            exception => WhisperServer.ShouldFallbackToLocal(exception, CancellationToken.None),
            exception =>
            {
                Assert.IsType<HttpRequestException>(exception);
                localActive = true;
                return Task.CompletedTask;
            });

        Assert.Equal("nas-result", result);
        Assert.Equal(2, calls);
        Assert.True(localActive);
    }

    [Fact]
    public void ShouldFallbackToLocal_AcceptsOnlyTransientTransportOrBackendFailures()
    {
        Assert.True(WhisperServer.ShouldFallbackToLocal(
            new HttpRequestException("Synthetic transport failure"),
            CancellationToken.None));
        Assert.True(WhisperServer.ShouldFallbackToLocal(
            new TaskCanceledException("Synthetic transport timeout"),
            CancellationToken.None));
        Assert.True(WhisperServer.ShouldFallbackToLocal(
            new WhisperApiException(503, "overloaded", "Synthetic overload", string.Empty),
            CancellationToken.None));
        Assert.True(WhisperServer.ShouldFallbackToLocal(
            new WhisperApiException(502, "backend_failure", "Synthetic backend failure", string.Empty),
            CancellationToken.None));

        Assert.False(WhisperServer.ShouldFallbackToLocal(
            new WhisperApiException(400, "invalid_language", "Synthetic request failure", string.Empty),
            CancellationToken.None));
        Assert.False(WhisperServer.ShouldFallbackToLocal(
            new WhisperApiException(502, "timing_granularity_downgraded", "Synthetic contract failure", string.Empty),
            CancellationToken.None));
        Assert.False(WhisperServer.ShouldFallbackToLocal(
            new OperationCanceledException("Synthetic cancellation"),
            CancellationToken.None));
    }

    [Fact]
    public void ShouldFallbackToLocal_NeverOverridesCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.False(WhisperServer.ShouldFallbackToLocal(
            new HttpRequestException("Synthetic transport failure", null, HttpStatusCode.ServiceUnavailable),
            cancellation.Token));
    }
}
