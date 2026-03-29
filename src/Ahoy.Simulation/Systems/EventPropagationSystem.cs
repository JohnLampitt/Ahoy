using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Systems.Propagation;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 5 — runs after FactionSystem.
/// Iteratively drains the pending event queue and applies matching PropagationRules
/// to generate secondary effects. Maximum propagation depth = 3. Loop guards
/// prevent the same (RuleId, EventType, EntityId) combination from firing twice
/// within a single tick.
/// </summary>
public sealed class EventPropagationSystem : IWorldSystem
{
    private const int MaxDepth = 3;

    private readonly IReadOnlyList<PropagationRule> _rules;
    private readonly TickEventEmitter _emitter;

    public EventPropagationSystem(TickEventEmitter emitter, IReadOnlyList<PropagationRule>? rules = null)
    {
        _emitter = emitter;
        _rules = rules ?? BuildDefaultRules();
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
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
            }
        }
    }

    private static IReadOnlyList<PropagationRule> BuildDefaultRules() =>
    [
        // Economic
        new StormTradeDisruptionRule(),
        new ProsperityFactionIncomeRule(),
        new RaidingProsperityRule(),
        // Political
        new PortCaptureRelationshipRule(),
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
