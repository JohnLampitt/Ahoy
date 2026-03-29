using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// Base class for all event propagation rules.
/// Rules inspect a WorldEvent and may produce secondary effects (state mutations + new events).
/// Max propagation depth is enforced by EventPropagationSystem.
/// </summary>
public abstract class PropagationRule
{
    /// <summary>Unique name for loop-guard deduplication.</summary>
    public abstract string RuleId { get; }

    /// <summary>Returns true if this rule applies to the given event.</summary>
    public abstract bool Matches(WorldEvent worldEvent);

    /// <summary>
    /// Applies secondary effects. May emit new events (which will be queued for the next
    /// propagation pass if depth allows). Must not re-emit the same event type on the same
    /// entity — the loop guard enforces this.
    /// </summary>
    public abstract void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events);
}
