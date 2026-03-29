namespace Ahoy.Core.Enums;

/// <summary>
/// Named thresholds for faction-to-faction (or faction-to-player) relationship.
/// Underlying value is a float -100 to +100.
/// </summary>
public enum RelationshipStanding
{
    AtWar       = -4,   // < -75
    Hostile     = -3,   // -75 to -40
    Unfriendly  = -2,   // -40 to -10
    Neutral     = -1,   // -10 to +10
    Friendly    =  1,   // +10 to +40
    Allied      =  2,   // +40 to +75
    Loyal       =  3,   // > +75
}
