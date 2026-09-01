using System;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Creates RFC 7636 S256 values for a server-held OAuth flow transaction.</summary>
public static class ScryerPkce
{
    public static ScryerPkcePair Create()
    {
        Span<byte> verifierBytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64UrlEncode(verifierBytes);
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return new ScryerPkcePair(verifier, Base64UrlEncode(digest));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Contains a secret verifier and must remain inside server-side flow state.</summary>
public sealed class ScryerPkcePair
{
    public ScryerPkcePair(string verifier, string challenge)
    {
        Verifier = verifier;
        Challenge = challenge;
    }

    [JsonIgnore]
    public string Verifier { get; }
    public string Challenge { get; }

    public override string ToString() => nameof(ScryerPkcePair) + " [redacted]";
}
