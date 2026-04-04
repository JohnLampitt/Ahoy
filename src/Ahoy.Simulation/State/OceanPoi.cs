using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

public sealed class OceanPoi
{
    public OceanPoiId Id               { get; init; }
    public required string Name        { get; set; }
    public RegionId RegionId           { get; init; }
    public PoiType Type                { get; init; }
    public required string Description { get; set; }
    public bool IsDiscovered           { get; set; }
    public int LootGold                { get; set; }    // Shipwreck/PirateCache: ground truth pool; NOT exposed in claims
    public float HazardSeverity        { get; set; }    // ReefHazard/StormEye: 0..1

    /// <summary>
    /// Derives the epistemic cache status to embed in OceanPoiClaim.
    /// Buckets exact gold into coarse gossip-safe categories.
    /// </summary>
    public PoiCacheStatus DeriveStatus() => Type switch
    {
        PoiType.Shipwreck or PoiType.PirateCache =>
            LootGold <= 0    ? PoiCacheStatus.Looted
            : LootGold < 300 ? PoiCacheStatus.PartiallyLooted
            :                  PoiCacheStatus.RumouredRich,
        _ => PoiCacheStatus.Unknown,
    };
}
