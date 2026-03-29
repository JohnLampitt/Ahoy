namespace Ahoy.Core.ValueObjects;

/// <summary>
/// Personality dimensions used by the LLM prompt builder and rule-based fallback
/// to colour NPC decision-making. All values -1.0 to +1.0.
/// </summary>
public sealed record PersonalityTraits
{
    /// <summary>Positive = self-interested; Negative = principled.</summary>
    public float Greed { get; init; }

    /// <summary>Positive = reckless; Negative = cautious.</summary>
    public float Boldness { get; init; }

    /// <summary>Positive = keeps word; Negative = opportunistic.</summary>
    public float Loyalty { get; init; }

    /// <summary>Positive = cunning and deceptive; Negative = straightforward.</summary>
    public float Cunning { get; init; }

    /// <summary>Positive = craves status and recognition; Negative = prefers anonymity.</summary>
    public float Ambition { get; init; }

    public static PersonalityTraits Neutral => new();

    public static PersonalityTraits Random(System.Random rng) => new()
    {
        Greed   = (float)(rng.NextDouble() * 2 - 1),
        Boldness = (float)(rng.NextDouble() * 2 - 1),
        Loyalty = (float)(rng.NextDouble() * 2 - 1),
        Cunning = (float)(rng.NextDouble() * 2 - 1),
        Ambition = (float)(rng.NextDouble() * 2 - 1),
    };
}
