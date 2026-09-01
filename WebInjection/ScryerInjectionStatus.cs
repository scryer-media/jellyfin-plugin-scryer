using System;
using System.Threading;

namespace Jellyfin.Plugin.Scryer.WebInjection;

/// <summary>
/// Thread-safe, identity-free observability for request-time web injection.
/// </summary>
public sealed class ScryerInjectionStatus
{
    private long _indexRequests;
    private long _successfulInjections;
    private long _alreadyPresent;
    private long _failedInjections;
    private long _lastSuccessfulInjectionTicks;
    private string? _lastFailure;

    public void RecordIndexRequest() => Interlocked.Increment(ref _indexRequests);

    public void RecordInjected()
    {
        Interlocked.Increment(ref _successfulInjections);
        Interlocked.Exchange(ref _lastSuccessfulInjectionTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordAlreadyPresent() => Interlocked.Increment(ref _alreadyPresent);

    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.Increment(ref _failedInjections);
        Volatile.Write(ref _lastFailure, SanitizeFailure(exception));
    }

    public ScryerInjectionSnapshot GetSnapshot()
    {
        var ticks = Interlocked.Read(ref _lastSuccessfulInjectionTicks);
        return new ScryerInjectionSnapshot(
            Interlocked.Read(ref _indexRequests),
            Interlocked.Read(ref _successfulInjections),
            Interlocked.Read(ref _alreadyPresent),
            Interlocked.Read(ref _failedInjections),
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            Volatile.Read(ref _lastFailure));
    }

    private static string SanitizeFailure(Exception exception)
    {
        var name = exception.GetType().Name;
        return string.IsNullOrWhiteSpace(name) ? "Injection failed." : $"Injection failed ({name}).";
    }
}

public sealed record ScryerInjectionSnapshot(
    long IndexRequests,
    long SuccessfulInjections,
    long AlreadyPresent,
    long FailedInjections,
    DateTimeOffset? LastSuccessfulInjectionAtUtc,
    string? LastFailure);
