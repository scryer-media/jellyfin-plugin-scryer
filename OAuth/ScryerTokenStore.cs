using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Scryer.OAuth;

public interface IScryerTokenStore
{
    Task<ScryerGrantReadResult> ReadAsync(ScryerGrantKey key, CancellationToken cancellationToken);
    Task<ScryerGrantReadResult> ReadCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<bool> SaveAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken);
    Task<bool> QuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken);
    Task<bool> QuarantineDetachedAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScryerRefreshGrant>> ReadDetachedQuarantinesAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<bool> DeleteDetachedQuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken);
    Task<bool> PromotePendingAsync(ScryerRefreshGrant pendingGrant, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetPendingUserIdsAsync(int maximumCount, string? afterUserId, CancellationToken cancellationToken);
    Task<ScryerLinkedGrantCount> GetActiveLinkedGrantCountAsync(int maximumEntries, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(ScryerGrantKey key, CancellationToken cancellationToken);
    Task<bool> DeleteCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken);
}

/// <summary>
/// Persists only encrypted refresh-token records. A record's filename is a one-way SHA-256
/// identifier and its protected payload repeats the complete grant binding before use.
/// </summary>
public sealed class ScryerTokenStore : IScryerTokenStore
{
    private const int MaximumProtectedGrantBytes = 128 * 1024;
    private const int MaximumPendingScanEntries = 256;
    private const string PurposeRoot = "Jellyfin.Plugin.Scryer";
    private const string PurposeRecord = "OAuthRefreshGrant";
    private const string PurposeVersion = "v1";
    private const string UndecryptableSuffix = ".undecryptable-";
    private static int _keyRingWarningEmitted;
    private readonly IDataProtector _protector;
    private readonly string _directory;
    private readonly ILogger _logger;

    /// <summary>
    /// Takes the plugin-owned key ring explicitly. Jellyfin's injected provider is ephemeral in a
    /// container deployment, so binding to it would silently drop every grant on restart.
    /// </summary>
    public ScryerTokenStore(ScryerDataProtection dataProtection, IApplicationPaths applicationPaths, ILogger<ScryerTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);
        _protector = dataProtection.CreateProtector(PurposeRoot, PurposeRecord, PurposeVersion);
        _directory = Path.Combine(applicationPaths.DataPath, "plugins", "scryer", "oauth-grants");
        _logger = logger;
    }

    public async Task<ScryerGrantReadResult> ReadAsync(ScryerGrantKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var current = await ReadCurrentAsync(key.JellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (current.State is not (ScryerGrantReadState.Found or ScryerGrantReadState.Legacy) || current.Grant is null)
        {
            return current;
        }

        return SameBinding(current.Grant.Key, key) ? current : ScryerGrantReadResult.Missing;
    }

    public async Task<ScryerGrantReadResult> ReadCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jellyfinUserId);
        var path = GetPath(jellyfinUserId);
        var journalPath = GetJournalPath(path);
        try
        {
            // A journal is a fully flushed, newer grant. Never fall back to the primary record
            // while it exists because the primary refresh token may already have been spent.
            var hasJournal = File.Exists(journalPath);
            var candidatePath = hasJournal ? journalPath : path;
            if (!File.Exists(candidatePath))
            {
                return ScryerGrantReadResult.Missing;
            }

            var protectedBytes = await ReadBoundedAsync(candidatePath, cancellationToken).ConfigureAwait(false);
            if (protectedBytes is null)
            {
                return await CorruptResultAsync(path, hasJournal ? journalPath : null, decryptionFailed: false, cancellationToken).ConfigureAwait(false);
            }

            var json = Encoding.UTF8.GetString(_protector.Unprotect(protectedBytes));
            var record = JsonSerializer.Deserialize<StoredGrant>(json);
            if (record is null || string.IsNullOrWhiteSpace(record.RefreshToken) ||
                !FixedTimeEquals(record.JellyfinUserId, jellyfinUserId) ||
                !IsValidStoredAuthority(record.Authority) || string.IsNullOrWhiteSpace(record.ClientId))
            {
                return await CorruptResultAsync(path, hasJournal ? journalPath : null, decryptionFailed: false, cancellationToken).ConfigureAwait(false);
            }

            if (hasJournal)
            {
                File.Move(journalPath, path, overwrite: true);
            }

            var key = new ScryerGrantKey(record.JellyfinUserId, record.Authority, record.ClientId);
            if (record.Version == 1)
            {
                // A v1 grant predates the dedicated consent scope. It is never eligible for
                // automatic linking or ordinary access; the session service revokes/deletes it.
                return new ScryerGrantReadResult(ScryerGrantReadState.Legacy, new ScryerRefreshGrant(key, record.RefreshToken, record.UpdatedAt));
            }

            if ((record.Version != 2 && record.Version != 3) ||
                !Enum.TryParse<ScryerGrantLinkState>(record.LinkState, ignoreCase: false, out var linkState) ||
                record.LinkIdempotencyKey is not null || record.LinkAttempts is < 0 or > 3)
            {
                return await CorruptResultAsync(path, hasJournal ? journalPath : null, decryptionFailed: false, cancellationToken).ConfigureAwait(false);
            }

            var grantedScope = record.Version == 2 ? ScryerOAuthScopes.Linked : record.GrantedScope;
            if (!ScryerOAuthScopes.TryNormalizeExact(grantedScope, out var normalizedScope))
            {
                return await CorruptResultAsync(path, hasJournal ? journalPath : null, decryptionFailed: false, cancellationToken).ConfigureAwait(false);
            }

            return new ScryerGrantReadResult(ScryerGrantReadState.Found,
                new ScryerRefreshGrant(key, record.RefreshToken, record.UpdatedAt, linkState, record.LinkIdempotencyKey, record.LinkAttempts, normalizedScope));
        }
        catch (CryptographicException)
        {
            return await CorruptResultAsync(path, File.Exists(journalPath) ? journalPath : null, decryptionFailed: true, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await CorruptResultAsync(path, File.Exists(journalPath) ? journalPath : null, decryptionFailed: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new ScryerGrantReadResult(ScryerGrantReadState.Unavailable, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new ScryerGrantReadResult(ScryerGrantReadState.Unavailable, null);
        }
    }

    public async Task<bool> SaveAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        return await WriteGrantAsync(grant, promoteJournal: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> QuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.LinkState != ScryerGrantLinkState.PendingRevoke)
        {
            return false;
        }

        return await WriteGrantAsync(grant, promoteJournal: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> QuarantineDetachedAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.LinkState == ScryerGrantLinkState.PendingRevoke &&
            await WriteGrantAsync(grant, promoteJournal: false, cancellationToken, GetDetachedQuarantinePath(grant)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScryerRefreshGrant>> ReadDetachedQuarantinesAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        var grants = new List<ScryerRefreshGrant>();
        if (!Directory.Exists(_directory)) return grants;
        foreach (var candidatePath in Directory.EnumerateFiles(_directory, "*.revoke.dat*"))
        {
            var path = candidatePath;
            if (path.EndsWith(".revoke.dat.next", StringComparison.Ordinal))
            {
                try
                {
                    var finalPath = path[..^".next".Length];
                    File.Move(path, finalPath, overwrite: true);
                    path = finalPath;
                }
                catch (IOException error)
                {
                    throw new IOException("A detached revocation journal could not be promoted.", error);
                }
                catch (UnauthorizedAccessException error)
                {
                    throw new IOException("A detached revocation journal could not be promoted.", error);
                }
            }
            else if (!path.EndsWith(".revoke.dat", StringComparison.Ordinal))
            {
                continue;
            }
            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                throw new IOException("A detached revocation record could not be read.");
            }
            var record = JsonSerializer.Deserialize<StoredGrant>(Encoding.UTF8.GetString(_protector.Unprotect(bytes)));
            if (record is null ||
                !IsValidStoredUserId(record.JellyfinUserId) ||
                record.LinkState != ScryerGrantLinkState.PendingRevoke.ToString() ||
                string.IsNullOrWhiteSpace(record.RefreshToken))
            {
                throw new InvalidDataException("A detached revocation record is invalid.");
            }
            if (record.JellyfinUserId == jellyfinUserId)
            {
                var grantedScope = record.Version == 2 ? ScryerOAuthScopes.Linked : record.GrantedScope;
                if (!ScryerOAuthScopes.TryNormalizeExact(grantedScope, out var normalizedScope))
                {
                    throw new InvalidDataException("A detached revocation record has an invalid scope.");
                }
                grants.Add(new ScryerRefreshGrant(new ScryerGrantKey(record.JellyfinUserId, record.Authority, record.ClientId), record.RefreshToken, record.UpdatedAt, ScryerGrantLinkState.PendingRevoke, record.LinkIdempotencyKey, record.LinkAttempts, normalizedScope));
            }
        }
        return grants;
    }

    public Task<bool> DeleteDetachedQuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetDetachedQuarantinePath(grant);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var journalPath = GetJournalPath(path);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            return Task.FromResult(!File.Exists(path) && !File.Exists(journalPath));
        }
        catch (IOException) { return Task.FromResult(false); }
        catch (UnauthorizedAccessException) { return Task.FromResult(false); }
    }

    private async Task<bool> WriteGrantAsync(
        ScryerRefreshGrant grant,
        bool promoteJournal,
        CancellationToken cancellationToken,
        string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrWhiteSpace(grant.RefreshToken) ||
            grant.LinkIdempotencyKey is not null || grant.LinkAttempts is < 0 or > 3 ||
            !ScryerOAuthScopes.TryNormalizeExact(grant.GrantedScope, out var normalizedScope) ||
            !string.Equals(normalizedScope, grant.GrantedScope, StringComparison.Ordinal))
        {
            return false;
        }

        var path = targetPath ?? GetPath(grant.Key.JellyfinUserId);
        var journalPath = GetJournalPath(path);
        var temporaryPath = path + "." + CreateTemporarySuffix();
        try
        {
            Directory.CreateDirectory(_directory);
            var record = new StoredGrant(
                3,
                grant.Key.JellyfinUserId,
                grant.Key.Authority,
                grant.Key.ClientId,
                grant.RefreshToken,
                grant.UpdatedAt,
                grant.LinkState.ToString(),
                grant.LinkIdempotencyKey,
                grant.LinkAttempts,
                grant.GrantedScope);
            var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record)));

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // The stable journal is a recoverable commit marker. A process crash can still occur
            // after the remote server issues a token and before this first rename, but after the
            // journal exists a restart will never fall back to the older, potentially spent grant.
            File.Move(temporaryPath, journalPath, overwrite: true);
            if (!promoteJournal)
            {
                return true;
            }
            try
            {
                File.Move(journalPath, path, overwrite: true);
            }
            catch (IOException)
            {
                // A concurrent recovery may have promoted our journal. Treat only an exact,
                // protected binding/token match as success; never fall back to the old primary.
                return await MatchesStoredGrantAsync(path, grant, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            DeleteOnce(temporaryPath);
        }
    }

    public async Task<bool> PromotePendingAsync(ScryerRefreshGrant pendingGrant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingGrant);
        if (pendingGrant.LinkState != ScryerGrantLinkState.PendingLink || pendingGrant.LinkIdempotencyKey is not null) return false;
        var current = await ReadAsync(pendingGrant.Key, cancellationToken).ConfigureAwait(false);
        if (current.State != ScryerGrantReadState.Found || current.Grant is null ||
            current.Grant.LinkState != ScryerGrantLinkState.PendingLink ||
            !SameGrant(current.Grant, pendingGrant)) return false;
        return await SaveAsync(new ScryerRefreshGrant(
            pendingGrant.Key,
            pendingGrant.RefreshToken,
            DateTimeOffset.UtcNow,
            grantedScope: pendingGrant.GrantedScope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPendingUserIdsAsync(int maximumCount, string? afterUserId, CancellationToken cancellationToken)
    {
        if (maximumCount <= 0) return Array.Empty<string>();
        var users = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(_directory)) return Array.Empty<string>();
            var inspected = 0;
            var visited = 0;
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visited++ >= MaximumPendingScanEntries) break;
                if (!path.EndsWith(".dat", StringComparison.Ordinal) && !path.EndsWith(".next", StringComparison.Ordinal)) continue;
                inspected++;
                try
                {
                    var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var record = JsonSerializer.Deserialize<StoredGrant>(Encoding.UTF8.GetString(_protector.Unprotect(bytes)));
                    if (record is not null && IsValidStoredUserId(record.JellyfinUserId))
                    {
                        if ((path.EndsWith(".revoke.dat", StringComparison.Ordinal) ||
                             path.EndsWith(".revoke.dat.next", StringComparison.Ordinal)) &&
                            record.LinkState == ScryerGrantLinkState.PendingRevoke.ToString())
                        {
                            users.Add(record.JellyfinUserId);
                            continue;
                        }
                        var current = await ReadCurrentAsync(record.JellyfinUserId, cancellationToken).ConfigureAwait(false);
                        if (current.State == ScryerGrantReadState.Found && current.Grant is not null &&
                            current.Grant.LinkState is ScryerGrantLinkState.PendingLink or ScryerGrantLinkState.PendingRevoke)
                        {
                            users.Add(record.JellyfinUserId);
                        }
                    }
                }
                catch (CryptographicException) { }
                catch (JsonException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return users
            .Where(userId => string.IsNullOrEmpty(afterUserId) || string.CompareOrdinal(userId, afterUserId) > 0)
            .OrderBy(userId => userId, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
    }

    /// <summary>
    /// Counts only active, locally protected grants without returning, logging, or retaining any
    /// Jellyfin identity. The bounded scan is deliberately read-only: it never promotes journals
    /// or repairs corrupt records as a side effect of an administrator diagnostic request.
    /// </summary>
    public async Task<ScryerLinkedGrantCount> GetActiveLinkedGrantCountAsync(int maximumEntries, CancellationToken cancellationToken)
    {
        if (maximumEntries <= 0)
        {
            return new ScryerLinkedGrantCount(0, true);
        }

        var entryLimit = Math.Min(maximumEntries, MaximumPendingScanEntries);
        var records = new Dictionary<string, (StoredGrant Record, bool IsJournal)>(StringComparer.Ordinal);
        var truncated = false;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return new ScryerLinkedGrantCount(0, false);
            }

            var inspected = 0;
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isJournal = path.EndsWith(".next", StringComparison.Ordinal);
                if (!isJournal && !path.EndsWith(".dat", StringComparison.Ordinal))
                {
                    continue;
                }

                if (inspected >= entryLimit)
                {
                    truncated = true;
                    break;
                }

                inspected++;
                try
                {
                    var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        continue;
                    }

                    var record = JsonSerializer.Deserialize<StoredGrant>(Encoding.UTF8.GetString(_protector.Unprotect(bytes)));
                    if (record is null || !IsCountableStoredGrant(record))
                    {
                        continue;
                    }

                    if (!records.TryGetValue(record.JellyfinUserId, out var existing) || isJournal && !existing.IsJournal)
                    {
                        records[record.JellyfinUserId] = (record, isJournal);
                    }
                }
                catch (CryptographicException) { }
                catch (JsonException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new ScryerLinkedGrantCount(
            records.Values.Count(static item => item.Record.LinkState == ScryerGrantLinkState.Active.ToString()),
            truncated);
    }

    public async Task<bool> DeleteAsync(ScryerGrantKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var current = await ReadCurrentAsync(key.JellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (current.State == ScryerGrantReadState.Found && current.Grant is not null && SameBinding(current.Grant.Key, key))
        {
            return await DeleteCurrentAsync(key.JellyfinUserId, cancellationToken).ConfigureAwait(false);
        }

        return current.State == ScryerGrantReadState.Missing &&
            await BothAbsentAsync(GetPath(key.JellyfinUserId), GetJournalPath(GetPath(key.JellyfinUserId)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jellyfinUserId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(jellyfinUserId);
        var journalPath = GetJournalPath(path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DeleteOnce(path);
            DeleteOnce(journalPath);
            if (await BothAbsentAsync(path, journalPath, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt == 0)
            {
                await Task.Yield();
            }
        }

        return false;
    }

    private string GetPath(string jellyfinUserId)
    {
        var identity = Encoding.UTF8.GetBytes(jellyfinUserId);
        var hash = SHA256.HashData(identity);
        return Path.Combine(_directory, Convert.ToHexString(hash).ToLowerInvariant() + ".dat");
    }

    private string GetDetachedQuarantinePath(ScryerRefreshGrant grant)
    {
        var identity = Encoding.UTF8.GetBytes(grant.Key.CacheIdentity + "\u001f" + grant.RefreshToken);
        var hash = SHA256.HashData(identity);
        return Path.Combine(_directory, Convert.ToHexString(hash).ToLowerInvariant() + ".revoke.dat");
    }

    private async Task<bool> MatchesStoredGrantAsync(string path, ScryerRefreshGrant expected, CancellationToken cancellationToken)
    {
        try
        {
            var protectedBytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (protectedBytes is null)
            {
                return false;
            }

            var json = Encoding.UTF8.GetString(_protector.Unprotect(protectedBytes));
            var record = JsonSerializer.Deserialize<StoredGrant>(json);
            return record is not null && record.Version == 3 &&
                FixedTimeEquals(record.JellyfinUserId, expected.Key.JellyfinUserId) &&
                FixedTimeEquals(record.Authority, expected.Key.Authority) &&
                FixedTimeEquals(record.ClientId, expected.Key.ClientId) &&
                FixedTimeEquals(record.RefreshToken, expected.RefreshToken) &&
                string.Equals(record.LinkState, expected.LinkState.ToString(), StringComparison.Ordinal) &&
                string.Equals(record.LinkIdempotencyKey, expected.LinkIdempotencyKey, StringComparison.Ordinal) &&
                record.LinkAttempts == expected.LinkAttempts &&
                string.Equals(record.GrantedScope, expected.GrantedScope, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameBinding(ScryerGrantKey left, ScryerGrantKey right) =>
        FixedTimeEquals(left.JellyfinUserId, right.JellyfinUserId) &&
        FixedTimeEquals(left.Authority, right.Authority) &&
        FixedTimeEquals(left.ClientId, right.ClientId);

    private static bool IsValidStoredAuthority(string authority) =>
        Uri.TryCreate(authority + "/", UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(parsed.Host) && string.IsNullOrEmpty(parsed.UserInfo) &&
        string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment);

    private static bool IsValidStoredUserId(string value) => Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty &&
        string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);

    private static bool IsCountableStoredGrant(StoredGrant? record) => record is not null &&
        (record.Version == 2 || record.Version == 3) &&
        (record.LinkState == ScryerGrantLinkState.Active.ToString() ||
         record.LinkState == ScryerGrantLinkState.PendingLink.ToString() ||
         record.LinkState == ScryerGrantLinkState.PendingRevoke.ToString()) &&
        record.LinkIdempotencyKey is null &&
        record.LinkAttempts is >= 0 and <= 3 &&
        !string.IsNullOrWhiteSpace(record.RefreshToken) &&
        IsValidStoredUserId(record.JellyfinUserId) &&
        IsValidStoredAuthority(record.Authority) &&
        !string.IsNullOrWhiteSpace(record.ClientId) &&
        (record.Version == 2 || string.Equals(record.GrantedScope, ScryerOAuthScopes.Linked, StringComparison.Ordinal));

    private static bool SameGrant(ScryerRefreshGrant left, ScryerRefreshGrant right) => SameBinding(left.Key, right.Key) &&
        FixedTimeEquals(left.RefreshToken, right.RefreshToken) && string.Equals(left.LinkIdempotencyKey, right.LinkIdempotencyKey, StringComparison.Ordinal) &&
        left.LinkState == right.LinkState && left.LinkAttempts == right.LinkAttempts &&
        string.Equals(left.GrantedScope, right.GrantedScope, StringComparison.Ordinal);

    private static string CreateTemporarySuffix()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string GetJournalPath(string path) => path + ".next";

    private static async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumProtectedGrantBytes)
        {
            return null;
        }

        var buffer = new byte[MaximumProtectedGrantBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaximumProtectedGrantBytes || stream.ReadByte() != -1)
        {
            return null;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>
    /// Retires an unusable record without destroying it. An undecryptable grant is almost always a
    /// key-ring problem rather than real corruption, and the operator can still see that a grant
    /// existed. The user is reported as unusable either way and must reconnect Scryer.
    /// </summary>
    private async Task<ScryerGrantReadResult> CorruptResultAsync(
        string path,
        string? journalPath,
        bool decryptionFailed,
        CancellationToken cancellationToken)
    {
        if (decryptionFailed)
        {
            if (Interlocked.Exchange(ref _keyRingWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "Scryer could not decrypt a stored OAuth grant with the plugin key ring. The affected Jellyfin users are disconnected and must reconnect Scryer in Jellyfin Web. Undecryptable records were renamed with a '{Suffix}' suffix rather than deleted.",
                    UndecryptableSuffix);
            }
        }
        else
        {
            _logger.LogWarning(
                "A stored Scryer OAuth grant failed validation and was quarantined. The affected Jellyfin user must reconnect Scryer.");
        }

        QuarantineOnce(path);
        if (journalPath is not null)
        {
            QuarantineOnce(journalPath);
        }

        var absent = await BothAbsentAsync(path, journalPath, cancellationToken).ConfigureAwait(false);
        return new ScryerGrantReadResult(
            absent ? ScryerGrantReadState.Corrupt : ScryerGrantReadState.Unavailable,
            null,
            RequiresInvalidation: !absent);
    }

    private static void QuarantineOnce(string path)
    {
        var quarantinePath = path + UndecryptableSuffix +
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, quarantinePath, overwrite: true);
        }
        catch (FileNotFoundException)
        {
            // Nothing to retire.
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to retire.
        }
        catch (IOException)
        {
            // A record that cannot be retired is treated as unusable on every subsequent read.
        }
        catch (UnauthorizedAccessException)
        {
            // A record that cannot be retired is treated as unusable on every subsequent read.
        }
    }

    private static void DeleteOnce(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale corrupt record is treated as unusable on every subsequent read.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale corrupt record is treated as unusable on every subsequent read.
        }
    }

    private static async Task<bool> BothAbsentAsync(string path, string? journalPath, CancellationToken cancellationToken)
    {
        return await IsAbsentAsync(path, cancellationToken).ConfigureAwait(false) &&
            (journalPath is null || await IsAbsentAsync(journalPath, cancellationToken).ConfigureAwait(false));
    }

    private static Task<bool> IsAbsentAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.FromResult(false);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(true);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    private sealed record StoredGrant(
        int Version,
        string JellyfinUserId,
        string Authority,
        string ClientId,
        string RefreshToken,
        DateTimeOffset UpdatedAt,
        string? LinkState = null,
        string? LinkIdempotencyKey = null,
        int LinkAttempts = 0,
        string? GrantedScope = null);
}

/// <summary>Identity-free, bounded diagnostic count of active locally stored OAuth grants.</summary>
public sealed record ScryerLinkedGrantCount(int Count, bool IsTruncated);
