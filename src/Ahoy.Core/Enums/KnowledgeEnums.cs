namespace Ahoy.Core.Enums;

/// <summary>How sensitive a piece of knowledge is — affects propagation rate and broker pricing.</summary>
public enum KnowledgeSensitivity
{
    Public,         // Freely circulates — prices, weather, port names
    Restricted,     // Guarded — patrol routes, faction intentions
    Secret,         // Actively suppressed — military movements, treasure locations
    Disinformation, // False information deliberately seeded
}

/// <summary>Category of subject matter the fact describes.</summary>
public enum KnowledgeClaimType
{
    PortPrice,          // Price of a good at a specific port
    PortProsperity,     // General prosperity level of a port
    PortFactionControl, // Which faction currently controls a port
    ShipLocation,       // Last known position of a specific ship
    ShipCargo,          // Cargo manifest of a ship
    FactionStrength,    // Military/economic standing of a faction
    FactionIntention,   // What a faction is planning
    WeatherCondition,   // Weather in a region
    RouteHazard,        // Danger on a specific trade route
    IndividualWhereabouts, // Location of a named individual
    TreasureLocation,   // Location of buried / hidden valuables
    Custom,             // Free-form text fact
}
