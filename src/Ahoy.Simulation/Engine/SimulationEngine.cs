using Ahoy.Core.Ids;
using Ahoy.Simulation.Decisions;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Systems;

namespace Ahoy.Simulation.Engine;

/// <summary>
/// Orchestrates the 12-step tick pipeline.
/// Owns the TickEventEmitter and the ordered system list.
/// Exposes WorldSnapshot and an event callback for the frontend.
/// </summary>
public sealed class SimulationEngine
{
    private readonly WorldState _state;
    private readonly IPlayerCommandQueue _commandQueue;
    private readonly TickEventEmitter _emitter;
    private readonly IReadOnlyList<IWorldSystem> _systems;
    private readonly DecisionQueue _decisionQueue;

    private int _tickNumber;

    public event Action<WorldSnapshot>? TickCompleted;
    public event Action<WorldEvent>? EventOccurred;

    public WorldState State => _state;

    /// <summary>Enqueue a player command to be applied at the start of the next tick.</summary>
    public void EnqueueCommand(PlayerCommand command) => _commandQueue.Enqueue(command);

    private SimulationEngine(
        WorldState state,
        IPlayerCommandQueue commandQueue,
        TickEventEmitter emitter,
        IReadOnlyList<IWorldSystem> systems,
        DecisionQueue decisionQueue)
    {
        _state = state;
        _commandQueue = commandQueue;
        _emitter = emitter;
        _systems = systems;
        _decisionQueue = decisionQueue;
    }

    /// <summary>
    /// Factory — wires up all systems in the correct order.
    /// Supply an IAsyncActorDecisionProvider to enable LLM decisions.
    /// </summary>
    public static SimulationEngine BuildEngine(
        WorldState state,
        IPlayerCommandQueue? commandQueue = null,
        IAsyncActorDecisionProvider? llmProvider = null,
        IReadOnlyList<QuestTemplate>? questTemplates = null,
        Random? rng = null)
    {
        rng ??= new Random();
        commandQueue ??= new PlayerCommandQueue();

        var emitter = new TickEventEmitter();
        var syncDecisions = new RuleBasedDecisionProvider();
        var decisionQueue = new DecisionQueue(syncDecisions, llmProvider);

        var systems = new List<IWorldSystem>
        {
            new WeatherSystem(rng),                                        // Step 1
            new ShipMovementSystem(rng),                                   // Step 2
            new EconomySystem(rng),                                        // Step 3
            new FactionSystem(rng),                                        // Step 4
            new EventPropagationSystem(emitter),                           // Step 5
            new KnowledgeSystem(emitter, rng),                             // Step 6
            new QuestSystem(questTemplates ?? Array.Empty<QuestTemplate>()), // Step 7
        };

        return new SimulationEngine(state, commandQueue, emitter, systems, decisionQueue);
    }

    /// <summary>
    /// Advance the simulation by one tick.
    /// Thread-safety: must be called from a single thread (the simulation thread).
    /// </summary>
    public void Tick()
    {
        _tickNumber++;

        // === PRE-TICK ===

        // 1. Apply pending player commands
        var commands = _commandQueue.DrainPending();
        foreach (var cmd in commands)
            ApplyCommand(cmd);

        // 2. Drain completed async decisions
        var completedDecisions = _decisionQueue.DrainCompleted();
        foreach (var request in completedDecisions)
            ApplyCompletedDecision(request);

        // 3. Advance simulation date
        _state.Date = _state.Date.Advance(1);

        // 4. Build immutable SimulationContext (LOD map)
        var context = new SimulationContext(_state, _tickNumber);

        // === SYSTEMS (1–6) ===
        foreach (var system in _systems)
            system.Tick(_state, context, _emitter);

        // === POST-TICK ===

        // 5. Clear transient ship arrival flags
        foreach (var ship in _state.Ships.Values)
            ship.ArrivedThisTick = false;

        // 6. Expire price modifiers (already decremented in EconomySystem)

        // 7. Drain all events — dispatch to frontend
        var tickEvents = _emitter.DrainAll();

        foreach (var ev in tickEvents)
            EventOccurred?.Invoke(ev);

        // 8. Build and dispatch snapshot
        var snapshot = BuildSnapshot(tickEvents, context);
        TickCompleted?.Invoke(snapshot);
    }

    // ---- Command application ----

    private void ApplyCommand(PlayerCommand command)
    {
        switch (command)
        {
            case SetCourseCommand sc:
                if (_state.Ships.TryGetValue(sc.ShipId, out var ship) &&
                    _state.Ports.TryGetValue(sc.DestinationPort, out var destPort))
                {
                    ship.RoutingDestination = sc.DestinationPort;
                    // If at port, departure will be handled by ShipMovementSystem
                    // If at sea, movement system will plot course
                }
                break;

            case AnchorCommand ac:
                if (_state.Ships.TryGetValue(ac.ShipId, out var anchorShip))
                    anchorShip.RoutingDestination = null;
                break;

            case BuyGoodCommand buy:
                if (_state.Ships.TryGetValue(buy.ShipId, out var buyShip) &&
                    _state.Ports.TryGetValue(buy.PortId, out var buyPort) &&
                    buyShip.Location is AtPort buyAtPort &&
                    buyAtPort.Port == buy.PortId)
                {
                    var supply = buyPort.Economy.Supply.GetValueOrDefault(buy.Good);
                    var qty = Math.Min(buy.Quantity, supply);
                    var price = buyPort.Economy.EffectivePrice(buy.Good);
                    var cost = price * qty;

                    if (_state.Player.PersonalGold >= cost && qty > 0)
                    {
                        _state.Player.PersonalGold -= cost;
                        buyPort.Economy.Supply[buy.Good] -= qty;
                        buyShip.Cargo[buy.Good] = buyShip.Cargo.GetValueOrDefault(buy.Good) + qty;
                        _emitter.Emit(new TradeCompleted(_state.Date, Core.Enums.SimulationLod.Local,
                            buy.ShipId, buy.PortId, buy.Good, qty, price, true),
                            Core.Enums.SimulationLod.Local);
                    }
                }
                break;

            case SellGoodCommand sell:
                if (_state.Ships.TryGetValue(sell.ShipId, out var sellShip) &&
                    _state.Ports.TryGetValue(sell.PortId, out var sellPort) &&
                    sellShip.Location is AtPort sellAtPort &&
                    sellAtPort.Port == sell.PortId)
                {
                    var cargoQty = sellShip.Cargo.GetValueOrDefault(sell.Good);
                    var qty = Math.Min(sell.Quantity, cargoQty);
                    var price = sellPort.Economy.EffectivePrice(sell.Good);

                    if (qty > 0)
                    {
                        _state.Player.PersonalGold += price * qty;
                        sellShip.Cargo[sell.Good] -= qty;
                        if (sellShip.Cargo[sell.Good] <= 0) sellShip.Cargo.Remove(sell.Good);
                        sellPort.Economy.Supply[sell.Good] = sellPort.Economy.Supply.GetValueOrDefault(sell.Good) + qty;
                        _emitter.Emit(new TradeCompleted(_state.Date, Core.Enums.SimulationLod.Local,
                            sell.ShipId, sell.PortId, sell.Good, qty, price, false),
                            Core.Enums.SimulationLod.Local);
                    }
                }
                break;

            case ChooseQuestBranchCommand qb:
                var quest = _state.Quests.ActiveQuests
                    .FirstOrDefault(q => q.Id == qb.QuestInstanceId);
                if (quest is null) break;
                var branch = quest.Template.Branches
                    .FirstOrDefault(b => b.BranchId == qb.BranchId);
                if (branch is null) break;
                quest.ChosenBranch = branch;
                quest.Status       = QuestStatus.Completed;
                quest.ResolvedDate = _state.Date;
                foreach (var ev in branch.OutcomeEvents(_state))
                    _emitter.Emit(ev, ev.SourceLod);
                _state.Quests.Resolve(quest);
                break;
        }
    }

    private static void ApplyCompletedDecision(ActorDecisionRequest request)
    {
        if (request.PendingMatrix is null) return;
        request.ResolvedDecision = request.PendingMatrix.Resolve(request.Intervention);
    }

    // ---- Snapshot builder ----

    private WorldSnapshot BuildSnapshot(IReadOnlyList<WorldEvent> events, SimulationContext context)
    {
        var playerFacts = _state.Knowledge.GetFacts(new State.PlayerHolder());

        var observablePorts = _state.Ports.Values
            .Where(p => context.GetLod(p.RegionId) != Core.Enums.SimulationLod.Distant ||
                        playerFacts.Any(f => f.Claim is PortPriceClaim pc && pc.Port == p.Id))
            .Select(p => new PortSnapshot
            {
                Id = p.Id,
                Name = p.Name,
                RegionId = p.RegionId,
                Prosperity = p.Prosperity,
                InstitutionalReputation = p.InstitutionalReputation,
                PersonalReputation = p.PersonalReputation,
                KnownPrices = playerFacts
                    .Where(f => f.Claim is PortPriceClaim pc && pc.Port == p.Id && f.Confidence > 0.3f)
                    .Select(f => (PortPriceClaim)f.Claim)
                    .GroupBy(c => c.Good)
                    .ToDictionary(g => g.Key, g => g.Last().Price),
            })
            .ToList();

        // Filter observable ships — player ships always visible; others by knowledge
        var observableShips = _state.Ships.Values
            .Where(s => s.IsPlayerShip ||
                        playerFacts.Any(f => f.Claim is ShipLocationClaim sc && sc.Ship == s.Id && f.Confidence > 0.2f))
            .Select(s =>
            {
                var locFact = playerFacts.FirstOrDefault(f =>
                    f.Claim is ShipLocationClaim sc && sc.Ship == s.Id);
                return new ShipSnapshot
                {
                    Id = s.Id,
                    Name = s.Name,
                    Location = s.IsPlayerShip ? s.Location
                        : (locFact?.Claim is ShipLocationClaim slc ? slc.LastKnownLocation : s.Location),
                    ConfidenceInLocation = s.IsPlayerShip ? 1.0f : (locFact?.Confidence ?? 0f),
                    IsPlayerShip = s.IsPlayerShip,
                };
            })
            .ToList();

        return new WorldSnapshot
        {
            Date = _state.Date,
            TickNumber = _tickNumber,
            CaptainName = _state.Player.CaptainName,
            PlayerRegionId = _state.Player.CurrentRegionId,
            FleetIds = _state.Player.FleetIds,
            PersonalGold = _state.Player.PersonalGold,
            Notoriety = _state.Player.Notoriety,
            ObservablePorts = observablePorts,
            ObservableShips = observableShips,
            TickEvents = events,
        };
    }
}
