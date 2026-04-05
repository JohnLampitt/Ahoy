using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

[Flags]
public enum PortConditionFlags
{
    None        = 0,
    Famine      = 1 << 0,
    Plague      = 1 << 1,
    GoodHarvest = 1 << 2,
    Blockaded   = 1 << 3,
}

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
    /// Resident population. Drives consumption (TargetSupply), production capacity,
    /// and tax revenue. Floor: 100 (abandoned outpost). No ceiling — Malthusian Trap
    /// provides natural carrying capacity.
    /// </summary>
    public int Population { get; private set; } = 1000;

    public void AdjustPopulation(int delta)
        => Population = Math.Max(100, Population + delta);

    /// <summary>
    /// Overall prosperity 0–100. Drives production efficiency and trade volume.
    /// Separate from Population: a recently starved port can have low population
    /// but recovering prosperity as food arrives.
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
    public PortConditionFlags Conditions { get; private set; } = PortConditionFlags.None;

    /// <summary>Ticks remaining until epidemic clears naturally. Null = no active epidemic timer.</summary>
    public int? EpidemicTicksRemaining { get; private set; }

    /// <summary>Active crisis ID for famine relief. Prevents duplicate plea couriers.</summary>
    public Guid? ActiveCrisisId { get; set; }

    // ---- Condition mutation methods (enforce correlated invariants) ----

    /// <summary>Start an epidemic at this port. Sets Plague flag and decay timer together.</summary>
    public void StartEpidemic(int durationTicks = 30)
    {
        Conditions |= PortConditionFlags.Plague;
        EpidemicTicksRemaining = durationTicks;
    }

    /// <summary>Clear the epidemic. Removes Plague flag and timer together.</summary>
    public void ClearEpidemic()
    {
        Conditions &= ~PortConditionFlags.Plague;
        EpidemicTicksRemaining = null;
    }

    /// <summary>Decrement epidemic timer. Returns true if epidemic expired this tick.</summary>
    public bool TickEpidemic()
    {
        if (!EpidemicTicksRemaining.HasValue) return false;
        EpidemicTicksRemaining--;
        if (EpidemicTicksRemaining <= 0)
        {
            ClearEpidemic();
            return true;
        }
        return false;
    }

    /// <summary>Set or clear a non-epidemic condition flag.</summary>
    public void SetCondition(PortConditionFlags flag, bool active)
    {
        if (flag == PortConditionFlags.Plague)
            throw new InvalidOperationException("Use StartEpidemic()/ClearEpidemic() for Plague flag");
        if (active) Conditions |= flag;
        else Conditions &= ~flag;
    }
}
