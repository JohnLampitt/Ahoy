namespace Ahoy.Core.Enums;

public enum WindStrength
{
    Calm,
    Light,
    Moderate,
    Strong,
    Gale,
    Hurricane,
}

public enum WindDirection
{
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest,
}

public enum StormPresence
{
    None,
    Approaching,    // Storm enters region next tick
    Active,         // Full storm — hazard to navigation
    Dissipating,    // Clearing — reduced hazard
}

public enum Visibility
{
    Clear,
    Hazy,
    Poor,           // Fog / heavy rain
    None,           // Zero visibility — storm core
}
