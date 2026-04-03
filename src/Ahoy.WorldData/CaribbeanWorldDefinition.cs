using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.WorldData;

/// <summary>
/// The hand-crafted Caribbean world circa 1680.
///
/// Five regions, adjacency graph, 28 ports, four colonial factions,
/// two pirate brotherhoods, a starting governor pool, and two starting ships.
///
/// Regions (west to east, roughly):
///   1. Gulf of Mexico       — Spanish heartland, rich but remote
///   2. Western Caribbean    — Mixed colonial, active piracy
///   3. Central Caribbean    — Contested middle ground, player start zone
///   4. Eastern Caribbean    — English/French sugar islands
///   5. Spanish Main         — South American coast, treasure fleets
///
/// Adjacency (bidirectional):
///   Gulf  ↔ Western
///   Gulf  ↔ SpanishMain
///   Western ↔ Central
///   Western ↔ SpanishMain
///   Central ↔ Eastern
///   Central ↔ SpanishMain
///   Eastern ↔ SpanishMain
/// </summary>
public sealed class CaribbeanWorldDefinition : IWorldDefinition
{
    public void Populate(WorldState state)
    {
        state.Date = new WorldDate(1680, 3, 1);

        var regionIds = DefineRegions(state);
        var factionIds = DefineFactions(state, regionIds);
        var portIds = DefinePorts(state, regionIds, factionIds);
        DefineWeather(state, regionIds);
        DefineIndividuals(state, portIds, factionIds);
        DefineShips(state, portIds, factionIds);

        // Update player starting state
        state.Player.CaptainName = "Captain";
        state.Player.PersonalGold = 500;
        state.Player.CurrentRegionId = regionIds.Central;
    }

    // ================================================================
    // REGIONS
    // ================================================================

    private sealed record RegionIds(
        RegionId Gulf, RegionId Western, RegionId Central,
        RegionId Eastern, RegionId SpanishMain);

    private static RegionIds DefineRegions(WorldState state)
    {
        var gulf      = RegionId.New();
        var western   = RegionId.New();
        var central   = RegionId.New();
        var eastern   = RegionId.New();
        var spanishMain = RegionId.New();

        state.Regions[gulf] = new Region
        {
            Id = gulf,
            Name = "Gulf of Mexico",
            Description = "Warm, enclosed waters dominated by Spain. Deep ports shelter treasure galleons bound for Seville.",
        };
        state.Regions[western] = new Region
        {
            Id = western,
            Name = "Western Caribbean",
            Description = "Contested waters between Cuba and the Yucatan. Pirates haunt the cays; Dutch merchants brave the routes.",
        };
        state.Regions[central] = new Region
        {
            Id = central,
            Name = "Central Caribbean",
            Description = "The crossroads of the Indies. Every flag flies here and every rumour passes through.",
        };
        state.Regions[eastern] = new Region
        {
            Id = eastern,
            Name = "Eastern Caribbean",
            Description = "The sugar islands — English and French plantations drive insatiable demand for labour and luxury.",
        };
        state.Regions[spanishMain] = new Region
        {
            Id = spanishMain,
            Name = "Spanish Main",
            Description = "The north coast of South America. Silver, gold, and the scent of empire — also the most dangerous waters in the Indies.",
        };

        // Adjacency
        Link(state, gulf, western, 7f);
        Link(state, gulf, spanishMain, 10f);
        Link(state, western, central, 5f);
        Link(state, western, spanishMain, 8f);
        Link(state, central, eastern, 4f);
        Link(state, central, spanishMain, 6f);
        Link(state, eastern, spanishMain, 5f);

        // Starting dominant factions (assigned after factions are created — patched below)
        return new RegionIds(gulf, western, central, eastern, spanishMain);
    }

    private static void Link(WorldState state, RegionId a, RegionId b, float days)
    {
        state.Regions[a].AdjacentRegions.Add(b);
        state.Regions[a].BaseTravelDays[b] = days;
        state.Regions[b].AdjacentRegions.Add(a);
        state.Regions[b].BaseTravelDays[a] = days;
    }

    // ================================================================
    // FACTIONS
    // ================================================================

    private sealed record FactionIds(
        FactionId Spain, FactionId England, FactionId France, FactionId Netherlands,
        FactionId BrethrensOfBlood, FactionId SilverCoast);

    private static FactionIds DefineFactions(WorldState state, RegionIds r)
    {
        var spain       = FactionId.New();
        var england     = FactionId.New();
        var france      = FactionId.New();
        var netherlands = FactionId.New();
        var brethren    = FactionId.New();
        var silverCoast = FactionId.New();

        state.Factions[spain] = new Faction
        {
            Id = spain, Name = "Spain", Type = FactionType.Colonial,
            MotherCountry = "Spain",
            TreasuryGold = 12000, NavalStrength = 18,
            DesiredNavalAllocationFraction = 0.30f,
            CurrentNavalAllocationFraction = 0.30f,
            IntelligenceCapability = 0.55f,
        };
        state.Factions[spain].ClaimedRegions.AddRange([r.Gulf, r.Western, r.SpanishMain]);
        state.Factions[spain].PatrolAllocations[r.Gulf] = 6;
        state.Factions[spain].PatrolAllocations[r.SpanishMain] = 5;
        state.Factions[spain].PatrolAllocations[r.Western] = 4;

        state.Factions[england] = new Faction
        {
            Id = england, Name = "England", Type = FactionType.Colonial,
            MotherCountry = "England",
            TreasuryGold = 8000, NavalStrength = 12,
            DesiredNavalAllocationFraction = 0.25f,
            CurrentNavalAllocationFraction = 0.25f,
            IntelligenceCapability = 0.45f,
        };
        state.Factions[england].ClaimedRegions.AddRange([r.Eastern, r.Central]);
        state.Factions[england].PatrolAllocations[r.Eastern] = 5;
        state.Factions[england].PatrolAllocations[r.Central] = 3;

        state.Factions[france] = new Faction
        {
            Id = france, Name = "France", Type = FactionType.Colonial,
            MotherCountry = "France",
            TreasuryGold = 7000, NavalStrength = 10,
            DesiredNavalAllocationFraction = 0.25f,
            CurrentNavalAllocationFraction = 0.25f,
            IntelligenceCapability = 0.40f,
        };
        state.Factions[france].ClaimedRegions.AddRange([r.Eastern, r.Western]);
        state.Factions[france].PatrolAllocations[r.Eastern] = 4;
        state.Factions[france].PatrolAllocations[r.Western] = 2;

        state.Factions[netherlands] = new Faction
        {
            Id = netherlands, Name = "Netherlands", Type = FactionType.Colonial,
            MotherCountry = "Netherlands",
            TreasuryGold = 9000, NavalStrength = 9,
            DesiredNavalAllocationFraction = 0.20f,
            CurrentNavalAllocationFraction = 0.20f,
            IntelligenceCapability = 0.50f,
        };
        state.Factions[netherlands].ClaimedRegions.AddRange([r.Western, r.Central]);
        state.Factions[netherlands].PatrolAllocations[r.Western] = 3;
        state.Factions[netherlands].PatrolAllocations[r.Central] = 2;

        // Pirate brotherhoods
        state.Factions[brethren] = new Faction
        {
            Id = brethren, Name = "Brethren of the Blood", Type = FactionType.PirateBrotherhood,
            TreasuryGold = 3000, NavalStrength = 6,
            Cohesion = 72f, RaidingMomentum = 55f,
            IntelligenceCapability = 0.30f,
        };
        state.Factions[brethren].HavenPresence[r.Western] = 65f;
        state.Factions[brethren].HavenPresence[r.Central] = 30f;

        state.Factions[silverCoast] = new Faction
        {
            Id = silverCoast, Name = "Silver Coast Rovers", Type = FactionType.PirateBrotherhood,
            TreasuryGold = 2000, NavalStrength = 5,
            Cohesion = 58f, RaidingMomentum = 62f,
            IntelligenceCapability = 0.25f,
        };
        state.Factions[silverCoast].HavenPresence[r.SpanishMain] = 70f;
        state.Factions[silverCoast].HavenPresence[r.Eastern] = 20f;

        // Faction relationships (starting values)
        SetRel(state, spain, england, -30f);
        SetRel(state, spain, france, -20f);
        SetRel(state, spain, netherlands, -35f);
        SetRel(state, england, france, -15f);
        SetRel(state, england, netherlands, 20f);
        SetRel(state, france, netherlands, 10f);
        SetRel(state, spain, brethren, -80f);
        SetRel(state, spain, silverCoast, -80f);
        SetRel(state, england, brethren, -50f);
        SetRel(state, england, silverCoast, -40f);
        SetRel(state, france, brethren, -40f);
        SetRel(state, france, silverCoast, -45f);
        SetRel(state, netherlands, brethren, -30f);
        SetRel(state, netherlands, silverCoast, -35f);

        // Assign region dominant factions
        state.Regions[r.Gulf].DominantFactionId = spain;
        state.Regions[r.SpanishMain].DominantFactionId = spain;
        state.Regions[r.Eastern].DominantFactionId = england;
        state.Regions[r.Western].DominantFactionId = netherlands;
        state.Regions[r.Central].DominantFactionId = null; // contested

        return new FactionIds(spain, england, france, netherlands, brethren, silverCoast);
    }

    private static void SetRel(WorldState state, FactionId a, FactionId b, float value)
    {
        state.Factions[a].Relationships[b] = value;
        state.Factions[b].Relationships[a] = value;
    }

    // ================================================================
    // PORTS  (5–6 per region, 28 total)
    // ================================================================

    private sealed record PortIds(
        // Gulf
        PortId Veracruz, PortId Campeche, PortId Merida, PortId Havana, PortId SantiagoDeCuba,
        // Western
        PortId PortRoyal, PortId Tortuga, PortId Cayos, PortId SantoDomingo, PortId CapHaitian,
        // Central
        PortId Nassau, PortId SantaCruz, PortId SanJuan, PortId Charlotte, PortId Bridgetown,
        // Eastern
        PortId Barbados, PortId Martinique, PortId Guadeloupe, PortId Antigua, PortId StKitts,
        PortId Montserrat,
        // Spanish Main
        PortId Cartagena, PortId Maracaibo, PortId PortOfSpain, PortId Cumana,
        PortId SantaMarta, PortId Willemstad, PortId Paramaribo);

    private static PortIds DefinePorts(WorldState state, RegionIds r, FactionIds f)
    {
        // Helper that creates + registers a port and adds it to its region
        Port MakePort(PortId id, string name, RegionId region, FactionId? faction,
            float prosperity, bool pirateHaven = false)
        {
            var port = new Port
            {
                Id = id, Name = name, RegionId = region,
                ControllingFactionId = faction,
                Prosperity = prosperity,
                IsPirateHaven = pirateHaven,
            };
            state.Ports[id] = port;
            state.Regions[region].Ports.Add(id);
            if (faction.HasValue)
                state.Factions[faction.Value].ControlledPorts.Add(id);
            return port;
        }

        // ---- Gulf of Mexico ----
        var veracruz      = PortId.New();
        var campeche      = PortId.New();
        var merida        = PortId.New();
        var havana        = PortId.New();
        var santiagoDeCuba = PortId.New();

        MakePort(veracruz,   "Veracruz",          r.Gulf,    f.Spain,   75f);
        MakePort(campeche,   "Campeche",           r.Gulf,    f.Spain,   60f);
        MakePort(merida,     "Mérida",             r.Gulf,    f.Spain,   50f);
        MakePort(havana,     "Havana",             r.Gulf,    f.Spain,   80f);
        MakePort(santiagoDeCuba, "Santiago de Cuba", r.Gulf,  f.Spain,   55f);

        // ---- Western Caribbean ----
        var portRoyal     = PortId.New();
        var tortuga       = PortId.New();
        var cayos         = PortId.New();
        var santoDomingo  = PortId.New();
        var capHaitian    = PortId.New();

        MakePort(portRoyal,   "Port Royal",        r.Western, f.England, 70f);
        MakePort(tortuga,     "Tortuga",           r.Western, null,      45f, pirateHaven: true);
        MakePort(cayos,       "Los Cayos",         r.Western, null,      35f, pirateHaven: true);
        MakePort(santoDomingo,"Santo Domingo",     r.Western, f.Spain,   58f);
        MakePort(capHaitian,  "Cap-Haïtien",       r.Western, f.France,  52f);

        // ---- Central Caribbean ----
        var nassau        = PortId.New();
        var santaCruz     = PortId.New();
        var sanJuan       = PortId.New();
        var charlotte     = PortId.New();
        var bridgetown    = PortId.New();

        MakePort(nassau,      "Nassau",            r.Central, f.England, 55f);
        MakePort(santaCruz,   "Santa Cruz",        r.Central, f.Netherlands, 60f);
        MakePort(sanJuan,     "San Juan",          r.Central, f.Spain,   65f);
        MakePort(charlotte,   "Charlotte Amalie",  r.Central, null, 50f);
        MakePort(bridgetown,  "Bridgetown",        r.Central, f.England, 68f);

        // ---- Eastern Caribbean ----
        var barbados      = PortId.New();
        var martinique    = PortId.New();
        var guadeloupe    = PortId.New();
        var antigua       = PortId.New();
        var stKitts       = PortId.New();
        var montserrat    = PortId.New();

        MakePort(barbados,    "Bridgetown, Barbados", r.Eastern, f.England,     72f);
        MakePort(martinique,  "Fort-Royal",           r.Eastern, f.France,      68f);
        MakePort(guadeloupe,  "Basse-Terre",          r.Eastern, f.France,      60f);
        MakePort(antigua,     "English Harbour",      r.Eastern, f.England,     58f);
        MakePort(stKitts,     "Basseterre",           r.Eastern, f.England,     55f);
        MakePort(montserrat,  "Plymouth",             r.Eastern, f.England,     48f);

        // ---- Spanish Main ----
        var cartagena     = PortId.New();
        var maracaibo     = PortId.New();
        var portOfSpain   = PortId.New();
        var cumana        = PortId.New();
        var santaMarta    = PortId.New();
        var willemstad    = PortId.New();
        var paramaribo    = PortId.New();

        MakePort(cartagena,   "Cartagena",         r.SpanishMain, f.Spain,        78f);
        MakePort(maracaibo,   "Maracaibo",         r.SpanishMain, f.Spain,        62f);
        MakePort(portOfSpain, "Port of Spain",     r.SpanishMain, f.Spain,        55f);
        MakePort(cumana,      "Cumaná",            r.SpanishMain, f.Spain,        50f);
        MakePort(santaMarta,  "Santa Marta",       r.SpanishMain, f.Spain,        58f);
        MakePort(willemstad,  "Willemstad",        r.SpanishMain, f.Netherlands,  65f);
        MakePort(paramaribo,  "Paramaribo",        r.SpanishMain, f.Netherlands,  60f);

        // Assign economic profiles
        PopulateEconomies(state,
            veracruz, campeche, havana,       // Gulf
            portRoyal, tortuga,               // Western
            nassau, bridgetown,               // Central
            barbados, martinique,             // Eastern
            cartagena, maracaibo, willemstad  // Spanish Main
        );

        return new PortIds(
            veracruz, campeche, merida, havana, santiagoDeCuba,
            portRoyal, tortuga, cayos, santoDomingo, capHaitian,
            nassau, santaCruz, sanJuan, charlotte, bridgetown,
            barbados, martinique, guadeloupe, antigua, stKitts, montserrat,
            cartagena, maracaibo, portOfSpain, cumana, santaMarta, willemstad, paramaribo);
    }

    private static void PopulateEconomies(WorldState state, params PortId[] ports)
    {
        // Assign archetype economies to a representative subset
        foreach (var portId in ports)
        {
            if (!state.Ports.TryGetValue(portId, out var port)) continue;
            var eco = port.Economy;

            var archetype = port.Name switch
            {
                // Treasure/export hubs
                "Veracruz" or "Cartagena" => EconomicArchetype.TreasurePort,
                // Sugar islands
                "Bridgetown, Barbados" or "Fort-Royal" or "Bridgetown" => EconomicArchetype.SugarProducer,
                // Trade entrepôts
                "Havana" or "Port Royal" or "Willemstad" or "Nassau" => EconomicArchetype.Entrepot,
                // Pirate havens
                "Tortuga" => EconomicArchetype.PirateHaven,
                // Timber/shipyard
                "Maracaibo" => EconomicArchetype.TimberPort,
                _ => EconomicArchetype.GeneralTrade,
            };

            ApplyArchetype(eco, archetype);
        }
    }

    private enum EconomicArchetype
    {
        TreasurePort, SugarProducer, Entrepot, PirateHaven, TimberPort, GeneralTrade
    }

    private static void ApplyArchetype(EconomicProfile eco, EconomicArchetype arch)
    {
        switch (arch)
        {
            case EconomicArchetype.TreasurePort:
                eco.BaseProduction[TradeGood.Gold]   = 5;
                eco.BaseProduction[TradeGood.Silver]  = 8;
                eco.BaseConsumption[TradeGood.Food]   = 10;
                eco.BaseConsumption[TradeGood.Cloth]  = 4;
                eco.BaseConsumption[TradeGood.Tools]  = 3;
                eco.BasePrice[TradeGood.Gold]   = 800;
                eco.BasePrice[TradeGood.Silver]  = 400;
                eco.BasePrice[TradeGood.Food]    = 20;
                eco.BasePrice[TradeGood.Cloth]   = 45;
                eco.Supply[TradeGood.Gold] = 15;
                eco.Supply[TradeGood.Silver] = 25;
                break;

            case EconomicArchetype.SugarProducer:
                eco.BaseProduction[TradeGood.Sugar]   = 30;
                eco.BaseProduction[TradeGood.Rum]     = 15;
                eco.BaseProduction[TradeGood.Indigo]  = 10;
                eco.BaseConsumption[TradeGood.Food]   = 12;
                eco.BaseConsumption[TradeGood.Tools]  = 5;
                eco.BaseConsumption[TradeGood.Cloth]  = 3;
                eco.BasePrice[TradeGood.Sugar]   = 60;
                eco.BasePrice[TradeGood.Rum]     = 50;
                eco.BasePrice[TradeGood.Indigo]  = 80;
                eco.BasePrice[TradeGood.Food]    = 25;
                eco.Supply[TradeGood.Sugar] = 80;
                eco.Supply[TradeGood.Rum]   = 40;
                break;

            case EconomicArchetype.Entrepot:
                eco.BaseProduction[TradeGood.Cloth]    = 8;
                eco.BaseProduction[TradeGood.Tools]    = 6;
                eco.BaseProduction[TradeGood.Rope]     = 5;
                eco.BaseConsumption[TradeGood.Sugar]   = 8;
                eco.BaseConsumption[TradeGood.Tobacco] = 6;
                eco.BaseConsumption[TradeGood.Rum]     = 5;
                eco.BasePrice[TradeGood.Cloth]    = 55;
                eco.BasePrice[TradeGood.Tools]    = 40;
                eco.BasePrice[TradeGood.Rope]     = 30;
                eco.BasePrice[TradeGood.Sugar]    = 75;
                eco.BasePrice[TradeGood.Tobacco]  = 65;
                eco.Supply[TradeGood.Cloth] = 30;
                eco.Supply[TradeGood.Tools] = 20;
                break;

            case EconomicArchetype.PirateHaven:
                eco.BaseProduction[TradeGood.Rum]      = 10;
                eco.BaseProduction[TradeGood.Gunpowder] = 3;
                eco.BaseConsumption[TradeGood.Food]    = 8;
                eco.BaseConsumption[TradeGood.Weapons] = 4;
                eco.BaseConsumption[TradeGood.Ammunition] = 5;
                eco.BasePrice[TradeGood.Rum]       = 35;
                eco.BasePrice[TradeGood.Gunpowder] = 90;
                eco.BasePrice[TradeGood.Weapons]   = 120;
                eco.BasePrice[TradeGood.Food]      = 30;
                eco.Supply[TradeGood.Rum] = 25;
                break;

            case EconomicArchetype.TimberPort:
                eco.BaseProduction[TradeGood.Timber]   = 25;
                eco.BaseProduction[TradeGood.Rope]     = 8;
                eco.BaseConsumption[TradeGood.Food]    = 8;
                eco.BaseConsumption[TradeGood.Iron]    = 5;
                eco.BasePrice[TradeGood.Timber]  = 35;
                eco.BasePrice[TradeGood.Rope]    = 30;
                eco.BasePrice[TradeGood.Iron]    = 60;
                eco.Supply[TradeGood.Timber] = 60;
                break;

            case EconomicArchetype.GeneralTrade:
            default:
                eco.BaseProduction[TradeGood.Food]     = 8;
                eco.BaseProduction[TradeGood.Tobacco]  = 5;
                eco.BaseConsumption[TradeGood.Cloth]   = 4;
                eco.BaseConsumption[TradeGood.Tools]   = 3;
                eco.BasePrice[TradeGood.Food]    = 22;
                eco.BasePrice[TradeGood.Tobacco] = 60;
                eco.BasePrice[TradeGood.Cloth]   = 50;
                eco.BasePrice[TradeGood.Tools]   = 42;
                eco.Supply[TradeGood.Food] = 20;
                break;
        }

        // Seed demand at a sensible starting level
        foreach (var (good, cons) in eco.BaseConsumption)
            eco.Demand[good] = eco.Demand.GetValueOrDefault(good) + cons * 3;
    }

    // ================================================================
    // WEATHER
    // ================================================================

    private static void DefineWeather(WorldState state, RegionIds r)
    {
        foreach (var regionId in state.Regions.Keys)
        {
            state.Weather[regionId] = new RegionWeather
            {
                RegionId = regionId,
                WindStrength = WindStrength.Moderate,
                WindDirection = WindDirection.East,
                StormPresence = StormPresence.None,
                Visibility = Visibility.Clear,
            };
        }
    }

    // ================================================================
    // INDIVIDUALS
    // ================================================================

    private static void DefineIndividuals(WorldState state, PortIds p, FactionIds f)
    {
        var rng = new Random(42); // deterministic seed for starting world

        // Spanish governors
        AddGovernor(state, rng, "Don Hernando", "de Soto",  p.Havana,    f.Spain,   70f);
        AddGovernor(state, rng, "Don Miguel",   "Ramirez",  p.Veracruz,  f.Spain,   60f);
        AddGovernor(state, rng, "Don Carlos",   "Montoya",  p.Cartagena, f.Spain,   75f);

        // English governors
        AddGovernor(state, rng, "Sir Thomas",   "Modyford", p.PortRoyal,  f.England, 65f);
        AddGovernor(state, rng, "Sir Edward",   "Blake",    p.Bridgetown, f.England, 58f);

        // French governors
        AddGovernor(state, rng, "Monsieur",     "Auger",    p.CapHaitian,  f.France, 55f);
        AddGovernor(state, rng, "Monsieur",     "Dutertre", p.Martinique,  f.France, 60f);

        // Dutch governors
        AddGovernor(state, rng, "Meinheer",     "van Dyke", p.SantaCruz,  f.Netherlands, 62f);
        AddGovernor(state, rng, "Meinheer",     "de Groot", p.Willemstad, f.Netherlands, 58f);

        // Pirate captains
        AddPirate(state, rng, "Calico",      "Jack",    p.Tortuga,    f.BrethrensOfBlood);
        AddPirate(state, rng, "Anne",        "Bonny",   p.Cayos,      f.BrethrensOfBlood);
        AddPirate(state, rng, "Bartholomew", "Roberts", p.Cartagena,  f.SilverCoast);

        // Knowledge brokers (factionId = sponsoring faction for gold pool; null = independent)
        AddBroker(state, rng, "Pedro",   "Ruiz",    p.Nassau,     null);
        AddBroker(state, rng, "Hannah",  "Clarke",  p.PortRoyal,  f.England);
        AddBroker(state, rng, "Ibrahim", "al-Kazi", p.Willemstad, f.Netherlands);
    }

    private static void AddGovernor(WorldState state, Random rng,
        string first, string last, PortId portId, FactionId factionId, float authority)
    {
        var id = IndividualId.New();
        var ind = new Individual
        {
            Id = id, FirstName = first, LastName = last,
            Role = IndividualRole.Governor,
            FactionId = factionId,
            LocationPortId = portId,
            HomePortId = portId,   // governors return home after inspection tours
            Authority = authority,
            Personality = PersonalityTraits.Random(rng),
        };
        state.Individuals[id] = ind;
        if (state.Ports.TryGetValue(portId, out var port))
            port.GovernorId = id;

        // Seed a low-confidence whereabouts rumour in player knowledge (tavern-grade: 0.25–0.35).
        // This is below the 0.40 threshold that triggers Quest A3 (The Governor's Quill).
        var rumourConfidence = 0.25f + (float)rng.NextDouble() * 0.10f;
        var whereaboutsFact = new KnowledgeFact
        {
            Claim = new IndividualWhereaboutsClaim(id, portId),
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = rumourConfidence,
            BaseConfidence = rumourConfidence,
            ObservedDate = state.Date,
            HopCount = 2,   // already travelled through a few hands
        };
        state.Knowledge.AddFact(new PortHolder(portId), whereaboutsFact);
        state.Knowledge.AddFact(new PlayerHolder(), whereaboutsFact);
    }

    private static void AddPirate(WorldState state, Random rng,
        string first, string last, PortId portId, FactionId factionId)
    {
        var id = IndividualId.New();
        state.Individuals[id] = new Individual
        {
            Id = id, FirstName = first, LastName = last,
            Role = IndividualRole.PirateCaptain,
            FactionId = factionId,
            LocationPortId = portId,
            Authority = 40f,
            Personality = PersonalityTraits.Random(rng),
        };
    }

    private static void AddBroker(WorldState state, Random rng,
        string first, string last, PortId portId, FactionId? factionId = null)
    {
        var id = IndividualId.New();
        var broker = new Individual
        {
            Id = id, FirstName = first, LastName = last,
            Role = IndividualRole.KnowledgeBroker,
            FactionId = factionId,
            LocationPortId = portId,
            HomePortId = portId,
            Authority = 30f,
            Personality = PersonalityTraits.Random(rng),
        };
        broker.CurrentGold = 200;
        state.Individuals[id] = broker;

        // Bootstrap broker's knowledge with FactionStrengthClaims for all factions
        // Simulates them having spent time listening to gossip before play begins
        var brokerHolder = new Ahoy.Simulation.State.IndividualHolder(id);
        foreach (var (factFactionId, faction) in state.Factions)
        {
            var seedConfidence = 0.55f + (float)rng.NextDouble() * 0.15f;
            var fact = new Ahoy.Simulation.State.KnowledgeFact
            {
                Claim = new Ahoy.Simulation.State.FactionStrengthClaim(
                    factFactionId, faction.NavalStrength, faction.TreasuryGold),
                Sensitivity = Ahoy.Core.Enums.KnowledgeSensitivity.Restricted,
                Confidence = seedConfidence,
                BaseConfidence = seedConfidence,
                ObservedDate = state.Date,
                HopCount = 1,
                SourceHolder = new Ahoy.Simulation.State.FactionHolder(factFactionId),
            };
            state.Knowledge.AddFact(brokerHolder, fact);
        }
    }

    // ================================================================
    // SHIPS
    // ================================================================

    private static void DefineShips(WorldState state, PortIds p, FactionIds f)
    {
        var rng = new Random(43); // deterministic seed — different from DefineIndividuals (42)
        // Player's starting sloop — docked at Nassau
        var playerShipId = ShipId.New();
        var playerShip = new Ship
        {
            Id = playerShipId,
            Name = "Sea Rover",
            Class = ShipClass.Sloop,
            Location = new AtPort(p.Nassau),
            MaxCargoTons = 60,
            MaxCrew = 25,
            Guns = 6,
            CurrentCrew = 20,
            GoldOnBoard = 0,
            IsPlayerShip = true,
            OwnerFactionId = null,
        };
        playerShip.Cargo[TradeGood.Food] = 10;
        state.Ships[playerShipId] = playerShip;
        state.Ports[p.Nassau].DockedShips.Add(playerShipId);
        state.Player.FlagshipId = playerShipId;
        state.Player.FleetIds.Add(playerShipId);

        // A handful of NPC merchant brigs — each with a named captain (Individual-first model).
        // Captains accumulate knowledge via IndividualHolder as they sail between ports,
        // and drive knowledge-gated routing decisions via ShipMovementSystem.AssignNpcRoute().
        AddMerchant(state, rng, "San Cristóbal",  ShipClass.Brig,       p.Veracruz,   f.Spain,       TradeGood.Silver, 30, "Diego",    "Montoya");
        AddMerchant(state, rng, "Prosperity",      ShipClass.Brig,       p.Bridgetown, f.England,     TradeGood.Sugar,  50, "Thomas",   "Hartwell");
        AddMerchant(state, rng, "Volendammer",     ShipClass.Brig,       p.Willemstad, f.Netherlands, TradeGood.Cloth,  40, "Pieter",   "van Houten");
        AddMerchant(state, rng, "Belle Étoile",    ShipClass.Brigantine, p.Martinique, f.France,      TradeGood.Indigo, 25, "Étienne",  "Moreau");

        // A colonial patrol frigate
        AddPatrol(state, "HMS Resolute",     ShipClass.Frigate, p.PortRoyal,   f.England);
        AddPatrol(state, "San Felipe",       ShipClass.Frigate, p.Havana,      f.Spain);
    }

    private static void AddMerchant(WorldState state, Random rng, string name, ShipClass cls,
        PortId portId, FactionId? faction, TradeGood cargo, int cargoQty,
        string? captainFirst = null, string? captainLast = null)
    {
        var id = ShipId.New();
        var ship = new Ship
        {
            Id = id, Name = name, Class = cls,
            Location = new AtPort(portId),
            MaxCargoTons = 120, MaxCrew = 40, Guns = 4,
            CurrentCrew = 35,
            GoldOnBoard = 300,
            OwnerFactionId = faction,
        };
        ship.Cargo[cargo] = cargoQty;
        state.Ships[id] = ship;
        if (state.Ports.TryGetValue(portId, out var port))
            port.DockedShips.Add(id);
        if (faction.HasValue)
            state.Factions[faction.Value].ControlledPorts.Contains(portId);

        // Create a named captain Individual (Individual-first model).
        // The captain's IndividualHolder accumulates knowledge from ports they visit,
        // enabling knowledge-gated routing decisions in ShipMovementSystem.AssignNpcRoute().
        if (captainFirst is not null && captainLast is not null)
        {
            var captainId = IndividualId.New();
            var captain = new Individual
            {
                Id             = captainId,
                FirstName      = captainFirst,
                LastName       = captainLast,
                Role           = IndividualRole.PortMerchant,
                FactionId      = faction,
                LocationPortId = portId,
                HomePortId     = portId,
                Personality    = PersonalityTraits.Random(rng),
            };
            captain.CurrentGold = 150 + rng.Next(0, 100);
            state.Individuals[captainId] = captain;
            ship.CaptainId = captainId;
        }
    }

    private static void AddPatrol(WorldState state, string name, ShipClass cls,
        PortId portId, FactionId factionId)
    {
        var id = ShipId.New();
        var ship = new Ship
        {
            Id = id, Name = name, Class = cls,
            Location = new AtPort(portId),
            MaxCargoTons = 20, MaxCrew = 80, Guns = 28,
            CurrentCrew = 75,
            GoldOnBoard = 0,
            OwnerFactionId = factionId,
        };
        state.Ships[id] = ship;
        if (state.Ports.TryGetValue(portId, out var port))
            port.DockedShips.Add(id);
    }
}

