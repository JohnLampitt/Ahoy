using Ahoy.Simulation.State;

namespace Ahoy.WorldData;

/// <summary>
/// Instantiates a WorldState from a IWorldDefinition.
/// Keeps construction logic separate from data definitions.
/// </summary>
public static class WorldFactory
{
    public static WorldState Create(IWorldDefinition definition)
    {
        var state = new WorldState();
        definition.Populate(state);
        return state;
    }
}

/// <summary>
/// Contract for hand-crafted world definitions.
/// Implementations populate a blank WorldState with regions, ports, factions, etc.
/// </summary>
public interface IWorldDefinition
{
    void Populate(WorldState state);
}
