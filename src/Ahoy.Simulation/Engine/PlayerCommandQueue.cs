using System.Collections.Concurrent;

namespace Ahoy.Simulation.Engine;

public sealed class PlayerCommandQueue : IPlayerCommandQueue
{
    private readonly ConcurrentQueue<PlayerCommand> _queue = new();

    public void Enqueue(PlayerCommand command) => _queue.Enqueue(command);

    public IReadOnlyList<PlayerCommand> DrainPending()
    {
        var results = new List<PlayerCommand>();
        while (_queue.TryDequeue(out var cmd))
            results.Add(cmd);
        return results;
    }
}
