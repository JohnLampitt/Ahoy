using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Tests.Infrastructure;

/// <summary>
/// Constructs minimal, isolated WorldStates for Petri dish testing.
/// Builds a microscopic world — 1-3 regions, a few ports, a handful of NPCs —
/// so tests can assert outcomes without full-Caribbean noise.
/// </summary>
public sealed class ScenarioBuilder
{
    private readonly WorldState _state = new();
    private readonly Random _rng;
    private readonly int _seed;

    public ScenarioBuilder(int seed = 42)
    {
        _seed = seed;
        _rng = new Random(seed);
    }

    // ---- Regions ----

    public RegionId AddRegion(string name = "Test Region")
    {
        var id = new RegionId(Guid.NewGuid());
        _state.Regions[id] = new Region
        {
            Id = id,
            Name = name,
            Description = $"Test region: {name}",
        };
        return id;
    }

    public ScenarioBuilder ConnectRegions(RegionId a, RegionId b, float travelDays = 3f)
    {
        _state.Regions[a].AdjacentRegions.Add(b);
        _state.Regions[b].AdjacentRegions.Add(a);
        _state.Regions[a].BaseTravelDays[b] = travelDays;
        _state.Regions[b].BaseTravelDays[a] = travelDays;
        return this;
    }

    // ---- Factions ----

    public FactionId AddFaction(string name, FactionType type = FactionType.Colonial,
        int treasury = 5000, int navalStrength = 10)
    {
        var id = new FactionId(Guid.NewGuid());
        _state.Factions[id] = new Faction
        {
            Id = id,
            Name = name,
            Type = type,
            TreasuryGold = treasury,
            NavalStrength = navalStrength,
        };
        return id;
    }

    public ScenarioBuilder SetFactionRelationship(FactionId a, FactionId b, float value)
    {
        _state.Factions[a].Relationships[b] = value;
        _state.Factions[b].Relationships[a] = value;
        return this;
    }

    // ---- Ports ----

    public PortId AddPort(string name, RegionId region, FactionId? controllingFaction = null,
        float prosperity = 50f, int population = 2000)
    {
        var id = new PortId(Guid.NewGuid());
        var port = new Port
        {
            Id = id,
            Name = name,
            RegionId = region,
            ControllingFactionId = controllingFaction,
            Prosperity = prosperity,
        };
        port.AdjustPopulation(population - port.Population);

        // Default economy — produces and consumes basic goods
        var foodNeeded = population / 100;
        port.Economy.BaseProduction[TradeGood.Sugar] = 5;
        port.Economy.BaseProduction[TradeGood.Food] = Math.Max(5, foodNeeded);
        port.Economy.BaseConsumption[TradeGood.Food] = foodNeeded;
        port.Economy.Supply[TradeGood.Sugar] = 20;
        port.Economy.Supply[TradeGood.Food] = foodNeeded * 30; // 30 days supply
        port.Economy.BasePrice[TradeGood.Sugar] = 10;
        port.Economy.BasePrice[TradeGood.Food] = 8;

        _state.Ports[id] = port;
        _state.Regions[region].Ports.Add(id);

        if (controllingFaction.HasValue && _state.Factions.ContainsKey(controllingFaction.Value))
            _state.Factions[controllingFaction.Value].ControlledPorts.Add(id);

        return id;
    }

    // ---- Individuals ----

    public IndividualId AddIndividual(string firstName, string lastName, IndividualRole role,
        FactionId? factionId = null, PortId? locationPort = null, int gold = 100)
    {
        var id = new IndividualId(Guid.NewGuid());
        _state.Individuals[id] = new Individual
        {
            Id = id,
            FirstName = firstName,
            LastName = lastName,
            Role = role,
            FactionId = factionId,
            LocationPortId = locationPort,
            HomePortId = locationPort,
            CurrentGold = gold,
            Personality = PersonalityTraits.Random(_rng),
        };

        // Set as port governor if role matches
        if (role == IndividualRole.Governor && locationPort.HasValue
            && _state.Ports.TryGetValue(locationPort.Value, out var port))
        {
            port.GovernorId = id;
        }

        return id;
    }

    // ---- Ships ----

    public ShipId AddShip(string name, FactionId? ownerFaction = null, IndividualId? captainId = null,
        PortId? dockedAt = null, int guns = 10, int crew = 30)
    {
        var id = new ShipId(Guid.NewGuid());
        ShipLocation location = dockedAt.HasValue
            ? new AtPort(dockedAt.Value)
            : new AtSea(_state.Regions.Keys.First());

        var ship = new Ship
        {
            Id = id,
            Name = name,
            Class = ShipClass.Brigantine,
            OwnerFactionId = ownerFaction,
            CaptainId = captainId,
            Location = location,
            MaxCargoTons = 100,
            MaxCrew = 50,
            Guns = guns,
            CurrentCrew = crew,
        };

        _state.Ships[id] = ship;

        if (dockedAt.HasValue && _state.Ports.TryGetValue(dockedAt.Value, out var port))
            port.DockedShips.Add(id);

        return id;
    }

    // ---- Knowledge ----

    public ScenarioBuilder SeedFact(KnowledgeHolderId holder, KnowledgeClaim claim,
        float confidence = 0.80f, KnowledgeSensitivity sensitivity = KnowledgeSensitivity.Public)
    {
        var fact = new KnowledgeFact
        {
            Claim = claim,
            Sensitivity = sensitivity,
            Confidence = confidence,
            BaseConfidence = confidence,
            ObservedDate = _state.Date,
        };
        _state.Knowledge.AddFact(holder, fact);

        // Apply relationship consequences when seeding deeds into individual holders
        if (holder is IndividualHolder ih && claim is IndividualActionClaim action)
        {
            var observerId = ih.Individual;
            if (observerId != action.ActorId && _state.Individuals.TryGetValue(observerId, out var observer))
            {
                var p = (float)action.Polarity;
                var s = (int)action.Severity / 100f;
                var c = confidence;
                var mTrait = action.Polarity == ActionPolarity.Hostile
                    ? 1.0f + (observer.Personality.Loyalty * 0.3f)
                    : 1.0f + (observer.Personality.Greed * -0.3f);
                var delta = p * s * c * mTrait;
                if (observer.FactionId.HasValue && action.Polarity == ActionPolarity.Hostile
                    && _state.Individuals.TryGetValue(action.TargetId, out var target)
                    && target.FactionId == observer.FactionId)
                    delta *= 1.5f;
                _state.AdjustRelationship(observerId, action.ActorId, delta);
                if (action.BeneficiaryId.HasValue)
                    _state.AdjustRelationship(observerId, action.BeneficiaryId.Value, delta * 0.5f);
            }
        }

        // Apply pardon effect when seeding into individual holder
        if (holder is IndividualHolder ih2 && claim is PardonClaim pardon)
        {
            var observerId2 = ih2.Individual;
            if (_state.Individuals.TryGetValue(observerId2, out var observer2)
                && observer2.FactionId == pardon.Faction)
            {
                var currentRel = _state.GetRelationship(observerId2, pardon.PardonedActor);
                if (currentRel < 0)
                    _state.AdjustRelationship(observerId2, pardon.PardonedActor, -currentRel * 0.5f * confidence);
            }
        }

        return this;
    }

    // ---- Relationships ----

    public ScenarioBuilder SetRelationship(IndividualId observer, IndividualId subject, float value)
    {
        _state.AdjustRelationship(observer, subject, value);
        return this;
    }

    // ---- Build ----

    /// <summary>Build the engine with deterministic RNG for reproducible tests.</summary>
    public SimulationEngine Build()
    {
        // Initialize weather for all regions
        foreach (var region in _state.Regions.Values)
        {
            _state.Weather[region.Id] = new RegionWeather
            {
                RegionId = region.Id,
                WindDirection = WindDirection.East,
                WindStrength = WindStrength.Moderate,
                StormPresence = StormPresence.None,
            };
        }

        return SimulationEngine.BuildEngine(_state, rng: new Random(_seed));
    }

    /// <summary>Build and return both engine and state for direct inspection.</summary>
    public (SimulationEngine Engine, WorldState State) BuildWithState()
    {
        var engine = Build();
        return (engine, _state);
    }
}
