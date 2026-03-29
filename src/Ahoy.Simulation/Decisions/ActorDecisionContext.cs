using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Decisions;

/// <summary>
/// The full context passed to both sync and async decision providers.
/// Contains everything needed to produce an ActorDecisionMatrix.
/// </summary>
public sealed class ActorDecisionContext
{
    public required IndividualId ActorId { get; init; }
    public required string SituationSummary { get; init; }
    public required WorldDate CurrentDate { get; init; }

    // Relevant world snapshot snippets
    public PortId? RelevantPortId { get; init; }
    public FactionId? RelevantFactionId { get; init; }
    public ShipId? RelevantShipId { get; init; }

    /// <summary>The actor's personality traits for prompt colouring.</summary>
    public Core.ValueObjects.PersonalityTraits? PersonalityTraits { get; init; }

    /// <summary>The actor's current relationship with the player (-100..+100).</summary>
    public float PlayerRelationship { get; init; }
}

/// <summary>
/// A pending or completed decision request tracked by the decision queue.
/// </summary>
public sealed class ActorDecisionRequest
{
    public required ActorDecisionContext Context { get; init; }

    /// <summary>
    /// The window (in ticks) during which the player can intervene.
    /// This is a GAME MECHANIC — not related to inference latency.
    /// </summary>
    public int InterventionWindowTicks { get; init; }

    public int TicksElapsed { get; set; }

    public bool InterventionWindowOpen => TicksElapsed < InterventionWindowTicks;

    /// <summary>Set by background inference when ready. Null until inference completes.</summary>
    public ActorDecisionMatrix? PendingMatrix { get; set; }

    /// <summary>Set if the player has intervened before the window closed.</summary>
    public PlayerIntervention? Intervention { get; set; }

    /// <summary>Resolved once window closes and matrix is available.</summary>
    public ActorDecision? ResolvedDecision { get; set; }
}
