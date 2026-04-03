using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class Faction
{
    public FactionId Id { get; init; }
    public required string Name { get; init; }
    public FactionType Type { get; init; }

    // --- Treasury ---
    public int TreasuryGold { get; set; }
    public int IncomePerTick { get; set; }
    public int ExpenditurePerTick { get; set; }

    // --- Naval ---
    /// <summary>Total warships available (not on specific missions).</summary>
    public int NavalStrength { get; set; }

    /// <summary>Desired fraction of income spent on navy (0..1). Converges 20%/tick.</summary>
    public float DesiredNavalAllocationFraction { get; set; } = 0.25f;

    /// <summary>Current actual fraction spent on naval this tick.</summary>
    public float CurrentNavalAllocationFraction { get; set; } = 0.25f;

    /// <summary>Patrol allocation by region — number of patrol-ships assigned.</summary>
    public Dictionary<RegionId, int> PatrolAllocations { get; } = new();

    // --- Relationships with other factions (-100..+100) ---
    public Dictionary<FactionId, float> Relationships { get; } = new();

    // --- Ports and territories ---
    public List<PortId> ControlledPorts { get; } = new();
    public List<RegionId> ClaimedRegions { get; } = new();

    // --- Active goals ---
    public List<FactionGoal> ActiveGoals { get; } = new();

    // --- Colonial-specific ---
    /// <summary>Mother country name (Spain / England / France / Netherlands).</summary>
    public string? MotherCountry { get; init; }

    // --- Pirate brotherhood-specific ---
    /// <summary>Cohesion 0–100. Below 20 risks fracture.</summary>
    public float Cohesion { get; set; } = 70f;

    /// <summary>Raiding momentum 0–100. Drives income and recruitment.</summary>
    public float RaidingMomentum { get; set; } = 50f;

    /// <summary>Haven presence by region 0–100.</summary>
    public Dictionary<RegionId, float> HavenPresence { get; } = new();

    // --- Intelligence ---
    /// <summary>
    /// Intelligence capability 0..1. Clamped [0.10, 0.95].
    /// Effects: (1) disinformation BaseConfidence = 0.70 + cap*0.25;
    /// (2) gates DeceptionExposed detection (roll > cap to detect);
    /// (3) reduces remote investigation yield against this faction.
    /// Mutated by FactionSystem: +0.005/tick on EspionageGoal; -0.05 on DeceptionExposed.
    /// </summary>
    public float IntelligenceCapability { get; set; } = 0.30f;
}

// ---- Goal hierarchy ----

public abstract record FactionGoal
{
    public float Utility { get; set; }
    public int TicksActive { get; set; }
}

public record ExpandTerritory(RegionId TargetRegion) : FactionGoal;
public record ConsolidateTerritory(RegionId TargetRegion) : FactionGoal;
public record SuppressPiracy(RegionId TargetRegion) : FactionGoal;
public record BuildNavy : FactionGoal;
public record AccumulateTreasury : FactionGoal;
public record NegotiateTreaty(FactionId TargetFaction) : FactionGoal;
public record RaidShippingLane(RegionId TargetRegion) : FactionGoal;   // Pirate-specific
public record EstablishHaven(PortId TargetPort) : FactionGoal;          // Pirate-specific
public record EspionageGoal : FactionGoal;              // drives intelligence operations
