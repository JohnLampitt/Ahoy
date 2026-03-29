using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class PlayerState
{
    public required string CaptainName { get; set; }

    /// <summary>The player's flagship / primary vessel.</summary>
    public ShipId? FlagshipId { get; set; }

    /// <summary>All ships in the player's fleet.</summary>
    public List<ShipId> FleetIds { get; } = new();

    /// <summary>Player's current region (derived from flagship location).</summary>
    public RegionId? CurrentRegionId { get; set; }

    /// <summary>Gold held by the captain (separate from ship cargo gold).</summary>
    public int PersonalGold { get; set; }

    /// <summary>Notoriety 0–100. Affects pirate recognition and colonial hostility.</summary>
    public float Notoriety { get; set; }
}
