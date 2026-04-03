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
    private readonly Random _rng;

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
        DecisionQueue decisionQueue,
        Random rng)
    {
        _state = state;
        _commandQueue = commandQueue;
        _emitter = emitter;
        _systems = systems;
        _decisionQueue = decisionQueue;
        _rng = rng;
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

        return new SimulationEngine(state, commandQueue, emitter, systems, decisionQueue, rng);
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
        var context = new SimulationContext(_state, _tickNumber, _rng);

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
                        sellPort.PersonalReputation += 1f;
                        _emitter.Emit(new TradeCompleted(_state.Date, Core.Enums.SimulationLod.Local,
                            sell.ShipId, sell.PortId, sell.Good, qty, price, false),
                            Core.Enums.SimulationLod.Local);
                    }
                }
                break;

            case BribeGovernorCommand bribe:
            {
                if (!_state.Individuals.TryGetValue(bribe.GovernorId, out var governor)) break;
                if (!governor.IsAlive || governor.Role != Core.Enums.IndividualRole.Governor) break;
                if (!governor.LocationPortId.HasValue) break;
                if (!_state.Ports.TryGetValue(governor.LocationPortId.Value, out var briberPort)) break;

                // Reject path: too honourable or too hostile
                if (governor.Authority > 80f || governor.PlayerRelationship < -50f)
                {
                    governor.PlayerRelationship = Math.Clamp(governor.PlayerRelationship - 5f, -100f, 100f);
                    _emitter.Emit(new BribeRejected(_state.Date, Core.Enums.SimulationLod.Local,
                        bribe.GovernorId, briberPort.Id), Core.Enums.SimulationLod.Local);
                    break;
                }

                if (_state.Player.PersonalGold < bribe.GoldAmount) break;
                _state.Player.PersonalGold -= bribe.GoldAmount;

                // Diminishing returns: each bribe is worth less as relationship rises
                var gain = (bribe.GoldAmount / 20f) * (1.0f - (Math.Max(0f, governor.PlayerRelationship) / 80f));
                governor.PlayerRelationship = Math.Clamp(governor.PlayerRelationship + gain, -100f, 80f);
                briberPort.PersonalReputation += 5f;

                _emitter.Emit(new BribeAccepted(_state.Date, Core.Enums.SimulationLod.Local,
                    bribe.GovernorId, briberPort.Id, bribe.GoldAmount), Core.Enums.SimulationLod.Local);
                break;
            }

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
                // PlayerHolder copy is decay-exempt: the player's own ledger of their actions
                // is a record of agency, not an epistemic guess that should fade.
                var playerCopyFact = new State.KnowledgeFact
                {
                    Claim          = new State.PlayerActionClaim(quest.Template.Id.Value, branch.BranchId, playerPortForAction),
                    Sensitivity    = Core.Enums.KnowledgeSensitivity.Restricted,
                    Confidence     = 1.0f,
                    BaseConfidence = 1.0f,
                    ObservedDate   = _state.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                    IsDecayExempt  = true,
                };
                _state.Knowledge.AddFact(new State.PlayerHolder(), playerCopyFact);
                // Port copy decays normally — witnesses forget, but the player never does.
                if (playerPortForAction.HasValue)
                {
                    var portCopyFact = new State.KnowledgeFact
                    {
                        Claim          = new State.PlayerActionClaim(quest.Template.Id.Value, branch.BranchId, playerPortForAction),
                        Sensitivity    = Core.Enums.KnowledgeSensitivity.Restricted,
                        Confidence     = 1.0f,
                        BaseConfidence = 1.0f,
                        ObservedDate   = _state.Date,
                        HopCount       = 0,
                        SourceHolder   = null,
                    };
                    _state.Knowledge.AddFact(new State.PortHolder(playerPortForAction.Value), portCopyFact);
                }

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

            case InvestigateLocalCommand local:
            {
                // Prior-fact gate: player must already know something about this subject
                var priorFact = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .FirstOrDefault(f => !f.IsSuperseded
                        && KnowledgeFact.GetSubjectKey(f.Claim) == local.SubjectKey);
                if (priorFact is null) break;

                // Player must be at a port
                var playerShip = _state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
                if (playerShip?.Location is not State.AtPort localAtPort) break;

                // Find the best matching fact in the current port's knowledge pool
                var portFact = _state.Knowledge.GetFacts(new State.PortHolder(localAtPort.Port))
                    .Where(f => !f.IsSuperseded
                        && KnowledgeFact.GetSubjectKey(f.Claim) == local.SubjectKey)
                    .OrderByDescending(f => f.Confidence)
                    .FirstOrDefault();

                if (portFact != null)
                {
                    var observed = new State.KnowledgeFact
                    {
                        Claim = portFact.Claim,
                        Sensitivity = portFact.Sensitivity,
                        Confidence = 0.90f,
                        BaseConfidence = 0.90f,
                        ObservedDate = _state.Date,
                        HopCount = 0,
                        SourceHolder = null,
                        OriginatingAgentId = portFact.OriginatingAgentId,
                    };
                    _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), observed, _tickNumber);
                    _state.Knowledge.AddFact(new State.PlayerHolder(), observed);
                    _emitter.Emit(new Events.InvestigationResolved(_state.Date, Core.Enums.SimulationLod.Local,
                        local.SubjectKey, observed.Id, true), Core.Enums.SimulationLod.Local);
                }
                else
                {
                    _emitter.Emit(new Events.InvestigationResolved(_state.Date, Core.Enums.SimulationLod.Local,
                        local.SubjectKey, null, false), Core.Enums.SimulationLod.Local);
                }
                break;
            }

            case InvestigateRemoteCommand remote:
            {
                // Prior-fact gate
                var hasPrior = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .Any(f => !f.IsSuperseded
                        && KnowledgeFact.GetSubjectKey(f.Claim) == remote.SubjectKey);
                if (!hasPrior) break;

                if (_state.Player.PersonalGold < remote.GoldCost) break;

                _state.Player.PersonalGold -= remote.GoldCost;
                _state.PendingInvestigations.Add(new State.PendingInvestigation
                {
                    SubjectKey = remote.SubjectKey,
                    GoldCost = remote.GoldCost,
                    SubmittedOnTick = _tickNumber,
                });
                break;
            }

            case BuyKnowledgeCommand buy2:
            {
                if (!_state.Individuals.TryGetValue(buy2.BrokerId, out var buyBroker)) break;
                if (buyBroker.Role != Core.Enums.IndividualRole.KnowledgeBroker) break;
                if (!buyBroker.IsAlive || buyBroker.IsCompromised) break;

                // Broker must be at the same port as the player
                var playerShipForBuy = _state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
                if (playerShipForBuy?.Location is not State.AtPort buyAtPort2) break;
                if (buyBroker.LocationPortId != buyAtPort2.Port) break;

                // Find the fact in broker's IndividualHolder
                var buyFact = _state.Knowledge.GetFacts(new State.IndividualHolder(buy2.BrokerId))
                    .FirstOrDefault(f => f.Id == buy2.FactId && !f.IsSuperseded);
                if (buyFact is null) break;

                if (_state.Player.PersonalGold < buy2.Price) break;
                _state.Player.PersonalGold -= buy2.Price;

                // Copy fact to player's knowledge
                var buyCopy = new State.KnowledgeFact
                {
                    Claim = buyFact.Claim,
                    Sensitivity = buyFact.Sensitivity,
                    Confidence = buyFact.Confidence,
                    BaseConfidence = buyFact.Confidence,
                    ObservedDate = buyFact.ObservedDate,
                    IsDisinformation = buyFact.IsDisinformation,
                    HopCount = buyFact.HopCount + 1,
                    SourceHolder = new State.IndividualHolder(buy2.BrokerId),
                    OriginatingAgentId = buyFact.OriginatingAgentId,
                };
                _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), buyCopy, _tickNumber);
                _state.Knowledge.AddFact(new State.PlayerHolder(), buyCopy);

                buyBroker.PlayerRelationship = Math.Clamp(buyBroker.PlayerRelationship + 3f, -100f, 100f);
                break;
            }

            case SellFactCommand sell:
            {
                // Find broker
                if (!_state.Individuals.TryGetValue(sell.BrokerId, out var broker)) break;
                if (broker.Role != Core.Enums.IndividualRole.KnowledgeBroker) break;
                if (!broker.IsAlive || broker.IsCompromised) break;

                // Broker must be at the same port as the player
                var playerShipForSell = _state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
                if (playerShipForSell?.Location is not State.AtPort sellFactAtPort) break;
                if (broker.LocationPortId != sellFactAtPort.Port) break;

                // Find the fact in player's knowledge
                var playerFact = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .FirstOrDefault(f => f.Id == sell.FactId && !f.IsSuperseded);
                if (playerFact is null) break;

                // Confidence floor
                if (playerFact.Confidence < 0.60f) break;

                // Deduplication: broker must not already have a better version
                var brokerHolder = new State.IndividualHolder(sell.BrokerId);
                var subjectKey = KnowledgeFact.GetSubjectKey(playerFact.Claim);
                var brokerExisting = _state.Knowledge.GetFacts(brokerHolder)
                    .FirstOrDefault(f => !f.IsSuperseded
                        && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey);
                if (brokerExisting != null && brokerExisting.Confidence >= playerFact.Confidence) break;

                // Price: draw from faction treasury
                if (!broker.FactionId.HasValue) break;
                if (!_state.Factions.TryGetValue(broker.FactionId.Value, out var brokerFaction)) break;

                var price = playerFact.Sensitivity switch
                {
                    Core.Enums.KnowledgeSensitivity.Public  => 50,
                    Core.Enums.KnowledgeSensitivity.Restricted => 150,
                    Core.Enums.KnowledgeSensitivity.Secret => 300,
                    _ => 50,
                };
                price = (int)(price * playerFact.Confidence); // scale by confidence

                if (brokerFaction.TreasuryGold < price) break;
                brokerFaction.TreasuryGold -= price;
                _state.Player.PersonalGold += price;

                // Copy fact to broker's IndividualHolder
                var brokerCopy = new State.KnowledgeFact
                {
                    Claim = playerFact.Claim,
                    Sensitivity = playerFact.Sensitivity,
                    Confidence = playerFact.Confidence * 0.85f, // slight discount for second-hand
                    BaseConfidence = playerFact.Confidence * 0.85f,
                    ObservedDate = playerFact.ObservedDate,
                    IsDisinformation = playerFact.IsDisinformation,
                    HopCount = playerFact.HopCount + 1,
                    SourceHolder = new State.PlayerHolder(),
                    OriginatingAgentId = playerFact.OriginatingAgentId,
                };
                if (brokerExisting != null)
                    _state.Knowledge.MarkSuperseded(brokerHolder, brokerCopy, _tickNumber);
                _state.Knowledge.AddFact(brokerHolder, brokerCopy);

                broker.PlayerRelationship = Math.Clamp(broker.PlayerRelationship + 5f, -100f, 100f);
                break;
            }

            case BurnAgentCommand burn:
            {
                if (!_state.Individuals.TryGetValue(burn.AgentId, out var target)) break;
                if (!target.IsAlive || target.IsCompromised) break;

                // Player must have a fact that traces back to this agent
                var hasLinkedFact = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .Any(f => !f.IsSuperseded && f.OriginatingAgentId == burn.AgentId);
                if (!hasLinkedFact) break;

                // Burn the agent
                target.IsCompromised = true;

                // Penalise the agent's entire IndividualHolder inventory
                foreach (var fact in _state.Knowledge.GetFacts(new State.IndividualHolder(burn.AgentId)))
                    fact.Confidence = Math.Max(0f, fact.Confidence - 0.30f);

                // Emit event
                var owningFaction = target.FactionId
                    ?? _state.Factions.Keys.FirstOrDefault();
                if (target.FactionId.HasValue)
                {
                    _emitter.Emit(new Events.AgentBurned(_state.Date, Core.Enums.SimulationLod.Local,
                        burn.AgentId, target.FactionId.Value), Core.Enums.SimulationLod.Local);
                }

                // Seed public rumour in agent's current port; penalise port rep
                if (target.LocationPortId.HasValue)
                {
                    var assetExposed = new State.KnowledgeFact
                    {
                        Claim = new State.CustomClaim("AssetExposed",
                            $"{target.FullName} was exposed as a spy"),
                        Sensitivity = Core.Enums.KnowledgeSensitivity.Restricted,
                        Confidence = 0.85f,
                        BaseConfidence = 0.85f,
                        ObservedDate = _state.Date,
                        HopCount = 0,
                        SourceHolder = null,
                    };
                    _state.Knowledge.AddFact(
                        new State.PortHolder(target.LocationPortId.Value), assetExposed);

                    // Port rep hit — dockworkers and merchants saw what happened
                    if (_state.Ports.TryGetValue(target.LocationPortId.Value, out var burnPort))
                        burnPort.PersonalReputation -= 20f;
                }

                // Faction members at the same port resent the player
                if (target.FactionId.HasValue && target.LocationPortId.HasValue)
                {
                    foreach (var ind in _state.Individuals.Values)
                    {
                        if (ind.IsAlive
                            && ind.FactionId == target.FactionId
                            && ind.LocationPortId == target.LocationPortId
                            && ind.Id != burn.AgentId)
                        {
                            ind.PlayerRelationship = Math.Clamp(ind.PlayerRelationship - 15f, -100f, 100f);
                        }
                    }
                }

                // Player gains notoriety for burning an asset
                _state.Player.Notoriety = Math.Clamp(_state.Player.Notoriety + 5f, 0f, 100f);
                break;
            }
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

        // Resolve the deceiving faction from the lie's source holder.
        // The faction whose controlled port seeded the lie is the deceiver.
        var deceivingFaction = lie.SourceHolder switch
        {
            State.FactionHolder fh => (Core.Ids.FactionId?)fh.Faction,
            State.PortHolder ph when state.Ports.TryGetValue(ph.Port, out var p) => p.ControllingFactionId,
            _ => null,
        };

        if (deceivingFaction.HasValue)
        {
            // Emit the event so other systems (and eventually FactionSystem goal-scoring) can react.
            var exposedAtPort = (correction.Claim is State.ShipLocationClaim slc2)
                ? (state.Ships.TryGetValue(slc2.Ship, out var s2) && s2.Location is State.AtPort ap2 ? (Core.Ids.PortId?)ap2.Port : null)
                : null;
            _emitter.Emit(
                new Events.DeceptionExposed(state.Date, Core.Enums.SimulationLod.Local,
                    deceivingFaction.Value, exposedAtPort),
                Core.Enums.SimulationLod.Local);

            // Seed a fact to the deceiving faction's FactionHolder so their
            // future decision-making can know the player detected their plant.
            state.Knowledge.AddFact(
                new State.FactionHolder(deceivingFaction.Value),
                new State.KnowledgeFact
                {
                    Claim          = new State.CustomClaim("DeceptionExposed",
                                         $"Deception against player detected and corrected."),
                    Sensitivity    = Core.Enums.KnowledgeSensitivity.Secret,
                    Confidence     = 0.90f,
                    BaseConfidence = 0.90f,
                    ObservedDate   = state.Date,
                    HopCount       = 0,
                    SourceHolder   = null,
                });
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
