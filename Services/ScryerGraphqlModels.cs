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

/// <summary>A watch-history-derived recommendation group returned from one GraphQL batch.</summary>
public sealed record ScryerRecommendationGroup(
    string Title,
    IReadOnlyList<ScryerTvDiscoveryItem> Items);

/// <summary>One external identifier Scryer knows for a title, with the source name lowercased.</summary>
public sealed record ScryerTvExternalId(string Source, string Value);

/// <summary>
/// A title projected into the native Jellyfin channel surface. TargetKind is the facet the title
/// is acted on under (MOVIE, SERIES or ANIME), taken from Scryer's content type when it sends one,
/// because a recommendation names an anime as a plain series. ExternalIds are what the title was
/// matched on, kept so the row can still be added when Scryer's discovery store has no detail for
/// its key.
/// </summary>
public sealed record ScryerTvDiscoveryItem(
    string TargetKey,
    string TargetKind,
    string DisplayTitle,
    int? Year,
    string? PosterUrl,
    string? Overview,
    IReadOnlyList<ScryerTvExternalId>? ExternalIds = null);

/// <summary>
/// What the Jellyfin row itself knows about a title, offered to the action when Scryer returns no
/// discovery detail for the row's key. Recommendation rails come from Scryer's title graph, and a
/// title there is not necessarily in the discovery store the detail query reads.
/// </summary>
public sealed record ScryerTvActionFallback(
    string Title,
    int? Year,
    string? Overview,
    IReadOnlyList<ScryerTvExternalId> ExternalIds);

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
