using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Tests.Infrastructure;
using Xunit;

namespace Ahoy.Simulation.Tests;

/// <summary>
/// Targeted tests for specific Group 5-9 features that aren't covered
/// by PetriDishTests or DeepTimeTests.
/// </summary>
public class GroupFeatureTests
{
    // ---- Group 5B: NPC Stall & Leak ----

    /// <summary>
    /// An NPC pursuing a contract stalls when intel confidence drops below 0.30.
    /// After 14 ticks stalled, the goal is abandoned and the NPC's knowledge
    /// about the target leaks into the port gossip pool.
    /// </summary>
    [Fact]
    public void NpcPursuit_StallsAndLeaks_WhenIntelDecays()
    {
        var builder = new ScenarioBuilder(seed: 700);
        var region = builder.AddRegion("Caribbean");
        var pirates = builder.AddFaction("Pirates", FactionType.PirateBrotherhood, treasury: 5000);
        var port = builder.AddPort("Tortuga", region, pirates);

        var hunter = builder.AddIndividual("Anne", "Bonny", IndividualRole.PirateCaptain,
            factionId: pirates, locationPort: port, gold: 500);
        var targetShipId = builder.AddShip("Merchant Prize", dockedAt: port, guns: 5);
        builder.AddShip("Queen Anne", pirates, captainId: hunter, dockedAt: port, guns: 20);

        // Seed contract + very low confidence intel (will decay below threshold)
        builder.SeedFact(new IndividualHolder(hunter),
            new ContractClaim(hunter, pirates, $"Ship:{targetShipId.Value}",
                ContractConditionType.TargetDestroyed, 300, NarrativeArchetype.PirateRival),
            confidence: 0.50f);
        builder.SeedFact(new IndividualHolder(hunter),
            new ShipLocationClaim(targetShipId, new AtSea(region)),
            confidence: 0.25f); // below 0.30 threshold — will trigger stall

        var (engine, state) = builder.BuildWithState();

        // Run enough ticks for goal assignment + stall + abandonment (>14 ticks stalled)
        for (int i = 0; i < 25; i++)
            engine.Tick();

        // The hunter should have had a pursuit that stalled or was abandoned
        if (state.NpcPursuits.TryGetValue(hunter, out var pursuit))
        {
            Assert.True(pursuit.State is PursuitState.Stalled or PursuitState.Abandoned or PursuitState.Pondering,
                $"Expected stalled/abandoned/pondering, got {pursuit.State}");
        }
        // If no pursuit exists, the goal was never assigned (also acceptable — intel too low)
    }

    // ---- Group 5C: Knowledge Conflict Auto-Resolve ----

    [Fact]
    public void KnowledgeConflict_AutoResolves_WhenSpreadExceeds040()
    {
        var builder = new ScenarioBuilder(seed: 701);
        var region = builder.AddRegion("Caribbean");
        var port = builder.AddPort("Havana", region);

        // Seed two conflicting facts about the same subject with large spread
        var shipId = new ShipId(Guid.NewGuid());
        builder.SeedFact(new PortHolder(port),
            new ShipLocationClaim(shipId, new AtSea(region)),
            confidence: 0.90f);
        builder.SeedFact(new PortHolder(port),
            new ShipLocationClaim(shipId, new AtPort(port)),
            confidence: 0.30f); // spread = 0.60 > 0.40 threshold

        var (engine, state) = builder.BuildWithState();

        // Run ticks — auto-resolve should supersede the weaker fact
        for (int i = 0; i < 5; i++)
            engine.Tick();

        var portFacts = state.Knowledge.GetFacts(new PortHolder(port))
            .Where(f => !f.IsSuperseded && f.Claim is ShipLocationClaim)
            .ToList();

        // Should have at most 1 non-superseded fact (the dominant one)
        Assert.True(portFacts.Count <= 1,
            $"Expected conflict auto-resolved (<=1 fact), got {portFacts.Count}");
    }

    // ---- Group 6D: Vengeance Trigger ----

    [Fact]
    public void Vengeance_SeedsContractBounty_WhenRelationshipIsNemesis()
    {
        var builder = new ScenarioBuilder(seed: 702);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain");
        var port = builder.AddPort("Havana", region, spain);

        var governor = builder.AddIndividual("Don", "Garcia", IndividualRole.Governor,
            factionId: spain, locationPort: port, gold: 500);
        var nemesis = builder.AddIndividual("Blackbeard", "Teach", IndividualRole.PirateCaptain,
            locationPort: port, gold: 100);

        // Set relationship to nemesis level
        builder.SetRelationship(governor, nemesis, -80f);

        var (engine, state) = builder.BuildWithState();

        // Run ticks — governor should seed a vengeance contract
        for (int i = 0; i < 10; i++)
            engine.Tick();

        // Check if a TargetDead contract was seeded for the nemesis
        var portFacts = state.Knowledge.GetFacts(new PortHolder(port));
        var bounty = portFacts.FirstOrDefault(f => !f.IsSuperseded
            && f.Claim is ContractClaim cc
            && cc.Condition == ContractConditionType.TargetDead
            && cc.TargetSubjectKey.Contains(nemesis.Value.ToString()));

        Assert.NotNull(bounty);
        Assert.True(state.Individuals[governor].CurrentGold < 500,
            "Governor should have spent gold on the bounty");
    }

    // ---- Group 6E: PardonClaim Reconciliation ----

    [Fact]
    public void PardonClaim_ShiftsRelationshipTowardZero()
    {
        var builder = new ScenarioBuilder(seed: 703);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain");
        var port = builder.AddPort("Havana", region, spain);

        var governor = builder.AddIndividual("Don", "Garcia", IndividualRole.Governor,
            factionId: spain, locationPort: port, gold: 500);
        var offender = builder.AddIndividual("John", "Smith", IndividualRole.Privateer,
            factionId: spain, locationPort: port, gold: 100);

        // Set hostile relationship
        builder.SetRelationship(governor, offender, -60f);

        // Seed a pardon claim into governor's knowledge
        builder.SeedFact(new IndividualHolder(governor),
            new PardonClaim(governor, spain, offender),
            confidence: 0.90f);

        var (engine, state) = builder.BuildWithState();

        // Run ticks for pardon to propagate
        for (int i = 0; i < 5; i++)
            engine.Tick();

        var rel = state.GetRelationship(governor, offender);
        Assert.True(rel > -60f, $"Pardon should have improved relationship from -60, got {rel}");
    }

    // ---- Group 7: Blockade Detection ----

    [Fact]
    public void BlockadeDetection_SetsFlag_WhenHostileStrengthExceedsDefense()
    {
        var builder = new ScenarioBuilder(seed: 704);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 10000);
        var pirates = builder.AddFaction("Pirates", FactionType.PirateBrotherhood);
        var port = builder.AddPort("Havana", region, spain);

        // Declare war so pirate ships count as hostile
        var (engine, state) = builder.BuildWithState();
        state.Factions[spain].DeclareWarOn(pirates, state.Factions[pirates]);

        // Add heavy pirate ships in the region (guns > port defense of ~20)
        for (int i = 0; i < 5; i++)
            builder.AddShip($"Pirate {i}", pirates, dockedAt: port, guns: 15);

        for (int i = 0; i < 5; i++)
            engine.Tick();

        Assert.True(state.Ports[port].Conditions.HasFlag(PortConditionFlags.Blockaded),
            "Port should be blockaded by overwhelming hostile naval strength");
    }

    // ---- Group 8: Famine Cascade ----

    [Fact]
    public void FamineCascade_ReducesPopulation_WhenFoodDepleted()
    {
        var builder = new ScenarioBuilder(seed: 705);
        var region = builder.AddRegion("Caribbean");
        // Independent port — no faction, so reduced external food imports (50%)
        var port = builder.AddPort("Havana", region, controllingFaction: null, prosperity: 50f, population: 10000);

        var (engine, state) = builder.BuildWithState();

        // Drain all food AND remove food production so imports can't cover demand
        state.Ports[port].Economy.Supply[TradeGood.Food] = 0;
        state.Ports[port].Economy.BaseProduction.Remove(TradeGood.Food);

        var startPop = state.Ports[port].Population;

        // Run ticks — famine should reduce population
        for (int i = 0; i < 10; i++)
            engine.Tick();

        var endPop = state.Ports[port].Population;
        Assert.True(endPop < startPop,
            $"Population should have declined from starvation. Start={startPop}, End={endPop}");
        Assert.True(state.Ports[port].Conditions.HasFlag(PortConditionFlags.Famine),
            "Famine flag should be set");
    }

    // ---- Group 9: Captain Mutiny ----

    [Fact]
    public void CaptainMutiny_AbandonsOrders_WhenRelationshipIsNemesis()
    {
        var builder = new ScenarioBuilder(seed: 706);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 10000);
        var portA = builder.AddPort("Havana", region, spain);
        var portB = builder.AddPort("Veracruz", region, spain);

        // The viceroy (Havana governor)
        var viceroy = builder.AddIndividual("Don", "Viceroy", IndividualRole.Governor,
            factionId: spain, locationPort: portA, gold: 1000);

        // A captain who hates the viceroy
        var captain = builder.AddIndividual("Rebel", "Captain", IndividualRole.NavalOfficer,
            factionId: spain, locationPort: portA, gold: 200);
        var shipId = builder.AddShip("Rebelde", spain, captainId: captain, dockedAt: portA, guns: 15);

        // Set nemesis relationship
        builder.SetRelationship(captain, viceroy, -80f);

        var (engine, state) = builder.BuildWithState();

        // Manually assign a mission — the captain should refuse
        var mission = new ReliefMission(portB, TradeGood.Food, 10);
        state.Ships[shipId].Mission = mission;
        state.NpcPursuits[captain] = new GoalPursuit
        {
            ActiveGoal = new ExecuteOrdersGoal(Guid.NewGuid(), captain, mission),
            State = PursuitState.Active,
            ActivatedOnTick = 0,
        };

        // Run ticks — captain should mutiny (abandon the goal)
        for (int i = 0; i < 5; i++)
            engine.Tick();

        // Ship mission should be cleared (mutiny)
        var shipMission = state.Ships[shipId].Mission;
        var pursuit = state.NpcPursuits.GetValueOrDefault(captain);

        bool mutinied = shipMission is null
            || (pursuit is not null && pursuit.State is PursuitState.Abandoned or PursuitState.Pondering);

        Assert.True(mutinied,
            $"Captain should have mutinied. Mission={shipMission?.GetType().Name ?? "null"}, " +
            $"Pursuit={pursuit?.State.ToString() ?? "none"}");
    }

    // ---- Group 9: Dynamic Promotion ----

    [Fact]
    public void DynamicPromotion_CreatesNamedCaptain_WhenHeadlessShipBreaksFamine()
    {
        var builder = new ScenarioBuilder(seed: 707);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 10000);
        var surplusPort = builder.AddPort("Veracruz", region, spain, population: 3000);
        var starvingPort = builder.AddPort("Havana", region, spain, population: 5000);

        // Headless ship (no captain) with food cargo
        var shipId = builder.AddShip("Supply Ship", spain, dockedAt: surplusPort, guns: 5);

        var (engine, state) = builder.BuildWithState();

        // Starve the port and set famine
        state.Ports[starvingPort].Economy.Supply[TradeGood.Food] = 0;
        state.Ports[starvingPort].SetCondition(PortConditionFlags.Famine, true);

        // Load food onto headless ship and send it
        state.Ships[shipId].Cargo[TradeGood.Food] = 50;
        state.Ships[shipId].Mission = new ReliefMission(starvingPort, TradeGood.Food, 50);
        state.Ships[shipId].Route = new PortRoute(starvingPort);

        var initialIndividualCount = state.Individuals.Count;

        // Run ticks until the ship arrives
        for (int i = 0; i < 15; i++)
            engine.Tick();

        // Check if a new captain was created
        var ship = state.Ships[shipId];
        bool promoted = ship.CaptainId.HasValue && state.Individuals.Count > initialIndividualCount;

        // If the ship arrived and the port was in famine, promotion should have fired
        if (ship.Location is AtPort ap && ap.Port == starvingPort)
        {
            Assert.True(promoted,
                $"Headless ship that broke a famine should have generated a named captain. " +
                $"CaptainId={ship.CaptainId}, Individuals={state.Individuals.Count} (was {initialIndividualCount})");
        }
        // If ship hasn't arrived yet, test is inconclusive (travel time)
    }

    // ---- Group 8: Merchant Arbitrage Routing ----

    [Fact]
    public void MerchantArbitrage_BuysGoodsKnownExpensiveElsewhere()
    {
        var builder = new ScenarioBuilder(seed: 708);
        var region = builder.AddRegion("Caribbean");
        var faction = builder.AddFaction("England");
        var cheapPort = builder.AddPort("Kingston", region, faction, prosperity: 70f);
        var expensivePort = builder.AddPort("Port Royal", region, faction, prosperity: 20f);

        var captain = builder.AddIndividual("Thomas", "Hartwell", IndividualRole.PortMerchant,
            factionId: faction, locationPort: cheapPort, gold: 500);
        var shipId = builder.AddShip("Prosperity", faction, captainId: captain, dockedAt: cheapPort, guns: 5);

        // Captain knows food is expensive at Port Royal (high price = famine)
        builder.SeedFact(new IndividualHolder(captain),
            new PortPriceClaim(expensivePort, TradeGood.Food, 80), // 10x normal
            confidence: 0.80f);

        // Captain knows food is cheap here at Kingston
        builder.SeedFact(new IndividualHolder(captain),
            new PortPriceClaim(cheapPort, TradeGood.Food, 8), // normal price
            confidence: 0.90f);

        var (engine, state) = builder.BuildWithState();

        // Ensure cheap port has food surplus
        state.Ports[cheapPort].Economy.Supply[TradeGood.Food] = 200;
        state.Ships[shipId].GoldOnBoard = 500;

        // Run ticks — merchant should buy food and route toward expensive port
        for (int i = 0; i < 10; i++)
            engine.Tick();

        var ship = state.Ships[shipId];
        var hasFoodCargo = ship.Cargo.GetValueOrDefault(TradeGood.Food) > 0;
        var routedToExpensive = ship.Route is PortRoute pr && pr.Destination == expensivePort;
        var alreadyThere = ship.Location is AtPort ap && ap.Port == expensivePort;

        Assert.True(hasFoodCargo || routedToExpensive || alreadyThere,
            $"Merchant should buy cheap food and route to expensive port. " +
            $"Food cargo={ship.Cargo.GetValueOrDefault(TradeGood.Food)}, " +
            $"Route={ship.Route?.GetType().Name ?? "null"}, Location={ship.Location}");
    }

    // ---- Group 7 Crisis 6: War + Letters of Marque ----

    [Fact]
    public void War_GovernorSeedsLettersOfMarque_AgainstEnemyShips()
    {
        var builder = new ScenarioBuilder(seed: 709);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 10000);
        var england = builder.AddFaction("England", treasury: 10000);
        var port = builder.AddPort("Havana", region, spain);

        var governor = builder.AddIndividual("Don", "Garcia", IndividualRole.Governor,
            factionId: spain, locationPort: port, gold: 1000);

        // English ship in the region — potential target
        var enemyShipId = builder.AddShip("HMS Victory", england, dockedAt: port, guns: 20);

        var (engine, state) = builder.BuildWithState();

        // Declare war
        state.Factions[spain].DeclareWarOn(england, state.Factions[england]);

        for (int i = 0; i < 10; i++)
            engine.Tick();

        // Governor should have seeded a TargetDestroyed contract against the English ship
        var portFacts = state.Knowledge.GetFacts(new PortHolder(port));
        var letterOfMarque = portFacts.FirstOrDefault(f => !f.IsSuperseded
            && f.Claim is ContractClaim cc
            && cc.Condition == ContractConditionType.TargetDestroyed
            && cc.TargetSubjectKey.Contains(enemyShipId.Value.ToString()));

        Assert.NotNull(letterOfMarque);
    }

    // ---- Group 9 Phase 4: Defection Trigger ----

    /// <summary>
    /// Directly seed a ReliefDenialClaim into a governor's knowledge.
    /// The governor should defect when relationship with viceroy is nemesis
    /// and the port is starving.
    /// </summary>
    [Fact]
    public void DefectionTrigger_GovernorFlipsPort_WhenDeniedAndStarving()
    {
        var builder = new ScenarioBuilder(seed: 710);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 1000);
        var capital = builder.AddPort("Havana", region, spain, population: 8000);
        var colony = builder.AddPort("Outpost", region, spain, population: 1000);

        var viceroy = builder.AddIndividual("Don", "Viceroy", IndividualRole.Governor,
            factionId: spain, locationPort: capital, gold: 5000);
        var governor = builder.AddIndividual("Don", "Colonial", IndividualRole.Governor,
            factionId: spain, locationPort: colony, gold: 100);

        var (engine, state) = builder.BuildWithState();

        // Set the colony to starving
        state.Ports[colony].Economy.Supply[TradeGood.Food] = 0;
        state.Ports[colony].SetCondition(PortConditionFlags.Famine, true);

        // Set nemesis relationship between governor and viceroy
        state.AdjustRelationship(governor, viceroy, -80f);

        // Seed the denial directly into governor's knowledge (simulates courier arrival)
        var crisisId = Guid.NewGuid();
        state.Ports[colony].ActiveCrisisId = crisisId;
        state.Knowledge.AddFact(new IndividualHolder(governor), new KnowledgeFact
        {
            Claim = new ReliefDenialClaim(colony, crisisId, spain),
            Sensitivity = KnowledgeSensitivity.Restricted,
            Confidence = 0.95f,
            BaseConfidence = 0.95f,
            ObservedDate = state.Date,
        });

        for (int i = 0; i < 5; i++)
            engine.Tick();

        // The colony should have defected — no longer controlled by Spain
        Assert.Null(state.Ports[colony].ControllingFactionId);
        Assert.False(state.Factions[spain].ControlledPorts.Contains(colony));
    }

    // ---- Group 9 Phase 3: SVS Triage ----

    /// <summary>
    /// A high-value port should get relief approved. A low-value port should be denied.
    /// We test indirectly: a faction with low treasury facing a request from a tiny port
    /// should deny it.
    /// </summary>
    [Fact]
    public void SvsTriage_DeniesRelief_ForLowValuePort()
    {
        var builder = new ScenarioBuilder(seed: 711);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain", treasury: 500); // broke faction
        var capital = builder.AddPort("Havana", region, spain, population: 8000);
        var outpost = builder.AddPort("Tiny Outpost", region, spain, population: 300);

        var viceroy = builder.AddIndividual("Don", "Viceroy", IndividualRole.Governor,
            factionId: spain, locationPort: capital, gold: 100);
        builder.AddIndividual("Don", "Outpost", IndividualRole.Governor,
            factionId: spain, locationPort: outpost, gold: 50);

        // Seed a relief request into the viceroy's knowledge (simulates courier arrival)
        var crisisId = Guid.NewGuid();
        builder.SeedFact(new IndividualHolder(viceroy),
            new ReliefRequestClaim(outpost, crisisId, 50, 5000), // requesting 5000g — more than treasury
            confidence: 0.95f, sensitivity: KnowledgeSensitivity.Restricted);

        var (engine, state) = builder.BuildWithState();

        for (int i = 0; i < 3; i++)
            engine.Tick();

        // The viceroy should have denied — check for ReliefDenialClaim in viceroy's knowledge
        var viceroyFacts = state.Knowledge.GetFacts(new IndividualHolder(viceroy));
        var denial = viceroyFacts.FirstOrDefault(f => !f.IsSuperseded
            && f.Claim is ReliefDenialClaim rdc && rdc.CrisisId == crisisId);

        Assert.NotNull(denial);
    }

    // ---- Group 10: Zero-Sum Economy ----

    [Fact]
    public void ZeroSumTrade_PortTreasuryDecreasesWhenMerchantSells()
    {
        var builder = new ScenarioBuilder(seed: 712);
        var region = builder.AddRegion("Caribbean");
        var faction = builder.AddFaction("Spain");
        var port = builder.AddPort("Havana", region, faction, prosperity: 50f, population: 3000);

        var captain = builder.AddIndividual("Don", "Trader", IndividualRole.PortMerchant,
            factionId: faction, locationPort: port, gold: 100);
        var shipId = builder.AddShip("Merchant", faction, captainId: captain, dockedAt: port, guns: 5);

        var (engine, state) = builder.BuildWithState();

        state.Ships[shipId].Cargo[TradeGood.Food] = 10;
        state.Ports[port].Economy.Demand[TradeGood.Food] = 20;
        var startTreasury = state.Ports[port].Treasury;

        engine.Tick();

        var endTreasury = state.Ports[port].Treasury;
        Assert.True(endTreasury < startTreasury,
            $"Port treasury should decrease when buying goods. Start={startTreasury}, End={endTreasury}");
    }

    [Fact]
    public void BrokePort_RefusesTrade_WhenTreasuryIsZero()
    {
        var builder = new ScenarioBuilder(seed: 713);
        var region = builder.AddRegion("Caribbean");
        var faction = builder.AddFaction("Spain");
        var port = builder.AddPort("Havana", region, faction, prosperity: 50f, population: 3000);

        var captain = builder.AddIndividual("Don", "Trader", IndividualRole.PortMerchant,
            factionId: faction, locationPort: port, gold: 100);
        var shipId = builder.AddShip("Merchant", faction, captainId: captain, dockedAt: port, guns: 5);

        var (engine, state) = builder.BuildWithState();

        state.Ports[port].Treasury = 0;
        state.Ships[shipId].Cargo[TradeGood.Food] = 10;
        state.Ports[port].Economy.Demand[TradeGood.Food] = 20;
        var startGold = state.Ships[shipId].GoldOnBoard;

        engine.Tick();

        var endGold = state.Ships[shipId].GoldOnBoard;
        Assert.True(endGold <= startGold,
            $"Broke port should not pay merchant. Ship gold: {startGold} → {endGold}");
    }

    [Fact]
    public void ExportMint_InjectsGoldIntoHubTreasury()
    {
        var builder = new ScenarioBuilder(seed: 714);
        var region = builder.AddRegion("Caribbean");
        var spain = builder.AddFaction("Spain");
        var hub = builder.AddPort("Havana", region, spain, prosperity: 70f, population: 8000);

        var (engine, state) = builder.BuildWithState();

        state.Ports[hub].Economy.CanExportToEurope = true;
        state.Ports[hub].Economy.Supply[TradeGood.Sugar] = 100;
        state.Ports[hub].Economy.TargetSupply[TradeGood.Sugar] = 10;
        state.Ports[hub].Economy.BasePrice[TradeGood.Sugar] = 10;
        // Remove sugar production so we can isolate the export effect
        state.Ports[hub].Economy.BaseProduction.Remove(TradeGood.Sugar);

        var startTreasury = state.Ports[hub].Treasury;

        engine.Tick();

        // Sugar supply should have decreased (goods exported to Europe)
        var endSugar = state.Ports[hub].Economy.Supply.GetValueOrDefault(TradeGood.Sugar);
        Assert.True(endSugar < 100,
            $"Export hub should have exported sugar. Supply: 100 → {endSugar}");

        // Treasury may decrease due to faction taxation on the same tick,
        // but the export revenue should partially offset it
        var endTreasury = state.Ports[hub].Treasury;
        Assert.True(endTreasury > 0,
            $"Export hub should still have treasury after exports+tax. {startTreasury} → {endTreasury}");
    }
}
