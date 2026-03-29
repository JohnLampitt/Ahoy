using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class Region
{
    public RegionId Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Adjacent regions — defines the movement graph.</summary>
    public List<RegionId> AdjacentRegions { get; } = new();

    /// <summary>Ports located in this region.</summary>
    public List<PortId> Ports { get; } = new();

    /// <summary>
    /// Aggregate health of trade flowing through this region (0–100).
    /// Feeds faction income calculations at Regional/Distant LOD.
    /// </summary>
    public float TradeHealth { get; set; } = 50f;

    /// <summary>Dominant controlling faction (null = contested / neutral).</summary>
    public FactionId? DominantFactionId { get; set; }

    /// <summary>Base transit time in days between adjacent regions from this one.</summary>
    public Dictionary<RegionId, float> BaseTravelDays { get; } = new();
}
