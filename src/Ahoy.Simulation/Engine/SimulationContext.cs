using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Immutable per-tick context. Computed once by SimulationEngine before systems run.
/// Provides LOD resolution for all systems without mutating WorldState.
/// </summary>
public sealed class SimulationContext
{
    private readonly Dictionary<RegionId, SimulationLod> _lodMap;

    public RegionId? PlayerRegion { get; }
    public int TickNumber { get; }

    public SimulationContext(WorldState state, int tickNumber)
    {
        TickNumber = tickNumber;
        PlayerRegion = state.Player.CurrentRegionId;
        _lodMap = ComputeLodMap(state, PlayerRegion);
    }

    /// <summary>Returns the LOD for a given region this tick.</summary>
    public SimulationLod GetLod(RegionId regionId)
        => _lodMap.TryGetValue(regionId, out var lod) ? lod : SimulationLod.Distant;

    /// <summary>
    /// BFS over the region adjacency graph from the player's region.
    /// Depth 0 = Local, Depth 1 = Regional, Depth 2+ = Distant.
    /// </summary>
    private static Dictionary<RegionId, SimulationLod> ComputeLodMap(
        WorldState state, RegionId? playerRegion)
    {
        var map = new Dictionary<RegionId, SimulationLod>();
        if (playerRegion is null)
        {
            foreach (var id in state.Regions.Keys)
                map[id] = SimulationLod.Distant;
            return map;
        }

        var queue = new Queue<(RegionId Id, int Depth)>();
        queue.Enqueue((playerRegion.Value, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (map.ContainsKey(current)) continue;

            map[current] = depth switch
            {
                0 => SimulationLod.Local,
                1 => SimulationLod.Regional,
                _ => SimulationLod.Distant,
            };

            if (depth < 2 && state.Regions.TryGetValue(current, out var region))
                foreach (var adjacent in region.AdjacentRegions)
                    if (!map.ContainsKey(adjacent))
                        queue.Enqueue((adjacent, depth + 1));
        }

        // Ensure all regions have an entry
        foreach (var id in state.Regions.Keys)
            map.TryAdd(id, SimulationLod.Distant);

        return map;
    }

    /// <summary>Convenience: is the player currently in this region?</summary>
    public bool IsLocalRegion(RegionId id) => GetLod(id) == SimulationLod.Local;
}
