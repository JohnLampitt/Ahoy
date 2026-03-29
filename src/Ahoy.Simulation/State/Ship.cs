using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class Ship
{
    public ShipId Id { get; init; }
    public required string Name { get; set; }
    public ShipClass Class { get; init; }
    public FactionId? OwnerFactionId { get; set; }
    public IndividualId? CaptainId { get; set; }

    // --- Location ---
    public ShipLocation Location { get; set; } = null!;

    /// <summary>
    /// Transient flag: set to true by ShipMovementSystem when the ship arrives at a port
    /// this tick. Cleared in the post-tick phase. Used by EconomySystem and KnowledgeSystem
    /// to process arrival gossip within the same tick.
    /// </summary>
    public bool ArrivedThisTick { get; set; }

    // --- Hull & cargo ---
    public int MaxCargoTons { get; init; }
    public int MaxCrew { get; init; }
    public int Guns { get; init; }
    public Dictionary<TradeGood, int> Cargo { get; } = new();
    public int CurrentCrew { get; set; }
    public float HullIntegrity { get; set; } = 1.0f;   // 0..1

    // --- Economics ---
    public int GoldOnBoard { get; set; }

    // --- Routing state ---
    /// <summary>Accumulated fractional day progress on current route leg.</summary>
    public float RouteProgressAccumulator { get; set; }

    /// <summary>If set, the ship has a destination it is routing toward.</summary>
    public PortId? RoutingDestination { get; set; }

    // --- Flags ---
    public bool IsPlayerShip { get; init; }
    public bool IsPirate { get; set; }
}
