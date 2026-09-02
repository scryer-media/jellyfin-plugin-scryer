using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Scryer.AndroidTv;

internal enum ScryerTvJournalState
{
    Pending,
    Completed
}

internal enum ScryerTvJournalBeginResult
{
    Started,
    Pending,
    Completed,
    Unavailable
}

internal sealed record ScryerTvJournalEntry(
    string JellyfinUserId,
    string TargetKey,
    string TargetKind,
    Guid JellyfinItemId,
    ScryerTvJournalState State,
    DateTimeOffset UpdatedAt);

internal interface IScryerTvActionJournal
{
    Task<ScryerTvJournalBeginResult> BeginAsync(ScryerTvJournalEntry entry, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken);
    Task<bool> RearmAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken);
    Task<bool> AbandonAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScryerTvJournalEntry>> GetPendingAsync(CancellationToken cancellationToken);
}

/// <summary>A bounded, token-free action journal with atomic replacement persistence.</summary>
internal sealed class ScryerTvActionJournal : IScryerTvActionJournal
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 1024;
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumValueLength = 256;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private Dictionary<string, ScryerTvJournalEntry>? _entries;

    public ScryerTvActionJournal(IApplicationPaths applicationPaths)
        : this(Path.Combine((applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath, "plugins", "scryer", "android-tv-actions.json"), TimeProvider.System)
    {
    }

    internal ScryerTvActionJournal(string path, TimeProvider timeProvider)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ScryerTvJournalBeginResult> BeginAsync(ScryerTvJournalEntry entry, CancellationToken cancellationToken)
    {
        if (!IsValid(entry))
        {
            return ScryerTvJournalBeginResult.Unavailable;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ScryerTvJournalBeginResult.Unavailable;
            }

            var key = Key(entry.JellyfinUserId, entry.TargetKey);
            if (_entries!.TryGetValue(key, out var existing))
            {
                return existing.State == ScryerTvJournalState.Completed
                    ? ScryerTvJournalBeginResult.Completed
                    : ScryerTvJournalBeginResult.Pending;
            }

            var pruned = PruneCompletedForInsert();
            if (_entries.Count >= MaximumEntries)
            {
                Restore(pruned);
                return ScryerTvJournalBeginResult.Unavailable;
            }

            _entries[key] = entry with { State = ScryerTvJournalState.Pending, UpdatedAt = _timeProvider.GetUtcNow() };
            if (!await PersistAsync(cancellationToken).ConfigureAwait(false))
            {
                _entries.Remove(key);
                Restore(pruned);
                return ScryerTvJournalBeginResult.Unavailable;
            }

            return ScryerTvJournalBeginResult.Started;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> CompleteAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken) =>
        UpdateAsync(jellyfinUserId, targetKey, complete: true, cancellationToken);

    public Task<bool> RearmAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken) =>
        UpdateAsync(jellyfinUserId, targetKey, complete: false, cancellationToken);

    public Task<bool> AbandonAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken) =>
        UpdateAsync(jellyfinUserId, targetKey, complete: false, cancellationToken);

    public async Task<IReadOnlyList<ScryerTvJournalEntry>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false))
            {
                return Array.Empty<ScryerTvJournalEntry>();
            }

            return _entries!.Values
                .Where(entry => entry.State == ScryerTvJournalState.Pending)
                .OrderBy(entry => entry.UpdatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> UpdateAsync(string jellyfinUserId, string targetKey, bool complete, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var key = Key(jellyfinUserId, targetKey);
            if (!_entries!.TryGetValue(key, out var existing))
            {
                return true;
            }

            if (complete)
            {
                _entries[key] = existing with { State = ScryerTvJournalState.Completed, UpdatedAt = _timeProvider.GetUtcNow() };
            }
            else
            {
                _entries.Remove(key);
            }

            if (await PersistAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _entries[key] = existing;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return true;
        }

        try
        {
            if (!File.Exists(_path))
            {
                _entries = new Dictionary<string, ScryerTvJournalEntry>(StringComparer.Ordinal);
                return true;
            }

            var info = new FileInfo(_path);
            if (info.Length is < 0 or > MaximumBytes)
            {
                return false;
            }

            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var file = await JsonSerializer.DeserializeAsync<JournalFile>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (file is null || file.Version != CurrentVersion || file.Entries is null ||
                file.Entries.Count > MaximumEntries || file.Entries.Any(entry => entry is null || !IsValid(entry)))
            {
                return false;
            }

            _entries = file.Entries
                .GroupBy(entry => Key(entry.JellyfinUserId, entry.TargetKey), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAt).First(), StringComparer.Ordinal);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var temporaryPath = _path + "." + Convert.ToHexString(Guid.NewGuid().ToByteArray()) + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new JournalFile(CurrentVersion, _entries!.Values.OrderBy(entry => entry.UpdatedAt).ToArray()), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumBytes)
            {
                return false;
            }

            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private IReadOnlyList<KeyValuePair<string, ScryerTvJournalEntry>> PruneCompletedForInsert()
    {
        var removed = new List<KeyValuePair<string, ScryerTvJournalEntry>>();
        while (_entries!.Count >= MaximumEntries)
        {
            var oldestCompleted = _entries
                .Where(pair => pair.Value.State == ScryerTvJournalState.Completed)
                .OrderBy(pair => pair.Value.UpdatedAt)
                .FirstOrDefault();
            if (oldestCompleted.Key is null)
            {
                break;
            }

            removed.Add(oldestCompleted);
            _entries.Remove(oldestCompleted.Key);
        }

        return removed;
    }

    private void Restore(IReadOnlyList<KeyValuePair<string, ScryerTvJournalEntry>> entries)
    {
        foreach (var entry in entries)
        {
            _entries![entry.Key] = entry.Value;
        }
    }

    private static bool IsValid(ScryerTvJournalEntry entry) =>
        entry.JellyfinItemId != Guid.Empty &&
        !string.IsNullOrEmpty(entry.JellyfinUserId) && entry.JellyfinUserId.Length <= MaximumValueLength &&
        Guid.TryParseExact(entry.JellyfinUserId, "N", out _) &&
        !string.IsNullOrEmpty(entry.TargetKey) && entry.TargetKey.Length <= MaximumValueLength &&
        entry.TargetKind is "MOVIE" or "SERIES" or "ANIME" &&
        entry.State is ScryerTvJournalState.Pending or ScryerTvJournalState.Completed;

    private static string Key(string jellyfinUserId, string targetKey) => jellyfinUserId + "\u001f" + targetKey;

    private sealed record JournalFile(int Version, IReadOnlyList<ScryerTvJournalEntry> Entries);
}
