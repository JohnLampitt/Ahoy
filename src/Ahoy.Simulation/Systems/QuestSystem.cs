using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 7 — runs after KnowledgeSystem.
/// Contract-only quest system:
///   1. Ticks active contract quests (fulfillment checks, LostTrail, expiry).
///   2. Scans player ContractClaims and activates new ContractQuestInstances.
/// </summary>
public sealed class QuestSystem : IWorldSystem
{
    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        TickActiveContractQuests(state, context, events);
        ScanForContractQuests(state, context, events);
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
            port.Economy.Supply[good] = port.Economy.Supply.GetValueOrDefault(good) + DeliveryQty;

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
}
