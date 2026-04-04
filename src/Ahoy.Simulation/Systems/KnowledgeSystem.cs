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

    // Exponential decay: multiply each tick. ≈ e^(-0.015) ≈ 0.9851.
    // Matches old linear rate near 1.0 but produces asymptotic tail at low confidence,
    // so old rumours linger as faint noise rather than hard-flooring to zero.
    private const float DecayFactor         = 0.9851f;
    // Multiplicative hop penalty: 15% reduction per retelling.
    // A 0.20-confidence fact drops to 0.17, not 0.10 — stays above pruning floor.
    private const float HopPenaltyFraction  = 0.15f;
    // Sensitivity-gated propagation: Public/Disinformation spread freely, Restricted rarely, Secret never.
    // Eliminates the need for an active per-tick suppression pass (O(N) nightmare).
    // Use PropagationChanceFor(sensitivity) instead of this constant.
    private static float PropagationChanceFor(KnowledgeSensitivity sensitivity) => sensitivity switch
    {
        KnowledgeSensitivity.Public         => 0.30f,
        KnowledgeSensitivity.Restricted     => 0.10f,
        KnowledgeSensitivity.Secret         => 0.00f,   // never spreads passively
        KnowledgeSensitivity.Disinformation => 0.30f,   // false facts spread freely by design
        _                                   => 0.30f,
    };
    // Bayesian corroboration weight: diminishing-returns confidence update.
    private const float CorroborationWeight = 0.50f;

    public KnowledgeSystem(TickEventEmitter emitter, Random? rng = null)
    {
        _emitter = emitter;
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        var allEvents = _emitter.PeekAll();
        var tick = context.TickNumber;

        // 1. Convert events to facts held by appropriate parties
        foreach (var worldEvent in allEvents)
            IngestEvent(worldEvent, state, context, tick);

        // 2. Propagate facts via ships arriving in port this tick
        PropagateViaArrivingShips(state, context, tick);

        // 2b. Brokers passively absorb high-confidence port facts
        SkimBrokerFacts(state, tick);

        // 3. Generate first-hand observations when the player ship arrives at a port
        GeneratePlayerObservations(state, tick);

        // 4. Degrade confidence on all existing facts
        DegradeConfidence(state);

        // 4. Prune expired facts (keep superseded for one tick)
        state.Knowledge.PruneExpired(tick);
    }

    // ---- Event ingestion ----

    private static void IngestEvent(WorldEvent worldEvent, WorldState state, SimulationContext context, int tick)
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
                    BaseConfidence = baseConfidence,
                    ObservedDate = ps.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(ps.PortId), priceFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), priceFact, tick);
                break;

            case PortProsperityChanged pc:
                var prospFact = new KnowledgeFact
                {
                    Claim = new PortProsperityClaim(pc.PortId, pc.NewValue),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    BaseConfidence = baseConfidence,
                    ObservedDate = pc.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(pc.PortId), prospFact, tick);
                break;

            case PortCaptured capture:
                var controlFact = new KnowledgeFact
                {
                    Claim = new PortControlClaim(capture.PortId, capture.NewFaction),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    BaseConfidence = baseConfidence,
                    ObservedDate = capture.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(capture.PortId), controlFact, tick);
                AddAndSupersede(state.Knowledge, new FactionHolder(capture.NewFaction), controlFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), controlFact, tick);
                break;

            case ShipArrived sa:
                if (!state.Ships.TryGetValue(sa.ShipId, out _)) break;
                var shipLocFact = new KnowledgeFact
                {
                    Claim = new ShipLocationClaim(sa.ShipId, new AtPort(sa.PortId)),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    BaseConfidence = baseConfidence,
                    ObservedDate = sa.Date,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(sa.PortId), shipLocFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), shipLocFact, tick);
                break;

            case StormFormed sf:
                var weatherFact = new KnowledgeFact
                {
                    Claim = new WeatherClaim(sf.RegionId, Core.Enums.WindStrength.Gale, Core.Enums.StormPresence.Active),
                    Sensitivity = KnowledgeSensitivity.Public,
                    Confidence = baseConfidence,
                    BaseConfidence = baseConfidence,
                    ObservedDate = sf.Date,
                };
                if (state.Regions.TryGetValue(sf.RegionId, out var stormRegion))
                    foreach (var portId in stormRegion.Ports)
                        AddAndSupersede(state.Knowledge, new PortHolder(portId), weatherFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), weatherFact, tick);
                break;

            case IndividualMoved im:
                // Direct observation of an individual's movement.
                // Seeded ONLY into the departure and arrival port pools — not faction-wide.
                // This creates geographical information asymmetry: you must visit (or hear
                // from ships that visited) those ports to learn where they've gone.
                var movedFact = new KnowledgeFact
                {
                    Claim          = new IndividualWhereaboutsClaim(im.IndividualId, im.ToPort),
                    Sensitivity    = KnowledgeSensitivity.Public,
                    Confidence     = 0.90f,
                    BaseConfidence = 0.90f,
                    ObservedDate   = im.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                };
                AddAndSupersede(state.Knowledge, new PortHolder(im.FromPort), movedFact, tick);
                AddAndSupersede(state.Knowledge, new PortHolder(im.ToPort),   movedFact, tick);
                break;

            case IndividualDied ev:
                // Death notice — seeded into the port where the individual died.
                // Shares the "Individual:{id}" subject key with IndividualWhereaboutsClaim
                // so it automatically supersedes any live location claim for the same person.
                if (!state.Individuals.TryGetValue(ev.IndividualId, out var deceased)) break;
                var deathFact = new KnowledgeFact
                {
                    Claim          = new IndividualStatusClaim(ev.IndividualId, deceased.Role, deceased.FactionId, IsAlive: false),
                    Sensitivity    = KnowledgeSensitivity.Restricted,
                    Confidence     = 0.80f,
                    BaseConfidence = 0.80f,
                    ObservedDate   = ev.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                };
                if (deceased.LocationPortId.HasValue)
                    AddAndSupersede(state.Knowledge, new PortHolder(deceased.LocationPortId.Value), deathFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), deathFact, tick);
                break;

            case ShipDestroyed sd:
                // Ship destroyed in combat: knowledge goes into the attacker's ShipHolder.
                // It propagates to ports only when the attacker docks — realistic lag.
                // Weather destruction has no witnesses; the ship's location claim decays naturally.
                if (sd.AttackerId is not { } attackerShipId) break;
                var regionOfDestruction = sd.SourceLod == SimulationLod.Local && context.PlayerRegion.HasValue
                    ? context.PlayerRegion
                    : null;
                var sinkFact = new KnowledgeFact
                {
                    Claim          = new ShipStatusClaim(sd.ShipId, IsDestroyed: true, regionOfDestruction),
                    Sensitivity    = KnowledgeSensitivity.Public,
                    Confidence     = 0.75f,
                    BaseConfidence = 0.75f,
                    ObservedDate   = sd.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                };
                AddAndSupersede(state.Knowledge, new ShipHolder(attackerShipId), sinkFact, tick);
                if (sd.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), sinkFact, tick);
                break;
        }
    }

    private static void AddAndSupersede(KnowledgeStore store, KnowledgeHolderId holder, KnowledgeFact fact, int tick)
    {
        store.MarkSuperseded(holder, fact, tick);
        store.AddFact(holder, fact);
    }

    // ---- Propagation via ship arrivals ----

    private void PropagateViaArrivingShips(WorldState state, SimulationContext context, int tick)
    {
        foreach (var ship in state.Ships.Values.Where(s => s.ArrivedThisTick))
        {
            if (ship.Location is not AtPort atPort) continue;

            var portHolder = new PortHolder(atPort.Port);
            var shipHolder = new ShipHolder(ship.Id);

            // Captain (IndividualHolder) ↔ Port: all sensitivities.
            // The captain is the epistemic agent — they pick up strategic and secret intel,
            // and their personal accumulated knowledge informs routing decisions.
            if (ship.CaptainId.HasValue)
            {
                var captainHolder = new IndividualHolder(ship.CaptainId.Value);
                ShareFacts(captainHolder, portHolder, state, tick);
                ShareFacts(portHolder, captainHolder, state, tick);
            }

            // Crew collective (ShipHolder) ↔ Port: Public + Restricted + Disinformation only.
            // Crew absorb tavern gossip and spread rumours between ports, but don't handle secrets.
            ShareFacts(shipHolder, portHolder, state, tick, excludeSecrets: true);
            ShareFacts(portHolder, shipHolder, state, tick, excludeSecrets: true);
        }
    }

    /// <summary>
    /// Brokers passively absorb high-confidence facts from their current port.
    /// This keeps their inventory alive after the initial world-creation bootstrap.
    /// Only absorbs facts above the 0.65 confidence threshold to prevent accumulating noise.
    /// </summary>
    private static void SkimBrokerFacts(WorldState state, int tick)
    {
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive || individual.IsCompromised) continue;
            if (individual.Role != Core.Enums.IndividualRole.KnowledgeBroker) continue;
            if (!individual.LocationPortId.HasValue) continue;

            var portHolder = new PortHolder(individual.LocationPortId.Value);
            var brokerHolder = new IndividualHolder(individual.Id);
            var brokerFacts = state.Knowledge.GetFacts(brokerHolder);

            foreach (var fact in state.Knowledge.GetFacts(portHolder))
            {
                if (fact.IsSuperseded || fact.Confidence <= 0.65f) continue;

                var subjectKey = KnowledgeFact.GetSubjectKey(fact.Claim);
                var alreadyKnows = brokerFacts.Any(f =>
                    !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
                if (alreadyKnows) continue;

                var skimmed = new KnowledgeFact
                {
                    Claim = fact.Claim,
                    Sensitivity = fact.Sensitivity,
                    Confidence = fact.Confidence * (1f - HopPenaltyFraction),
                    BaseConfidence = fact.Confidence * (1f - HopPenaltyFraction),
                    ObservedDate = fact.ObservedDate,
                    IsDisinformation = fact.IsDisinformation,
                    HopCount = fact.HopCount + 1,
                    SourceHolder = portHolder,
                    OriginatingAgentId = fact.OriginatingAgentId,
                };
                state.Knowledge.AddFact(brokerHolder, skimmed);
            }
        }
    }

    private void ShareFacts(KnowledgeHolderId from, KnowledgeHolderId to, WorldState state, int tick,
        bool excludeSecrets = false)
    {
        var sourceFacts = state.Knowledge.GetFacts(from);
        foreach (var fact in sourceFacts)
        {
            if (fact.IsSuperseded) continue;   // don't gossip stale beliefs
            if (excludeSecrets && fact.Sensitivity == KnowledgeSensitivity.Secret) continue;
            var propChance = PropagationChanceFor(fact.Sensitivity);
            if (propChance == 0f || _rng.NextDouble() > propChance) continue;

            // Multiplicative hop penalty scaled by source reliability
            var reliability = state.Knowledge.GetSourceReliability(from);
            var newConfidence = fact.Confidence * (1f - HopPenaltyFraction) * reliability;
            if (newConfidence <= 0.10f) continue;

            var subjectKey = KnowledgeFact.GetSubjectKey(fact.Claim);
            var existing = state.Knowledge.GetFacts(to)
                .FirstOrDefault(f => !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);

            if (existing is null)
            {
                // Holder does not yet know about this subject — create new propagated fact
                var propagated = new KnowledgeFact
                {
                    Claim = fact.Claim,
                    Sensitivity = fact.Sensitivity,
                    Confidence = newConfidence,
                    BaseConfidence = newConfidence,
                    ObservedDate = fact.ObservedDate,
                    IsDisinformation = fact.IsDisinformation,
                    HopCount = fact.HopCount + 1,
                    SourceHolder = from,
                    OriginatingAgentId = fact.OriginatingAgentId,
                };
                AddAndSupersede(state.Knowledge, to, propagated, tick);
            }
            else if (existing.Claim == fact.Claim)
            {
                // Same claim — corroborate if from a different source
                if (!Equals(existing.SourceHolder, from))
                {
                    existing.CorroborationCount++;
                    // Faction-origin guard: a faction's own echo chamber (multiple ports
                    // all controlled by the same faction) can only boost confidence once.
                    // Player / direct witness (null faction) always corroborates.
                    var fromFaction = ResolveFactionId(from, state);
                    var isNewFaction = fromFaction is null || existing.CorroboratingFactionIds.Add(fromFaction.Value);
                    if (isNewFaction)
                    {
                        // Bayesian update: C_new = C_old + W * C_incoming * (1 - C_old)
                        // Gives diminishing returns — can't rumour-chain to 1.0
                        existing.Confidence = Math.Min(0.95f,
                            existing.Confidence + CorroborationWeight * newConfidence * (1f - existing.Confidence));
                    }
                }
                // Same source retelling → skip
            }
            else
            {
                // Different claim, same subject — contradiction
                // Create the contradicting fact; KnowledgeStore.AddFact will register the conflict
                var contradicting = new KnowledgeFact
                {
                    Claim = fact.Claim,
                    Sensitivity = fact.Sensitivity,
                    Confidence = newConfidence,
                    BaseConfidence = newConfidence,
                    ObservedDate = fact.ObservedDate,
                    IsDisinformation = fact.IsDisinformation,
                    HopCount = fact.HopCount + 1,
                    SourceHolder = from,
                    OriginatingAgentId = fact.OriginatingAgentId,
                };
                var isNewConflict = state.Knowledge.AddFact(to, contradicting); // no supersession — both live
                if (isNewConflict)
                    _emitter.Emit(
                        new KnowledgeConflictDetected(state.Date, SimulationLod.Local,
                            subjectKey, existing.Id, contradicting.Id, to),
                        SimulationLod.Local);
            }
        }
    }

    // ---- Passive player investigation (arrival at port) ----

    /// <summary>
    /// When the player ship docks, generate direct-observation (HopCount=0, SourceHolder=null)
    /// facts for the port's current real state. These facts are authoritative and will
    /// supersede any stale or disinformation-seeded beliefs the player held.
    /// Any existing player facts that are superseded by these observations feed back
    /// into source reputation tracking.
    /// </summary>
    private static void GeneratePlayerObservations(WorldState state, int tick)
    {
        var playerShip = state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
        if (playerShip is null || !playerShip.ArrivedThisTick) return;
        if (playerShip.Location is not AtPort atPort) return;
        if (!state.Ports.TryGetValue(atPort.Port, out var port)) return;

        const float ObservationConfidence = 0.90f;
        var player = new PlayerHolder();

        // --- Port control: what faction actually controls this port? ---
        var controlClaim = new PortControlClaim(port.Id, port.ControllingFactionId);
        var controlFact = new KnowledgeFact
        {
            Claim = new PortControlClaim(port.Id, port.ControllingFactionId),
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = ObservationConfidence,
            BaseConfidence = ObservationConfidence,
            ObservedDate = state.Date,
            HopCount = 0,
            SourceHolder = null,
        };
        RecordOutcomeForSuperseded(state.Knowledge, player, controlFact);
        AddAndSupersede(state.Knowledge, player, controlFact, tick);

        // --- Port prices: actual effective prices for each good ---
        foreach (var (good, _) in port.Economy.Supply)
        {
            var price = port.Economy.EffectivePrice(good);
            var priceFact = new KnowledgeFact
            {
                Claim = new PortPriceClaim(port.Id, good, price),
                Sensitivity = KnowledgeSensitivity.Public,
                Confidence = ObservationConfidence,
                BaseConfidence = ObservationConfidence,
                ObservedDate = state.Date,
                HopCount = 0,
                SourceHolder = null,
            };
            RecordOutcomeForSuperseded(state.Knowledge, player, priceFact);
            AddAndSupersede(state.Knowledge, player, priceFact, tick);
        }
    }

    /// <summary>
    /// Before superseding player facts with a new observation, check if the player's
    /// existing belief about the same subject was accurate. Records the outcome against
    /// that belief's SourceHolder for reputation tracking.
    /// </summary>
    private static void RecordOutcomeForSuperseded(KnowledgeStore store, KnowledgeHolderId holder,
        KnowledgeFact incoming)
    {
        var subjectKey = KnowledgeFact.GetSubjectKey(incoming.Claim);
        var existing = store.GetFacts(holder)
            .FirstOrDefault(f => !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
        if (existing?.SourceHolder is null) return;

        var wasAccurate = existing.Claim == incoming.Claim;
        store.RecordSourceOutcome(existing.SourceHolder, wasAccurate);
    }

    // ---- Confidence decay ----

    private static void DegradeConfidence(WorldState state)
    {
        foreach (var fact in state.Knowledge.GetAllFacts())
        {
            if (fact.IsDecayExempt) continue;   // player's own action ledger never fades
            fact.Confidence = Math.Max(0f, fact.Confidence * DecayFactor);
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Resolve a KnowledgeHolderId to the FactionId that controls it, if any.
    /// Used to gate corroboration: a single faction's echo chamber should only
    /// boost confidence once, regardless of how many ports they control.
    /// </summary>
    private static FactionId? ResolveFactionId(KnowledgeHolderId holder, WorldState state) => holder switch
    {
        FactionHolder fh                                               => fh.Faction,
        PortHolder ph when state.Ports.TryGetValue(ph.Port, out var p) => p.ControllingFactionId,
        _                                                              => null,
    };

    private static float LodToConfidence(SimulationLod lod) => lod switch
    {
        SimulationLod.Local    => 0.95f,
        SimulationLod.Regional => 0.65f,
        SimulationLod.Distant  => 0.35f,
        _ => 0.35f,
    };
}
