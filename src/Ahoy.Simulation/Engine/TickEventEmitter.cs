using Ahoy.Core.Enums;
using Ahoy.Simulation.Events;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Collects events emitted during a single tick.
///
/// Two buckets:
///   _all     — every event emitted this tick (never cleared mid-tick)
///   _pending — events not yet consumed by EventPropagationSystem
///
/// Access patterns:
///   DrainPending()  — EventPropagationSystem: take-and-clear the pending queue
///   PeekAll()       — KnowledgeSystem: read all events without clearing
///   DrainAll()      — SimulationEngine post-tick: take all events for frontend dispatch
/// </summary>
public sealed class TickEventEmitter : IEventEmitter
{
    private readonly List<WorldEvent> _all = new();
    private readonly List<WorldEvent> _pending = new();

    public void Emit(WorldEvent worldEvent, SimulationLod sourceLod)
    {
        // SourceLod is baked into the event at construction time by the caller.
        // We record it here for completeness; the event already carries it.
        _all.Add(worldEvent);
        _pending.Add(worldEvent);
    }

    /// <summary>
    /// Returns and clears pending events.
    /// Called by EventPropagationSystem during its tick phase.
    /// </summary>
    public IReadOnlyList<WorldEvent> DrainPending()
    {
        var snapshot = _pending.ToList();
        _pending.Clear();
        return snapshot;
    }

    /// <summary>
    /// Returns all events without clearing.
    /// Called by KnowledgeSystem to inspect the full tick's events.
    /// </summary>
    public IReadOnlyList<WorldEvent> PeekAll() => _all;

    /// <summary>
    /// Returns and clears all events.
    /// Called by SimulationEngine in the post-tick phase to dispatch to frontend.
    /// </summary>
    public IReadOnlyList<WorldEvent> DrainAll()
    {
        var snapshot = _all.ToList();
        _all.Clear();
        _pending.Clear();
        return snapshot;
    }
}
