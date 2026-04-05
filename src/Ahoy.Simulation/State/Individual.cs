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

    /// <summary>The port this individual calls home and returns to after tours. Null for nomadic individuals (pirates, brokers).</summary>
    public PortId? HomePortId { get; set; }

    /// <summary>Non-null while on an inspection/diplomatic tour. Counts down each tick; at 0 the individual returns home.</summary>
    public int? TourTicksRemaining { get; set; }

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
    public bool IsCompromised { get; set; }

    // --- Hidden loyalty ---
    /// <summary>
    /// The faction this individual publicly claims to serve.
    /// Null = no deception; their FactionId is their public identity.
    /// Non-null + different from FactionId = infiltrator.
    /// FactionId is always the ground truth (real allegiance).
    /// ClaimedFactionId is the cover story visible to others.
    /// </summary>
    public FactionId? ClaimedFactionId { get; set; }

    /// <summary>True when ClaimedFactionId is set and differs from FactionId.</summary>
    public bool IsInfiltrator => ClaimedFactionId.HasValue && ClaimedFactionId != FactionId;

    // --- Wealth ---
    public int CurrentGold { get; set; }

    // --- Captivity (Crisis 1: VIP Abduction) ---
    /// <summary>Non-null when this individual is held captive by another.</summary>
    public IndividualId? CaptorId { get; set; }
}
