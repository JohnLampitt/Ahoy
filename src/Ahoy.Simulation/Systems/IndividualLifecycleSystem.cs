using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 5 — runs after FactionSystem, before EventPropagationSystem.
/// Drives named individual movement: governors leave their home port on short
/// inspection/diplomatic tours and return after a few ticks.
///
/// Only individuals with HomePortId set (currently: Governors) travel.
/// Pirates and brokers roam freely without this system's oversight.
///
/// When an individual moves, an <see cref="IndividualMoved"/> event is emitted.
/// KnowledgeSystem converts this into an IndividualWhereaboutsClaim seeded only
/// at the departure and arrival ports — creating information asymmetry.
/// </summary>
public sealed class IndividualLifecycleSystem : IWorldSystem
{
    private const double TourStartChance        = 0.02;  // 2% per tick while at home
    private const int    TourMinTicks           = 3;
    private const int    TourMaxTicks           = 10;
    private const double BaseMortality          = 0.002; // ~1% per year (360-tick year)
    private const double CompromisedMultiplier  = 5.0;   // burned agents: ~5% per year

    private readonly Random _rng;

    public IndividualLifecycleSystem(Random rng)
    {
        _rng = rng;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // --- Mortality pass ---
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            var chance = individual.IsCompromised
                ? BaseMortality * CompromisedMultiplier
                : BaseMortality;
            if (_rng.NextDouble() < chance)
            {
                individual.IsAlive = false;
                var lod = individual.LocationPortId.HasValue
                    && state.Ports.TryGetValue(individual.LocationPortId.Value, out var p)
                    && state.Regions.TryGetValue(p.RegionId, out _)
                        ? context.GetLod(p.RegionId)
                        : SimulationLod.Distant;
                var cause = individual.IsCompromised ? "Burned agent — accident" : "Natural causes";
                events.Emit(new IndividualDied(state.Date, lod, individual.Id, cause), lod);
            }
        }

        // --- Tour movement pass ---
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.HomePortId is not { } homePort) continue;

            if (individual.TourTicksRemaining is null)
            {
                // 5A-2: Tour likelihood based on FactionHolder knowledge, not ground truth.
                // Governor consults what their faction knows about port prosperity.
                // Stable/prosperous factions travel more; factions under pressure stay home.
                var tourChance = TourStartChance;
                if (individual.FactionId.HasValue)
                {
                    var factionHolder = new FactionHolder(individual.FactionId.Value);
                    var knownProsperity = state.Knowledge.GetFacts(factionHolder)
                        .Where(f => !f.IsSuperseded && f.Claim is PortProsperityClaim)
                        .Select(f => (double)((PortProsperityClaim)f.Claim).Prosperity * f.Confidence)
                        .ToList();
                    if (knownProsperity.Count > 0)
                        tourChance *= Math.Max(0.5, knownProsperity.Average() / 100.0);
                }
                if (_rng.NextDouble() >= tourChance) continue;

                if (PickTourDestination(homePort, individual, state) is not { } dest)
                    continue;

                individual.LocationPortId     = dest;
                individual.TourTicksRemaining = _rng.Next(TourMinTicks, TourMaxTicks + 1);
                events.Emit(
                    new IndividualMoved(state.Date, SimulationLod.Local,
                        individual.Id, homePort, dest),
                    SimulationLod.Local);
            }
            else
            {
                // On tour — count down
                individual.TourTicksRemaining--;
                if (individual.TourTicksRemaining > 0) continue;

                // Tour complete — return home
                var fromPort = individual.LocationPortId ?? homePort;
                individual.LocationPortId     = homePort;
                individual.TourTicksRemaining = null;
                events.Emit(
                    new IndividualMoved(state.Date, SimulationLod.Local,
                        individual.Id, fromPort, homePort),
                    SimulationLod.Local);
            }
        }

        // --- 6D: Personal goal generation pass ---
        EvaluatePersonalGoals(state, context);

        // --- Group 7: Crisis-driven contract seeding ---
        SeedCrisisContracts(state);
    }

    // ---- Group 7 Phase 3: Crisis Contract Seeding ----

    private void SeedCrisisContracts(WorldState state)
    {
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.Role != IndividualRole.Governor) continue;
            if (individual.LocationPortId is not { } portId) continue;
            if (!state.Ports.TryGetValue(portId, out var port)) continue;

            // Crisis 2: Epidemic — seed Medicine and Food delivery contracts
            if (port.Conditions.HasFlag(PortConditionFlags.Plague))
                SeedEpidemicContracts(individual, port, state);

            // Crisis 3: Blockade — seed bounty on blockading flagship
            if (port.Conditions.HasFlag(PortConditionFlags.Blockaded))
                SeedBlockadeBounty(individual, port, state);

            // Crisis 6: War — seed Letters of Marque against enemy ships
            if (individual.FactionId.HasValue
                && state.Factions.TryGetValue(individual.FactionId.Value, out var faction)
                && faction.AtWarWith.Count > 0)
                SeedLettersOfMarque(individual, faction, state);
        }
    }

    private void SeedEpidemicContracts(Individual governor, Port port, WorldState state)
    {
        var targetKey = $"Port:{port.Id.Value}:Medicine";
        if (state.Quests.IsOnCooldown(targetKey, state.Date)) return;

        // Premium scales with epidemic severity (prosperity loss)
        var premium = Math.Max(200, (int)(500 * (1f - port.Prosperity / 100f)));

        var contract = new ContractClaim(
            IssuerId: governor.Id,
            IssuerFactionId: governor.FactionId ?? default,
            TargetSubjectKey: targetKey,
            Condition: ContractConditionType.GoodsDelivered,
            GoldReward: premium,
            Archetype: NarrativeArchetype.DesperatePlea);

        var fact = new KnowledgeFact
        {
            Claim = contract,
            Sensitivity = KnowledgeSensitivity.Public, // desperate — broadcast widely
            Confidence = 0.90f,
            BaseConfidence = 0.90f,
            ObservedDate = state.Date,
            SourceHolder = new IndividualHolder(governor.Id),
        };
        state.Knowledge.AddFact(new PortHolder(port.Id), fact);
        state.Quests.RecordCooldown(targetKey, state.Date.Advance(10));
    }

    private void SeedBlockadeBounty(Individual governor, Port port, WorldState state)
    {
        // Find the strongest hostile ship in the region (the flagship)
        ShipId? flagship = null;
        int bestGuns = 0;
        foreach (var ship in state.Ships.Values)
        {
            if (ship.OwnerFactionId is null) continue;
            if (ship.OwnerFactionId == governor.FactionId) continue;
            var shipRegion = state.GetShipRegion(ship.Id);
            if (shipRegion != port.RegionId) continue;
            if (ship.Guns > bestGuns) { flagship = ship.Id; bestGuns = ship.Guns; }
        }

        if (flagship is null) return;

        var targetKey = $"Ship:{flagship.Value.Value}";
        if (state.Quests.IsOnCooldown(targetKey, state.Date)) return;

        // Governor empties treasury for this
        var reward = Math.Min(governor.CurrentGold, 500);
        if (reward < 100) return;
        governor.CurrentGold -= reward;

        var contract = new ContractClaim(
            IssuerId: governor.Id,
            IssuerFactionId: governor.FactionId ?? default,
            TargetSubjectKey: targetKey,
            Condition: ContractConditionType.TargetDestroyed,
            GoldReward: reward,
            Archetype: NarrativeArchetype.ColonialCommission);

        var fact = new KnowledgeFact
        {
            Claim = contract,
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = 0.85f,
            BaseConfidence = 0.85f,
            ObservedDate = state.Date,
            SourceHolder = new IndividualHolder(governor.Id),
        };
        state.Knowledge.AddFact(new PortHolder(port.Id), fact);
        state.Quests.RecordCooldown(targetKey, state.Date.Advance(15));
    }

    private void SeedLettersOfMarque(Individual governor, Faction faction, WorldState state)
    {
        // Seed bounties on enemy faction ships — one per tick, targeting the most valuable
        foreach (var enemyFactionId in faction.AtWarWith)
        {
            var targetShip = state.Ships.Values
                .Where(s => s.OwnerFactionId == enemyFactionId && !s.IsPlayerShip && s.HullIntegrity > 0)
                .OrderByDescending(s => s.Guns + s.GoldOnBoard)
                .FirstOrDefault();

            if (targetShip is null) continue;

            var targetKey = $"Ship:{targetShip.Id.Value}";
            if (state.Quests.IsOnCooldown(targetKey, state.Date)) continue;

            var contract = new ContractClaim(
                IssuerId: governor.Id,
                IssuerFactionId: governor.FactionId ?? default,
                TargetSubjectKey: targetKey,
                Condition: ContractConditionType.TargetDestroyed,
                GoldReward: 100 + targetShip.Guns * 10, // bounty scales with threat
                Archetype: NarrativeArchetype.ColonialCommission);

            var fact = new KnowledgeFact
            {
                Claim = contract,
                Sensitivity = KnowledgeSensitivity.Public,
                Confidence = 0.90f,
                BaseConfidence = 0.90f,
                ObservedDate = state.Date,
                SourceHolder = new FactionHolder(governor.FactionId!.Value),
            };
            state.Knowledge.AddFact(new PortHolder(governor.LocationPortId!.Value), fact);
            state.Quests.RecordCooldown(targetKey, state.Date.Advance(20));
            break; // one letter of marque per governor per tick
        }
    }

    // ---- 6D: Personal Goal Generation ----

    /// <summary>
    /// Evaluate NPC relationships and knowledge to generate personal contracts.
    /// Vengeance: R &lt; -75 → seed bounty ContractClaim from personal wealth.
    /// Patronage: R &gt; +50 → inject exclusive intel into ally's knowledge.
    /// Extortion: deferred (requires ExtortGoal in EpistemicResolver).
    /// </summary>
    private void EvaluatePersonalGoals(WorldState state, SimulationContext context)
    {
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.LocationPortId is null) continue; // must be at a port

            // --- Vengeance: seed a contract against nemesis ---
            if (individual.Role is IndividualRole.Governor
                or IndividualRole.PirateCaptain
                or IndividualRole.NavalOfficer
                or IndividualRole.Privateer)
            {
                TryVengeanceTrigger(individual, state, context);
            }

            // --- Patronage: inject intel to allies ---
            if (individual.Role is IndividualRole.Governor or IndividualRole.KnowledgeBroker)
            {
                TryPatronageTrigger(individual, state);
            }
        }
    }

    private void TryVengeanceTrigger(Individual npc, WorldState state, SimulationContext context)
    {
        const int VengeanceCost = 200;
        const float NemesisThreshold = -75f;

        if (npc.CurrentGold < VengeanceCost) return;

        // Find worst enemy
        IndividualId? worstEnemy = null;
        float worstRel = NemesisThreshold;

        foreach (var ((observer, subject), rel) in state.RelationshipMatrix)
        {
            if (observer != npc.Id) continue;
            if (rel >= worstRel) continue;
            if (!state.Individuals.TryGetValue(subject, out var target) || !target.IsAlive) continue;

            worstEnemy = subject;
            worstRel = rel;
        }

        if (worstEnemy is null) return;

        // Cooldown: don't seed duplicate contracts for the same target
        var targetKey = $"Individual:{worstEnemy.Value.Value}";
        if (state.Quests.IsOnCooldown(targetKey, state.Date)) return;

        // Deduct gold and seed a ContractClaim into the local port
        npc.CurrentGold -= VengeanceCost;

        var contract = new ContractClaim(
            IssuerId: npc.Id,
            IssuerFactionId: npc.FactionId ?? default,
            TargetSubjectKey: targetKey,
            Condition: ContractConditionType.TargetDead,
            GoldReward: VengeanceCost,
            Archetype: npc.Role == IndividualRole.PirateCaptain
                ? NarrativeArchetype.PirateRival
                : NarrativeArchetype.UnderworldHit);

        var fact = new KnowledgeFact
        {
            Claim = contract,
            Sensitivity = KnowledgeSensitivity.Restricted, // personal bounties aren't shouted publicly
            Confidence = 0.85f,
            BaseConfidence = 0.85f,
            ObservedDate = state.Date,
            HopCount = 0,
            SourceHolder = new IndividualHolder(npc.Id),
        };

        state.Knowledge.AddFact(new PortHolder(npc.LocationPortId!.Value), fact);
        state.Quests.RecordCooldown(targetKey, state.Date.Advance(30));
    }

    private void TryPatronageTrigger(Individual npc, WorldState state)
    {
        const float AllyThreshold = 50f;

        // Find best ally
        foreach (var ((observer, subject), rel) in state.RelationshipMatrix)
        {
            if (observer != npc.Id) continue;
            if (rel < AllyThreshold) continue;
            if (!state.Individuals.TryGetValue(subject, out var ally) || !ally.IsAlive) continue;

            // Share highest-confidence PortPriceClaim from our knowledge
            var npcHolder = new IndividualHolder(npc.Id);
            var bestPriceFact = state.Knowledge.GetFacts(npcHolder)
                .Where(f => !f.IsSuperseded && f.Claim is PortPriceClaim && f.Confidence > 0.60f)
                .OrderByDescending(f => f.Confidence)
                .FirstOrDefault();

            if (bestPriceFact is null) continue;

            // Inject directly into ally's IndividualHolder — exclusive intel
            var allyHolder = new IndividualHolder(subject);
            var existing = state.Knowledge.GetFacts(allyHolder)
                .Any(f => !f.IsSuperseded
                    && KnowledgeFact.GetSubjectKey(f.Claim) == KnowledgeFact.GetSubjectKey(bestPriceFact.Claim));
            if (existing) continue;

            state.Knowledge.AddFact(allyHolder, bestPriceFact);
            break; // one patronage action per tick per NPC
        }
    }

    /// <summary>
    /// 5A-2: Pick tour destination using governor's knowledge, not ground truth.
    /// Uses IndividualWhereaboutsClaim (visit known allied governors),
    /// PortProsperityClaim (visit struggling ports), and PortControlClaim
    /// (suppress tours to enemy-controlled ports).
    /// </summary>
    private PortId? PickTourDestination(PortId homePort, Individual individual, WorldState state)
    {
        var factionId = individual.FactionId;
        var governorHolder = new IndividualHolder(individual.Id);
        var factionHolder = factionId.HasValue ? new FactionHolder(factionId.Value) : null;

        // Gather what the governor knows about port control
        var knownControl = new Dictionary<PortId, FactionId?>();
        var holderFacts = state.Knowledge.GetFacts(governorHolder)
            .Where(f => !f.IsSuperseded)
            .ToList();
        if (factionHolder is not null)
            holderFacts.AddRange(state.Knowledge.GetFacts(factionHolder).Where(f => !f.IsSuperseded));

        foreach (var fact in holderFacts)
        {
            if (fact.Claim is PortControlClaim pcc && !knownControl.ContainsKey(pcc.Port))
                knownControl[pcc.Port] = pcc.FactionId;
        }

        // Score candidate ports
        var portScores = new Dictionary<PortId, float>();
        foreach (var port in state.Ports.Values)
        {
            if (port.Id == homePort) continue;

            // Use known control if available; if unknown, treat as neutral (allowed)
            if (knownControl.TryGetValue(port.Id, out var controller))
            {
                if (IsHostile(factionId, controller, state)) continue; // skip enemy ports
            }

            var score = 1f; // base

            // Prefer allied (same faction) ports the governor knows about
            if (knownControl.TryGetValue(port.Id, out var ctrl) && ctrl == factionId)
                score += 2f;

            // Prefer ports with known low prosperity (governor visits struggling ports)
            var prosperityFact = holderFacts
                .FirstOrDefault(f => f.Claim is PortProsperityClaim ppc && ppc.Port == port.Id);
            if (prosperityFact is not null)
            {
                var prosperity = ((PortProsperityClaim)prosperityFact.Claim).Prosperity;
                score += (100f - prosperity) / 50f * prosperityFact.Confidence; // lower prosperity = higher priority
            }

            // Prefer ports where known allied governors are located
            var allyPresent = holderFacts
                .Any(f => f.Claim is IndividualWhereaboutsClaim iwc && iwc.Port == port.Id);
            if (allyPresent) score += 1.5f;

            portScores[port.Id] = score;
        }

        if (portScores.Count == 0) return null;

        // Weighted random selection from top candidates (prevents always picking the same port)
        var sorted = portScores.OrderByDescending(kv => kv.Value).Take(5).ToList();
        var totalScore = sorted.Sum(kv => kv.Value);
        var roll = (float)(_rng.NextDouble() * totalScore);
        float acc = 0;
        foreach (var kv in sorted)
        {
            acc += kv.Value;
            if (roll <= acc) return kv.Key;
        }
        return sorted[0].Key;
    }

    /// <summary>Relationship below this threshold is considered hostile — governors won't travel there.</summary>
    private const float HostileThreshold = -25f;

    private static bool IsHostile(FactionId? traveller, FactionId? destination, WorldState state)
    {
        if (traveller is null || destination is null || traveller == destination) return false;
        if (!state.Factions.TryGetValue(traveller.Value, out var faction)) return false;
        return faction.Relationships.TryGetValue(destination.Value, out var rel) && rel < HostileThreshold;
    }
}
