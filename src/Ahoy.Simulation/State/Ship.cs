using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

// ---- ShipRoute discriminated union ----

/// <summary>Routing intent — where a ship wants to go and why.</summary>
public abstract record ShipRoute;

/// <summary>Route to a specific port for trade, resupply, or docking.</summary>
public record PortRoute(PortId Destination) : ShipRoute;

/// <summary>Pursue a target ship — navigate to last known region, intercept on arrival.</summary>
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;

/// <summary>Route to an ocean point of interest (wreck, cache, rendezvous).</summary>
public record PoiRoute(OceanPoiId Poi) : ShipRoute;

// ---- Ship ----

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

    /// <summary>
    /// Discriminated routing intent. Replaces PortId? RoutingDestination + OceanPoiId? PoiDestination.
    /// Null = no active route. Set by ShipMovementSystem, player commands, and NPC goal pursuit.
    /// </summary>
    public ShipRoute? Route { get; set; }

    /// <summary>
    /// Number of consecutive ticks this ship has been docked at its current port.
    /// Reset to 0 by ShipMovementSystem on arrival. Incremented each tick while docked.
    /// Used to compute idle crew expenditure and knowledge-gated departure.
    /// </summary>
    public int TicksDockedAtCurrentPort { get; set; }

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

    /// <summary>
    /// Recent port visit history (last 5 ports). Used by merchant routing to penalise
    /// ping-pong patterns and encourage trade route diversity.
    /// </summary>
    public Queue<PortId> RecentPorts { get; } = new();

    public void RecordPortVisit(PortId portId)
    {
        RecentPorts.Enqueue(portId);
        while (RecentPorts.Count > 5)
            RecentPorts.Dequeue();
    }

    // --- Crisis state ---
    /// <summary>True when crew carries infectious disease. Spreads to ports on dock. (Crisis 2: Epidemic)</summary>
    public bool HasInfectedCrew { get; set; }

    /// <summary>
    /// Physical packet of secret documents carried by this ship. References a KnowledgeFact
    /// (typically a FactionIntentionClaim). Transfers on boarding/sinking. Kept in the
    /// knowledge economy, not the commodity loop. (Crisis 5: Intelligence Compromise)
    /// </summary>
    public KnowledgeFactId? CarriedIntelPackage { get; set; }
}
