using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>
/// Owns the plugin's own persisted DataProtection key ring.
/// </summary>
/// <remarks>
/// Jellyfin's injected <see cref="IDataProtectionProvider"/> is ephemeral whenever the server runs
/// without a writable user profile or HKLM registry - the standard Docker deployment. Jellyfin logs
/// "Using an in-memory repository. Keys will not be persisted to storage." at startup and every
/// protected payload becomes undecryptable at the next restart. Refresh grants are long-lived, so
/// the plugin keys them from its own persisted file-backed key ring instead.
/// </remarks>
public sealed class ScryerDataProtection : IDataProtectionProvider
{
    private const string KeyRingApplicationName = "Jellyfin.Plugin.Scryer";
    private readonly IDataProtectionProvider _provider;

    public ScryerDataProtection(IApplicationPaths applicationPaths, ILogger<ScryerDataProtection> logger)
        : this(
            GetKeyRingDirectory((applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath),
            logger ?? throw new ArgumentNullException(nameof(logger)))
    {
    }

    private ScryerDataProtection(string keyRingDirectory, ILogger logger)
    {
        KeyRingDirectory = keyRingDirectory;
        var directory = CreateKeyRingDirectory(keyRingDirectory);
        _provider = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName(KeyRingApplicationName));
        logger.LogInformation(
            "Scryer protects stored OAuth grants with its own persisted key ring at {KeyRingDirectory}. Back it up with the grant directory; losing it forces every linked user to reconnect.",
            keyRingDirectory);
    }

    /// <summary>Absolute path of the plugin-owned key ring directory.</summary>
    public string KeyRingDirectory { get; }

    /// <summary>
    /// Builds the production key ring over a Jellyfin data path. Self-tests call this so they
    /// exercise the same persisted provider the server uses.
    /// </summary>
    public static ScryerDataProtection Create(string dataPath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        return new ScryerDataProtection(GetKeyRingDirectory(dataPath), logger ?? NullLogger.Instance);
    }

    public static string GetKeyRingDirectory(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        return Path.Combine(dataPath, "plugins", "scryer", "keys");
    }

    public IDataProtector CreateProtector(string purpose) => _provider.CreateProtector(purpose);

    private static DirectoryInfo CreateKeyRingDirectory(string keyRingDirectory)
    {
        var directory = Directory.CreateDirectory(keyRingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    keyRingDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
                // A restrictive mode is defence in depth. An exotic or remote filesystem that
                // rejects it must not stop the plugin from protecting grants at all.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }

        return directory;
    }
}
