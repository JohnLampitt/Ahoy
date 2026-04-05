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
        Random? rng = null)
    {
        rng ??= new Random();
        commandQueue ??= new PlayerCommandQueue();

        var emitter = new TickEventEmitter();
        var syncDecisions = new RuleBasedDecisionProvider();
        var decisionQueue = new DecisionQueue(syncDecisions, llmProvider);

        var systems = new List<IWorldSystem>
        {
            new WeatherSystem(rng),              // Step 1
            new ShipMovementSystem(rng),         // Step 2
            new EconomySystem(rng),              // Step 3
            new FactionSystem(rng),              // Step 4
            new IndividualLifecycleSystem(rng),  // Step 5
            new EventPropagationSystem(emitter), // Step 6
            new KnowledgeSystem(emitter, rng),   // Step 7
            new QuestSystem(),                   // Step 8
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
                    ship.Route = new State.PortRoute(sc.DestinationPort);
                }
                break;

            case AnchorCommand ac:
                if (_state.Ships.TryGetValue(ac.ShipId, out var anchorShip))
                    anchorShip.Route = null;
                break;

            case SetCourseToPoi scp:
                if (_state.Ships.TryGetValue(scp.ShipId, out var poiShip)
                    && _state.OceanPois.ContainsKey(scp.PoiId))
                {
                    poiShip.Route = new State.PoiRoute(scp.PoiId);
                }
                break;

            case SetFleetCourseCommand sfc:
            {
                if (!_state.Ports.ContainsKey(sfc.DestinationPort)) break;
                var convoyId = Guid.NewGuid();
                var dockedShips = new List<ShipId>();
                PortId? firstPort = null;

                foreach (var shipId in _state.Player.FleetIds)
                {
                    if (!_state.Ships.TryGetValue(shipId, out var fleetShip)) continue;
                    if (fleetShip.Location is not State.AtPort fleetAtPort) continue;

                    fleetShip.ConvoyId = convoyId;
                    fleetShip.Route = new State.PortRoute(sfc.DestinationPort);
                    dockedShips.Add(shipId);
                    firstPort ??= fleetAtPort.Port;
                }

                if (dockedShips.Count > 0 && firstPort.HasValue)
                {
                    _emitter.Emit(new Events.FleetDeparted(
                        _state.Date, Core.Enums.SimulationLod.Local,
                        dockedShips, firstPort.Value, sfc.DestinationPort),
                        Core.Enums.SimulationLod.Local);
                }
                break;
            }

            case TransferCargoCommand xfer:
            {
                if (!_state.Ships.TryGetValue(xfer.FromShip, out var fromS)) break;
                if (!_state.Ships.TryGetValue(xfer.ToShip, out var toS)) break;

                // Both ships must be AtPort at the same port
                if (fromS.Location is not State.AtPort fromAp) break;
                if (toS.Location is not State.AtPort toAp) break;
                if (fromAp.Port != toAp.Port) break;

                var available = fromS.Cargo.GetValueOrDefault(xfer.Good);
                var toUsed = toS.Cargo.Values.Sum();
                var toSpace = toS.MaxCargoTons - toUsed;
                var qty = Math.Min(xfer.Quantity, Math.Min(available, toSpace));
                if (qty <= 0) break;

                fromS.Cargo[xfer.Good] -= qty;
                if (fromS.Cargo[xfer.Good] <= 0) fromS.Cargo.Remove(xfer.Good);
                toS.Cargo[xfer.Good] = toS.Cargo.GetValueOrDefault(xfer.Good) + qty;

                _emitter.Emit(new Events.TradeCompleted(
                    _state.Date, Core.Enums.SimulationLod.Local,
                    xfer.ToShip, fromAp.Port, xfer.Good, qty, 0, true),
                    Core.Enums.SimulationLod.Local);
                break;
            }

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
                        buyPort.Treasury += cost; // Group 10: port receives payment
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
                        // Group 10: port pays from treasury (zero-sum)
                        var revenue = price * qty;
                        if (sellPort.Treasury < revenue)
                        {
                            qty = Math.Max(1, sellPort.Treasury / price);
                            revenue = price * qty;
                        }
                        if (revenue <= 0 || sellPort.Treasury <= 0) break;

                        sellPort.Treasury -= revenue;
                        _state.Player.PersonalGold += revenue;
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

            case FabricateFactCommand fab:
            {
                // Player must be at a port to plant disinformation
                var playerShipFab = _state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
                if (playerShipFab?.Location is not State.AtPort fabAtPort) break;

                // Cost: 200g per fabrication
                if (_state.Player.PersonalGold < 200) break;
                _state.Player.PersonalGold -= 200;

                var fabricatedFact = new State.KnowledgeFact
                {
                    Claim            = fab.FabricatedClaim,
                    Sensitivity      = Core.Enums.KnowledgeSensitivity.Restricted,
                    Confidence       = 0.85f,
                    BaseConfidence   = 0.85f,
                    ObservedDate     = _state.Date,
                    HopCount         = 0,
                    SourceHolder     = new State.FactionHolder(fab.AppearsToBeFromFaction),
                    IsDisinformation = true,
                    OriginatingAgentId = null,   // player planted it, not an NPC agent
                };

                // Seed into PlayerHolder (player knows what they planted)
                _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), fabricatedFact, _tickNumber);
                _state.Knowledge.AddFact(new State.PlayerHolder(), fabricatedFact);

                // Seed into 1–2 random ports in the player's current region
                if (_state.Ports.TryGetValue(fabAtPort.Port, out var fabPort)
                    && _state.Regions.TryGetValue(fabPort.RegionId, out var fabRegion))
                {
                    var portList = fabRegion.Ports.ToList();
                    var seedCount = Math.Min(portList.Count, 1 + _rng.Next(2)); // 1 or 2
                    foreach (var portId in portList.Take(seedCount))
                    {
                        var portCopy = new State.KnowledgeFact
                        {
                            Claim            = fab.FabricatedClaim,
                            Sensitivity      = Core.Enums.KnowledgeSensitivity.Restricted,
                            Confidence       = 0.70f,
                            BaseConfidence   = 0.70f,
                            ObservedDate     = _state.Date,
                            HopCount         = 1,
                            SourceHolder     = new State.FactionHolder(fab.AppearsToBeFromFaction),
                            IsDisinformation = true,
                            OriginatingAgentId = null,
                        };
                        _state.Knowledge.MarkSuperseded(new State.PortHolder(portId), portCopy, _tickNumber);
                        _state.Knowledge.AddFact(new State.PortHolder(portId), portCopy);
                    }
                }
                break;
            }

            case ClaimContractRewardCommand claim:
            {
                var questInstance = _state.Quests.ActiveContractQuests
                    .FirstOrDefault(q => q.Id == claim.QuestInstanceId);
                if (questInstance is null) break;
                if (questInstance.Status != Quests.ContractQuestStatus.Fulfilled) break;

                var contract = questInstance.Contract;

                // Guard: player ship at a port controlled by IssuerFactionId
                var playerShip = _state.Ships.Values.FirstOrDefault(s => s.IsPlayerShip);
                if (playerShip?.Location is not State.AtPort claimAtPort) break;
                if (!_state.Ports.TryGetValue(claimAtPort.Port, out var claimPort)) break;
                if (claimPort.ControllingFactionId != contract.IssuerFactionId) break;

                // Guard: issuer is alive
                if (!_state.Individuals.TryGetValue(contract.IssuerId, out var issuer)) break;
                if (!issuer.IsAlive) break;

                // Fraud check: does the issuer's IndividualHolder have a status fact about target
                // that predates the contract and already shows target as dead/destroyed?
                var issuerHolder = new State.IndividualHolder(contract.IssuerId);
                var contractFact = _state.Knowledge.GetFacts(new State.PlayerHolder())
                    .FirstOrDefault(f => f.Id == questInstance.ContractFactId);

                if (contractFact is not null)
                {
                    var issuerStatusFact = _state.Knowledge.GetFacts(issuerHolder)
                        .FirstOrDefault(f => !f.IsSuperseded
                            && KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey);

                    bool isFraud = false;
                    if (issuerStatusFact is not null
                        && issuerStatusFact.ObservedDate.CompareTo(contractFact.ObservedDate) < 0)
                    {
                        // Issuer knew target was already eliminated before the contract was issued
                        if (issuerStatusFact.Claim is State.ShipStatusClaim ssc && ssc.IsDestroyed)
                            isFraud = true;
                        else if (issuerStatusFact.Claim is State.IndividualStatusClaim isc && !isc.IsAlive)
                            isFraud = true;
                    }

                    if (isFraud)
                    {
                        // Fraud path: relationship penalty, emit counter-bounty
                        issuer.PlayerRelationship = Math.Clamp(issuer.PlayerRelationship - 50f, -100f, 100f);

                        // Seed counter-bounty ContractClaim into all faction ports
                        if (_state.Factions.TryGetValue(contract.IssuerFactionId, out var issuerFaction))
                        {
                            var counterContract = new State.ContractClaim(
                                contract.IssuerId,
                                contract.IssuerFactionId,
                                $"Individual:{_state.Player.CaptainName}",
                                ContractConditionType.TargetDead,
                                500,
                                NarrativeArchetype.PoliticalBounty);

                            foreach (var portId in issuerFaction.ControlledPorts)
                            {
                                var counterFact = new State.KnowledgeFact
                                {
                                    Claim          = counterContract,
                                    Sensitivity    = Core.Enums.KnowledgeSensitivity.Restricted,
                                    Confidence     = 0.90f,
                                    BaseConfidence = 0.90f,
                                    ObservedDate   = _state.Date,
                                    HopCount       = 0,
                                    SourceHolder   = new State.FactionHolder(contract.IssuerFactionId),
                                };
                                _state.Knowledge.AddFact(new State.PortHolder(portId), counterFact);
                            }
                        }
                        break; // Fraud path — no payment
                    }
                }

                // Legitimate path: pay reward
                var reward = contract.GoldReward;
                if (issuer.CurrentGold >= reward)
                {
                    issuer.CurrentGold -= reward;
                }
                else if (_state.Factions.TryGetValue(contract.IssuerFactionId, out var payingFaction)
                    && payingFaction.TreasuryGold >= reward)
                {
                    payingFaction.TreasuryGold -= reward;
                }
                else
                {
                    // Partial payment from whatever is available
                    var available = issuer.CurrentGold;
                    issuer.CurrentGold = 0;
                    if (_state.Factions.TryGetValue(contract.IssuerFactionId, out var partialFaction))
                    {
                        available += Math.Min(partialFaction.TreasuryGold, reward - available);
                        partialFaction.TreasuryGold = Math.Max(0, partialFaction.TreasuryGold - (reward - issuer.CurrentGold));
                    }
                    reward = available;
                }

                _state.Player.PersonalGold += reward;

                // MarkSuperseded the contract fact in PlayerHolder
                if (contractFact is not null)
                    _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), contractFact, _tickNumber);

                _emitter.Emit(new ContractFulfilled(
                    _state.Date, Core.Enums.SimulationLod.Local,
                    contract.IssuerId, contract.TargetSubjectKey, reward),
                    Core.Enums.SimulationLod.Local);

                questInstance.ResolvedDate = _state.Date;
                _state.Quests.Resolve(questInstance);
                break;
            }

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

                // Allegiance investigation: subject key format "Allegiance:{guid}"
                if (local.SubjectKey.StartsWith("Allegiance:", StringComparison.OrdinalIgnoreCase))
                {
                    var guidStr = local.SubjectKey["Allegiance:".Length..];
                    if (!Guid.TryParse(guidStr, out var allegianceGuid)) break;
                    var suspectId = new Core.Ids.IndividualId(allegianceGuid);
                    if (!_state.Individuals.TryGetValue(suspectId, out var suspect)) break;

                    // Cost: 500g flat
                    if (_state.Player.PersonalGold < 500) break;
                    _state.Player.PersonalGold -= 500;

                    if (suspect.IsInfiltrator)
                    {
                        // ClaimedFaction = the cover story; ActualFaction = ground-truth FactionId
                        var allegianceClaim = new State.IndividualAllegianceClaim(
                            suspectId,
                            ClaimedFaction: suspect.ClaimedFactionId!.Value,
                            ActualFaction:  suspect.FactionId ?? default);
                        var allegianceFact = new State.KnowledgeFact
                        {
                            Claim          = allegianceClaim,
                            Sensitivity    = Core.Enums.KnowledgeSensitivity.Secret,
                            Confidence     = 0.95f,
                            BaseConfidence = 0.95f,
                            ObservedDate   = _state.Date,
                            IsDecayExempt  = true,
                            HopCount       = 0,
                            SourceHolder   = null,
                        };
                        _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), allegianceFact, _tickNumber);
                        _state.Knowledge.AddFact(new State.PlayerHolder(), allegianceFact);
                        _emitter.Emit(new Events.AllegianceRevealed(
                            _state.Date, Core.Enums.SimulationLod.Local,
                            suspectId,
                            ActualFaction:  suspect.FactionId!.Value,
                            ClaimedFaction: suspect.ClaimedFactionId!.Value),
                            Core.Enums.SimulationLod.Local);
                    }
                    else
                    {
                        _emitter.Emit(new Events.InvestigationResolved(
                            _state.Date, Core.Enums.SimulationLod.Local,
                            local.SubjectKey, null, false),
                            Core.Enums.SimulationLod.Local);
                    }
                    break;
                }

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

                    // Check if this new observation contradicts any disinformation the player holds
                    var portFabAtPort = localAtPort.Port;
                    var disInfoFacts = _state.Knowledge.GetFacts(new State.PlayerHolder())
                        .Where(f => f.IsDisinformation && !f.IsSuperseded
                            && KnowledgeFact.GetSubjectKey(f.Claim) == local.SubjectKey)
                        .ToList();

                    foreach (var disFact in disInfoFacts)
                    {
                        if (disFact.Claim.GetType() == portFact.Claim.GetType()) continue;
                        FactionId? deceiver = (disFact.SourceHolder as State.FactionHolder)?.Faction;
                        if (deceiver.HasValue)
                        {
                            InjectDisinformationCorrection(
                                new State.PlayerHolder(),
                                disFact,
                                portFact.Claim,
                                deceiver.Value,
                                portFabAtPort);
                        }
                    }
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

                // Reveal actual allegiance if this individual is an infiltrator
                if (target.IsInfiltrator)
                {
                    var revealClaim = new State.IndividualAllegianceClaim(
                        burn.AgentId,
                        ClaimedFaction: target.ClaimedFactionId!.Value,
                        ActualFaction:  target.FactionId ?? default);
                    var revealFact = new State.KnowledgeFact
                    {
                        Claim          = revealClaim,
                        Sensitivity    = Core.Enums.KnowledgeSensitivity.Secret,
                        Confidence     = 1.0f,
                        BaseConfidence = 1.0f,
                        ObservedDate   = _state.Date,
                        IsDecayExempt  = true,
                        HopCount       = 0,
                        SourceHolder   = null,
                    };
                    _state.Knowledge.MarkSuperseded(new State.PlayerHolder(), revealFact, _tickNumber);
                    _state.Knowledge.AddFact(new State.PlayerHolder(), revealFact);
                    _emitter.Emit(new Events.AllegianceRevealed(
                        _state.Date, Core.Enums.SimulationLod.Local,
                        burn.AgentId,
                        ActualFaction:  target.FactionId!.Value,
                        ClaimedFaction: target.ClaimedFactionId!.Value),
                        Core.Enums.SimulationLod.Local);
                }

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

    /// <summary>
    /// Called when direct observation contradicts a disinformation fact held by the player.
    /// Supersedes the lie, degrades source reliability, emits DeceptionExposed,
    /// and enqueues a FactionStimulus so the deceiving faction responds next tick.
    /// </summary>
    private void InjectDisinformationCorrection(
        State.KnowledgeHolderId holder,
        State.KnowledgeFact disInfoFact,
        State.KnowledgeClaim actualClaim,
        Core.Ids.FactionId deceivingFactionId,
        Core.Ids.PortId? exposedAtPort)
    {
        // 1. Add correcting fact (high confidence, not disinformation) — supersedes the lie
        var correctionFact = new State.KnowledgeFact
        {
            Claim          = actualClaim,
            Sensitivity    = Core.Enums.KnowledgeSensitivity.Public,
            Confidence     = 0.90f,
            BaseConfidence = 0.90f,
            ObservedDate   = _state.Date,
            HopCount       = 0,
            SourceHolder   = null,    // player witnessed directly
        };
        _state.Knowledge.MarkSuperseded(holder, correctionFact, _tickNumber);
        _state.Knowledge.AddFact(holder, correctionFact);

        // 2. Degrade source reliability for the deceiving faction
        if (disInfoFact.SourceHolder is not null)
            _state.Knowledge.RecordSourceOutcome(disInfoFact.SourceHolder, wasAccurate: false);

        // 3. Emit DeceptionExposed event
        _emitter.Emit(new Events.DeceptionExposed(
            _state.Date, Core.Enums.SimulationLod.Local,
            deceivingFactionId, exposedAtPort),
            Core.Enums.SimulationLod.Local);

        // 4. Enqueue FactionStimulus for the deceiving faction
        _state.PendingFactionStimuli.Enqueue(new State.FactionStimulus
        {
            FactionId     = deceivingFactionId,
            StimulusType  = "DeceptionExposed",
            Magnitude     = 0.5f,
            Description   = "Player exposed disinformation planted by this faction",
            PortId        = exposedAtPort,
            PlayerIsOrigin = true,
        });
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
