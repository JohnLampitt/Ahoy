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
            new WeatherSystem(rng),                                          // Step 1
            new ShipMovementSystem(rng),                                     // Step 2
            new EconomySystem(rng),                                          // Step 3
            new FactionSystem(rng),                                          // Step 4
            new IndividualLifecycleSystem(rng),                              // Step 5
            new EventPropagationSystem(emitter),                             // Step 6
            new KnowledgeSystem(emitter, rng),                               // Step 7
            new QuestSystem(questTemplates ?? Array.Empty<QuestTemplate>()), // Step 8
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
                var playerFacts = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .Where(f => !f.IsSuperseded).ToList();
                var branch = quest.Template.Branches
                    .FirstOrDefault(b => b.BranchId == qb.BranchId
                        && (b.AvailabilityCondition is null || b.AvailabilityCondition(playerFacts)));
                if (branch is null) break;

                // Disinformation check: if trigger facts were planted, the player acted on false intel.
                // Inject correcting observations so the epistemic state self-heals without flag leakage.
                var disinfoFacts = quest.TriggerFacts.Where(f => f.IsDisinformation).ToList();
                var resolvedStatus = disinfoFacts.Count > 0 ? QuestStatus.Failed : QuestStatus.Completed;
                foreach (var df in disinfoFacts)
                    InjectDisinformationCorrection(df, _state, _tickNumber);

                quest.ChosenBranch = branch;
                quest.Status       = resolvedStatus;
                quest.ResolvedDate = _state.Date;
                foreach (var ev in branch.OutcomeEvents(_state))
                    _emitter.Emit(ev, ev.SourceLod);
                ApplyOutcomeActions(branch.OutcomeActions, quest, _state, _tickNumber);

                // Auto-emit PlayerActionClaim — world-observable record of the player's choice.
                // Faction systems read FactionHolder's knowledge pool to update standings;
                // they do NOT get magic direct notification.
                var playerPortForAction = (_state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip)?.Location
                    as State.AtPort)?.Port;
                var actionFact = new State.KnowledgeFact
                {
                    Claim          = new State.PlayerActionClaim(quest.Template.Id.Value, branch.BranchId, playerPortForAction),
                    Sensitivity    = Core.Enums.KnowledgeSensitivity.Restricted,
                    Confidence     = 1.0f,
                    BaseConfidence = 1.0f,
                    ObservedDate   = _state.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                };
                _state.Knowledge.AddFact(new State.PlayerHolder(), actionFact);
                if (playerPortForAction.HasValue)
                    _state.Knowledge.AddFact(new State.PortHolder(playerPortForAction.Value), actionFact);

                // Cooldown: suppress re-triggering this quest/subject for 20 ticks
                var actionSubjectKey = quest.TriggerFacts.Count > 0
                    ? KnowledgeFact.GetSubjectKey(quest.TriggerFacts[0].Claim)
                    : quest.Template.Id.Value;
                _state.Quests.RecordCooldown(quest.Template.Id, actionSubjectKey, _state.Date.Advance(20));

                _emitter.Emit(new QuestResolved(
                    _state.Date, Core.Enums.SimulationLod.Local,
                    quest.Template.Id.Value, quest.Id.ToString(),
                    quest.Title, resolvedStatus, branch.BranchId),
                    Core.Enums.SimulationLod.Local);
                _state.Quests.Resolve(quest);
                break;
        }
    }

    private void ApplyOutcomeActions(
        IReadOnlyList<Quests.QuestOutcomeAction> actions,
        Quests.QuestInstance quest,
        WorldState state,
        int tickNumber)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case Quests.SupersedeTriggerFacts:
                    foreach (var fact in quest.TriggerFacts)
                        state.Knowledge.MarkSuperseded(new State.PlayerHolder(), fact, tickNumber);
                    break;

                case Quests.AddKnowledgeFact af:
                    var newFact = new State.KnowledgeFact
                    {
                        Claim = af.Claim,
                        Sensitivity = af.Sensitivity,
                        Confidence = af.Confidence,
                        BaseConfidence = af.Confidence,
                        ObservedDate = state.Date,
                    };
                    foreach (var holder in af.Holders)
                    {
                        state.Knowledge.MarkSuperseded(holder, newFact, tickNumber);
                        state.Knowledge.AddFact(holder, newFact);
                    }
                    break;

                case Quests.EmitRumourAction er:
                    var portId = er.PortSelector(state);
                    if (portId.HasValue)
                        _emitter.Emit(
                            new Events.RumourSpread(state.Date, Core.Enums.SimulationLod.Regional,
                                portId.Value, er.Text),
                            Core.Enums.SimulationLod.Regional);
                    break;
            }
        }
    }

    /// <summary>
    /// When the player acts on a disinformation fact, inject a correcting observation
    /// into their knowledge store. This creates a KnowledgeConflict (or supersedes the lie)
    /// without ever leaking the IsDisinformation flag to the UI.
    /// Also degrades the injecting source's reliability.
    /// </summary>
    private void InjectDisinformationCorrection(State.KnowledgeFact lie, WorldState state, int tick)
    {
        State.KnowledgeFact correction;

        if (lie.Claim is State.ShipLocationClaim slc
            && state.Ships.TryGetValue(slc.Ship, out var actualShip))
        {
            // The ship IS somewhere — reveal its actual location as a direct observation.
            correction = new State.KnowledgeFact
            {
                Claim          = new State.ShipLocationClaim(slc.Ship, actualShip.Location),
                Sensitivity    = Core.Enums.KnowledgeSensitivity.Public,
                Confidence     = 0.90f,
                BaseConfidence = 0.90f,
                ObservedDate   = state.Date,
                HopCount       = 0,
                SourceHolder   = null,
            };
        }
        else
        {
            // Generic refutation for other claim types — marks the subject as contested.
            correction = new State.KnowledgeFact
            {
                Claim          = new State.CustomClaim("Disinformation",
                                     $"The intelligence about '{lie.Claim.GetType().Name.Replace("Claim", "")}' proved false."),
                Sensitivity    = Core.Enums.KnowledgeSensitivity.Public,
                Confidence     = 0.90f,
                BaseConfidence = 0.90f,
                ObservedDate   = state.Date,
                HopCount       = 0,
                SourceHolder   = null,
            };
            // Also supersede the original lie so it doesn't keep triggering quests.
            state.Knowledge.MarkSuperseded(new State.PlayerHolder(), lie, tick);
        }

        // Adding the correction triggers KnowledgeConflict detection in the store.
        state.Knowledge.AddFact(new State.PlayerHolder(), correction);

        // Degrade the reliability of whatever source passed this to the player.
        if (lie.SourceHolder is not null)
            state.Knowledge.RecordSourceOutcome(lie.SourceHolder, wasAccurate: false);
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
