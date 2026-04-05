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

        // 3a. Generate first-hand observations when the player ship arrives at a port
        GeneratePlayerObservations(state, tick);

        // 3b. Passive gossip: player absorbs port facts while docked (even without arriving this tick)
        AbsorbPortGossip(state, tick);

        // 4. Degrade confidence on all existing facts
        DegradeConfidence(state);

        // 4. Prune expired facts (keep superseded for one tick)
        state.Knowledge.PruneExpired(tick);

        // 5. Detect and supersede disinformation contradicted by high-confidence truth facts
        DetectDisinformationContradictions(state, tick);

        // 6. 5C: Auto-resolve knowledge conflicts where spread > 0.40
        AutoResolveConflicts(state, context, events, tick);

        // 7. Group 7: Seed PortConditionClaims for active crises
        SeedPortConditionClaims(state, tick);
    }

    // ---- Group 7: Port condition knowledge seeding ----

    private static void SeedPortConditionClaims(WorldState state, int tick)
    {
        foreach (var port in state.Ports.Values)
        {
            if (port.Conditions == PortConditionFlags.None) continue;

            var claim = new PortConditionClaim(port.Id, port.Conditions);
            var fact = new KnowledgeFact
            {
                Claim = claim,
                Sensitivity = KnowledgeSensitivity.Public,
                Confidence = 0.90f,
                BaseConfidence = 0.90f,
                ObservedDate = state.Date,
                HopCount = 0,
            };

            // Seed into the port's own holder — propagates via arriving ships
            var holder = new PortHolder(port.Id);
            var existing = state.Knowledge.GetFacts(holder)
                .Any(f => !f.IsSuperseded
                    && f.Claim is PortConditionClaim pcc
                    && pcc.Port == port.Id
                    && pcc.Condition == port.Conditions);
            if (!existing)
                state.Knowledge.AddFact(holder, fact);
        }
    }

    // ---- Event ingestion ----

    private void IngestEvent(WorldEvent worldEvent, WorldState state, SimulationContext context, int tick)
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
                // Use ClaimedFactionId as public-facing faction for infiltrators
                var publicFactionId = deceased.ClaimedFactionId ?? deceased.FactionId;
                var deathFact = new KnowledgeFact
                {
                    Claim          = new IndividualStatusClaim(ev.IndividualId, deceased.Role, publicFactionId, IsAlive: false),
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

                // If deceased was an infiltrator and player witnessed it locally, reveal actual allegiance
                if (worldEvent.SourceLod == SimulationLod.Local && deceased.IsInfiltrator)
                {
                    var deathRevealClaim = new IndividualAllegianceClaim(
                        deceased.Id,
                        ClaimedFaction: deceased.ClaimedFactionId!.Value,
                        ActualFaction:  deceased.FactionId ?? default);
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), new KnowledgeFact
                    {
                        Claim = deathRevealClaim,
                        Sensitivity = KnowledgeSensitivity.Secret,
                        Confidence = 0.95f, BaseConfidence = 0.95f,
                        ObservedDate = ev.Date, IsDecayExempt = true,
                        HopCount = 0, SourceHolder = null,
                    }, tick);
                    _emitter.Emit(new AllegianceRevealed(
                        state.Date, SimulationLod.Local,
                        deceased.Id,
                        ActualFaction:  deceased.FactionId!.Value,
                        ClaimedFaction: deceased.ClaimedFactionId!.Value), SimulationLod.Local);
                }
                break;

            case PoiDiscovered pd:
            {
                if (!state.OceanPois.TryGetValue(pd.PoiId, out var poi2)) break;
                // Use DeriveStatus() — never embed raw gold amounts in gossip
                var claim = new OceanPoiClaim(poi2.Id, poi2.RegionId, poi2.Type,
                    poi2.IsDiscovered, poi2.DeriveStatus());
                var sensitivity = KnowledgeSensitivity.Public;
                var conf = worldEvent.SourceLod == SimulationLod.Local ? 0.95f
                         : worldEvent.SourceLod == SimulationLod.Regional ? 0.65f : 0.35f;

                var poiFact = new KnowledgeFact
                {
                    Claim = claim, Sensitivity = sensitivity,
                    Confidence = conf, BaseConfidence = conf,
                    ObservedDate = pd.Date, HopCount = 0, SourceHolder = null,
                };

                AddAndSupersede(state.Knowledge, new ShipHolder(pd.DiscoveredBy), poiFact, tick);
                if (worldEvent.SourceLod == SimulationLod.Local)
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), poiFact, tick);

                // Seed to nearest port in the POI's region at reduced confidence (Regional gossip)
                var nearestPort = state.Ports.Values
                    .Where(p => p.RegionId == poi2.RegionId)
                    .FirstOrDefault();
                if (nearestPort is not null)
                {
                    var portConf = conf * 0.6f;
                    var portPoiFact = new KnowledgeFact
                    {
                        Claim = claim, Sensitivity = KnowledgeSensitivity.Public,
                        Confidence = portConf, BaseConfidence = portConf,
                        ObservedDate = pd.Date, HopCount = 1,
                        SourceHolder = new ShipHolder(pd.DiscoveredBy),
                    };
                    AddAndSupersede(state.Knowledge, new PortHolder(nearestPort.Id), portPoiFact, tick);
                }
                break;
            }

            case PoiEncountered pe:
            {
                if (!state.OceanPois.TryGetValue(pe.PoiId, out var poi3)) break;
                var updatedClaim = new OceanPoiClaim(poi3.Id, poi3.RegionId, poi3.Type,
                    poi3.IsDiscovered, poi3.DeriveStatus());

                // Player sees it directly at full confidence
                if (worldEvent.SourceLod == SimulationLod.Local)
                {
                    AddAndSupersede(state.Knowledge, new PlayerHolder(), new KnowledgeFact
                    {
                        Claim = updatedClaim, Sensitivity = KnowledgeSensitivity.Public,
                        Confidence = 0.95f, BaseConfidence = 0.95f,
                        ObservedDate = pe.Date, HopCount = 0, SourceHolder = null,
                    }, tick);
                }

                // NPC encounter: seed a Regional-confidence rumour to nearby ports
                // regardless of LOD. This is how players hear "the cache was raided"
                // before sailing there themselves. Only seed loot/damage outcomes.
                if (pe.GoldFound > 0 || pe.HullDamage > 0f)
                {
                    var gossipConf = worldEvent.SourceLod == SimulationLod.Local ? 0.70f : 0.40f;
                    var nearestGossipPort = state.Ports.Values
                        .Where(p => p.RegionId == poi3.RegionId)
                        .FirstOrDefault();
                    if (nearestGossipPort is not null)
                    {
                        AddAndSupersede(state.Knowledge, new PortHolder(nearestGossipPort.Id), new KnowledgeFact
                        {
                            Claim = updatedClaim, Sensitivity = KnowledgeSensitivity.Public,
                            Confidence = gossipConf, BaseConfidence = gossipConf,
                            ObservedDate = pe.Date, HopCount = 1,
                            SourceHolder = new ShipHolder(pe.ShipId),
                        }, tick);
                    }
                }
                break;
            }

            // 5E-1: RouteHazardClaim seeding — storm damage seeds hazard into affected ships
            case StormPropagated sp:
                SeedRouteHazardForRegion(sp.ToRegion, "Active storm — dangerous seas", state, sp.Date, baseConfidence, tick);
                break;

            // 5E-1: PatrolEngaged seeds pirate encounter hazard into surviving ship
            case PatrolEngaged pe:
                var peRegion = pe.RegionId;
                SeedRouteHazardForShip(pe.TargetShip, peRegion, "Naval patrol — hostile waters", state, pe.Date, baseConfidence, tick);
                SeedRouteHazardForShip(pe.PatrolShip, peRegion, "Pirate encounter", state, pe.Date, baseConfidence, tick);
                break;

            // 5E-1: ShipRaided seeds pirate hazard into victim's ship
            case ShipRaided sr:
            {
                var raidRegion = state.GetShipRegion(sr.TargetShipId);
                if (raidRegion.HasValue)
                    SeedRouteHazardForShip(sr.TargetShipId, raidRegion.Value, "Pirate raid — dangerous route", state, sr.Date, baseConfidence, tick);

                // 6A: Deed — raider attacked victim
                var raiderCaptain = GetCaptainId(sr.AttackerShipId, state);
                var raidVictimCaptain = GetCaptainId(sr.TargetShipId, state);
                if (raiderCaptain.HasValue && raidVictimCaptain.HasValue)
                    SeedDeed(raiderCaptain.Value, raidVictimCaptain.Value, null,
                        ActionPolarity.Hostile, ActionSeverity.Significant,
                        "Raided merchant vessel", state, sr.Date, worldEvent.SourceLod, tick);
                break;
            }

            case ShipDestroyed sd:
                // Ship destroyed in combat: knowledge goes into the attacker's ShipHolder.
                // It propagates to ports only when the attacker docks — realistic lag.
                // Weather destruction has no witnesses; the ship's location claim decays naturally.
                if (sd.AttackerId is not { } attackerShipId) break;

                // 6A: Deed — attacker destroyed target
                var destroyerCaptain = GetCaptainId(attackerShipId, state);
                var destroyedCaptain = GetCaptainId(sd.ShipId, state);
                if (destroyerCaptain.HasValue && destroyedCaptain.HasValue)
                    SeedDeed(destroyerCaptain.Value, destroyedCaptain.Value, null,
                        ActionPolarity.Hostile, ActionSeverity.Severe,
                        "Destroyed vessel in combat", state, sd.Date, worldEvent.SourceLod, tick);
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

            // 6A: Deed seeding for social/economic events
            case BribeAccepted ba:
                // Player bribed a governor — friendly deed toward governor
                if (state.Player.CaptainIndividualId is { } playerId1)
                    SeedDeed(playerId1, ba.GovernorId, null,
                        ActionPolarity.Friendly, ActionSeverity.Nuisance,
                        "Paid bribe", state, ba.Date, worldEvent.SourceLod, tick);
                break;

            case AgentBurned ab:
                // Burning an agent — hostile deed
                if (state.Player.CaptainIndividualId is { } playerId2)
                    SeedDeed(playerId2, ab.AgentId, null,
                        ActionPolarity.Hostile, ActionSeverity.Severe,
                        "Burned intelligence agent", state, ab.Date, worldEvent.SourceLod, tick);
                break;

            case ContractFulfilled cf:
                // Fulfilling a contract — friendly deed toward the issuer
                if (state.Player.CaptainIndividualId is { } playerId3)
                    SeedDeed(playerId3, cf.IssuerId, null,
                        ActionPolarity.Friendly, ActionSeverity.Significant,
                        "Fulfilled contract", state, cf.Date, worldEvent.SourceLod, tick);
                break;

            case NpcClaimedContract ncc:
                // NPC fulfilled a contract — friendly deed toward issuer
                // Find the issuer from the contract claim
                SeedDeed(ncc.NpcId, ncc.NpcId, null, // target = self for now, issuer resolved later
                    ActionPolarity.Friendly, ActionSeverity.Significant,
                    "Claimed contract bounty", state, ncc.Date, worldEvent.SourceLod, tick);
                break;
        }
    }

    // ---- 5E-1: RouteHazardClaim seeding helpers ----

    private static void SeedRouteHazardForRegion(
        RegionId regionId, string description, WorldState state, WorldDate date, float confidence, int tick)
    {
        // Seed into all ships currently in the affected region
        foreach (var ship in state.Ships.Values)
        {
            var shipRegion = state.GetShipRegion(ship.Id);
            if (shipRegion != regionId) continue;
            SeedRouteHazardForShip(ship.Id, regionId, description, state, date, confidence, tick);
        }
    }

    private static void SeedRouteHazardForShip(
        ShipId shipId, RegionId regionId, string description, WorldState state, WorldDate date, float confidence, int tick)
    {
        if (!state.Ships.ContainsKey(shipId)) return;

        // RouteHazardClaim uses From/To — we use regionId for both since the hazard
        // is at a point, not a specific route. On dock, this propagates to port naturally.
        var hazardFact = new KnowledgeFact
        {
            Claim = new RouteHazardClaim(regionId, regionId, description),
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = confidence,
            BaseConfidence = confidence,
            ObservedDate = date,
            HopCount = 0,
            SourceHolder = null,
        };
        AddAndSupersede(state.Knowledge, new ShipHolder(shipId), hazardFact, tick);
    }

    private static void AddAndSupersede(KnowledgeStore store, KnowledgeHolderId holder, KnowledgeFact fact, int tick)
    {
        store.MarkSuperseded(holder, fact, tick);
        store.AddFact(holder, fact);
    }

    // ---- 6A: IndividualActionClaim seeding ----

    /// <summary>
    /// Seed a deed record into the witnesses' knowledge pools.
    /// Witnesses = ships in the region + ports in the region + player (if local).
    /// </summary>
    private void SeedDeed(
        IndividualId actorId, IndividualId targetId, IndividualId? beneficiaryId,
        ActionPolarity polarity, ActionSeverity severity, string context,
        WorldState state, WorldDate date, SimulationLod lod, int tick)
    {
        var claim = new IndividualActionClaim(actorId, targetId, beneficiaryId, polarity, severity, context);
        var fact = new KnowledgeFact
        {
            Claim = claim,
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = LodToConfidence(lod),
            BaseConfidence = LodToConfidence(lod),
            ObservedDate = date,
            HopCount = 0,
            SourceHolder = null,
        };

        // Seed into actor's own holder (they know what they did)
        state.Knowledge.AddFact(new IndividualHolder(actorId), fact);
        ApplyRelationshipConsequences(actorId, fact, state);

        // Seed into target's holder (they know what happened to them)
        state.Knowledge.AddFact(new IndividualHolder(targetId), fact);
        ApplyRelationshipConsequences(targetId, fact, state);

        // Seed into port if either actor or target is at a port
        var actorPort = state.Individuals.TryGetValue(actorId, out var actorInd) ? actorInd.LocationPortId : null;
        var targetPort = state.Individuals.TryGetValue(targetId, out var targetInd) ? targetInd.LocationPortId : null;
        if (actorPort.HasValue)
            state.Knowledge.AddFact(new PortHolder(actorPort.Value), fact);
        if (targetPort.HasValue && targetPort != actorPort)
            state.Knowledge.AddFact(new PortHolder(targetPort.Value), fact);

        // Player sees local deeds
        if (lod == SimulationLod.Local)
            state.Knowledge.AddFact(new PlayerHolder(), fact);
    }

    /// <summary>Resolve a ShipId to its captain's IndividualId, if any.</summary>
    private static IndividualId? GetCaptainId(ShipId shipId, WorldState state) =>
        state.Ships.TryGetValue(shipId, out var ship) ? ship.CaptainId : null;

    // ---- 6C: Consequence Math ----

    /// <summary>
    /// When an IndividualActionClaim reaches an individual's knowledge, they adjust
    /// their relationship with the actor (and optionally the beneficiary).
    ///
    /// Δ = P × S × C × M_trait
    ///   P: ActionPolarity (+1/-1)
    ///   S: Severity weight normalised to 0..1
    ///   C: Fact confidence (distant rumours carry less weight)
    ///   M_trait: Personality modifier
    /// </summary>
    private static void ApplyRelationshipConsequences(
        IndividualId observerId, KnowledgeFact fact, WorldState state)
    {
        if (fact.Claim is not IndividualActionClaim action) return;
        if (observerId == action.ActorId) return; // don't judge yourself

        if (!state.Individuals.TryGetValue(observerId, out var observer)) return;

        var p = (float)action.Polarity;
        var s = (int)action.Severity / 100f;
        var c = fact.Confidence;

        // Personality modifier
        float mTrait;
        if (action.Polarity == ActionPolarity.Hostile)
        {
            // Loyal NPCs hold grudges harder
            mTrait = 1.0f + (observer.Personality.Loyalty * 0.3f);
        }
        else
        {
            // Principled NPCs (negative Greed) value kind acts more
            mTrait = 1.0f + (observer.Personality.Greed * -0.3f);
        }

        var delta = p * s * c * mTrait;

        // Factional loyalty multiplier: if observer shares faction with target, they care more
        if (observer.FactionId.HasValue && action.Polarity == ActionPolarity.Hostile)
        {
            if (state.Individuals.TryGetValue(action.TargetId, out var target)
                && target.FactionId == observer.FactionId)
            {
                delta *= 1.5f; // "He attacked one of ours"
            }
        }

        // Apply to actor
        state.AdjustRelationship(observerId, action.ActorId, delta);

        // Beneficiary gets half the delta
        if (action.BeneficiaryId.HasValue && action.BeneficiaryId.Value != action.ActorId)
            state.AdjustRelationship(observerId, action.BeneficiaryId.Value, delta * 0.5f);

        // Also update legacy Individual.PlayerRelationship for backward compatibility
        if (action.ActorId == state.Player.CaptainIndividualId)
            observer.PlayerRelationship = Math.Clamp(observer.PlayerRelationship + delta, -100f, 100f);
    }

    // ---- 6E: Pardon effect ----

    /// <summary>
    /// When a PardonClaim reaches an individual of the pardoning faction,
    /// shift their relationship with the pardoned actor 50% toward zero.
    /// </summary>
    private static void ApplyPardonEffect(IndividualId observerId, KnowledgeFact fact, WorldState state)
    {
        if (fact.Claim is not PardonClaim pardon) return;
        if (!state.Individuals.TryGetValue(observerId, out var observer)) return;

        // Only affects NPCs of the pardoning faction
        if (observer.FactionId != pardon.Faction) return;

        var currentRel = state.GetRelationship(observerId, pardon.PardonedActor);
        if (currentRel >= 0) return; // no hostility to pardon

        // Shift 50% toward zero, scaled by confidence
        var shift = -currentRel * 0.5f * fact.Confidence;
        state.AdjustRelationship(observerId, pardon.PardonedActor, shift);

        // Legacy compat
        if (pardon.PardonedActor == state.Player.CaptainIndividualId)
            observer.PlayerRelationship = Math.Clamp(observer.PlayerRelationship + shift, -100f, 100f);
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

                // 6C: Apply relationship consequences when gossip reaches an individual
                if (to is IndividualHolder ih)
                {
                    ApplyRelationshipConsequences(ih.Individual, propagated, state);
                    ApplyPardonEffect(ih.Individual, propagated, state);
                }
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
    /// <summary>
    /// While the player is docked, they passively hear tavern gossip.
    /// Each tick, each Public fact in the port's knowledge pool has a 20% chance
    /// of reaching the player. This fires even when ArrivedThisTick is false,
    /// ensuring the player learns about ContractClaims seeded after they arrived.
    /// </summary>
    private void AbsorbPortGossip(WorldState state, int tick)
    {
        var playerShip = state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
        if (playerShip?.Location is not AtPort atPort) return;

        var portHolder = new PortHolder(atPort.Port);
        var playerHolder = new PlayerHolder();
        var playerFacts = state.Knowledge.GetFacts(playerHolder);

        foreach (var fact in state.Knowledge.GetFacts(portHolder))
        {
            if (fact.IsSuperseded || fact.Sensitivity == KnowledgeSensitivity.Secret) continue;
            var propChance = PropagationChanceFor(fact.Sensitivity) * 0.65f; // 65% of normal rate
            if (_rng.NextDouble() > propChance) continue;

            var subjectKey = KnowledgeFact.GetSubjectKey(fact.Claim);
            var existing = playerFacts.FirstOrDefault(f =>
                !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
            // Only absorb if player doesn't know this at equal or better confidence
            if (existing != null && existing.Confidence >= fact.Confidence) continue;

            var reliability = state.Knowledge.GetSourceReliability(portHolder);
            var gossip = new KnowledgeFact
            {
                Claim = fact.Claim,
                Sensitivity = fact.Sensitivity,
                Confidence = fact.Confidence * (1f - HopPenaltyFraction) * reliability,
                BaseConfidence = fact.Confidence * (1f - HopPenaltyFraction) * reliability,
                ObservedDate = fact.ObservedDate,
                IsDisinformation = fact.IsDisinformation,
                HopCount = fact.HopCount + 1,
                SourceHolder = portHolder,
                OriginatingAgentId = fact.OriginatingAgentId,
            };
            if (existing != null)
                state.Knowledge.MarkSuperseded(playerHolder, gossip, tick);
            state.Knowledge.AddFact(playerHolder, gossip);
        }
    }

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

    /// <summary>
    /// Scans every holder for disinformation facts that are now directly contradicted
    /// by a non-disinformation fact with higher confidence (> 0.70) for the same subject key.
    /// When detected, supersedes the lie and triggers FactionStimulus.
    /// </summary>
    private void DetectDisinformationContradictions(WorldState state, int tick)
    {
        var byHolderAndSubject = state.Knowledge.GetAllFactsWithHolders()
            .Where(t => !t.Fact.IsSuperseded)
            .GroupBy(t => (t.Holder, Key: KnowledgeFact.GetSubjectKey(t.Fact.Claim)));

        foreach (var group in byHolderAndSubject)
        {
            var facts = group.ToList();
            var disinfoFacts = facts.Where(t => t.Fact.IsDisinformation).ToList();
            if (disinfoFacts.Count == 0) continue;

            var truthFacts = facts
                .Where(t => !t.Fact.IsDisinformation && t.Fact.Confidence > 0.70f)
                .ToList();
            if (truthFacts.Count == 0) continue;

            foreach (var (holder, disFact) in disinfoFacts)
            {
                bool contradicted = truthFacts.Any(t =>
                    t.Fact.Claim.GetType() != disFact.Claim.GetType()
                    || !t.Fact.Claim.Equals(disFact.Claim));
                if (!contradicted) continue;

                state.Knowledge.MarkSuperseded(holder, disFact, tick);

                if (disFact.SourceHolder is not null)
                    state.Knowledge.RecordSourceOutcome(disFact.SourceHolder, wasAccurate: false);

                FactionId? deceiverFactionId = (disFact.SourceHolder as FactionHolder)?.Faction;
                if (!deceiverFactionId.HasValue) continue;

                bool playerOrigin = disFact.OriginatingAgentId is null;
                state.PendingFactionStimuli.Enqueue(new FactionStimulus
                {
                    FactionId    = deceiverFactionId.Value,
                    StimulusType = "DeceptionExposed",
                    Magnitude    = 0.3f,
                    Description  = "Disinformation organically contradicted and superseded",
                    PlayerIsOrigin = playerOrigin,
                });

                _emitter.Emit(new DeceptionExposed(
                    state.Date, SimulationLod.Local,
                    deceiverFactionId.Value, null),
                    SimulationLod.Local);
            }
        }
    }

    // ---- 5C: Conflict auto-resolution ----

    /// <summary>
    /// Auto-resolve conflicts where the dominant fact leads by > 0.40 confidence.
    /// The weaker fact is discredited noise — supersede it to prevent tedious manual investigation.
    /// Close conflicts (spread < 0.40) remain for the player to investigate.
    /// </summary>
    private void AutoResolveConflicts(WorldState state, SimulationContext context,
        IEventEmitter events, int tick)
    {
        foreach (var (holder, conflict) in state.Knowledge.GetAllConflicts().ToList())
        {
            if (conflict.IsResolved) continue;
            if (conflict.ConfidenceSpread <= 0.40f) continue;

            var dominant = conflict.DominantFact;
            if (dominant is null) continue;

            // Supersede all non-dominant facts
            foreach (var fact in conflict.CompetingFacts)
            {
                if (fact.Id == dominant.Id) continue;
                if (fact.IsSuperseded) continue;
                state.Knowledge.MarkSuperseded(holder, fact, tick);
            }

            var lod = holder is PlayerHolder ? SimulationLod.Local : SimulationLod.Distant;
            _emitter.Emit(new KnowledgeConflictResolved(
                state.Date, lod, conflict.SubjectKey, dominant.Id, holder), lod);
        }
    }

    private static float LodToConfidence(SimulationLod lod) => lod switch
    {
        SimulationLod.Local    => 0.95f,
        SimulationLod.Regional => 0.65f,
        SimulationLod.Distant  => 0.35f,
        _ => 0.35f,
    };
}
