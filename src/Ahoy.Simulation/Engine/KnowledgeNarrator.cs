using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Translates the mechanical epistemic state of a <see cref="KnowledgeFact"/> into
/// qualitative language suitable for use in LLM prompts.
///
/// The LLM receives <see cref="EpistemicContext"/> alongside the raw fact data.
/// It should use <see cref="EpistemicContext.CertaintyInstruction"/> to calibrate
/// how confidently the NPC speaks, and the other fields to colour source attribution
/// and temporal framing.
/// </summary>
public static class KnowledgeNarrator
{
    /// <summary>
    /// Qualitative description of a fact's epistemic state, ready to embed in an LLM prompt.
    /// </summary>
    public sealed record EpistemicContext(
        /// <summary>Instruction to the LLM about how confidently to speak.</summary>
        string CertaintyInstruction,
        /// <summary>Describes how the holder came to know this. E.g. "You saw this yourself."</summary>
        string SourceDescription,
        /// <summary>Describes the age of the information relative to the current date.</summary>
        string AgeDescription,
        /// <summary>Null if not corroborated; otherwise a note about independent confirmation.</summary>
        string? CorroborationNote,
        /// <summary>True if a KnowledgeConflict is active for this fact's subject.</summary>
        bool IsContested)
    {
        /// <summary>
        /// A single-paragraph prompt fragment combining all context fields.
        /// Designed to be appended directly to an LLM system/user prompt.
        /// </summary>
        public string ToPromptFragment()
        {
            var lines = new List<string>
            {
                CertaintyInstruction,
                SourceDescription,
                AgeDescription,
            };
            if (CorroborationNote is not null) lines.Add(CorroborationNote);
            if (IsContested) lines.Add("You've heard conflicting reports about this. Express confusion or doubt about which account is true.");
            return string.Join(" ", lines);
        }
    }

    /// <summary>
    /// Produces an <see cref="EpistemicContext"/> for the given fact.
    /// </summary>
    /// <param name="fact">The fact to describe.</param>
    /// <param name="hasConflict">True if a <see cref="KnowledgeConflict"/> is active for this fact's subject.</param>
    /// <param name="currentDate">The current world date, used to compute information age.</param>
    public static EpistemicContext Describe(KnowledgeFact fact, bool hasConflict, WorldDate currentDate)
    {
        var certainty = CertaintyInstruction(fact.Confidence);
        var source    = SourceDescription(fact.HopCount);
        var age       = AgeDescription(fact.ObservedDate, currentDate);
        var corr      = CorroborationNote(fact.CorroborationCount);
        return new EpistemicContext(certainty, source, age, corr, hasConflict);
    }

    // ---- Certainty ----

    private static string CertaintyInstruction(float confidence) => confidence switch
    {
        >= 0.85f => "Speak with certainty — this is reliable intelligence.",
        >= 0.55f => "Speak with measured confidence, but allow for the possibility of error.",
        >= 0.30f => "Frame this as a persistent rumour; express clear hesitation.",
        _        => "Speak with strong uncertainty — this is barely a whisper, almost certainly unreliable.",
    };

    // ---- Source ----

    private static string SourceDescription(int hopCount) => hopCount switch
    {
        0 => "You witnessed this with your own eyes.",
        1 => "You heard this directly from someone who was there.",
        2 => "This came to you second-hand — someone told someone who told you.",
        _ => "This is third-hand information or worse; the chain of retelling is long.",
    };

    // ---- Age ----

    private static string AgeDescription(WorldDate observed, WorldDate current)
    {
        // Approximate days elapsed: year difference * 365 + day-of-year difference.
        var days = (current.Year - observed.Year) * 365
                 + (current.DayOfYear - observed.DayOfYear);
        if (days < 0) days = 0;
        return days switch
        {
            <= 1  => "This intelligence is fresh — no more than a day old.",
            <= 7  => $"You learned this {days} days ago — it may still be current.",
            <= 30 => $"This is {days}-day-old intelligence — treat it as somewhat stale.",
            _     => $"This is {days} days old — it may be badly out of date.",
        };
    }

    // ---- Corroboration ----

    private static string? CorroborationNote(int count) => count switch
    {
        0 => null,
        1 => "One other source has independently confirmed this.",
        _ => $"{count} independent sources have confirmed this — convergent intelligence.",
    };
}
