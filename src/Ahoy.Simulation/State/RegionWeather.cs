using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class RegionWeather
{
    public RegionId RegionId { get; init; }
    public WindStrength WindStrength { get; set; } = WindStrength.Moderate;
    public WindDirection WindDirection { get; set; } = WindDirection.East;
    public StormPresence StormPresence { get; set; } = StormPresence.None;
    public Visibility Visibility { get; set; } = Visibility.Clear;

    /// <summary>Ticks remaining in an active or dissipating storm.</summary>
    public int StormDaysRemaining { get; set; }

    /// <summary>Probability of storm spawning next tick during hurricane season (0..1).</summary>
    public float StormSpawnChance { get; set; }
}

/// <summary>
/// Seasonal wind/storm profiles used by WeatherSystem.
/// Three seasons: dry, wet-shoulder, and hurricane.
/// </summary>
public static class SeasonalProfiles
{
    public static SeasonalProfile Dry => new(
        BaseStormChance: 0.01f,
        HurricaneChance: 0.00f,
        DominantWind: WindDirection.East,
        TypicalStrength: WindStrength.Moderate);

    public static SeasonalProfile WetShoulder => new(
        BaseStormChance: 0.05f,
        HurricaneChance: 0.02f,
        DominantWind: WindDirection.SouthEast,
        TypicalStrength: WindStrength.Strong);

    public static SeasonalProfile HurricaneSeason => new(
        BaseStormChance: 0.10f,
        HurricaneChance: 0.08f,
        DominantWind: WindDirection.East,
        TypicalStrength: WindStrength.Strong);

    public static SeasonalProfile ForDate(int month) => month switch
    {
        >= 1 and <= 4 => Dry,
        5 or 12 => WetShoulder,
        >= 6 and <= 11 => HurricaneSeason,
        _ => Dry,
    };
}

public sealed record SeasonalProfile(
    float BaseStormChance,
    float HurricaneChance,
    WindDirection DominantWind,
    WindStrength TypicalStrength);
