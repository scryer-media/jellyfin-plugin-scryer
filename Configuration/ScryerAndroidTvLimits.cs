using System;

namespace Jellyfin.Plugin.Scryer.Configuration;

/// <summary>
/// Bounds on how much Scryer discovery the Android TV channel is allowed to publish.
/// </summary>
/// <remarks>
/// Scryer's discovery query takes no size arguments: the server decides how many sections it
/// returns and how many titles sit in each one. Jellyfin persists every published folder as a row
/// in its own library database, per Jellyfin user, and downloads a poster for each title, so an
/// unbounded response becomes unbounded storage on the Jellyfin server. The plugin therefore caps
/// what it hands to Jellyfin instead of publishing whatever arrives. The total-title cap is the
/// governing bound; the rail and per-rail caps shape how that budget is spent.
/// </remarks>
public static class ScryerAndroidTvLimits
{
    /// <summary>Rails published when the administrator has not chosen a limit.</summary>
    public const int DefaultRails = 40;

    /// <summary>Titles published inside one Scryer discovery section when the administrator has not chosen a limit.</summary>
    public const int DefaultItemsPerRail = 20;

    /// <summary>Titles published across the whole channel when the administrator has not chosen a limit.</summary>
    public const int DefaultTitles = 200;

    /// <summary>Highest rail count an administrator may configure.</summary>
    public const int MaximumRails = 60;

    /// <summary>Highest per-rail title count an administrator may configure.</summary>
    public const int MaximumItemsPerRail = 100;

    /// <summary>Highest total title count an administrator may configure.</summary>
    public const int MaximumTitles = 1000;

    /// <summary>
    /// Recently watched Jellyfin titles used as recommendation seeds. Each seed becomes one
    /// "More like ..." rail, published ahead of Scryer's own sections so the channel stays close to
    /// what the user actually watches.
    /// </summary>
    public const int RecommendationSeeds = 20;

    /// <summary>Titles published inside one "More like ..." rail. Fixed, not configurable.</summary>
    public const int ItemsPerRecommendationRail = 5;

    /// <summary>
    /// Resolves the configured rail cap, falling back to the default for a missing or nonsensical
    /// value and never exceeding <see cref="MaximumRails"/>.
    /// </summary>
    public static int RailCap(PluginConfiguration? configuration) =>
        Resolve(configuration?.MaxAndroidTvRails, DefaultRails, MaximumRails);

    /// <summary>
    /// Resolves the configured per-rail title cap, falling back to the default for a missing or
    /// nonsensical value and never exceeding <see cref="MaximumItemsPerRail"/>.
    /// </summary>
    public static int ItemsPerRailCap(PluginConfiguration? configuration) =>
        Resolve(configuration?.MaxAndroidTvItemsPerRail, DefaultItemsPerRail, MaximumItemsPerRail);

    /// <summary>
    /// Resolves the configured total title cap, falling back to the default for a missing or
    /// nonsensical value and never exceeding <see cref="MaximumTitles"/>.
    /// </summary>
    public static int TitleCap(PluginConfiguration? configuration) =>
        Resolve(configuration?.MaxAndroidTvTitles, DefaultTitles, MaximumTitles);

    private static int Resolve(int? configured, int fallback, int maximum) =>
        configured is null or < 1 ? fallback : Math.Min(configured.Value, maximum);
}
