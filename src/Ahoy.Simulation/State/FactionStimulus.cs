using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

/// <summary>
/// A deferred stimulus queued by EventPropagationSystem for FactionSystem
/// to process on the next tick. Keeps cross-tick faction effects clean
/// and prevents ordering issues within a single tick.
/// </summary>
public sealed class FactionStimulus
{
    public required FactionId FactionId { get; init; }
    public required string StimulusType { get; init; }
    public float Magnitude { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// True when the deception that triggered this stimulus was planted by the player
    /// (OriginatingAgentId is null on the disinformation fact).
    /// Used by FactionSystem to decide whether to target the player with retaliation.
    /// </summary>
    public bool PlayerIsOrigin { get; init; }

    // Optional context
    public RegionId? RegionId { get; init; }
    public PortId? PortId { get; init; }
}
