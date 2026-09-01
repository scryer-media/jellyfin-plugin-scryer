using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Scryer.Configuration;

public static class ScryerConfigurationValidator
{
    public const string OAuthCallbackPath = "/Scryer/Auth/Callback";

    public static string NormalizeBaseUrl(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return candidate;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string NormalizeClientId(string? value) => (value ?? string.Empty).Trim();

    public static ScryerConfigurationValidation Validate(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var internalUri = ValidateUrl(configuration.ScryerInternalBaseUrl, "Internal Scryer URL", errors);
        var publicScryerUri = ValidateUrl(configuration.ScryerPublicBaseUrl, "Public Scryer URL", errors);
        var jellyfinUri = ValidateUrl(configuration.JellyfinPublicBaseUrl, "Public Jellyfin URL", errors);

        if (internalUri is not null && internalUri.Scheme == Uri.UriSchemeHttp &&
            !internalUri.IsLoopback && !configuration.AllowInsecureInternalScryerHttp)
        {
            errors.Add("Internal Scryer HTTP requires the explicit insecure private-network opt-in.");
        }

        RequireHttpsExceptLoopback(publicScryerUri, "Public Scryer URL", errors);
        RequireHttpsExceptLoopback(jellyfinUri, "Public Jellyfin URL", errors);

        if (string.IsNullOrWhiteSpace(configuration.OAuthClientId))
        {
            errors.Add("OAuth client ID is required.");
        }
        else if (configuration.OAuthClientId.Length > 256 || configuration.OAuthClientId.Any(char.IsWhiteSpace))
        {
            errors.Add("OAuth client ID must be a single value no longer than 256 characters.");
        }

        if (!Enum.IsDefined(configuration.DiagnosticVerbosity))
        {
            errors.Add("Diagnostic verbosity is invalid.");
        }

        var callbackUri = jellyfinUri is null
            ? null
            : jellyfinUri.AbsoluteUri.TrimEnd('/') + OAuthCallbackPath;

        return new ScryerConfigurationValidation(
            errors.Count == 0,
            errors,
            internalUri?.AbsoluteUri.TrimEnd('/'),
            publicScryerUri?.AbsoluteUri.TrimEnd('/'),
            callbackUri);
    }

    private static Uri? ValidateUrl(string value, string label, ICollection<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"{label} must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.");
            return null;
        }

        return uri;
    }

    private static void RequireHttpsExceptLoopback(Uri? uri, string label, ICollection<string> errors)
    {
        if (uri is not null && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            errors.Add($"{label} must use HTTPS except for explicit loopback development URLs.");
        }
    }
}

public sealed record ScryerConfigurationValidation(
    bool IsValid,
    IReadOnlyList<string> Errors,
    string? InternalBaseUrl,
    string? PublicBaseUrl,
    string? CallbackUri);
