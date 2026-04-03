using Ahoy.Core.Enums;

namespace Ahoy.Simulation.Decisions;

/// <summary>
/// Synchronous fallback decision provider.
/// Uses rule-based heuristics driven by the actor's personality and situation.
/// Always returns a complete ActorDecisionMatrix with pre-computed intervention branches.
/// </summary>
public sealed class RuleBasedDecisionProvider : ISyncActorDecisionProvider
{
    /// <summary>
    /// Effective affinity blends the actor's personal relationship with the port's
    /// structural reputation. Port rep (populace view) has 60% weight; personal
    /// relationship has 40% weight. This creates emergent tension — a new hostile
    /// governor assigned to a port that loves the player will still be somewhat
    /// constrained by the port's positive culture.
    /// </summary>
    private static float EffectiveAffinity(ActorDecisionContext ctx)
        => (ctx.PortPersonalReputation * 0.6f) + (ctx.PlayerRelationship * 0.4f);

    public ActorDecisionMatrix ResolveMatrix(ActorDecisionContext context)
    {
        var traits = context.PersonalityTraits;
        var relationship = context.PlayerRelationship;

        return new ActorDecisionMatrix
        {
            BaseDecision = BuildBaseDecision(context),
            ConditionalDecisions = new()
            {
                [InterventionType.BribeSmall]     = BuildBribeResponse(context, isLarge: false),
                [InterventionType.BribeLarge]     = BuildBribeResponse(context, isLarge: true),
                [InterventionType.IntelProvided]  = BuildIntelResponse(context),
                [InterventionType.Threat]         = BuildThreatResponse(context),
                [InterventionType.FavourInvoked]  = BuildFavourResponse(context),
            }
        };
    }

    private static ActorDecision BuildBaseDecision(ActorDecisionContext ctx)
    {
        // Default: actor proceeds with their current agenda unchanged
        return new ActorDecision
        {
            ActionType = "Proceed",
            Reasoning = "No intervention — actor follows their own interests.",
        };
    }

    private static ActorDecision BuildBribeResponse(ActorDecisionContext ctx, bool isLarge)
    {
        var greed = ctx.PersonalityTraits?.Greed ?? 0f;
        var loyalty = ctx.PersonalityTraits?.Loyalty ?? 0f;

        // Greedy actors accept more readily; loyal actors resist corruption
        var acceptThreshold = isLarge ? -0.5f : 0.2f;
        var adjustedGreed = greed - loyalty * 0.5f;

        if (adjustedGreed >= acceptThreshold)
        {
            return new ActorDecision
            {
                ActionType = "AcceptBribe",
                Reasoning = isLarge ? "A substantial offer — hard to refuse." : "A small sweetener helps.",
                GoldAmount = isLarge ? 500 : 100,
            };
        }

        return new ActorDecision
        {
            ActionType = "RefuseBribe",
            Reasoning = "Principle outweighs personal gain.",
        };
    }

    private static ActorDecision BuildIntelResponse(ActorDecisionContext ctx)
    {
        var cunning = ctx.PersonalityTraits?.Cunning ?? 0f;

        // Cunning actors are suspicious of intel; credulous actors act on it
        if (cunning > 0.5f)
        {
            return new ActorDecision
            {
                ActionType = "WeighIntel",
                Reasoning = "Interesting — but I'll verify before acting.",
            };
        }

        return new ActorDecision
        {
            ActionType = "ActOnIntel",
            Reasoning = "This changes things considerably.",
        };
    }

    private static ActorDecision BuildThreatResponse(ActorDecisionContext ctx)
    {
        var boldness = ctx.PersonalityTraits?.Boldness ?? 0f;

        if (boldness > 0.3f)
        {
            return new ActorDecision
            {
                ActionType = "ResistThreat",
                Reasoning = "I won't be intimidated.",
            };
        }

        return new ActorDecision
        {
            ActionType = "YieldToThreat",
            Reasoning = "Discretion is the better part of valour.",
        };
    }

    private static ActorDecision BuildFavourResponse(ActorDecisionContext ctx)
    {
        var loyalty = ctx.PersonalityTraits?.Loyalty ?? 0f;

        if (loyalty > 0f && EffectiveAffinity(ctx) > 10f)
        {
            return new ActorDecision
            {
                ActionType = "HonourFavour",
                Reasoning = "A debt is a debt.",
            };
        }

        return new ActorDecision
        {
            ActionType = "DenyFavour",
            Reasoning = "Times change — I owe you nothing.",
        };
    }
}
