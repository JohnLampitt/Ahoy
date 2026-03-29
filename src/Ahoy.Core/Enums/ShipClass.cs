namespace Ahoy.Core.Enums;

/// <summary>
/// Broad classification of vessel size and role.
/// Larger class = more cargo / more guns / slower.
/// </summary>
public enum ShipClass
{
    Sloop,          // Fast, small — favoured by pirates and scouts
    Brigantine,     // Medium speed, moderate capacity
    Brig,           // Workhorse merchantman / light warship
    Frigate,        // Fast warship, limited cargo
    GalleonSmall,   // Heavy merchant, modest armament
    GalleonLarge,   // Flagship class — slow but powerful
    ManOfWar,       // Pure warship — colonial navies only
}
