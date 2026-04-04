namespace Ahoy.Core.Enums;

public enum PoiType { Shipwreck, ReefHazard, RendezvousPoint, PirateCache, StormEye }

/// <summary>
/// Epistemic status of a lootable POI as known to a specific holder.
/// Avoids leaking exact gold amounts into the gossip network — sailors talk
/// in broad terms, not accounting ledgers.
/// </summary>
public enum PoiCacheStatus { Unknown, RumouredRich, PartiallyLooted, Looted }
