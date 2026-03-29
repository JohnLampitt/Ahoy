using System.Text.Json;
using Ahoy.Core.Enums;
using Ahoy.Simulation.Decisions;

namespace Ahoy.Simulation.LlmDecisions;

/// <summary>
/// Parses the LLM's JSON response into an ActorDecisionMatrix.
/// Falls back gracefully on malformed output.
/// </summary>
public static class ResponseParser
{
    public static ActorDecisionMatrix Parse(string jsonResponse, ISyncActorDecisionProvider fallback,
        ActorDecisionContext ctx)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            var conditionals = new Dictionary<InterventionType, ActorDecision>();
            ActorDecision? baseDecision = null;

            foreach (var scenario in Enum.GetValues<InterventionType>())
            {
                var key = scenario.ToString();
                if (!root.TryGetProperty(key, out var elem)) continue;

                var action    = elem.TryGetProperty("action", out var a) ? a.GetString() ?? "Proceed" : "Proceed";
                var reasoning = elem.TryGetProperty("reasoning", out var r) ? r.GetString() : null;

                var decision = new ActorDecision { ActionType = action, Reasoning = reasoning };

                if (scenario == InterventionType.None)
                    baseDecision = decision;
                else
                    conditionals[scenario] = decision;
            }

            return new ActorDecisionMatrix
            {
                BaseDecision = baseDecision ?? new ActorDecision { ActionType = "Proceed" },
                ConditionalDecisions = conditionals,
            };
        }
        catch
        {
            // Malformed response — fall back to rule-based
            return fallback.ResolveMatrix(ctx);
        }
    }
}
