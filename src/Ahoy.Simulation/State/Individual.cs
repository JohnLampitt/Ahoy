using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;

namespace Ahoy.Simulation.State;

public sealed class Individual
{
    public IndividualId Id { get; init; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string FullName => $"{FirstName} {LastName}";

    public IndividualRole Role { get; set; }
    public FactionId? FactionId { get; set; }

    /// <summary>Current port they're based at (null = at sea / unknown).</summary>
    public PortId? LocationPortId { get; set; }

    /// <summary>
    /// Authority 0–100. Governors with high authority enforce laws effectively.
    /// Authority is a cascade target in EventPropagation (PoliticalRules).
    /// </summary>
    public float Authority { get; set; } = 50f;

    public PersonalityTraits Personality { get; init; } = PersonalityTraits.Neutral;
    public List<CareerEntry> CareerHistory { get; } = new();

    // --- Relationship with player (-100..+100) ---
    public float PlayerRelationship { get; set; }

    // --- Flags ---
    public bool IsAlive { get; set; } = true;
    public bool IsPlayerKnown { get; set; }
}
