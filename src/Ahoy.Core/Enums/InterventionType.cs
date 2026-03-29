namespace Ahoy.Core.Enums;

/// <summary>
/// The type of player intervention applied to a pending ActorDecisionRequest.
/// These map directly to the conditional branches pre-computed in an ActorDecisionMatrix.
/// </summary>
public enum InterventionType
{
    None,
    BribeSmall,
    BribeLarge,
    IntelProvided,
    Threat,
    FavourInvoked,

    /// <summary>
    /// An intervention that was not anticipated in the original matrix.
    /// Triggers a re-inference rather than a matrix lookup.
    /// </summary>
    Novel,
}
