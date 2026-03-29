using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Immutable snapshot of relevant world state delivered to the frontend after each tick.
/// Contains only what the player can observe (filtered by knowledge confidence).
/// </summary>
public sealed class WorldSnapshot
{
    public WorldDate Date { get; init; }
    public int TickNumber { get; init; }

    // Player
    public string CaptainName { get; init; } = string.Empty;
    public RegionId? PlayerRegionId { get; init; }
    public IReadOnlyList<ShipId> FleetIds { get; init; } = [];
    public int PersonalGold { get; init; }
    public float Notoriety { get; init; }

    // Observable ports (player has some knowledge of these)
    public IReadOnlyList<PortSnapshot> ObservablePorts { get; init; } = [];

    // Observable ships (player has some knowledge of these)
    public IReadOnlyList<ShipSnapshot> ObservableShips { get; init; } = [];

    // Events that occurred this tick (filtered to player-relevant)
    public IReadOnlyList<WorldEvent> TickEvents { get; init; } = [];
}

public sealed class PortSnapshot
{
    public PortId Id { get; init; }
    public required string Name { get; init; }
    public RegionId RegionId { get; init; }
    public float Prosperity { get; init; }
    public float InstitutionalReputation { get; init; }
    public float PersonalReputation { get; init; }
    public IReadOnlyDictionary<Core.Enums.TradeGood, int> KnownPrices { get; init; }
        = new Dictionary<Core.Enums.TradeGood, int>();
}

public sealed class ShipSnapshot
{
    public ShipId Id { get; init; }
    public required string Name { get; init; }
    public ShipLocation Location { get; init; } = null!;
    public float ConfidenceInLocation { get; init; }
    public bool IsPlayerShip { get; init; }
}
