using Ahoy.Core.Ids;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Decisions;

/// <summary>
/// Constructs a compact natural-language situation summary for use in LLM prompts
/// and in RuleBasedDecisionProvider context strings.
/// </summary>
public static class SituationSummaryBuilder
{
    public static string Build(WorldState state, IndividualId actorId, string triggerDescription)
    {
        if (!state.Individuals.TryGetValue(actorId, out var actor))
            return triggerDescription;

        var port = actor.LocationPortId.HasValue && state.Ports.TryGetValue(actor.LocationPortId.Value, out var p)
            ? p.Name : "unknown location";

        var faction = actor.FactionId.HasValue && state.Factions.TryGetValue(actor.FactionId.Value, out var f)
            ? f.Name : "no allegiance";

        return $"{actor.FullName}, {actor.Role} at {port} ({faction}). " +
               $"Trigger: {triggerDescription}. " +
               $"Date: {state.Date}.";
    }
}
