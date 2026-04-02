using Ahoy.Core.Enums;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.WorldData;

/// <summary>
/// Hardcoded quest templates for the Caribbean world.
/// These are the first two quests from the LLM design session (April 2026):
///   A1 — Bait and Bloodhound (disinformation / ship location)
///   A3 — The Governor's Quill (political branching / individual status)
/// </summary>
public static class CaribbeanQuestTemplates
{
    public static IReadOnlyList<QuestTemplate> All { get; } =
    [
        BaitAndBloodhound(),
        GovernorsQuill(),
    ];

    // ------------------------------------------------------------------ //
    //  A1: Bait and Bloodhound                                            //
    // ------------------------------------------------------------------ //

    private static QuestTemplate BaitAndBloodhound() => new()
    {
        Id       = new QuestTemplateId("A1_BaitAndBloodhound"),
        Title    = "Bait and Bloodhound",
        Synopsis = "A Spanish galleon has been sighted limping alone — but is the intelligence reliable, or bait?",
        NpcName  = "The Harbour Rat",
        DefaultNpcDialogue =
            "Word on the docks is a fat galleon rides low in the water not far hence. " +
            "Whether the tip is good... that's the question, Captain.",

        TriggerCondition = new FactCondition(
            f => f.Claim is ShipLocationClaim && f.Confidence >= 0.60f,
            "ShipLocationClaim with confidence >= 0.60"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is ShipLocationClaim && f.Confidence >= 0.70f)
            .OrderByDescending(f => f.Confidence)
            .Take(1)
            .ToList(),

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "intercept",
                Label       = "Sail to intercept",
                Description = "Plot a course for the galleon's last known position.",
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Regional,
                        state.Ports.Values.First().Id,
                        "A privateer was spotted hunting Spanish colours in open water."),
                ],
            },
            new QuestBranch
            {
                BranchId    = "sell_intel",
                Label       = "Sell the intelligence",
                Description = "Pass the tip to an English factor for a finder's fee.",
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "ignore",
                Label       = "Leave it be",
                Description = "The risk isn't worth the reward.",
                OutcomeEvents = _ => [],
            },
        ],

        // Expires 15 ticks after activation
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(15)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  A3: The Governor's Quill                                           //
    // ------------------------------------------------------------------ //

    private static QuestTemplate GovernorsQuill() => new()
    {
        Id       = new QuestTemplateId("A3_GovernorsQuill"),
        Title    = "The Governor's Quill",
        Synopsis = "Rumour places a Governor somewhere unexpected. The uncertainty is itself an opportunity.",
        NpcName  = null,
        DefaultNpcDialogue =
            "They say the Governor's not been seen at his usual table. Something's afoot.",

        // Triggers on any low-confidence individual whereabouts claim (rumour-grade)
        TriggerCondition = new FactCondition(
            f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.40f,
            "IndividualWhereaboutsClaim with confidence < 0.40 (rumour-grade)"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.40f)
            .Take(1)
            .ToList(),

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "deliver_to_french",
                Label       = "Deliver evidence to the French authorities",
                Description = "Turn the letters over to the loyalist French military.",
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Regional,
                        state.Ports.Values.First(p =>
                            state.Factions.TryGetValue(p.ControllingFactionId ?? default, out var f)
                            && f.Name.Contains("France")).Id,
                        "Governor Dubois has been arrested for treason. Martinique is under martial law."),
                ],
            },
            new QuestBranch
            {
                BranchId    = "sell_to_english",
                Label       = "Sell the letters to the English",
                Description = "Deliver the correspondence to Jamaica to help the English finalize the defection.",
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Distant,
                        state.Ports.Values.First(p =>
                            state.Factions.TryGetValue(p.ControllingFactionId ?? default, out var f)
                            && f.Name.Contains("England")).Id,
                        "England is reportedly preparing an invasion fleet for a French island."),
                ],
            },
            new QuestBranch
            {
                BranchId    = "blackmail",
                Label       = "Return the letters to the Governor — for a price",
                Description = "Gold smooths all feathers.",
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Regional,
                        state.Ports.Values.First().Id,
                        "Governor Dubois is fiercely loyal to France — the rumours of his disloyalty are nonsense."),
                ],
            },
        ],

        // Expires 30 ticks after activation
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(30)) > 0,
    };
}
