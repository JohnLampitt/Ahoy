using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 6 — runs last each tick.
/// Converts events from this tick into KnowledgeFacts, propagates facts between
/// adjacent ships and ports, degrades confidence over time, prunes expired facts,
/// and handles knowledge trading mechanics (broker availability).
///
/// BaseConfidence is derived from the event's SourceLod:
///   Local    → 0.95
///   Regional → 0.65
///   Distant  → 0.35
/// </summary>
public sealed class KnowledgeSystem : IWorldSystem
{
    private readonly TickEventEmitter _emitter;
    private readonly Random _rng;

    private const float ConfidenceDecayPerTick = 0.015f;
    private const float HopConfidencePenalty   = 0.10f;
    private const float PropagationChance      = 0.30f;

    public KnowledgeSystem(TickEventEmitter emitter, Random? rng = null)
    {
        _emitter = emitter;
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        var allEvents = _emitter.PeekAll();

        // 1. Convert events to facts held by appropriate parties
        foreach (var worldEvent in allEvents)
            IngestEvent(worldEvent, state, context);

        // 2. Propagate facts via ships arriving in port this tick
        PropagateViaArrivingShips(state, context);

        // 3. Degrade confidence on all existing facts
        DegradeConfidence(state);

        // 4. Prune expired facts
        state.Knowledge.PruneExpired();
    }

    // ---- Event ingestion ----

    private static void IngestEvent(WorldEvent worldEvent, WorldState state, SimulationContext context)
    {
        var baseConfidence = LodToConfidence(worldEvent.SourceLod);

        switch (worldEvent)
        {
            case PriceShifted ps:
                var priceFact = new KnowledgeFact
                {
                    Claim = new PortPriceClaim(ps.PortId, ps.Good, ps.NewPrice),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    ObservedDate = ps.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(ps.PortId), priceFact);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), priceFact);
                break;

            case PortProsperityChanged pc:
                var prospFact = new KnowledgeFact
                {
                    Claim = new PortProsperityClaim(pc.PortId, pc.NewValue),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    ObservedDate = pc.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(pc.PortId), prospFact);
                break;

            case PortCaptured capture:
                var controlFact = new KnowledgeFact
                {
                    Claim = new PortControlClaim(capture.PortId, capture.NewFaction),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    ObservedDate = capture.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(capture.PortId), controlFact);
                AddAndSupersede(state.Knowledge, new FactionHolder(capture.NewFaction), controlFact);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), controlFact);
                break;

            case ShipArrived sa:
                if (!state.Ships.TryGetValue(sa.ShipId, out _)) break;
                var shipLocFact = new KnowledgeFact
                {
                    Claim = new ShipLocationClaim(sa.ShipId, new AtPort(sa.PortId)),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    ObservedDate = sa.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(sa.PortId), shipLocFact);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), shipLocFact);
                break;

            case StormFormed sf:
                var weatherFact = new KnowledgeFact
                {
                    Claim = new WeatherClaim(sf.RegionId, Core.Enums.WindStrength.Gale, Core.Enums.StormPresence.Active),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    ObservedDate = sf.Date,
                };
                if (state.Regions.TryGetValue(sf.RegionId, out var stormRegion))
                    foreach (var portId in stormRegion.Ports)
                        AddAndSupersede(state.Knowledge, new PortHolder(portId), weatherFact);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), weatherFact);
                break;
        }
    }

    private static void AddAndSupersede(KnowledgeStore store, KnowledgeHolderId holder, KnowledgeFact fact)
    {
        store.MarkSuperseded(holder, fact);
        store.AddFact(holder, fact);
    }

    // ---- Propagation via ship arrivals ----

    private void PropagateViaArrivingShips(WorldState state, SimulationContext context)
    {
        foreach (var ship in state.Ships.Values.Where(s => s.ArrivedThisTick))
        {
            if (ship.Location is not AtPort atPort) continue;

            var shipHolder = new ShipHolder(ship.Id);
            var portHolder = new PortHolder(atPort.Port);

            // Ship gossips its knowledge to the port
            ShareFacts(shipHolder, portHolder, state, ship.Id.ToString());

            // Port gossips back to the ship
            ShareFacts(portHolder, shipHolder, state, atPort.Port.ToString());
        }
    }

    private void ShareFacts(KnowledgeHolderId from, KnowledgeHolderId to,
        WorldState state, string contextId)
    {
        var sourceFacts = state.Knowledge.GetFacts(from);
        foreach (var fact in sourceFacts)
        {
            if (_rng.NextDouble() > PropagationChance) continue;

            var propagated = new KnowledgeFact
            {
                Claim = fact.Claim,
                Sensitivity = fact.Sensitivity,
                Confidence = Math.Max(0f, fact.Confidence - HopConfidencePenalty),
                ObservedDate = fact.ObservedDate,
                IsDisinformation = fact.IsDisinformation,
                HopCount = fact.HopCount + 1,
            };

            // Only propagate if degraded confidence is still meaningful
            if (propagated.Confidence > 0.10f)
                AddAndSupersede(state.Knowledge, to, propagated);
        }
    }

    // ---- Confidence decay ----

    private static void DegradeConfidence(WorldState state)
    {
        foreach (var fact in state.Knowledge.GetAllFacts())
            fact.Confidence = Math.Max(0f, fact.Confidence - ConfidenceDecayPerTick);
    }

    private static float LodToConfidence(SimulationLod lod) => lod switch
    {
        SimulationLod.Local    => 0.95f,
        SimulationLod.Regional => 0.65f,
        SimulationLod.Distant  => 0.35f,
        _ => 0.35f,
    };
}
