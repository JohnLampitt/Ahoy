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

    /// <summary>
    /// Number of consecutive ticks this ship has been docked at its current port.
    /// Reset to 0 by ShipMovementSystem on arrival. Incremented each tick while docked.
    /// Used to compute idle crew expenditure and knowledge-gated departure.
    /// </summary>
    public int TicksDockedAtCurrentPort { get; set; }

    /// <summary>If set, the ship is routing to this POI instead of a port.</summary>
    public OceanPoiId? PoiDestination { get; set; }

    /// <summary>
    /// Non-null when this ship is part of an active convoy.
    /// All ships sharing a ConvoyId cap their travel speed to the convoy minimum.
    /// Cleared when all convoy members dock at the destination.
    /// </summary>
    public Guid? ConvoyId { get; set; }

    /// <summary>
    /// Non-null when the ship flies false colours.
    /// OwnerFactionId is always the actual owning faction (ground truth).
    /// ClaimedOwnerFactionId is what flag the ship flies — what others see.
    /// </summary>
    public FactionId? ClaimedOwnerFactionId { get; set; }

    // --- Flags ---
    public bool IsPlayerShip { get; init; }
    public bool IsPirate { get; set; }
}
