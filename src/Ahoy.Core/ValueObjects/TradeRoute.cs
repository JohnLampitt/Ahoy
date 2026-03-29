using Ahoy.Core.Ids;

namespace Ahoy.Core.ValueObjects;

/// <summary>A directed leg between two regions, used by merchant routing.</summary>
public readonly record struct TradeRoute(RegionId From, RegionId To);
