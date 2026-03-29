using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.Decisions;

/// <summary>The concrete action an actor will take.</summary>
public sealed class ActorDecision
{
    public required string ActionType { get; init; }
    public string? Reasoning { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();

    // Optional typed conveniences
    public FactionId? TargetFactionId { get; init; }
    public PortId? TargetPortId { get; init; }
    public ShipId? TargetShipId { get; init; }
    public IndividualId? TargetIndividualId { get; init; }
    public int? GoldAmount { get; init; }
}

/// <summary>
/// A matrix of pre-computed decisions covering the base case and all
/// anticipated player intervention types. Returned by a single LLM inference.
/// </summary>
public sealed class ActorDecisionMatrix
{
    public required ActorDecision BaseDecision { get; init; }
    public Dictionary<InterventionType, ActorDecision> ConditionalDecisions { get; init; } = new();

    /// <summary>
    /// Resolves the appropriate decision given the player's intervention (or lack thereof).
    /// For Novel interventions, returns BaseDecision — re-inference is triggered separately.
    /// </summary>
    public ActorDecision Resolve(PlayerIntervention? intervention)
    {
        if (intervention is null) return BaseDecision;
        if (ConditionalDecisions.TryGetValue(intervention.Type, out var conditional))
            return conditional;
        return BaseDecision;
    }
}

/// <summary>The player's attempt to influence a pending decision.</summary>
public sealed class PlayerIntervention
{
    public required InterventionType Type { get; init; }
    public int GoldOffered { get; init; }
    public string? IntelSummary { get; init; }
    public string? FavourDescription { get; init; }
    public string? NovelDescription { get; init; }
}
