using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Systems.Propagation;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 6 — runs after FactionSystem.
/// Iteratively drains the pending event queue and applies matching PropagationRules
/// to generate secondary effects. Maximum propagation depth = 3. Loop guards
/// prevent the same (RuleId, EventType, EntityId) combination from firing twice
/// within a single tick.
/// Also resolves completed PendingInvestigation requests each tick.
/// </summary>
public sealed class EventPropagationSystem : IWorldSystem
{
    private const int MaxDepth = 3;

    private readonly IReadOnlyList<PropagationRule> _rules;
    private readonly TickEventEmitter _emitter;
    private readonly Random _rng;

    public EventPropagationSystem(TickEventEmitter emitter, IReadOnlyList<PropagationRule>? rules = null,
        Random? rng = null)
    {
        _emitter = emitter;
        _rules = rules ?? BuildDefaultRules();
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Resolve completed remote investigations before processing propagation
        ResolveRemoteInvestigations(state, context);

        var loopGuard = new HashSet<string>();

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var pending = _emitter.DrainPending();
            if (pending.Count == 0) break;

            foreach (var worldEvent in pending)
            {
                foreach (var rule in _rules)
                {
                    if (!rule.Matches(worldEvent)) continue;

                    var guardKey = $"{rule.RuleId}:{worldEvent.GetType().Name}:{depth}";
                    if (!loopGuard.Add(guardKey)) continue;

                    rule.Apply(worldEvent, state, context, events);
                }

                // Queue faction stimulus when deception is exposed
                if (worldEvent is DeceptionExposed deception)
                {
                    state.PendingFactionStimuli.Enqueue(new FactionStimulus
                    {
                        FactionId = deception.DeceivingFactionId,
                        StimulusType = "DeceptionExposed",
                        Magnitude = 1f,
                        Description = "Player exposed faction deception — intelligence capability degraded",
                    });
                }
            }
        }
    }

    private void ResolveRemoteInvestigations(WorldState state, SimulationContext context)
    {
        var toResolve = state.PendingInvestigations
            .Where(p => p.ResolvesOnTick <= context.TickNumber)
            .ToList();

        foreach (var pending in toResolve)
        {
            state.PendingInvestigations.Remove(pending);

            var targetFaction = FindTargetFactionForSubject(pending.SubjectKey, state);
            var factionCapability = targetFaction.HasValue
                && state.Factions.TryGetValue(targetFaction.Value, out var tf)
                    ? tf.IntelligenceCapability
                    : 0f;

            var succeeded = _rng.NextDouble() > factionCapability;

            if (succeeded)
            {
                // Find the best authoritative fact from faction/port holders
                KnowledgeFact? bestFact = null;
                float bestConfidence = 0f;

                foreach (var factionId in state.Factions.Keys)
                {
                    var fact = state.Knowledge.GetFacts(new FactionHolder(factionId))
                        .FirstOrDefault(f => !f.IsSuperseded
                            && KnowledgeFact.GetSubjectKey(f.Claim) == pending.SubjectKey);
                    if (fact != null && fact.Confidence > bestConfidence)
                    {
                        bestFact = fact;
                        bestConfidence = fact.Confidence;
                    }
                }

                foreach (var portId in state.Ports.Keys)
                {
                    var fact = state.Knowledge.GetFacts(new PortHolder(portId))
                        .FirstOrDefault(f => !f.IsSuperseded
                            && KnowledgeFact.GetSubjectKey(f.Claim) == pending.SubjectKey);
                    if (fact != null && fact.Confidence > bestConfidence)
                    {
                        bestFact = fact;
                        bestConfidence = fact.Confidence;
                    }
                }

                if (bestFact != null)
                {
                    var resultConfidence = bestFact.Confidence * 0.60f;
                    var resultFact = new KnowledgeFact
                    {
                        Claim = bestFact.Claim,
                        Sensitivity = bestFact.Sensitivity,
                        Confidence = resultConfidence,
                        BaseConfidence = resultConfidence,
                        ObservedDate = state.Date,
                        IsDisinformation = bestFact.IsDisinformation,
                        HopCount = bestFact.HopCount + 2,
                        SourceHolder = null,
                        OriginatingAgentId = bestFact.OriginatingAgentId,
                    };
                    state.Knowledge.MarkSuperseded(new PlayerHolder(), resultFact, context.TickNumber);
                    state.Knowledge.AddFact(new PlayerHolder(), resultFact);

                    _emitter.Emit(new InvestigationResolved(state.Date, SimulationLod.Local,
                        pending.SubjectKey, resultFact.Id, true), SimulationLod.Local);
                }
                else
                {
                    // Nothing found — treat as failure
                    _emitter.Emit(new InvestigationResolved(state.Date, SimulationLod.Local,
                        pending.SubjectKey, null, false), SimulationLod.Local);
                }
            }
            else
            {
                // Counterintelligence blocked the spy
                _emitter.Emit(new InvestigationResolved(state.Date, SimulationLod.Local,
                    pending.SubjectKey, null, false), SimulationLod.Local);
            }
        }
    }

    private static FactionId? FindTargetFactionForSubject(string subjectKey, WorldState state)
    {
        // Scan all facts to find one matching this subject, then infer its faction
        foreach (var factionId in state.Factions.Keys)
        {
            var fact = state.Knowledge.GetFacts(new FactionHolder(factionId))
                .FirstOrDefault(f => !f.IsSuperseded
                    && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
            if (fact != null)
            {
                // Direct faction claim
                if (fact.Claim is FactionStrengthClaim or FactionIntentionClaim)
                    return factionId;
            }
        }

        // Check ship/individual ownership via port holders
        foreach (var portId in state.Ports.Keys)
        {
            var fact = state.Knowledge.GetFacts(new PortHolder(portId))
                .FirstOrDefault(f => !f.IsSuperseded
                    && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
            if (fact == null) continue;

            if (fact.Claim is ShipLocationClaim slc
                && state.Ships.TryGetValue(slc.Ship, out var ship))
                return ship.OwnerFactionId;

            if (fact.Claim is IndividualWhereaboutsClaim iwc
                && state.Individuals.TryGetValue(iwc.Individual, out var ind))
                return ind.FactionId;
        }

        return null;
    }

    private static IReadOnlyList<PropagationRule> BuildDefaultRules() =>
    [
        // Economic
        new StormTradeDisruptionRule(),
        new ProsperityFactionIncomeRule(),
        new RaidingProsperityRule(),
        // Political
        new PortCaptureRelationshipRule(),
        new PortDefectionRule(),
        new GovernorAuthorityRule(),
        // Military
        new PatrolSuppressRaidingRule(),
        new ShipDestroyedNavalLossRule(),
        // Weather
        new StormRecoveryRule(),
        // Social
        new GovernorDeathReplacementRule(),
    ];
}
