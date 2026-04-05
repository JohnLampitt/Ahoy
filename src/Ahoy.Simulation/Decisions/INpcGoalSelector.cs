using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Decisions;

/// <summary>
/// Payload sent to the LLM for NPC goal selection.
/// Represents the NPC's epistemic reality — what they know, not ground truth.
/// </summary>
public sealed class NpcGoalSelectionContext
{
    /// <summary>NPC identity: role, personality traits.</summary>
    public required IndividualId NpcId { get; init; }
    public required string Role { get; init; }
    public required PersonalityTraits? Traits { get; init; }

    /// <summary>Top facts from the NPC's IndividualHolder, ordered by confidence.</summary>
    public required IReadOnlyList<KnowledgeFact> TopFacts { get; init; }

    /// <summary>Ship status: hull integrity, crew, cargo value, gold.</summary>
    public int? ShipCrew { get; init; }
    public float? ShipHullIntegrity { get; init; }
    public int? GoldAvailable { get; init; }

    /// <summary>Current location context.</summary>
    public PortId? CurrentPort { get; init; }
    public RegionId? CurrentRegion { get; init; }
}

/// <summary>
/// Structured goal decision returned by the LLM.
/// </summary>
public sealed class NpcGoalDecision
{
    /// <summary>The selected goal type (maps to NpcGoal subtypes).</summary>
    public required string GoalType { get; init; }

    /// <summary>Target subject key (e.g., "Ship:{guid}") — required for FulfillContract goals.</summary>
    public string? TargetSubject { get; init; }

    /// <summary>LLM's reasoning for this choice (feeds into knowledge system as narrative flavour).</summary>
    public string? Rationale { get; init; }
}

/// <summary>
/// Interface for LLM-driven NPC goal selection.
/// Called when an NPC enters the Pondering state.
/// The simulation never depends on this — RuleBasedDecisionProvider assigns
/// a valid goal immediately; the LLM can override on the next tick.
///
/// Implementation deferred until rule-based goal selection is proven.
/// </summary>
public interface INpcGoalSelector
{
    /// <summary>
    /// Request an async goal decision for the given NPC.
    /// Returns null if the LLM declines to override the rule-based default.
    /// </summary>
    Task<NpcGoalDecision?> SelectGoalAsync(NpcGoalSelectionContext context, CancellationToken ct = default);
}
