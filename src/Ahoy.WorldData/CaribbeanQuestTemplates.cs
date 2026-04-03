using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.WorldData;

/// <summary>
/// Quest templates for the Caribbean world — all 12 from the LLM design session (April 2026).
///   A1 — Bait and Bloodhound         (ship location / disinformation)
///   A2 — A Squeeze on Sweetness      (price anomaly / trade economics)
///   A3 — The Governor's Quill        (individual whereabouts / political)
///   A4 — The Rot in the Timbers      (faction strength / timed window)
///   A5 — Eye of the Tempest          (weather / navigation hazard)
///   A6 — The Bloodless Coup          (port control / faction shift)
///   B1 — The Governor's Shadow       (port control + individual, AND condition)
///   B2 — The Sugar Windfall          (price anomaly / 3-branch variant)
///   B3 — The Vanishing Frigate       (ship location + faction strength, AND)
///   B4 — A Cure in Doubt             (individual status / uncertainty)
///   B5 — The Silent Convoy           (low-confidence ship location / disinformation)
///   B6 — The Map That Wasn't         (individual status OR weather, OR condition)
/// </summary>
public static class CaribbeanQuestTemplates
{
    public static IReadOnlyList<QuestTemplate> All { get; } =
    [
        BaitAndBloodhound(),
        SqueezeSweetness(),
        GovernorsQuill(),
        RotInTimbers(),
        EyeOfTempest(),
        BloodlessCoup(),
        GovernorsShadow(),
        SugarWindfall(),
        VanishingFrigate(),
        CureInDoubt(),
        SilentConvoy(),
        MapThatWasnt(),
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
            .Where(f => f.Claim is ShipLocationClaim && f.Confidence >= 0.60f)
            .OrderByDescending(f => f.Confidence)
            .Take(1)
            .ToList(),

        TitleFactory = facts =>
        {
            var shipId = (facts.FirstOrDefault(f => f.Claim is ShipLocationClaim)?.Claim
                as ShipLocationClaim)?.Ship;
            return shipId.HasValue
                ? $"Bait and Bloodhound — {shipId.Value}"
                : "Bait and Bloodhound";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "intercept",
                Label       = "Sail to intercept",
                Description = "Plot a course for the galleon's last known position.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                ],
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
                OutcomeActions = [ new SupersedeTriggerFacts() ],
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

        TitleFactory = facts =>
        {
            var indiv = (facts.FirstOrDefault(f => f.Claim is IndividualWhereaboutsClaim)?.Claim
                as IndividualWhereaboutsClaim)?.Individual;
            return indiv.HasValue
                ? $"The Governor's Quill — {indiv.Value}"
                : "The Governor's Quill";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "deliver_to_french",
                Label       = "Deliver evidence to the French authorities",
                Description = "Turn the letters over to the loyalist French military.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                ],
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Regional,
                        FrenchPort(state) ?? state.Ports.Values.First().Id,
                        "A local Governor has been arrested for treason and the port is under martial law."),
                ],
            },
            new QuestBranch
            {
                BranchId    = "sell_to_english",
                Label       = "Sell the letters to the English",
                Description = "Deliver the correspondence to an English port for a handsome sum.",
                // Only show if player has English-controlled port knowledge
                AvailabilityCondition = facts =>
                    facts.Any(f => f.Claim is PortControlClaim pc
                        && pc.FactionId.HasValue),
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                ],
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Distant,
                        EnglishPort(state) ?? state.Ports.Values.First().Id,
                        "England is reportedly preparing an invasion fleet for a nearby French island."),
                ],
            },
            new QuestBranch
            {
                BranchId    = "blackmail",
                Label       = "Return the letters to the Governor — for a price",
                Description = "Gold smooths all feathers.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                ],
                OutcomeEvents = state =>
                [
                    new RumourSpread(state.Date, SimulationLod.Regional,
                        state.Ports.Values.First().Id,
                        "The Governor's loyalty to his Crown is beyond question — scurrilous rumours have been put to rest."),
                ],
            },
        ],

        // Expires 30 ticks after activation
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(30)) > 0,
    };

    // ---- Port helpers ----

    private static PortId? FrenchPort(WorldState state) =>
        state.Ports.Values.FirstOrDefault(p =>
            p.ControllingFactionId.HasValue &&
            state.Factions.TryGetValue(p.ControllingFactionId.Value, out var f) &&
            f.Name.Contains("France"))?.Id;

    private static PortId? EnglishPort(WorldState state) =>
        state.Ports.Values.FirstOrDefault(p =>
            p.ControllingFactionId.HasValue &&
            state.Factions.TryGetValue(p.ControllingFactionId.Value, out var f) &&
            f.Name.Contains("England"))?.Id;

    private static PortId? PiratePort(WorldState state) =>
        state.Ports.Values.FirstOrDefault(p => p.IsPirateHaven)?.Id;

    private static PortId? NearestPort(WorldState state) =>
        state.Ports.Values.FirstOrDefault()?.Id;

    // ------------------------------------------------------------------ //
    //  A2: A Squeeze on Sweetness                                         //
    // ------------------------------------------------------------------ //

    private static QuestTemplate SqueezeSweetness() => new()
    {
        Id       = new QuestTemplateId("A2_SqueezeSweetness"),
        Title    = "A Squeeze on Sweetness",
        Synopsis = "Sugar prices have gone strange — someone is manipulating the market.",

        TriggerCondition = new FactCondition(
            f => f.Claim is PortPriceClaim pc
                 && pc.Good == TradeGood.Sugar
                 && pc.Price > 200,
            "PortPriceClaim for Sugar with price > 200"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is PortPriceClaim pc
                        && pc.Good == TradeGood.Sugar
                        && pc.Price > 200)
            .OrderByDescending(f => f.Confidence)
            .Take(2)
            .ToList(),

        TitleFactory = facts =>
        {
            var port = (facts.FirstOrDefault(f => f.Claim is PortPriceClaim pc
                             && pc.Good == TradeGood.Sugar)?.Claim
                        as PortPriceClaim)?.Port;
            return port.HasValue
                ? $"A Squeeze on Sweetness — near {port.Value}"
                : "A Squeeze on Sweetness";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "smuggle",
                Label       = "Exploit the price gap",
                Description = "Buy cheap, sell dear — arbitrage the anomaly before it closes.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "Sugar prices are starting to normalise — the shortage is easing.",
                        s => EnglishPort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "ignore",
                Label       = "Leave it alone",
                Description = "Not your fight — let the merchants sort it out.",
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(20)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  A4: The Rot in the Timbers                                         //
    // ------------------------------------------------------------------ //

    private static QuestTemplate RotInTimbers() => new()
    {
        Id       = new QuestTemplateId("A4_RotInTimbers"),
        Title    = "The Rot in the Timbers",
        Synopsis = "A faction's military strength has collapsed — a window has opened, but it won't stay open.",

        TriggerCondition = new FactCondition(
            f => f.Claim is FactionStrengthClaim fc
                 && fc.NavalStrength <= 4
                 && f.Confidence >= 0.60f,
            "FactionStrengthClaim with NavalStrength <= 4 and confidence >= 0.60"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is FactionStrengthClaim fc
                        && fc.NavalStrength <= 4
                        && f.Confidence >= 0.60f)
            .OrderBy(f => (f.Claim as FactionStrengthClaim)!.NavalStrength)
            .Take(1)
            .ToList(),

        TitleFactory = facts =>
        {
            var factionId = (facts.FirstOrDefault(f => f.Claim is FactionStrengthClaim)?.Claim
                as FactionStrengthClaim)?.Faction;
            return factionId.HasValue
                ? $"The Rot in the Timbers — {factionId.Value}"
                : "The Rot in the Timbers";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "raid",
                Label       = "Lead a raid while defences are down",
                Description = "Strike before reinforcements arrive. The window may already be closing.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "Pirates struck a weakened colonial outpost — the garrison was overwhelmed.",
                        s => PiratePort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "extort",
                Label       = "Extort the weakened faction",
                Description = "Offer 'protection' to the struggling faction in exchange for gold and favours.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "ignore",
                Label       = "Not worth the risk",
                Description = "Reinforcements could arrive any day.",
                OutcomeEvents = _ => [],
            },
        ],

        // Short window — confidence in the weakness decays fast
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(12)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  A5: Eye of the Tempest                                             //
    // ------------------------------------------------------------------ //

    private static QuestTemplate EyeOfTempest() => new()
    {
        Id       = new QuestTemplateId("A5_EyeOfTempest"),
        Title    = "Eye of the Tempest",
        Synopsis = "A storm's path creates a brief, violent opportunity.",

        TriggerCondition = new FactCondition(
            f => f.Claim is WeatherClaim wc
                 && wc.Storm == Core.Enums.StormPresence.Active
                 && f.Confidence >= 0.50f,
            "WeatherClaim with active storm and confidence >= 0.50"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is WeatherClaim wc
                        && wc.Storm == Core.Enums.StormPresence.Active
                        && f.Confidence >= 0.50f)
            .OrderByDescending(f => f.Confidence)
            .Take(1)
            .ToList(),

        TitleFactory = facts =>
        {
            var region = (facts.FirstOrDefault(f => f.Claim is WeatherClaim)?.Claim
                as WeatherClaim)?.Region;
            return region.HasValue
                ? $"Eye of the Tempest — {region.Value}"
                : "Eye of the Tempest";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "brave_storm",
                Label       = "Sail through the storm",
                Description = "Risk the ship to reach what the storm uncovers.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "A captain sailed into the great storm and came back richer. Or so the story goes.",
                        s => PiratePort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sell_path",
                Label       = "Sell the storm data to a merchant fleet",
                Description = "The Dutch will pay well for accurate weather routing.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
        ],

        // Storm window closes in 3 ticks
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(3)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  A6: The Bloodless Coup                                             //
    // ------------------------------------------------------------------ //

    private static QuestTemplate BloodlessCoup() => new()
    {
        Id       = new QuestTemplateId("A6_BloodlessCoup"),
        Title    = "The Bloodless Coup",
        Synopsis = "A port's allegiance is wavering — whoever acts first shapes who controls it next.",

        TriggerCondition = new FactCondition(
            f => f.Claim is PortControlClaim
                 && f.Confidence < 0.45f
                 && f.Confidence > 0.10f,
            "PortControlClaim with low confidence (contested)"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is PortControlClaim
                        && f.Confidence < 0.45f
                        && f.Confidence > 0.10f)
            .OrderBy(f => f.Confidence)
            .Take(1)
            .ToList(),

        TitleFactory = facts =>
        {
            var port = (facts.FirstOrDefault(f => f.Claim is PortControlClaim)?.Claim
                as PortControlClaim)?.Port;
            return port.HasValue
                ? $"The Bloodless Coup — {port.Value}"
                : "The Bloodless Coup";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "back_pirates",
                Label       = "Back the pirates",
                Description = "Help the Brethren reassert control before the merchants consolidate.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "The pirates have retaken the port. The merchant cartel has been driven out.",
                        s => PiratePort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "back_merchants",
                Label       = "Back the merchants",
                Description = "Help the merchant cartel formalise the takeover.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "The port has declared itself independent of pirate influence. Trade is open to all.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(25)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B1: The Governor's Shadow                                          //
    // ------------------------------------------------------------------ //

    private static QuestTemplate GovernorsShadow() => new()
    {
        Id       = new QuestTemplateId("B1_GovernorsShadow"),
        Title    = "The Governor's Shadow",
        Synopsis = "A port is firmly held — but its Governor is rumoured to be in secret talks with rivals.",

        // Both conditions must be true simultaneously: port control known (high conf)
        // AND individual whereabouts uncertain (low conf) — implies something is off
        TriggerCondition = new AndCondition(
        [
            new FactCondition(
                f => f.Claim is PortControlClaim && f.Confidence >= 0.65f,
                "PortControlClaim with confidence >= 0.65"),
            new FactCondition(
                f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.40f,
                "IndividualWhereaboutsClaim with confidence < 0.40"),
        ]),

        TriggerFactSelector = facts =>
        [
            .. facts.Where(f => f.Claim is PortControlClaim && f.Confidence >= 0.65f).Take(1),
            .. facts.Where(f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.40f).Take(1),
        ],

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "expose",
                Label       = "Expose the talks to the controlling faction",
                Description = "Bring proof to the faction authorities and let them handle it.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "A Governor has been exposed as a traitor. The port is under military lockdown.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "assist_rival",
                Label       = "Assist the rival faction",
                Description = "Help the other side secure the defection.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "A colonial port has changed hands through quiet diplomacy.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "blackmail",
                Label       = "Blackmail the Governor",
                Description = "Use the knowledge for personal gain.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(30)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B2: The Sugar Windfall                                             //
    // ------------------------------------------------------------------ //

    private static QuestTemplate SugarWindfall() => new()
    {
        Id       = new QuestTemplateId("B2_SugarWindfall"),
        Title    = "The Sugar Windfall",
        Synopsis = "A sudden sugar shortage has driven prices sky-high — but the cause may already be resolving.",

        TriggerCondition = new FactCondition(
            f => f.Claim is PortPriceClaim pc
                 && pc.Good == TradeGood.Sugar
                 && pc.Price > 220
                 && f.Confidence >= 0.55f,
            "PortPriceClaim Sugar > 220 with confidence >= 0.55"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is PortPriceClaim pc
                        && pc.Good == TradeGood.Sugar
                        && pc.Price > 220)
            .OrderByDescending(f => f.Confidence)
            .Take(1)
            .ToList(),

        TitleFactory = facts =>
        {
            var price = (facts.FirstOrDefault(f => f.Claim is PortPriceClaim pc
                              && pc.Good == TradeGood.Sugar)?.Claim as PortPriceClaim)?.Price;
            return price.HasValue
                ? $"The Sugar Windfall — {price.Value} gold/unit"
                : "The Sugar Windfall";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "exploit",
                Label       = "Rush a cargo there before prices fall",
                Description = "Buy sugar elsewhere and race to the hungry port.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "investigate",
                Label       = "Investigate the cause",
                Description = "Find out why prices spiked — there may be a bigger story here.",
                OutcomeActions =
                [
                    new EmitRumourAction(
                        "The sugar shortage stems from storm damage to the main supply convoy.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sabotage",
                Label       = "Sabotage the incoming supply convoy",
                Description = "Attack the relief convoy to keep prices high a little longer.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "The relief convoy has gone missing. The sugar shortage is worsening.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
        ],

        // Price anomalies close fast
        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(10)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B3: The Vanishing Frigate                                          //
    // ------------------------------------------------------------------ //

    private static QuestTemplate VanishingFrigate() => new()
    {
        Id       = new QuestTemplateId("B3_VanishingFrigate"),
        Title    = "The Vanishing Frigate",
        Synopsis = "A naval vessel has vanished and its faction is weaker for it — the situation is unstable.",

        TriggerCondition = new AndCondition(
        [
            new FactCondition(
                f => f.Claim is ShipLocationClaim
                     && f.Confidence is >= 0.30f and < 0.65f,
                "ShipLocationClaim with medium uncertainty (0.30–0.65)"),
            new FactCondition(
                f => f.Claim is FactionStrengthClaim fc && fc.NavalStrength <= 8,
                "FactionStrengthClaim showing reduced naval strength"),
        ]),

        TriggerFactSelector = facts =>
        [
            .. facts.Where(f => f.Claim is ShipLocationClaim
                                && f.Confidence is >= 0.30f and < 0.65f).Take(1),
            .. facts.Where(f => f.Claim is FactionStrengthClaim fc
                                && fc.NavalStrength <= 8).Take(1),
        ],

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "track_rescue",
                Label       = "Track the ship and rescue survivors",
                Description = "Find what happened — survivors will owe you a debt.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "Survivors of the lost frigate were pulled from the water — the ship was sunk in a storm.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "hunt_prize",
                Label       = "Hunt the ship as a prize",
                Description = "If it is still afloat, a crippled warship is worth a fortune.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sell_false_intel",
                Label       = "Sell false intelligence about its fate",
                Description = "Spread a convincing lie and watch the factions scramble.",
                OutcomeActions =
                [
                    new EmitRumourAction(
                        "The missing warship has reportedly joined the pirate brethren.",
                        s => PiratePort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(20)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B4: A Cure in Doubt                                                //
    // ------------------------------------------------------------------ //

    private static QuestTemplate CureInDoubt() => new()
    {
        Id       = new QuestTemplateId("B4_CureInDoubt"),
        Title    = "A Cure in Doubt",
        Synopsis = "A physician known for a rare cure has vanished — or at least no one is certain where they are.",
        NpcName  = "The Worried Merchant",
        DefaultNpcDialogue =
            "The healer, Doctor Esteves — I had word he was at Havana, but now they say he has gone. " +
            "My whole family is sick, Captain. Would you find him for me?",

        TriggerCondition = new FactCondition(
            f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.35f,
            "IndividualWhereaboutsClaim with confidence < 0.35 (uncertainty)"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.35f)
            .OrderBy(f => f.Confidence)
            .Take(1)
            .ToList(),

        AllowDuplicateInstances = false,

        TitleFactory = facts =>
        {
            var f = facts.FirstOrDefault(f => f.Claim is IndividualWhereaboutsClaim);
            return f?.Claim is IndividualWhereaboutsClaim c
                ? $"A Cure in Doubt — {c.Individual.Value}"
                : "A Cure in Doubt";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "search_last_known",
                Label       = "Search the physician's last known location",
                Description = "Sail to where the rumour placed him and investigate in person.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "The wandering physician has been found — and his cures are genuine.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "spread_false_cure",
                Label       = "Spread false news of a cure",
                Description = "Sell hope to the desperate — and line your pockets in the process.",
                AvailabilityCondition = facts =>
                    facts.Any(f => f.Claim is IndividualWhereaboutsClaim w && w.Port == null),
                OutcomeActions =
                [
                    new EmitRumourAction(
                        "A miraculous cure has been found — seek the physician at Port Royal.",
                        s => EnglishPort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "ignore",
                Label       = "Ignore the rumour entirely",
                Description = "The intelligence is too thin to act on.",
                OutcomeActions = [],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(25)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B5: The Silent Convoy                                              //
    // ------------------------------------------------------------------ //

    private static QuestTemplate SilentConvoy() => new()
    {
        Id       = new QuestTemplateId("B5_SilentConvoy"),
        Title    = "The Silent Convoy",
        Synopsis = "A low-confidence rumour places a treasure convoy nearby — but the intelligence may be planted.",
        NpcName  = "The Shadowy Broker",
        DefaultNpcDialogue =
            "I have word — barely a whisper — of a convoy moving without escort. " +
            "Whether it is true or a lure... well, that is what they pay you to find out, Captain.",

        TriggerCondition = new FactCondition(
            f => f.Claim is ShipLocationClaim && f.Confidence < 0.55f,
            "ShipLocationClaim with confidence < 0.55 (low-confidence rumour)"
        ),

        TriggerFactSelector = facts => facts
            .Where(f => f.Claim is ShipLocationClaim && f.Confidence < 0.55f)
            .OrderBy(f => f.Confidence)
            .Take(1)
            .ToList(),

        AllowDuplicateInstances = true,

        TitleFactory = facts =>
        {
            var f = facts.FirstOrDefault(f => f.Claim is ShipLocationClaim);
            return f?.Claim is ShipLocationClaim c
                ? $"The Silent Convoy — {c.Ship.Value}"
                : "The Silent Convoy";
        },

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "investigate_first",
                Label       = "Investigate before committing",
                Description = "Spend a day gathering intelligence before sailing into a possible trap.",
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new AddKnowledgeFact(
                        new CustomClaim("SilentConvoy", "Investigation revealed the convoy intelligence was genuine."),
                        0.80f,
                        KnowledgeSensitivity.Public,
                        [new PlayerHolder()]),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sail_direct",
                Label       = "Sail immediately on the rumour",
                Description = "Risk the trap — the prize may be real.",
                OutcomeActions = [ new SupersedeTriggerFacts() ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sell_intel",
                Label       = "Sell the rumour to a rival captain",
                Description = "Let someone else take the risk. You take the coin.",
                OutcomeActions =
                [
                    new EmitRumourAction(
                        "A rival captain bought a tip about a convoy — and sailed north in haste.",
                        s => PiratePort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(15)) > 0,
    };

    // ------------------------------------------------------------------ //
    //  B6: The Map That Wasn't                                            //
    // ------------------------------------------------------------------ //

    private static QuestTemplate MapThatWasnt() => new()
    {
        Id       = new QuestTemplateId("B6_MapThatWasnt"),
        Title    = "The Map That Wasn't",
        Synopsis = "Either a reclusive cartographer has gone missing, or a violent storm has swallowed the route they charted — the truth is uncertain.",
        NpcName  = "The Cartographer's Apprentice",
        DefaultNpcDialogue =
            "My master kept charts of a passage no one else knows. " +
            "Now he is gone — or perhaps the storm took the route itself. " +
            "I cannot tell you which disaster befell us, Captain.",

        TriggerCondition = new OrCondition(
        [
            new FactCondition(
                f => f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.50f,
                "IndividualWhereaboutsClaim with confidence < 0.50"
            ),
            new FactCondition(
                f => f.Claim is WeatherClaim wc && wc.Storm == StormPresence.Active,
                "WeatherClaim with active storm"
            ),
        ]),

        TriggerFactSelector = facts => facts
            .Where(f =>
                (f.Claim is IndividualWhereaboutsClaim && f.Confidence < 0.50f) ||
                (f.Claim is WeatherClaim wc && wc.Storm == StormPresence.Active))
            .Take(2)
            .ToList(),

        AllowDuplicateInstances = false,

        Branches =
        [
            new QuestBranch
            {
                BranchId    = "find_cartographer",
                Label       = "Search for the missing cartographer",
                Description = "The man himself is worth more than any chart — if he lives.",
                AvailabilityCondition = facts =>
                    facts.Any(f => f.Claim is IndividualWhereaboutsClaim),
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new EmitRumourAction(
                        "The lost cartographer was found alive — his charts are being copied at Havana.",
                        s => NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "chart_the_storm_route",
                Label       = "Chart the storm-wracked passage yourself",
                Description = "Sail the route while the storm rages — claim the discovery.",
                AvailabilityCondition = facts =>
                    facts.Any(f => f.Claim is WeatherClaim wc && wc.Storm == StormPresence.Active),
                OutcomeActions =
                [
                    new SupersedeTriggerFacts(),
                    new AddKnowledgeFact(
                        new CustomClaim("StormPassage", "Storm passage charted under fire — dangerous but navigable in calm weather."),
                        0.85f,
                        KnowledgeSensitivity.Restricted,
                        [new PlayerHolder()]),
                ],
                OutcomeEvents = _ => [],
            },
            new QuestBranch
            {
                BranchId    = "sell_false_chart",
                Label       = "Sell a forged chart",
                Description = "Fabricate the missing route and profit from desperate merchants.",
                OutcomeActions =
                [
                    new EmitRumourAction(
                        "A new sea-chart of the disputed passage is being sold at the docks — origin unknown.",
                        s => EnglishPort(s) ?? NearestPort(s)),
                ],
                OutcomeEvents = _ => [],
            },
        ],

        ExpiryPredicate = (instance, state) =>
            state.Date.CompareTo(instance.ActivatedDate.Advance(30)) > 0,
    };

}
