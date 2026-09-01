using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Bounded process-local OAuth flow state. All transitions are serialized.</summary>
public sealed class ScryerOAuthFlowStore
{
    private const int MaximumTransactions = 1024;
    private const int MaximumTransactionsPerUser = 3;
    private const int MaximumStartsPerUser = 6;
    private const int MaximumRateBuckets = 1024;
    private static readonly TimeSpan StartRateWindow = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, ScryerOAuthFlowTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _startsByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);

    internal bool TryCreate(ScryerOAuthFlowTransaction transaction, out bool rateLimited)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            CleanupUnsafe(now);
            rateLimited = false;
            if (_transactions.ContainsKey(transaction.State)) return false;
            if (!_startsByUser.TryGetValue(transaction.JellyfinUserId, out var starts))
            {
                if (_startsByUser.Count >= MaximumRateBuckets) { rateLimited = true; return false; }
                starts = new Queue<DateTimeOffset>();
                _startsByUser.Add(transaction.JellyfinUserId, starts);
            }
            if (starts.Count >= MaximumStartsPerUser) { rateLimited = true; return false; }
            EvictOldestOwnActiveUnsafe(transaction.JellyfinUserId);
            if (_transactions.Count >= MaximumTransactions) return false;
            transaction.Generation = GetGenerationUnsafe(transaction.JellyfinUserId);
            _transactions.Add(transaction.State, transaction);
            starts.Enqueue(now);
            return true;
        }
    }

    internal void Remove(string state)
    {
        lock (_gate) { CleanupUnsafe(DateTimeOffset.UtcNow); RemoveUnsafe(state); }
    }

    internal bool TryGet(string state, out ScryerOAuthFlowTransaction transaction)
    {
        transaction = null!;
        lock (_gate)
        {
            CleanupUnsafe(DateTimeOffset.UtcNow);
            return !string.IsNullOrWhiteSpace(state) && _transactions.TryGetValue(state, out transaction!);
        }
    }

    internal bool TryStageCallback(string state, string flowId, string browserBinding, string? code, string? error, string finalizeBinding, out ScryerOAuthFlowTransaction transaction)
    {
        transaction = null!;
        lock (_gate)
        {
            CleanupUnsafe(DateTimeOffset.UtcNow);
            if (!_transactions.TryGetValue(state, out var candidate) || candidate.Status != ScryerOAuthFlowStatus.Active || !FixedTimeEquals(candidate.FlowId, flowId)) return false;
            if (!FixedTimeEquals(candidate.BrowserBinding, browserBinding)) { RemoveUnsafe(state); return false; }
            candidate.CallbackCode = code;
            candidate.CallbackError = error;
            candidate.FinalizeBinding = finalizeBinding;
            candidate.Status = ScryerOAuthFlowStatus.PendingFinalize;
            transaction = candidate;
            return true;
        }
    }

    internal bool TryBeginFinalize(string flowId, string finalizeBinding, string jellyfinUserId, out ScryerOAuthFlowTransaction transaction)
    {
        transaction = null!;
        lock (_gate)
        {
            CleanupUnsafe(DateTimeOffset.UtcNow);
            var candidate = _transactions.Values.FirstOrDefault(flow => string.Equals(flow.FlowId, flowId, StringComparison.Ordinal));
            if (candidate is null || candidate.Status != ScryerOAuthFlowStatus.PendingFinalize || !FixedTimeEquals(candidate.FinalizeBinding ?? string.Empty, finalizeBinding)) return false;
            if (!FixedTimeEquals(candidate.JellyfinUserId, jellyfinUserId) || candidate.Generation != GetGenerationUnsafe(jellyfinUserId)) { RemoveUnsafe(candidate.State); return false; }
            candidate.Status = ScryerOAuthFlowStatus.Finalizing;
            transaction = candidate;
            return true;
        }
    }

    internal bool IsFinalizeCurrent(ScryerOAuthFlowTransaction transaction)
    {
        lock (_gate)
        {
            CleanupUnsafe(DateTimeOffset.UtcNow);
            return _transactions.TryGetValue(transaction.State, out var current) && ReferenceEquals(current, transaction) && current.Status == ScryerOAuthFlowStatus.Finalizing && !current.Invalidated && current.Generation == GetGenerationUnsafe(current.JellyfinUserId);
        }
    }

    internal void CompleteFinalize(ScryerOAuthFlowTransaction transaction)
    {
        lock (_gate) { RemoveUnsafe(transaction.State); CleanupUnsafe(DateTimeOffset.UtcNow); }
    }

    internal void InvalidateUser(string jellyfinUserId)
    {
        lock (_gate)
        {
            CleanupUnsafe(DateTimeOffset.UtcNow);
            var own = _transactions.Values.Where(flow => string.Equals(flow.JellyfinUserId, jellyfinUserId, StringComparison.Ordinal)).ToArray();
            if (own.Length == 0) return;
            _generations[jellyfinUserId] = GetGenerationUnsafe(jellyfinUserId) + 1;
            foreach (var flow in own)
            {
                if (flow.Status == ScryerOAuthFlowStatus.Finalizing) flow.Invalidated = true;
                else RemoveUnsafe(flow.State);
            }
        }
    }

    private void CleanupUnsafe(DateTimeOffset now)
    {
        foreach (var state in _transactions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) RemoveUnsafe(state);
        foreach (var pair in _startsByUser.ToArray())
        {
            while (pair.Value.Count != 0 && pair.Value.Peek().Add(StartRateWindow) <= now) pair.Value.Dequeue();
            if (pair.Value.Count == 0) _startsByUser.Remove(pair.Key);
        }
        foreach (var userId in _generations.Keys.ToArray())
            if (!_transactions.Values.Any(flow => string.Equals(flow.JellyfinUserId, userId, StringComparison.Ordinal))) _generations.Remove(userId);
    }

    private void EvictOldestOwnActiveUnsafe(string userId)
    {
        var active = _transactions.Where(pair => string.Equals(pair.Value.JellyfinUserId, userId, StringComparison.Ordinal) && pair.Value.Status != ScryerOAuthFlowStatus.Finalizing).OrderBy(pair => pair.Value.IssuedAt).ToArray();
        for (var index = 0; index < active.Length - MaximumTransactionsPerUser + 1; index++) RemoveUnsafe(active[index].Key);
    }

    private void RemoveUnsafe(string state) => _transactions.Remove(state);
    private long GetGenerationUnsafe(string userId) => _generations.TryGetValue(userId, out var generation) ? generation : 0;
    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal enum ScryerOAuthFlowStatus { Active, PendingFinalize, Finalizing }

/// <summary>Server-only secret material; it is neither public nor serialized by default.</summary>
internal sealed class ScryerOAuthFlowTransaction
{
    public ScryerOAuthFlowTransaction(string flowId, string state, string browserBinding, string jellyfinUserId, string codeVerifier, string redirectUri, string configurationFingerprint, string returnPage, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        FlowId = flowId; State = state; BrowserBinding = browserBinding; JellyfinUserId = jellyfinUserId; CodeVerifier = codeVerifier;
        RedirectUri = redirectUri; ConfigurationFingerprint = configurationFingerprint; ReturnPage = returnPage; IssuedAt = issuedAt; ExpiresAt = expiresAt;
    }
    internal string FlowId { get; }
    internal string State { get; }
    internal string BrowserBinding { get; }
    internal string JellyfinUserId { get; }
    internal string CodeVerifier { get; }
    internal string RedirectUri { get; }
    internal string ConfigurationFingerprint { get; }
    internal string ReturnPage { get; }
    internal DateTimeOffset IssuedAt { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal long Generation { get; set; }
    internal ScryerOAuthFlowStatus Status { get; set; }
    internal string? CallbackCode { get; set; }
    internal string? CallbackError { get; set; }
    internal string? FinalizeBinding { get; set; }
    internal bool Invalidated { get; set; }
    public override string ToString() => nameof(ScryerOAuthFlowTransaction) + " [redacted]";
}
