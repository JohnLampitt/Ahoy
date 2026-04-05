using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 8 — runs after KnowledgeSystem.
/// Contract quest system + NPC goal pursuit:
///   1. Ticks active contract quests (fulfillment checks, LostTrail, expiry, ClaimedByNpc).
///   2. Scans player ContractClaims and activates new ContractQuestInstances.
///   3. Assigns NPC goals (Pondering → Active) and ticks active pursuits (EpistemicResolver).
/// </summary>
public sealed class QuestSystem : IWorldSystem
{
    private const int StallAbandonThreshold = 14;
    private const float IntelConfidenceFloor = 0.30f;

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        TickActiveContractQuests(state, context, events);
        ScanForContractQuests(state, context, events);
        TickNpcGoalPursuit(state, context, events);
    }

    // ---- Active quest maintenance ----

    private static void TickActiveContractQuests(WorldState state, SimulationContext context,
        IEventEmitter events)
    {
        var playerHolder = new PlayerHolder();

        foreach (var quest in state.Quests.ActiveContractQuests.ToList())
        {
            if (quest.Status != ContractQuestStatus.Active
                && quest.Status != ContractQuestStatus.LostTrail)
                continue;

            var contract = quest.Contract;

            // --- Check fulfillment ---
            bool fulfilled = false;
            if (contract.Condition == ContractConditionType.TargetDestroyed)
            {
                if (TryParseShipId(contract.TargetSubjectKey, out var targetShipId)
                    && state.Ships.TryGetValue(targetShipId, out var targetShip)
                    && targetShip.HullIntegrity <= 0)
                {
                    fulfilled = true;
                }
            }
            else if (contract.Condition == ContractConditionType.TargetDead)
            {
                if (TryParseIndividualId(contract.TargetSubjectKey, out var targetIndId)
                    && state.Individuals.TryGetValue(targetIndId, out var targetInd)
                    && !targetInd.IsAlive)
                {
                    fulfilled = true;
                }
            }
            else if (contract.Condition == ContractConditionType.GoodsDelivered)
            {
                fulfilled = TryFulfillGoodsDelivery(contract, state, events, context.TickNumber);
            }

            if (fulfilled)
            {
                quest.Status = ContractQuestStatus.Fulfilled;
                quest.ResolvedDate = state.Date;
                events.Emit(new QuestResolved(
                    state.Date, SimulationLod.Local,
                    $"Contract:{contract.IssuerId.Value}:{contract.TargetSubjectKey}",
                    quest.Id.ToString(),
                    $"Contract fulfilled: {contract.TargetSubjectKey}",
                    ContractQuestStatus.Fulfilled),
                    SimulationLod.Local);
                continue;
            }

            // --- Check LostTrail: intel confidence dropped below 0.50 ---
            var intelFact = state.Knowledge.GetFacts(playerHolder)
                .FirstOrDefault(f => !f.IsSuperseded
                    && KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey);

            if (intelFact is null || intelFact.Confidence < 0.50f)
            {
                if (quest.Status == ContractQuestStatus.Active)
                    quest.Status = ContractQuestStatus.LostTrail;
            }
            else if (quest.Status == ContractQuestStatus.LostTrail)
            {
                // Intel recovered
                quest.Status = ContractQuestStatus.Active;
            }

            // --- Check Expired: contract claim decayed below 0.20 ---
            var contractFactId = quest.ContractFactId;
            var contractFact = state.Knowledge.GetFacts(playerHolder)
                .FirstOrDefault(f => f.Id == contractFactId);

            if (contractFact is null || contractFact.Confidence < 0.20f)
            {
                quest.Status = ContractQuestStatus.Expired;
                quest.ResolvedDate = state.Date;
                state.Quests.RecordCooldown(contract.TargetSubjectKey, state.Date.Advance(20));
                state.Quests.Resolve(quest);
                events.Emit(new QuestResolved(
                    state.Date, SimulationLod.Local,
                    $"Contract:{contract.IssuerId.Value}:{contract.TargetSubjectKey}",
                    quest.Id.ToString(),
                    $"Contract expired: {contract.TargetSubjectKey}",
                    ContractQuestStatus.Expired),
                    SimulationLod.Local);
            }
        }
    }

    // ---- New quest activation ----

    private static void ScanForContractQuests(WorldState state, SimulationContext context,
        IEventEmitter events)
    {
        var playerHolder = new PlayerHolder();
        var playerFacts = state.Knowledge.GetFacts(playerHolder)
            .Where(f => !f.IsSuperseded)
            .ToList();

        // Find ContractClaim facts with Confidence > 0.40
        var contractFacts = playerFacts
            .Where(f => f.Claim is ContractClaim && f.Confidence > 0.40f)
            .ToList();

        foreach (var contractFact in contractFacts)
        {
            var contract = (ContractClaim)contractFact.Claim;

            // Dedup: already have an active quest for this target
            if (state.Quests.HasActiveContractQuest(contract.TargetSubjectKey))
                continue;

            // Cooldown check
            if (state.Quests.IsOnCooldown(contract.TargetSubjectKey, state.Date))
                continue;

            // Intel gate: require a separate fact about TargetSubjectKey with Confidence > 0.50
            // GoodsDelivered uses a Port:... key — no separate location fact will exist for it.
            // Treat the contract itself as self-validating for GoodsDelivered.
            var intelFact = contract.Condition == ContractConditionType.GoodsDelivered
                ? contractFact   // self-validating: the contract IS the intel
                : playerFacts.FirstOrDefault(f =>
                    KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey
                    && f.Confidence > 0.50f);

            if (intelFact is null)
                continue;

            // Activate quest
            var promptFragment = KnowledgeNarrator.DescribeContractPrompt(
                contractFact, contract.Archetype, state.Date);

            var instance = new ContractQuestInstance
            {
                ContractFactId = contractFact.Id,
                Contract = contract,
                ActivatedDate = state.Date,
                NarrativePromptFragment = promptFragment,
            };

            state.Quests.AddActive(instance);
            state.Quests.RecordCooldown(contract.TargetSubjectKey, state.Date.Advance(20));

            events.Emit(new QuestActivated(
                state.Date, SimulationLod.Local,
                $"Contract:{contract.IssuerId.Value}:{contract.TargetSubjectKey}",
                instance.Id.ToString(),
                $"Contract: {contract.TargetSubjectKey} ({contract.Condition}) — {contract.GoldReward}g"),
                SimulationLod.Local);
        }
    }

    // ---- Helpers ----

    private static bool TryParseShipId(string subjectKey, out ShipId shipId)
    {
        shipId = default;
        // Format: "Ship:{guid}"
        if (!subjectKey.StartsWith("Ship:", StringComparison.OrdinalIgnoreCase)) return false;
        var guidStr = subjectKey["Ship:".Length..];
        if (!Guid.TryParse(guidStr, out var guid)) return false;
        shipId = new ShipId(guid);
        return true;
    }

    private static bool TryParseIndividualId(string subjectKey, out IndividualId individualId)
    {
        individualId = default;
        // Format: "Individual:{guid}"
        if (!subjectKey.StartsWith("Individual:", StringComparison.OrdinalIgnoreCase)) return false;
        var guidStr = subjectKey["Individual:".Length..];
        if (!Guid.TryParse(guidStr, out var guid)) return false;
        individualId = new IndividualId(guid);
        return true;
    }

    private static bool TryParseGoodsDeliveryKey(
        string subjectKey, out PortId portId, out TradeGood good)
    {
        portId = default; good = default;
        // Expected format: "Port:{guid}:{TradeGoodName}"
        var parts = subjectKey.Split(':');
        if (parts.Length != 3 || parts[0] != "Port") return false;
        if (!Guid.TryParse(parts[1], out var g)) return false;
        if (!Enum.TryParse<TradeGood>(parts[2], out good)) return false;
        portId = new PortId(g);
        return true;
    }

    /// <summary>
    /// Aggregates cargo across the entire fleet at the port, deducts iteratively,
    /// mutates state, and returns true if the delivery was completed.
    /// </summary>
    private static bool TryFulfillGoodsDelivery(
        ContractClaim contract, WorldState state, IEventEmitter events, int currentTick)
    {
        if (!TryParseGoodsDeliveryKey(contract.TargetSubjectKey, out var portId, out var good))
            return false;
        const int DeliveryQty = 20;

        // Aggregate available cargo across all fleet ships docked at portId
        var playerShip = state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
        var allFleetIds = state.Player.FleetIds.AsEnumerable();
        if (playerShip is not null)
            allFleetIds = allFleetIds.Prepend(playerShip.Id);

        var dockedFleetShips = allFleetIds
            .Distinct()
            .Where(id => state.Ships.TryGetValue(id, out var s)
                         && s.Location is AtPort ap && ap.Port == portId)
            .Select(id => state.Ships[id])
            .ToList();

        var totalAvailable = dockedFleetShips.Sum(s => s.Cargo.GetValueOrDefault(good));
        if (totalAvailable < DeliveryQty) return false;

        // Deduct iteratively across ships until DeliveryQty satisfied
        var remaining = DeliveryQty;
        foreach (var ship in dockedFleetShips)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, ship.Cargo.GetValueOrDefault(good));
            ship.Cargo[good] -= take;
            if (ship.Cargo[good] <= 0) ship.Cargo.Remove(good);
            remaining -= take;
        }

        // Add supply to port
        if (state.Ports.TryGetValue(portId, out var port))
        {
            port.Economy.Supply[good] = port.Economy.Supply.GetValueOrDefault(good) + DeliveryQty;

            // Crisis 2: Medicine delivery cures epidemic
            if (good == TradeGood.Medicine && port.Conditions.HasFlag(PortConditionFlags.Plague))
            {
                port.Conditions &= ~PortConditionFlags.Plague;
                port.EpidemicTicksRemaining = null;

                // Cure all infected crew in docked ships
                foreach (var dockedShipId in port.DockedShips)
                    if (state.Ships.TryGetValue(dockedShipId, out var dockedShip))
                        dockedShip.HasInfectedCrew = false;
            }
        }

        // Pay player
        state.Player.PersonalGold += contract.GoldReward;

        // Supersede the ContractClaim in PlayerHolder — pass actual tick
        var contractFact = state.Knowledge.GetFacts(new PlayerHolder())
            .FirstOrDefault(f => !f.IsSuperseded && f.Claim is ContractClaim cc
                && KnowledgeFact.GetSubjectKey(cc) == KnowledgeFact.GetSubjectKey(contract));
        if (contractFact is not null)
            state.Knowledge.MarkSuperseded(new PlayerHolder(), contractFact, currentTick);

        events.Emit(new ContractFulfilled(
            state.Date, SimulationLod.Local,
            contract.IssuerId, contract.TargetSubjectKey, contract.GoldReward),
            SimulationLod.Local);

        return true;
    }

    // ======== 5B: NPC Goal Pursuit ========

    private static void TickNpcGoalPursuit(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Phase 1: Assign goals to Pondering NPCs
        AssignNpcGoals(state, context);

        // Phase 2: Tick active pursuits (EpistemicResolver)
        foreach (var (npcId, pursuit) in state.NpcPursuits.ToList())
        {
            switch (pursuit.State)
            {
                case PursuitState.Active:
                    TickActivePursuit(npcId, pursuit, state, context, events);
                    break;

                case PursuitState.Stalled:
                    pursuit.TicksStalled++;
                    if (pursuit.TicksStalled > StallAbandonThreshold)
                    {
                        pursuit.State = PursuitState.Abandoned;
                        EmitPursuitAbandoned(npcId, pursuit, state, events);
                    }
                    break;

                case PursuitState.Completed:
                case PursuitState.Abandoned:
                    // Transition to Pondering for next goal on next tick
                    state.NpcPursuits[npcId] = new GoalPursuit
                    {
                        ActiveGoal = pursuit.ActiveGoal, // placeholder — will be replaced
                        State = PursuitState.Pondering,
                        ActivatedOnTick = context.TickNumber,
                    };
                    break;
            }
        }
    }

    // ---- 5B-3: NPC Goal Assignment ----

    private static void AssignNpcGoals(WorldState state, SimulationContext context)
    {
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;

            // Only combat-capable roles pursue goals
            if (individual.Role is not (IndividualRole.PirateCaptain
                or IndividualRole.NavalOfficer
                or IndividualRole.Privateer
                or IndividualRole.Informant))
                continue;

            // Check if already has a pursuit
            if (state.NpcPursuits.TryGetValue(individual.Id, out var existing)
                && existing.State is not PursuitState.Pondering)
                continue;

            // Rule-based goal assignment: scan IndividualHolder for actionable ContractClaims
            var holder = new IndividualHolder(individual.Id);
            var facts = state.Knowledge.GetFacts(holder)
                .Where(f => !f.IsSuperseded)
                .ToList();

            var bestContract = facts
                .Where(f => f.Claim is ContractClaim && f.Confidence > 0.40f)
                .OrderByDescending(f => f.Confidence)
                .Select(f => (ContractClaim)f.Claim)
                .FirstOrDefault();

            if (bestContract is null) continue;

            // Informants: only TargetDead contracts (assassination, not naval combat)
            if (individual.Role == IndividualRole.Informant
                && bestContract.Condition != ContractConditionType.TargetDead)
                continue;

            // Intel gate: need a separate fact about the target
            var hasIntel = facts.Any(f =>
                KnowledgeFact.GetSubjectKey(f.Claim) == bestContract.TargetSubjectKey
                && f.Confidence > 0.50f);
            if (!hasIntel) continue;

            // Assign goal
            var goal = new FulfillContractGoal(
                Guid.NewGuid(), individual.Id, bestContract);

            state.NpcPursuits[individual.Id] = new GoalPursuit
            {
                ActiveGoal = goal,
                State = PursuitState.Active,
                ActivatedOnTick = context.TickNumber,
            };

            // Set routing — find target's last known location
            SetPursuitRoute(individual, bestContract, facts, state);
        }
    }

    /// <summary>Set the NPC's ship route based on known target location.</summary>
    private static void SetPursuitRoute(Individual npc, ContractClaim contract,
        List<KnowledgeFact> npcFacts, WorldState state)
    {
        // Find the NPC's ship
        var ship = state.Ships.Values.FirstOrDefault(s => s.CaptainId == npc.Id);
        if (ship is null) return;

        // Find target's last known location
        if (contract.Condition == ContractConditionType.TargetDestroyed
            && TryParseShipId(contract.TargetSubjectKey, out var targetShipId))
        {
            var locationFact = npcFacts
                .Where(f => f.Claim is ShipLocationClaim slc
                    && KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey)
                .OrderByDescending(f => f.Confidence)
                .FirstOrDefault();

            if (locationFact?.Claim is ShipLocationClaim slc)
            {
                var targetRegion = slc.LastKnownLocation switch
                {
                    AtSea ats => ats.Region,
                    AtPort atp when state.Ports.TryGetValue(atp.Port, out var tp) => tp.RegionId,
                    EnRoute er => er.To,
                    _ => (RegionId?)null,
                };

                if (targetRegion.HasValue)
                    ship.Route = new PursuitRoute(targetShipId, targetRegion.Value);
            }
        }
        else if (contract.Condition == ContractConditionType.TargetDead
            && TryParseIndividualId(contract.TargetSubjectKey, out var targetIndId))
        {
            // Route to target individual's known location
            var whereabouts = npcFacts
                .Where(f => f.Claim is IndividualWhereaboutsClaim iwc && iwc.Individual == targetIndId)
                .OrderByDescending(f => f.Confidence)
                .FirstOrDefault();

            if (whereabouts?.Claim is IndividualWhereaboutsClaim iwc && iwc.Port.HasValue)
                ship.Route = new PortRoute(iwc.Port.Value);
        }
    }

    // ---- 5B-4: EpistemicResolver — Active Pursuit Tick ----

    private static void TickActivePursuit(IndividualId npcId, GoalPursuit pursuit,
        WorldState state, SimulationContext context, IEventEmitter events)
    {
        if (!state.Individuals.TryGetValue(npcId, out var npc) || !npc.IsAlive)
        {
            pursuit.State = PursuitState.Abandoned;
            return;
        }

        // Dispatch by goal type
        switch (pursuit.ActiveGoal)
        {
            case FulfillContractGoal contractGoal:
                TickContractPursuit(npcId, npc, contractGoal, pursuit, state, context, events);
                break;

            case PatrolRegionGoal patrolGoal:
                TickPatrolPursuit(npcId, npc, patrolGoal, pursuit, state);
                break;

            case RansomGoal ransomGoal:
                TickRansomPursuit(npcId, npc, ransomGoal, pursuit, state, context, events);
                break;

            // ExtortGoal deferred
        }
    }

    private static void TickPatrolPursuit(IndividualId npcId, Individual npc,
        PatrolRegionGoal goal, GoalPursuit pursuit, WorldState state)
    {
        var ship = state.Ships.Values.FirstOrDefault(s => s.CaptainId == npcId);
        if (ship is null) { pursuit.State = PursuitState.Abandoned; return; }

        var currentRegion = state.GetShipRegion(ship.Id);

        // Route toward patrol region if not already there
        if (currentRegion != goal.Region)
        {
            // Find a port in the patrol region to route to
            var patrolPort = state.Ports.Values.FirstOrDefault(p => p.RegionId == goal.Region);
            if (patrolPort is not null)
                ship.Route = new PortRoute(patrolPort.Id);
            return;
        }

        // In the patrol region — loiter. If docked, depart to sea. If at sea, stay.
        if (ship.Location is AtPort && ship.TicksDockedAtCurrentPort >= 2)
        {
            // Depart — pick a random adjacent port to keep moving
            var adjacentPort = state.Ports.Values
                .Where(p => p.RegionId == goal.Region && p.Id != (ship.Location as AtPort)!.Port)
                .FirstOrDefault();
            if (adjacentPort is not null)
                ship.Route = new PortRoute(adjacentPort.Id);
        }

        // Patrol continues until war ends or goal abandoned (30 tick limit)
        if (pursuit.ActivatedOnTick + 30 < state.Date.DaysSinceStart)
            pursuit.State = PursuitState.Completed;
    }

    private static void TickRansomPursuit(IndividualId npcId, Individual npc,
        RansomGoal goal, GoalPursuit pursuit, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ship = state.Ships.Values.FirstOrDefault(s => s.CaptainId == npcId);
        if (ship is null) { pursuit.State = PursuitState.Abandoned; return; }

        // Route toward a port controlled by the target faction
        if (ship.Route is null)
        {
            var targetPort = state.Ports.Values
                .FirstOrDefault(p => p.ControllingFactionId == goal.TargetFactionId);
            if (targetPort is not null)
                ship.Route = new PortRoute(targetPort.Id);
            else
            {
                pursuit.State = PursuitState.Stalled;
                return;
            }
        }

        // If docked at target faction's port — ransom demand is "in progress"
        // Resolution happens via the contract system (TargetRescued condition)
        // For now, the ransom goal auto-completes after 20 ticks (NPC collected ransom or gave up)
        if (pursuit.ActivatedOnTick + 20 < context.TickNumber)
        {
            // Ransom timeout — release captive
            if (state.Individuals.TryGetValue(goal.CaptiveId, out var captive))
                captive.CaptorId = null;
            pursuit.State = PursuitState.Abandoned;
        }
    }

    private static void TickContractPursuit(IndividualId npcId, Individual npc,
        FulfillContractGoal contractGoal, GoalPursuit pursuit,
        WorldState state, SimulationContext context, IEventEmitter events)
    {

        var contract = contractGoal.Contract;
        var ship = state.Ships.Values.FirstOrDefault(s => s.CaptainId == npcId);
        if (ship is null)
        {
            pursuit.State = PursuitState.Abandoned;
            return;
        }

        var holder = new IndividualHolder(npcId);

        // Check if intel has decayed below floor → Stall
        var intelFact = state.Knowledge.GetFacts(holder)
            .FirstOrDefault(f => !f.IsSuperseded
                && KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey);

        if (intelFact is null || intelFact.Confidence < IntelConfidenceFloor)
        {
            pursuit.State = PursuitState.Stalled;
            pursuit.TicksStalled = 1;
            ship.Route = null; // clear pursuit route, fall back to normal routing
            return;
        }

        // Check fulfillment — same logic as player quests
        bool fulfilled = false;
        if (contract.Condition == ContractConditionType.TargetDestroyed
            && TryParseShipId(contract.TargetSubjectKey, out var targetShipId))
        {
            // Check if in same region as target
            var npcRegion = state.GetShipRegion(ship.Id);
            var targetRegion = state.GetShipRegion(targetShipId);

            if (npcRegion.HasValue && targetRegion.HasValue && npcRegion == targetRegion)
            {
                // Same region — attempt interception (simplified stat check)
                if (state.Ships.TryGetValue(targetShipId, out var target) && target.HullIntegrity <= 0)
                    fulfilled = true;
                // Future: actual combat stat check here
            }
        }
        else if (contract.Condition == ContractConditionType.TargetDead
            && TryParseIndividualId(contract.TargetSubjectKey, out var targetIndId))
        {
            if (state.Individuals.TryGetValue(targetIndId, out var target2) && !target2.IsAlive)
                fulfilled = true;

            // Informant assassination: stat-check on arrival at target's port
            if (npc.Role == IndividualRole.Informant && target2 is { IsAlive: true }
                && ship.Location is AtPort atPort && target2.LocationPortId == atPort.Port)
            {
                var cap = npc.FactionId.HasValue
                    && state.Factions.TryGetValue(npc.FactionId.Value, out var f)
                    ? f.IntelligenceCapability : 0.30f;
                // Stat check: ~cap chance of success
                // Note: no RNG available in static context. Defer actual combat to a future system.
                // For now, Informants route but don't auto-resolve.
            }
        }

        if (fulfilled)
        {
            pursuit.State = PursuitState.Completed;
            npc.CurrentGold += contract.GoldReward;
            ship.Route = null;

            var lod = state.GetShipRegion(ship.Id) is { } r ? context.GetLod(r) : SimulationLod.Distant;
            events.Emit(new NpcClaimedContract(
                state.Date, lod, npcId, contract.TargetSubjectKey, contract.GoldReward), lod);

            // Expire player's quest for the same target
            foreach (var quest in state.Quests.ActiveContractQuests)
            {
                if (quest.Contract.TargetSubjectKey == contract.TargetSubjectKey
                    && quest.Status == ContractQuestStatus.Active)
                {
                    quest.Status = ContractQuestStatus.ClaimedByNpc;
                    quest.ResolvedDate = state.Date;
                }
            }
        }
    }

    // ---- Stall & Leak ----

    private static void EmitPursuitAbandoned(IndividualId npcId, GoalPursuit pursuit,
        WorldState state, IEventEmitter events)
    {
        var description = pursuit.ActiveGoal switch
        {
            FulfillContractGoal fcg => $"Abandoned pursuit of {fcg.Contract.TargetSubjectKey}",
            _ => "Abandoned goal",
        };

        var lod = SimulationLod.Distant;
        if (state.Individuals.TryGetValue(npcId, out var npc) && npc.LocationPortId.HasValue
            && state.Ports.TryGetValue(npc.LocationPortId.Value, out var port)
            && state.Regions.ContainsKey(port.RegionId))
        {
            // Emit at the NPC's current location LOD
        }

        events.Emit(new NpcPursuitAbandoned(state.Date, lod, npcId, description), lod);

        // Stall & Leak: inject highest-confidence fact about the goal into port gossip
        // If AtSea, fact stays in ShipHolder and leaks on next dock (via ArrivedThisTick propagation)
        var ship = state.Ships.Values.FirstOrDefault(s => s.CaptainId == npcId);
        if (ship is null) return;

        ship.Route = null; // clear pursuit route

        if (pursuit.ActiveGoal is FulfillContractGoal contractGoal)
        {
            var holder = new IndividualHolder(npcId);
            var bestFact = state.Knowledge.GetFacts(holder)
                .Where(f => !f.IsSuperseded
                    && KnowledgeFact.GetSubjectKey(f.Claim) == contractGoal.Contract.TargetSubjectKey)
                .OrderByDescending(f => f.Confidence)
                .FirstOrDefault();

            if (bestFact is not null)
            {
                // If docked, leak directly to port. If at sea, seed into ShipHolder for dock propagation.
                if (ship.Location is AtPort atPort)
                {
                    state.Knowledge.AddFact(new PortHolder(atPort.Port), bestFact);
                }
                else
                {
                    // Already in ShipHolder — it will propagate on next dock via ArrivedThisTick
                    state.Knowledge.AddFact(new ShipHolder(ship.Id), bestFact);
                }
            }
        }
    }
}
