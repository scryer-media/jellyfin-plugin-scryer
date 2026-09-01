using System;
using System.Collections.Generic;
using System.Security.Claims;

namespace Jellyfin.Plugin.Scryer.Api;

/// <summary>
/// Extracts the sole authenticated, non-API-key Jellyfin user identity accepted by Scryer.
/// </summary>
public static class TrustedJellyfinActor
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";
    private const string JellyfinIsApiKeyClaim = "Jellyfin-IsApiKey";

    public static bool TryGetUserId(ClaimsPrincipal? principal, out string jellyfinUserId)
    {
        jellyfinUserId = string.Empty;
        if (principal is null)
        {
            return false;
        }

        var foundTrustedIdentity = false;
        foreach (var identity in principal.Identities)
        {
            if (!identity.IsAuthenticated)
            {
                continue;
            }

            var apiKeyValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var userIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claim in identity.FindAll(JellyfinIsApiKeyClaim))
            {
                apiKeyValues.Add(claim.Value.Trim());
            }

            foreach (var claim in identity.FindAll(JellyfinUserIdClaim))
            {
                if (!Guid.TryParse(claim.Value.Trim(), out var parsed) || parsed == Guid.Empty)
                {
                    return false;
                }

                userIds.Add(parsed.ToString("N"));
            }

            if (apiKeyValues.Count == 0 && userIds.Count == 0)
            {
                continue;
            }

            if (foundTrustedIdentity || apiKeyValues.Count != 1 || userIds.Count != 1 ||
                !bool.TryParse(GetOnlyValue(apiKeyValues), out var isApiKey) || isApiKey)
            {
                return false;
            }

            jellyfinUserId = GetOnlyValue(userIds);
            foundTrustedIdentity = true;
        }

        return foundTrustedIdentity;
    }

    private static string GetOnlyValue(IReadOnlyCollection<string> values)
    {
        foreach (var value in values)
        {
            return value;
        }

        return string.Empty;
    }
}
