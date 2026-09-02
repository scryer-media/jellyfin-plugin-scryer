using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Scryer.Services;

/// <summary>
/// A bounded, token-free projection of Scryer's current-user authorization state.
/// Scryer, not Jellyfin administrator status, remains the authority for these capabilities.
/// </summary>
public sealed record ScryerCapabilitySnapshot(
    string ScryerUserId,
    string Username,
    IReadOnlyList<string> AppPermissions,
    IReadOnlyList<ScryerLibraryCapabilities> Libraries);

/// <summary>Effective plugin capabilities for a single Scryer library.</summary>
public sealed record ScryerLibraryCapabilities(
    string LibraryId,
    bool CanView,
    bool CanRequest,
    bool CanAutoApproveRequests,
    bool CanManageTitles);

/// <summary>A Jellyfin watch-history seed used to build a per-user recommendation rail.</summary>
public sealed record ScryerRecommendationSeed(
    string Title,
    string Facet,
    IReadOnlyDictionary<string, string> ProviderIds);

/// <summary>A title projected into the native Jellyfin channel surface.</summary>
public sealed record ScryerTvDiscoveryItem(
    string TargetKey,
    string TargetKind,
    string DisplayTitle,
    int? Year,
    string? PosterUrl,
    string? Overview);

/// <summary>A named native-TV rail containing bounded, typed discovery items.</summary>
public sealed record ScryerTvDiscoveryRail(
    string Key,
    string Title,
    IReadOnlyList<ScryerTvDiscoveryItem> Items);

public enum ScryerTvActionKind
{
    Added,
    AlreadyPresent,
    Requested
}

/// <summary>The safe result rendered through Jellyfin's native display-message command.</summary>
public sealed record ScryerTvActionResult(ScryerTvActionKind Kind, string LibraryName);
