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
            TickIntentionLeaks(faction, factionId, state, context, events);
            TickCounterIntelligence(faction, factionId, state, context.TickNumber);

            TickWarDeclarations(faction, factionId, state, context, events);

            if (faction.Type == FactionType.Colonial)
                SeedContracts(faction, factionId, state, context);
        }

        TickBlockadeDetection(state);
        TickBurnReplacements(state, context, events);
    }

    // ---- Crisis 6: War declaration ----

    private void TickWarDeclarations(Faction faction, FactionId factionId,
        WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Check for DeclareWar goals
        foreach (var goal in faction.ActiveGoals.OfType<DeclareWar>().ToList())
        {
            if (faction.AtWarWith.Contains(goal.TargetFaction)) continue;

            // War requires relationship below -80
            if (faction.Relationships.TryGetValue(goal.TargetFaction, out var rel) && rel > -80f)
                continue;

            faction.AtWarWith.Add(goal.TargetFaction);

            // Reciprocal — the target is also at war with us
            if (state.Factions.TryGetValue(goal.TargetFaction, out var targetFaction))
                targetFaction.AtWarWith.Add(factionId);

            // Seed war intention at Public sensitivity — everyone hears fast
            var warFact = new KnowledgeFact
            {
                Claim = new FactionIntentionClaim(factionId, $"Declared war on {goal.TargetFaction.Value}"),
                Sensitivity = KnowledgeSensitivity.Public,
                Confidence = 0.95f,
                BaseConfidence = 0.95f,
                ObservedDate = state.Date,
                SourceHolder = new FactionHolder(factionId),
            };
            foreach (var portId in faction.ControlledPorts)
                state.Knowledge.AddFact(new PortHolder(portId), warFact);
        }

        // Check for SeekPeace goals — end war via treaty
        foreach (var goal in faction.ActiveGoals.OfType<SeekPeace>().ToList())
        {
            if (!faction.AtWarWith.Contains(goal.TargetFaction)) continue;

            // Peace requires both sides to have SeekPeace goals
            if (!state.Factions.TryGetValue(goal.TargetFaction, out var targetFaction2))
                continue;
            if (!targetFaction2.ActiveGoals.Any(g => g is SeekPeace sp && sp.TargetFaction == factionId))
                continue;

            faction.AtWarWith.Remove(goal.TargetFaction);
            targetFaction2.AtWarWith.Remove(factionId);

            // Reset relationship to neutral-ish
            faction.Relationships[goal.TargetFaction] = -20f; // still tense, not friendly
            targetFaction2.Relationships[factionId] = -20f;
        }
    }

    // ---- Crisis 3: Blockade detection ----

    /// <summary>
    /// Detect blockades: if hostile naval strength in a port's region exceeds
    /// the port's defense rating, set Blockaded flag. Uses combat tonnage,
    /// not raw ship count.
    /// </summary>
    private static void TickBlockadeDetection(WorldState state)
    {
        foreach (var port in state.Ports.Values)
        {
            if (port.ControllingFactionId is not { } portFaction) continue;

            // Port defense = faction's patrol allocation for this region × 10 + base 20
            var defenseRating = 20;
            if (state.Factions.TryGetValue(portFaction, out var faction))
                defenseRating += faction.PatrolAllocations.GetValueOrDefault(port.RegionId) * 10;

            // Sum hostile naval strength in port's region
            var hostileStrength = 0;
            foreach (var ship in state.Ships.Values)
            {
                if (ship.OwnerFactionId is null) continue;
                if (ship.OwnerFactionId == portFaction) continue;

                // Check if this faction is at war with the port's faction
                if (!state.Factions.TryGetValue(ship.OwnerFactionId.Value, out var shipFaction))
                    continue;
                if (!shipFaction.AtWarWith.Contains(portFaction)) continue;

                var shipRegion = state.GetShipRegion(ship.Id);
                if (shipRegion != port.RegionId) continue;

                hostileStrength += ship.Guns; // combat tonnage approximated by guns
            }

            if (hostileStrength > defenseRating)
                port.SetCondition(PortConditionFlags.Blockaded, true);
            else
                port.SetCondition(PortConditionFlags.Blockaded, false);
        }
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
            var hadEspionage = faction.ActiveGoals.Any(g => g is EspionageGoal);
            var candidates = GenerateCandidateGoals(faction, factionId, state);
            foreach (var candidate in candidates)
            {
                if (faction.ActiveGoals.Count >= MaxActiveGoals) break;
                candidate.Utility = ScoreGoal(candidate, faction, state);
                if (candidate.Utility >= GoalAdoptThreshold)
                {
                    faction.ActiveGoals.Add(candidate);
                    SeedIntentionFact(candidate, factionId, state, context.TickNumber);

                    // Newly adopted EspionageGoal → seed infiltrators into rival factions
                    if (!hadEspionage && candidate is EspionageGoal)
                        SeedInfiltratorsForEspionage(factionId, state);
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

    /// <summary>
    /// Called when a faction newly adopts EspionageGoal.
    /// Marks ~5% of the faction's living non-governor members as infiltrators for a random rival.
    /// FactionId = actual master (the rival); ClaimedFactionId = cover identity (the hiring faction).
    /// </summary>
    private void SeedInfiltratorsForEspionage(FactionId factionId, WorldState state)
    {
        var rivals = state.Factions.Values
            .Where(f => f.Type == FactionType.Colonial && f.Id != factionId)
            .ToList();
        if (rivals.Count == 0) return;

        var members = state.Individuals.Values
            .Where(i => i.IsAlive && i.FactionId == factionId
                        && i.Role != IndividualRole.Governor)
            .ToList();

        foreach (var member in members)
        {
            if (_rng.NextSingle() < 0.05f)
            {
                // Flip allegiance: FactionId becomes the rival (truth), ClaimedFactionId becomes the old faction (cover)
                member.ClaimedFactionId = member.FactionId;
                member.FactionId        = rivals[_rng.Next(rivals.Count)].Id;
            }
        }
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

            // 10% chance: replacement is secretly loyal to a rival colonial power
            if (_rng.NextSingle() < 0.10f
                && faction.ActiveGoals.Any(g => g is EspionageGoal)
                && faction.Type == FactionType.Colonial)
            {
                var rivals = state.Factions.Values
                    .Where(f => f.Type == FactionType.Colonial && f.Id != burned.FactionId)
                    .ToList();
                if (rivals.Count > 0)
                {
                    // FactionId stays as their actual master; ClaimedFactionId is the cover identity
                    replacement.ClaimedFactionId = replacement.FactionId;   // cover: appears loyal to hiring faction
                    replacement.FactionId        = rivals[_rng.Next(rivals.Count)].Id;  // truth: serves the rival
                }
            }

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
                    if (s.PlayerIsOrigin)
                    {
                        faction.TreasuryGold = Math.Max(0, faction.TreasuryGold - 200);
                        if (!faction.ActiveGoals.Any(g => g is EspionageGoal))
                            faction.ActiveGoals.Add(new EspionageGoal { Utility = 0.80f });
                    }
                    break;
            }
        }
    }

    // ---- 5E-3: FactionIntentionClaim leak mechanic ----

    /// <summary>
    /// Factions leak Secret intentions under stress or low capability.
    /// Low-capability factions (< 0.35): 2%/tick passive leak — structurally leaky organisations.
    /// All factions under systemic stress: 15%/tick event-driven leak:
    ///   1. Treasury < expenditure * 5 (can't pay spies)
    ///   2. NavalStrength dropped > 50% in 10 ticks (faction in disarray)
    /// Leaked intention downgrades from Secret to Restricted in a random controlled port.
    /// </summary>
    private void TickIntentionLeaks(Faction faction, FactionId factionId,
        WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Determine leak chance
        var leakChance = 0.0;

        // Low-capability passive leak
        if (faction.IntelligenceCapability < 0.35f)
            leakChance = 0.02;

        // Stress-driven leaks (override passive if higher)
        bool underStress = false;

        // Stress trigger 1: treasury crisis
        var expenditure = faction.ControlledPorts.Count * 50 + faction.NavalStrength * 20; // rough estimate
        if (faction.TreasuryGold < expenditure * 5)
            underStress = true;

        // Stress trigger 2: naval collapse (> 50% drop tracked via _lastEmittedNavalStrength)
        if (_lastEmittedNavalStrength.TryGetValue(factionId, out var lastStrength)
            && lastStrength > 0
            && faction.NavalStrength < lastStrength / 2)
            underStress = true;

        if (underStress)
            leakChance = Math.Max(leakChance, 0.15);

        if (leakChance <= 0 || _rng.NextDouble() >= leakChance) return;

        // Find a Secret FactionIntentionClaim in this faction's holder
        var factionHolder = new FactionHolder(factionId);
        var secretIntention = state.Knowledge.GetFacts(factionHolder)
            .FirstOrDefault(f => !f.IsSuperseded
                && f.Claim is FactionIntentionClaim
                && f.Sensitivity == KnowledgeSensitivity.Secret);

        if (secretIntention is null) return;

        // Pick a random controlled port to leak into
        var controlledPorts = faction.ControlledPorts
            .Where(pid => state.Ports.ContainsKey(pid))
            .ToList();
        if (controlledPorts.Count == 0) return;

        var leakPort = controlledPorts[_rng.Next(controlledPorts.Count)];

        // Inject a Restricted copy into the port's knowledge pool
        var leakedFact = new KnowledgeFact
        {
            Claim = secretIntention.Claim,
            Sensitivity = KnowledgeSensitivity.Restricted, // downgraded from Secret
            Confidence = secretIntention.Confidence * 0.70f, // degraded — tavern whisper
            BaseConfidence = secretIntention.Confidence * 0.70f,
            ObservedDate = state.Date,
            HopCount = 1,
            SourceHolder = factionHolder,
        };
        state.Knowledge.AddFact(new PortHolder(leakPort), leakedFact);
    }

    // ---- 5E-2: IndividualAllegianceClaim counter-intelligence ----

    /// <summary>
    /// Faction counter-intelligence: high-capability factions can detect enemy infiltrators
    /// in their controlled ports and emit allegiance facts into FactionHolder.
    /// Chance = IntelligenceCapability * 0.01 per tick per infiltrator (~1% at cap 1.0).
    /// </summary>
    private void TickCounterIntelligence(Faction faction, FactionId factionId,
        WorldState state, int tick)
    {
        if (faction.IntelligenceCapability < 0.40f) return; // low-cap factions don't counter-intel

        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.FactionId != factionId) continue; // not our agent
            if (individual.ClaimedFactionId is null) continue; // not an infiltrator
            if (individual.ClaimedFactionId == factionId) continue; // claiming to be us (not infiltrating us)

            // This individual claims to belong to another faction but actually serves us.
            // Skip — we already know about our own agents.
            // What we want: detect enemy infiltrators pretending to be OUR people.
        }

        // Detect enemy infiltrators in our ports: individuals whose FactionId != factionId
        // but whose ClaimedFactionId == factionId (pretending to be one of ours)
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.FactionId == factionId) continue; // actually ours
            if (individual.ClaimedFactionId != factionId) continue; // not pretending to be ours
            if (individual.IsCompromised) continue; // already burned

            var detectChance = faction.IntelligenceCapability * 0.01;
            if (_rng.NextDouble() >= detectChance) continue;

            // Detected! Seed allegiance fact into FactionHolder
            var allegianceFact = new KnowledgeFact
            {
                Claim = new IndividualAllegianceClaim(
                    individual.Id,
                    ClaimedFaction: factionId,
                    ActualFaction: individual.FactionId),
                Sensitivity = KnowledgeSensitivity.Restricted,
                Confidence = 0.80f,
                BaseConfidence = 0.80f,
                ObservedDate = state.Date,
                SourceHolder = new FactionHolder(factionId),
            };
            state.Knowledge.AddFact(new FactionHolder(factionId), allegianceFact);
        }
    }
}
