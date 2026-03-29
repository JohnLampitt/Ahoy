using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class Port
{
    public PortId Id { get; init; }
    public required string Name { get; init; }
    public RegionId RegionId { get; init; }
    public FactionId? ControllingFactionId { get; set; }

    /// <summary>Governor or harbour master (may be null for minor outposts).</summary>
    public IndividualId? GovernorId { get; set; }

    // --- Port state intermediaries (cascade targets for EventPropagation) ---

    /// <summary>
    /// Overall prosperity 0–100. Drives trade volume, price baselines and
    /// faction income. Key target for economic propagation rules.
    /// </summary>
    public float Prosperity { get; set; } = 50f;

    /// <summary>
    /// Institutional reputation — how the controlling faction views this port's
    /// compliance and stability. 0–100.
    /// </summary>
    public float FactionAuthority { get; set; } = 50f;

    // --- Economics ---
    public EconomicProfile Economy { get; } = new();

    // --- Ships currently docked ---
    public List<ShipId> DockedShips { get; } = new();

    // --- Reputation (dual-layer) ---
    /// <summary>Institution-level: how the port's faction regards the player (-100..+100).</summary>
    public float InstitutionalReputation { get; set; }

    /// <summary>Personal-level: how the current governor regards the player (-100..+100).</summary>
    public float PersonalReputation { get; set; }

    // --- Flags ---
    public bool IsPirateHaven { get; set; }
    public bool IsNeutral { get; set; }
}
