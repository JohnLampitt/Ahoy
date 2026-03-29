using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// All simulation systems implement this interface.
/// Systems are called in a fixed order each tick by SimulationEngine.
/// Each system receives WorldState (mutable), SimulationContext (read-only),
/// and IEventEmitter for broadcasting events.
/// </summary>
public interface IWorldSystem
{
    void Tick(WorldState state, SimulationContext context, IEventEmitter events);
}
