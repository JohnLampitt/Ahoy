using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

// ---- The Behavioural Objective (immutable) ----

/// <summary>
/// An NPC's strategic goal — what they are trying to achieve.
/// Separated from GoalPursuit (the execution state machine).
/// </summary>
public abstract record NpcGoal(Guid Id, IndividualId NpcId);

/// <summary>
/// Pursue a contract: hunt a target ship, assassinate an individual, or deliver goods.
/// </summary>
public record FulfillContractGoal(
    Guid Id, IndividualId NpcId,
    ContractClaim Contract) : NpcGoal(Id, NpcId);

/// <summary>Demand ransom for a captured VIP. Route to victim's faction port, demand gold. (Crisis 1)</summary>
public record RansomGoal(
    Guid Id, IndividualId NpcId,
    IndividualId CaptiveId,
    FactionId TargetFactionId,
    int DemandGold) : NpcGoal(Id, NpcId);

/// <summary>Extort a target using held secret knowledge. Route to target's port, demand payment. (Crisis 5, 6D)</summary>
public record ExtortGoal(
    Guid Id, IndividualId NpcId,
    IndividualId TargetId,
    KnowledgeFactId LeverageFactId) : NpcGoal(Id, NpcId);

/// <summary>Patrol a region — loiter and intercept enemy ships. (Crisis 3, 6)</summary>
public record PatrolRegionGoal(
    Guid Id, IndividualId NpcId,
    RegionId Region) : NpcGoal(Id, NpcId);

/// <summary>
/// Execute a faction mission order. Captain validates route against their knowledge
/// and can refuse (mutiny) if relationship with viceroy drops below -75.
/// </summary>
public record ExecuteOrdersGoal(
    Guid Id, IndividualId NpcId,
    FactionMission Mission) : NpcGoal(Id, NpcId);

/// <summary>Recapture a defected port. Assigned to NavalOfficer. (Group 9 Phase 5)</summary>
public record RecapturePortGoal(
    Guid Id, IndividualId NpcId,
    PortId TargetPort) : NpcGoal(Id, NpcId);

// Future goal types:
// public record TradeGoal(Guid Id, IndividualId NpcId, PortId TargetPort) : NpcGoal(Id, NpcId);
// public record InvestigateGoal(Guid Id, IndividualId NpcId, string SubjectKey) : NpcGoal(Id, NpcId);
// public record FleeGoal(Guid Id, IndividualId NpcId, RegionId DangerRegion) : NpcGoal(Id, NpcId);

// ---- The Execution State Machine ----

public enum PursuitState
{
    /// <summary>NPC needs a goal — triggers rule-based assignment + optional async LLM override.</summary>
    Pondering,
    /// <summary>Actively pursuing the goal — EpistemicResolver determines next action each tick.</summary>
    Active,
    /// <summary>Cannot make progress — lacking knowledge, gold, or capability. Increments TicksStalled.</summary>
    Stalled,
    /// <summary>Goal achieved — NPC transitions to Pondering for next goal.</summary>
    Completed,
    /// <summary>Goal abandoned after stalling too long (>14 ticks). Triggers Stall & Leak.</summary>
    Abandoned,
}

/// <summary>
/// Tracks the execution state of an NPC's current goal.
/// Lives on WorldState.NpcPursuits for cross-system visibility.
/// </summary>
public sealed class GoalPursuit
{
    private PursuitState _state = PursuitState.Active;

    public required NpcGoal ActiveGoal { get; init; }
    public PursuitState State { get => _state; init => _state = value; }

    /// <summary>Number of consecutive ticks the NPC has been unable to make progress.</summary>
    public int TicksStalled { get; private set; }

    public required int ActivatedOnTick { get; init; }

    /// <summary>Transition to Stalled state. Resets stall counter.</summary>
    public void Stall() { _state = PursuitState.Stalled; TicksStalled = 1; }

    /// <summary>Increment stall counter. Returns true if threshold exceeded.</summary>
    public bool TickStall(int abandonThreshold = 14)
    {
        TicksStalled++;
        if (TicksStalled > abandonThreshold)
        {
            _state = PursuitState.Abandoned;
            return true;
        }
        return false;
    }

    public void Complete() => _state = PursuitState.Completed;
    public void Abandon() => _state = PursuitState.Abandoned;
    public void Activate() => _state = PursuitState.Active;
}
