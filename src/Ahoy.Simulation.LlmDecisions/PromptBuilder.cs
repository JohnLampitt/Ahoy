using Ahoy.Core.Enums;
using Ahoy.Simulation.Decisions;

namespace Ahoy.Simulation.LlmDecisions;

/// <summary>
/// Builds a compact structured prompt for Phi-3 Mini.
/// The prompt asks the model to return a JSON object with one decision
/// per InterventionType scenario.
/// </summary>
public static class PromptBuilder
{
    private static readonly InterventionType[] Scenarios =
    [
        InterventionType.None,
        InterventionType.BribeSmall,
        InterventionType.BribeLarge,
        InterventionType.IntelProvided,
        InterventionType.Threat,
        InterventionType.FavourInvoked,
    ];

    public static string Build(ActorDecisionContext ctx)
    {
        var traits = ctx.PersonalityTraits;
        var personality = traits is null ? "unknown" :
            $"greed={traits.Greed:+0.0;-0.0}, boldness={traits.Boldness:+0.0;-0.0}, " +
            $"loyalty={traits.Loyalty:+0.0;-0.0}, cunning={traits.Cunning:+0.0;-0.0}";

        var scenarioList = string.Join("\n", Scenarios.Select(s => $"  - {s}"));

        return $$"""
            You are a decision engine for a pirate simulation game.

            Actor: {{ctx.SituationSummary}}
            Personality: {{personality}}
            Player relationship: {{ctx.PlayerRelationship:+0;-0}} (-100 hostile … +100 loyal)
            Date: {{ctx.CurrentDate}}

            For each of the following player intervention scenarios, decide what this actor does.
            Return ONLY a JSON object with keys matching each scenario name and values being:
              { "action": "<ActionType>", "reasoning": "<one sentence>" }

            Scenarios:
            {{scenarioList}}

            Respond with valid JSON only. No prose outside the JSON object.
            """;
    }
}
