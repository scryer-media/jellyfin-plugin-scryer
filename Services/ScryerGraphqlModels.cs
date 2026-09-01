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
