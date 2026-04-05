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

    /// <summary>
    /// Discrete war state. Prevents float-oscillation around the hostility threshold.
    /// Requires a formal Peace Treaty event to clear. (Crisis 6: Colonial War)
    /// </summary>
    public HashSet<FactionId> AtWarWith { get; } = new();

    /// <summary>Declare war on another faction. Sets both sides' AtWarWith and clamps relationship.</summary>
    public void DeclareWarOn(FactionId other, Faction otherFaction)
    {
        AtWarWith.Add(other);
        otherFaction.AtWarWith.Add(Id);
        Relationships[other] = Math.Min(Relationships.GetValueOrDefault(other), -80f);
        otherFaction.Relationships[Id] = Math.Min(otherFaction.Relationships.GetValueOrDefault(Id), -80f);
    }

    /// <summary>End war with another faction. Clears both sides' AtWarWith and resets relationship to -20.</summary>
    public void MakePeaceWith(FactionId other, Faction otherFaction)
    {
        AtWarWith.Remove(other);
        otherFaction.AtWarWith.Remove(Id);
        Relationships[other] = -20f;
        otherFaction.Relationships[Id] = -20f;
    }

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
public record DeclareWar(FactionId TargetFaction) : FactionGoal;    // Crisis 6: triggers AtWarWith
public record SeekPeace(FactionId TargetFaction) : FactionGoal;     // Crisis 6: ends war via treaty
