namespace Ahoy.Core.Enums;

/// <summary>
/// Simulation Level-of-Detail — also expresses knowledge fidelity.
/// The LOD of the region in which an event occurs determines the
/// BaseConfidence of the resulting KnowledgeFacts.
/// </summary>
public enum SimulationLod
{
    /// <summary>Player's current region. Full simulation, full confidence.</summary>
    Local,

    /// <summary>Adjacent regions. Moderate simulation, reduced confidence.</summary>
    Regional,

    /// <summary>All other regions. Coarse simulation, low confidence.</summary>
    Distant,
}
