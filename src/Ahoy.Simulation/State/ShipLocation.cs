using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

/// <summary>Discriminated union describing where a ship currently is.</summary>
public abstract record ShipLocation;

/// <summary>Ship is docked at a port.</summary>
public record AtPort(PortId Port) : ShipLocation;

/// <summary>Ship is sailing within a region but not on a direct route.</summary>
public record AtSea(RegionId Region) : ShipLocation;

/// <summary>Ship is in transit between two adjacent regions.</summary>
public record EnRoute(RegionId From, RegionId To, float ProgressDays, float TotalDays) : ShipLocation
{
    /// <summary>Fractional progress 0..1.</summary>
    public float Progress => TotalDays > 0 ? ProgressDays / TotalDays : 1f;
}
