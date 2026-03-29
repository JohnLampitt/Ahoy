using Ahoy.Core.Enums;
using Ahoy.Simulation.Events;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Abstraction used by IWorldSystem implementations to emit events.
/// The SourceLod parameter determines the BaseConfidence of any KnowledgeFacts
/// the KnowledgeSystem derives from the event.
/// </summary>
public interface IEventEmitter
{
    void Emit(WorldEvent worldEvent, SimulationLod sourceLod);
}
