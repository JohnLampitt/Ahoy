namespace Ahoy.Simulation.Engine;

/// <summary>
/// Thread-safe queue for player commands flowing from the frontend into the simulation.
/// The simulation drains this at the start of each tick (pre-tick phase).
/// </summary>
public interface IPlayerCommandQueue
{
    void Enqueue(PlayerCommand command);
    IReadOnlyList<PlayerCommand> DrainPending();
}
