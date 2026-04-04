using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 4 — runs after EconomySystem.
/// Handles faction income/expenditure, naval allocation convergence,
/// goal adoption/abandonment, relationship drift, and self-reinforcing decline loops.
/// Drains PendingFactionStimuli at the start of its run.
/// </summary>
public sealed class FactionSystem : IWorldSystem
{
    private readonly Random _rng;
    // Tracks the last NavalStrength value for which we emitted a FactionStrengthClaim per faction.
    // We re-emit whenever strength changes by ≥ 10% (or drops to 0 for the first time).
    private readonly Dictionary<FactionId, int> _lastEmittedNavalStrength = new();

    // Key: burned IndividualId. Value: tick on which replacement should be spawned.
    private readonly Dictionary<IndividualId, int> _burnReplacementTimers = new();

    // Utility scoring thresholds
    private const float GoalAdoptThreshold  = 0.65f;
    private const float GoalAbandonThreshold = 0.25f;
    private const int MaxActiveGoals = 3;

    public FactionSystem(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Drain stimuli queued by EventPropagationSystem last tick
        var stimuli = new List<FactionStimulus>();
        while (state.PendingFactionStimuli.TryDequeue(out var s))
            stimuli.Add(s);

        foreach (var (factionId, faction) in state.Factions)
        {
            ApplyStimuli(faction, stimuli.Where(s => s.FactionId == factionId));

            switch (faction.Type)
            {
                case FactionType.Colonial:
                    TickColonial(faction, factionId, state, events, context);
                    break;
                case FactionType.PirateBrotherhood:
                    TickPirate(faction, factionId, state, events, context);
                    break;
            }

            UpdateGoals(faction, factionId, state, context);
            TickRelationshipDecay(faction);
            TickIntelligence(faction);
            EmitStrengthFactIfChanged(faction, factionId, state, context);

            if (faction.Type == FactionType.Colonial)
                SeedContracts(faction, factionId, state, context);
        }

        TickBurnReplacements(state, context, events);
    }

    // ---- Colonial ----

    private void TickColonial(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events, SimulationContext context)
    {
        // ~1% chance per tick: plant disinformation in a pirate haven's knowledge pool
        if (_rng.NextDouble() < 0.01)
            SeedDisinformation(faction, factionId, state, context);
        // Income: port taxes + trade duties (derived from port prosperity)
        var portIncome = state.Ports.Values
            .Where(p => p.ControllingFactionId == factionId)
            .Sum(p => (int)(p.Prosperity * 2.5f)); // rough formula

        faction.IncomePerTick = portIncome;

        // Expenditure: naval maintenance + patrol costs
        var navalCost = (int)(faction.NavalStrength * 15f * faction.CurrentNavalAllocationFraction);
        faction.ExpenditurePerTick = navalCost;

        faction.TreasuryGold += faction.IncomePerTick - faction.ExpenditurePerTick;
        faction.TreasuryGold = Math.Max(0, faction.TreasuryGold);

        // Naval allocation convergence (20%/tick toward desired)
        var allocationDelta = (faction.DesiredNavalAllocationFraction - faction.CurrentNavalAllocationFraction) * 0.20f;
        faction.CurrentNavalAllocationFraction = Math.Clamp(
            faction.CurrentNavalAllocationFraction + allocationDelta, 0f, 1f);

        // Self-reinforcing decline: if treasury is low, can't maintain patrols
        if (faction.TreasuryGold < navalCost * 3)
        {
            faction.NavalStrength = Math.Max(0, faction.NavalStrength - 1);
        }
        else if (faction.TreasuryGold > navalCost * 10 && _rng.NextDouble() < 0.1f)
        {
            faction.NavalStrength++;
        }
    }

    // ---- Pirate brotherhood ----

    private void TickPirate(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events, SimulationContext context)
    {
        // Income: raiding proceeds + haven fees
        var raidIncome = (int)(faction.RaidingMomentum * 5f);
        var havenFees = faction.HavenPresence.Values.Sum(h => (int)(h * 0.8f));
        faction.IncomePerTick = raidIncome + havenFees;

        faction.TreasuryGold += faction.IncomePerTick;

        // Cohesion drift
        if (faction.RaidingMomentum > 60f)
            faction.Cohesion = Math.Min(100f, faction.Cohesion + 0.5f);
        else if (faction.RaidingMomentum < 30f)
            faction.Cohesion = Math.Max(0f, faction.Cohesion - 1.0f);

        // Fracture risk below 20
        if (faction.Cohesion < 20f && _rng.NextDouble() < 0.05f)
            TriggerFracture(faction, factionId, state, events);

        // Raiding momentum natural decay
        faction.RaidingMomentum = Math.Max(0f, faction.RaidingMomentum - 1f);
    }

    private void TriggerFracture(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events)
    {
        // Split off a portion of haven presence — simplified model
        foreach (var region in faction.HavenPresence.Keys.ToList())
            faction.HavenPresence[region] *= 0.6f;

        faction.Cohesion = 30f;
        faction.RaidingMomentum = Math.Max(0f, faction.RaidingMomentum - 20f);
    }

    // ---- Goal management ----

    private void UpdateGoals(Faction faction, FactionId factionId, WorldState state,
        SimulationContext context)
    {
        // Increment active goal ticks
        foreach (var goal in faction.ActiveGoals)
            goal.TicksActive++;

        // Abandon low-utility goals
        var toAbandon = faction.ActiveGoals
            .Where(g => ScoreGoal(g, faction, state) < GoalAbandonThreshold)
            .ToList();
        foreach (var g in toAbandon)
            faction.ActiveGoals.Remove(g);

        // Adopt high-utility new goals if room
        if (faction.ActiveGoals.Count < MaxActiveGoals)
        {
            var candidates = GenerateCandidateGoals(faction, factionId, state);
            foreach (var candidate in candidates)
            {
                if (faction.ActiveGoals.Count >= MaxActiveGoals) break;
                candidate.Utility = ScoreGoal(candidate, faction, state);
                if (candidate.Utility >= GoalAdoptThreshold)
                {
                    faction.ActiveGoals.Add(candidate);
                    SeedIntentionFact(candidate, factionId, state, context.TickNumber);
                }
            }
        }
    }

    private static float ScoreGoal(FactionGoal goal, Faction faction, WorldState state) =>
        goal switch
        {
            ExpandTerritory et => faction.TreasuryGold > 5000 && faction.NavalStrength > 5 ? 0.80f : 0.30f,
            SuppressPiracy sp  => faction.Type == FactionType.Colonial ? 0.70f : 0.10f,
            BuildNavy          => faction.TreasuryGold > 3000 && faction.NavalStrength < 10 ? 0.75f : 0.25f,
            AccumulateTreasury => faction.TreasuryGold < 2000 ? 0.80f : 0.20f,
            RaidShippingLane r => faction.Type == FactionType.PirateBrotherhood ? 0.75f : 0.05f,
            EstablishHaven eh  => faction.Type == FactionType.PirateBrotherhood ? 0.65f : 0.00f,
            EspionageGoal      => faction.IntelligenceCapability < 0.80f ? 0.70f : 0.20f,
            _                  => 0.50f,
        };

    private static List<FactionGoal> GenerateCandidateGoals(Faction faction, FactionId factionId,
        WorldState state)
    {
        var goals = new List<FactionGoal>();

        if (faction.Type == FactionType.Colonial)
        {
            // Expand into uncontrolled regions
            foreach (var (regionId, region) in state.Regions)
                if (region.DominantFactionId != factionId)
                    goals.Add(new ExpandTerritory(regionId));

            goals.Add(new BuildNavy());
            goals.Add(new AccumulateTreasury());
            goals.Add(new EspionageGoal());

            foreach (var (regionId, _) in state.Regions)
                goals.Add(new SuppressPiracy(regionId));
        }
        else
        {
            foreach (var (regionId, _) in state.Regions)
                goals.Add(new RaidShippingLane(regionId));

            foreach (var (portId, port) in state.Ports.Where(p => p.Value.IsPirateHaven))
                goals.Add(new EstablishHaven(portId));
        }

        return goals;
    }

    /// <summary>
    /// Emits a FactionStrengthClaim into the faction's own holder and all ports it controls
    /// whenever naval strength has changed by ≥ 10% since the last emission.
    /// This is the knowledge-layer signal that quests A4 and B3 listen for.
    /// </summary>
    private void EmitStrengthFactIfChanged(Faction faction, FactionId factionId,
        WorldState state, SimulationContext context)
    {
        var current = faction.NavalStrength;
        if (!_lastEmittedNavalStrength.TryGetValue(factionId, out var last))
        {
            // First tick for this faction — always emit
            _lastEmittedNavalStrength[factionId] = current;
            EmitStrengthFact(faction, factionId, state, context, current, 0.80f);
            return;
        }

        // Threshold: 10% relative change, or absolute drop of 1 when last was ≤ 5
        var changed = last == 0
            ? current != 0
            : Math.Abs(current - last) / (float)last >= 0.10f || (last <= 5 && current != last);

        if (!changed) return;
        _lastEmittedNavalStrength[factionId] = current;

        // Confidence reflects how "newsworthy" the change is — big drops are more noticed
        var delta = last - current;
        var confidence = delta > 0 ? Math.Clamp(0.50f + delta * 0.03f, 0.50f, 0.90f) : 0.55f;
        EmitStrengthFact(faction, factionId, state, context, current, confidence);
    }

    private void EmitStrengthFact(Faction faction, FactionId factionId,
        WorldState state, SimulationContext context, int strength, float confidence)
    {
        var fact = new KnowledgeFact
        {
            Claim = new FactionStrengthClaim(factionId, strength, faction.TreasuryGold),
            Sensitivity = KnowledgeSensitivity.Restricted,
            Confidence = confidence,
            BaseConfidence = confidence,
            ObservedDate = state.Date,
            SourceHolder = new FactionHolder(factionId),
        };

        // Seed into faction holder
        state.Knowledge.MarkSuperseded(new FactionHolder(factionId), fact, context.TickNumber);
        state.Knowledge.AddFact(new FactionHolder(factionId), fact);

        // Seed into ports this faction controls — word spreads from there
        foreach (var port in state.Ports.Values.Where(p => p.ControllingFactionId == factionId))
        {
            state.Knowledge.MarkSuperseded(new PortHolder(port.Id), fact, context.TickNumber);
            state.Knowledge.AddFact(new PortHolder(port.Id), fact);
        }
    }

    /// <summary>
    /// Seeds a FactionIntentionClaim into the faction's own knowledge pool when a new goal
    /// is adopted. Sensitivity = Secret — it does not propagate passively and can only be
    /// obtained by active investigation, a broker, or a faction betrayal.
    /// Future quest templates can trigger on known faction intentions.
    /// </summary>
    private static void SeedIntentionFact(FactionGoal goal, FactionId factionId,
        WorldState state, int tick)
    {
        var summary = goal.GetType().Name switch
        {
            "ExpandTerritory"    => "Expanding naval presence into new territory",
            "SuppressPiracy"     => "Conducting anti-piracy operations",
            "BuildNavy"          => "Building naval strength",
            "AccumulateTreasury" => "Consolidating treasury reserves",
            "RaidShippingLane"   => "Raiding merchant shipping lanes",
            "EstablishHaven"     => "Establishing a pirate haven",
            "EspionageGoal"      => "Expanding intelligence and counter-intelligence operations",
            _                    => "Pursuing undisclosed strategic objectives",
        };

        var fact = new KnowledgeFact
        {
            Claim          = new FactionIntentionClaim(factionId, summary),
            Sensitivity    = KnowledgeSensitivity.Secret,
            Confidence     = 0.90f,
            BaseConfidence = 0.90f,
            ObservedDate   = state.Date,
            SourceHolder   = new FactionHolder(factionId),
        };
        // Secret — seed only to the faction's own intelligence pool.
        // 0% passive propagation means it cannot spread without active extraction.
        state.Knowledge.MarkSuperseded(new FactionHolder(factionId), fact, tick);
        state.Knowledge.AddFact(new FactionHolder(factionId), fact);
    }

    /// <summary>
    /// Plant a false ShipLocationClaim in a pirate haven's knowledge pool.
    /// The false fact has high confidence and IsDisinformation=true — holders believe it is genuine.
    /// This models factions luring enemies into traps with planted rumours (e.g. the Q-ship bait from Quest A1).
    /// </summary>
    private void SeedDisinformation(Faction faction, FactionId factionId, WorldState state,
        SimulationContext context)
    {
        // Pick a pirate haven to seed the rumour into
        var havens = state.Ports.Values.Where(p => p.IsPirateHaven).ToList();
        if (havens.Count == 0) return;
        var targetPort = havens[_rng.Next(havens.Count)];

        // Pick one of this faction's own ships as the "bait"
        var baitShip = state.Ships.Values
            .FirstOrDefault(s => s.OwnerFactionId == factionId && !s.IsPlayerShip);
        if (baitShip is null) return;

        // Place the false location in an adjacent region to make it actionable
        var targetRegion = state.Ports.TryGetValue(targetPort.Id, out var tp)
            ? tp.RegionId : default;
        var adjacentRegions = state.Regions.TryGetValue(targetRegion, out var reg)
            ? reg.AdjacentRegions : [];
        var baitRegion = adjacentRegions.Count > 0
            ? adjacentRegions[_rng.Next(adjacentRegions.Count)]
            : targetRegion;

        // Confidence scales with faction's intelligence capability (0.70–0.95)
        var confidence = Math.Clamp(0.70f + faction.IntelligenceCapability * 0.25f, 0.70f, 0.95f);

        var falseFact = new KnowledgeFact
        {
            Claim = new ShipLocationClaim(baitShip.Id, new AtSea(baitRegion)),
            Sensitivity = KnowledgeSensitivity.Public,
            Confidence = confidence,
            BaseConfidence = confidence,
            ObservedDate = state.Date,
            IsDisinformation = true,
            HopCount = 1,  // appears to have already spread once — more believable
            SourceHolder = new FactionHolder(factionId),
            OriginatingAgentId = baitShip.CaptainId,
        };

        // Seed into the haven's knowledge pool; it will propagate naturally from there
        state.Knowledge.MarkSuperseded(new PortHolder(targetPort.Id), falseFact, context.TickNumber);
        state.Knowledge.AddFact(new PortHolder(targetPort.Id), falseFact);
    }

    // ---- Contract seeding ----

    private void SeedContracts(Faction faction, FactionId factionId, WorldState state,
        SimulationContext context)
    {
        // Find the governor for this faction
        var governor = state.Individuals.Values.FirstOrDefault(ind =>
            ind.IsAlive
            && ind.Role == IndividualRole.Governor
            && ind.FactionId == factionId
            && ind.HomePortId.HasValue
            && faction.ControlledPorts.Contains(ind.HomePortId.Value));

        if (governor is null) return;

        // 1. SuppressPiracy goals → pirate ship bounties
        foreach (var goal in faction.ActiveGoals.OfType<SuppressPiracy>())
        {
            var targetRegionId = goal.TargetRegion;

            // Find pirate ships in the target region (at sea OR docked at a pirate haven)
            var pirateShips = state.Ships.Values
                .Where(s => s.IsPirate && ShipInRegion(s, targetRegionId, state))
                .ToList();

            foreach (var pirateShip in pirateShips)
            {
                var shipSubjectKey = $"Ship:{pirateShip.Id.Value}";
                var bounty = (int)(500 + faction.TreasuryGold * 0.02f);

                // Rate-limit: check all port knowledge stores in controlled ports for existing contract
                var alreadyContracted = faction.ControlledPorts.Any(portId =>
                    state.Knowledge.GetFacts(new PortHolder(portId))
                        .Any(f => !f.IsSuperseded
                            && f.Claim is ContractClaim cc
                            && cc.TargetSubjectKey == shipSubjectKey));

                if (alreadyContracted) continue;

                // Seed into ports in target region and adjacent regions
                var seedPorts = GetSeedPorts(faction, targetRegionId, state);
                SeedContractInPorts(seedPorts, governor.Id, factionId, shipSubjectKey,
                    ContractConditionType.TargetDestroyed, bounty,
                    NarrativeArchetype.ColonialCommission, state, context.TickNumber,
                    targetShip: pirateShip);
            }
        }

        // 2. Famine ports → GoodsDelivered contracts
        foreach (var portId in faction.ControlledPorts.ToList())
        {
            if (!state.Ports.TryGetValue(portId, out var port)) continue;
            if (!port.Conditions.HasFlag(PortConditionFlags.Famine)) continue;

            var deliverySubjectKey = $"Port:{portId.Value}:Food";
            var foodReward = (int)(800 + faction.TreasuryGold * 0.03f);

            // Rate-limit
            var alreadyContracted = faction.ControlledPorts.Any(pid =>
                state.Knowledge.GetFacts(new PortHolder(pid))
                    .Any(f => !f.IsSuperseded
                        && f.Claim is ContractClaim cc
                        && cc.TargetSubjectKey == deliverySubjectKey));

            if (alreadyContracted) continue;

            // Seed into nearby ports (all controlled ports)
            var seedPorts = faction.ControlledPorts.Where(pid => pid != portId).ToList();
            SeedContractInPorts(seedPorts, governor.Id, factionId, deliverySubjectKey,
                ContractConditionType.GoodsDelivered, foodReward,
                NarrativeArchetype.DesperatePlea, state, context.TickNumber);
        }
    }

    private static bool ShipInRegion(Ship ship, RegionId regionId, WorldState state)
        => ship.Location switch
        {
            AtSea ats   => ats.Region == regionId,
            EnRoute er  => er.From == regionId || er.To == regionId,
            AtPort atp  => state.Ports.TryGetValue(atp.Port, out var p) && p.RegionId == regionId,
            _           => false,
        };

    private static List<PortId> GetSeedPorts(Faction faction, RegionId targetRegionId,
        WorldState state)
    {
        // Ports in target region + adjacent regions that are controlled by this faction
        var adjacentRegions = state.Regions.TryGetValue(targetRegionId, out var reg)
            ? reg.AdjacentRegions : [];

        return faction.ControlledPorts
            .Where(pid =>
            {
                if (!state.Ports.TryGetValue(pid, out var p)) return false;
                return p.RegionId == targetRegionId || adjacentRegions.Contains(p.RegionId);
            })
            .ToList();
    }

    private static void SeedContractInPorts(
        List<PortId> seedPorts,
        IndividualId issuerId,
        FactionId issuerFactionId,
        string targetSubjectKey,
        ContractConditionType condition,
        int goldReward,
        NarrativeArchetype archetype,
        WorldState state,
        int tickNumber,
        Ship? targetShip = null)
    {
        var claim = new ContractClaim(
            issuerId, issuerFactionId, targetSubjectKey,
            condition, goldReward, archetype);

        foreach (var portId in seedPorts)
        {
            var fact = new KnowledgeFact
            {
                Claim          = claim,
                Sensitivity    = KnowledgeSensitivity.Public,  // bounties spread freely
                Confidence     = 0.80f,
                BaseConfidence = 0.80f,
                ObservedDate   = state.Date,
                HopCount       = 0,
                SourceHolder   = new FactionHolder(issuerFactionId),
            };
            state.Knowledge.AddFact(new PortHolder(portId), fact);

            // Co-seed last-known-location intel so the quest intel gate can be satisfied.
            // A bounty notice naturally includes the sighting that prompted it.
            if (targetShip is not null && condition == ContractConditionType.TargetDestroyed)
            {
                var locFact = new KnowledgeFact
                {
                    Claim          = new ShipLocationClaim(targetShip.Id, targetShip.Location),
                    Sensitivity    = KnowledgeSensitivity.Public,
                    Confidence     = 0.70f,
                    BaseConfidence = 0.70f,
                    ObservedDate   = state.Date,
                    HopCount       = 1,
                    SourceHolder   = new FactionHolder(issuerFactionId),
                };
                var portHolder = new PortHolder(portId);
                var subjectKey = KnowledgeFact.GetSubjectKey(locFact.Claim);
                var existing = state.Knowledge.GetFacts(portHolder)
                    .FirstOrDefault(f => !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
                if (existing is null || existing.Confidence < 0.70f)
                {
                    if (existing is not null)
                        state.Knowledge.MarkSuperseded(portHolder, locFact, tickNumber);
                    state.Knowledge.AddFact(portHolder, locFact);
                }
            }
        }
    }

    private static void TickIntelligence(Faction faction)
    {
        if (faction.ActiveGoals.Any(g => g is EspionageGoal))
            faction.IntelligenceCapability = Math.Clamp(faction.IntelligenceCapability + 0.005f, 0.10f, 0.95f);
    }

    private void TickBurnReplacements(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Start timers for any newly-compromised individuals not yet tracked
        foreach (var (id, individual) in state.Individuals)
        {
            if (individual.IsAlive && individual.IsCompromised && individual.FactionId.HasValue
                && !_burnReplacementTimers.ContainsKey(id))
            {
                _burnReplacementTimers[id] = context.TickNumber + 30;
            }
        }

        // Fire replacements when timer elapses
        foreach (var (agentId, replaceTick) in _burnReplacementTimers.ToList())
        {
            if (context.TickNumber < replaceTick) continue;

            _burnReplacementTimers.Remove(agentId);

            if (!state.Individuals.TryGetValue(agentId, out var burned)) continue;
            if (!burned.IsAlive || !burned.IsCompromised || !burned.FactionId.HasValue) continue;
            if (!state.Factions.TryGetValue(burned.FactionId.Value, out var faction)) continue;

            // Check treasury can support a new agent
            if (faction.TreasuryGold < 500) continue;
            faction.TreasuryGold -= 500;

            var replacement = new Individual
            {
                Id = IndividualId.New(),
                FirstName = "New",
                LastName = burned.LastName,
                Role = burned.Role,
                FactionId = burned.FactionId,
                LocationPortId = burned.LocationPortId ?? burned.HomePortId,
                HomePortId = burned.HomePortId,
            };
            replacement.IsAlive = true;
            state.Individuals[replacement.Id] = replacement;

            burned.IsAlive = false;

            events.Emit(new AgentReplaced(state.Date, SimulationLod.Local,
                agentId, replacement.Id, burned.FactionId.Value), SimulationLod.Local);
        }
    }

    private static void TickRelationshipDecay(Faction faction)
    {
        // Relationships drift very slowly toward 0 over time
        foreach (var key in faction.Relationships.Keys.ToList())
        {
            var val = faction.Relationships[key];
            faction.Relationships[key] = val > 0
                ? Math.Max(0f, val - 0.05f)
                : Math.Min(0f, val + 0.05f);
        }
    }

    private static void ApplyStimuli(Faction faction, IEnumerable<FactionStimulus> stimuli)
    {
        foreach (var s in stimuli)
        {
            switch (s.StimulusType)
            {
                case "TradeDisrupted":
                    faction.TreasuryGold = Math.Max(0, (int)(faction.TreasuryGold - s.Magnitude));
                    break;
                case "PortCaptured":
                    // Remove from controlled ports — handled by the event that queued this stimulus
                    break;
                case "RaidingGain":
                    faction.TreasuryGold += (int)s.Magnitude;
                    faction.RaidingMomentum = Math.Min(100f, faction.RaidingMomentum + s.Magnitude * 0.1f);
                    break;
                case "NavalLoss":
                    faction.NavalStrength = Math.Max(0, faction.NavalStrength - (int)s.Magnitude);
                    break;
                case "DeceptionExposed":
                    faction.IntelligenceCapability = Math.Clamp(
                        faction.IntelligenceCapability - 0.05f, 0.10f, 0.95f);
                    break;
            }
        }
    }
}
