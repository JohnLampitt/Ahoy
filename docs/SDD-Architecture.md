# Ahoy — Architecture Review

> **Status:** Living document.
> **Version:** 0.1 — Pre-implementation review
> **Covers:** Full system integration, project structure, data flow, wiring, and readiness assessment

---

## 1. Purpose

This document reviews the full architecture before implementation begins. It resolves cross-cutting concerns, clarifies ambiguities across individual SDDs, and provides an authoritative reference for how all systems connect.

---

## 2. Project Structure

```
Ahoy/
│
├── Ahoy.Core/                              # Shared primitives — no simulation dependencies
│   ├── Ids/                                # Typed entity ID structs
│   │   └── PortId, RegionId, FactionId, ShipId, IndividualId,
│   │       KnowledgeFactId, NavalDeploymentId, ...
│   ├── Enums/
│   │   └── TradeGood, ShipClass, FactionType, WindStrength,
│   │       WindDirection, StormPresence, SimulationLod,
│   │       IndividualRole, InterventionType, ...
│   └── ValueObjects/
│       └── WorldDate, PersonalityTraits, CareerEntry, ...
│
├── Ahoy.Simulation/                        # Core simulation library — no UI dependencies
│   ├── State/                              # WorldState and all entity data models
│   │   └── WorldState, Region, Port, Faction, Ship, Individual,
│   │       PlayerState, EconomicProfile, RegionWeather,
│   │       KnowledgeStore, KnowledgeFact, ...
│   ├── Systems/                            # IWorldSystem implementations
│   │   ├── WeatherSystem
│   │   ├── ShipMovementSystem
│   │   ├── EconomySystem
│   │   ├── FactionSystem
│   │   ├── EventPropagationSystem
│   │   ├── KnowledgeSystem
│   │   └── Propagation/                    # PropagationRule implementations
│   │       ├── EconomicRules
│   │       ├── PoliticalRules
│   │       ├── MilitaryRules
│   │       ├── WeatherRules
│   │       └── SocialRules
│   ├── Events/                             # WorldEvent hierarchy
│   │   ├── WorldEvent (abstract base)
│   │   ├── EconomicEvents
│   │   ├── MilitaryEvents
│   │   ├── PoliticalEvents
│   │   ├── WeatherEvents
│   │   └── NavigationEvents
│   ├── Decisions/                          # Actor decision system
│   │   ├── ISyncActorDecisionProvider
│   │   ├── IAsyncActorDecisionProvider
│   │   ├── ActorDecisionContext
│   │   ├── ActorDecisionMatrix
│   │   ├── DecisionQueue
│   │   ├── RuleBasedDecisionProvider       # Fallback — always available
│   │   └── SituationSummaryBuilder
│   └── Engine/                             # Orchestration
│       ├── SimulationEngine
│       ├── SimulationContext
│       ├── TickEventEmitter
│       ├── WorldSnapshot
│       ├── IPlayerCommandQueue
│       ├── PlayerCommand (hierarchy)
│       ├── ContinuitySystem
│       └── WorldFactory                    # Instantiates WorldState from definition data
│
├── Ahoy.Simulation.LlmDecisions/           # Optional — LLM decision provider
│   ├── LlmActorDecisionProvider            # Implements IAsyncActorDecisionProvider
│   ├── PromptBuilder
│   └── ResponseParser
│   (depends on: LLamaSharp, Ahoy.Simulation)
│
├── Ahoy.WorldData/                         # Hand-crafted Caribbean world content
│   ├── CaribbeanWorldDefinition            # Assembles the full initial WorldState
│   ├── Regions/                            # Per-region port and geography data
│   ├── Factions/                           # Faction definitions, initial relationships
│   └── Ships/                              # Named ships, starting configurations
│   (depends on: Ahoy.Simulation, Ahoy.Core)
│
└── Ahoy.Console/                           # First frontend — simulation runner + observer
    ├── Program
    ├── SimulationRunner                    # Drives the tick loop
    └── Observers/                          # Console output for watching simulation events
    (depends on: all above)
```

### Dependency Rules

```
Ahoy.Core                   ← no dependencies
Ahoy.Simulation             ← Ahoy.Core
Ahoy.Simulation.LlmDecisions← Ahoy.Simulation, Ahoy.Core, LLamaSharp
Ahoy.WorldData              ← Ahoy.Simulation, Ahoy.Core
Ahoy.Console                ← all of the above
```

**Critical constraint:** `Ahoy.Simulation` has zero knowledge of `Ahoy.Simulation.LlmDecisions`. The LLM provider is registered at composition root in `Ahoy.Console` via the `IAsyncActorDecisionProvider` interface. The simulation never takes a hard dependency on LLM infrastructure.

---

## 3. SimulationEngine — Composition and Wiring

The engine is assembled at the composition root. Systems are constructed with their dependencies injected.

```csharp
// Ahoy.Console/SimulationRunner.cs

public static SimulationEngine BuildEngine(bool useLlm = true)
{
    var worldState = CaribbeanWorldDefinition.Build();

    // Decision providers
    var ruleBasedProvider = new RuleBasedDecisionProvider();
    var decisionQueue     = new DecisionQueue(
        fallback:      ruleBasedProvider,
        maxWaitTicks:  30);

    IAsyncActorDecisionProvider asyncProvider = useLlm
        ? new LlmActorDecisionProvider(modelPath: "models/phi-3-mini-q4.gguf")
        : decisionQueue;   // DecisionQueue wraps the async interface; without LLM it fast-falls to sync

    decisionQueue.StartInferenceLoop(asyncProvider);

    // Propagation rules — registered in priority order
    var propagationRules = new IPropagationRule[]
    {
        new CommodityShortageRule(),
        new PortProsperityDeclineRule(),
        new GovernorAuthorityDeclineRule(),
        new PowerVacuumRule(),
        new WarTradeDisruptionRule(),
        new HurricaneProductionRule(),
        new PopulationUnrestRule(),
        // ... etc.
    };

    // Tick pipeline — order is authoritative
    var systems = new IWorldSystem[]
    {
        new WeatherSystem(),
        new ShipMovementSystem(),
        new EconomySystem(),
        new FactionSystem(decisionQueue),
        new EventPropagationSystem(propagationRules),
        new KnowledgeSystem(),
    };

    var continuitySystem = new ContinuitySystem(new EconomySystem(), new RuleBasedDecisionProvider());

    return new SimulationEngine(worldState, systems, decisionQueue, continuitySystem);
}
```

---

## 4. Authoritative Tick Pipeline

This is the single canonical ordering of operations within a tick. All SDDs defer to this.

```
SimulationEngine.Tick()
│
│  ── PRE-TICK ──────────────────────────────────────────────────────────────
│
├─ 1. Apply completed ActorDecisionMatrix results
│       For each (request, matrix) in decisionQueue.DrainCompleted():
│           store request.PendingMatrix = matrix
│
├─ 2. Age pending decision requests
│       For each pending request:
│           request.TicksElapsed++
│           if !request.InterventionWindowOpen:
│               resolve = request.PendingMatrix ?? fallback.ResolveMatrix(context)
│               decision = resolve.Resolve(request.Intervention)
│               decision.Apply(state, subject, emitter)
│               ClearPendingDecision(subject, state)
│               EmitDecisionFact(request, decision, emitter)
│
├─ 3. Compute SimulationContext (LOD tiers for all regions)
│
│  ── SYSTEMS ────────────────────────────────────────────────────────────────
│
├─ 4. WeatherSystem.Tick(state, context, emitter)
│       Mutates: state.Weather[*]
│       Emits:   StormEntered, HurricaneEvent, WeatherCleared, BecalmedConditions
│
├─ 5. ShipMovementSystem.Tick(state, context, emitter)
│       Reads:   state.Weather[*]  (written by step 4)
│       Mutates: ship.Location for all EnRoute ships
│                ship.ArrivedThisTick = true on arrival
│                ship.HullIntegrity on storm damage
│       Emits:   ShipArrivedAtPort, ShipEnteredRegion, ShipEncounterDetected,
│                ShipDamagedByWeather, ShipDestroyed (weather)
│
├─ 6. EconomySystem.Tick(state, context, emitter)
│       Reads:   ships where ArrivedThisTick == true  (set by step 5)
│       Mutates: port.Inventory (production, consumption, unloading, loading)
│                ship.Cargo (loading, unloading)
│                port.Prosperity (from TradeVolume changes)
│                Individual.MerchantState (desperation, knowledge updates)
│       Emits:   CommodityPriceChanged, CommodityShortage, TradeVolumeGrew
│       Enqueues: ActorDecisionRequests (merchant desperation inflection points)
│
├─ 7. FactionSystem.Tick(state, context, emitter)
│       Reads:   state.PendingFactionStimuli (drained here)
│       Mutates: faction.Treasury, faction.NavalStrength
│                faction.Goals, faction.Relationships, faction.PatrolAllocations
│                port.ControllingFaction (on conquest completion)
│                port.HavenPresence (pirate brotherhood)
│                Individual.Authority (governor)
│       Emits:   PortControlChanged, WarDeclared, AllianceFormed, PeaceNegotiated,
│                PatrolPresenceChanged, ConquestInProgress, HavenEstablished,
│                BrotherhoodFractured, RegionSafetyChanged, FactionTreasuryCollapsed
│       Enqueues: ActorDecisionRequests (faction inflection points)
│
├─ 8. EventPropagationSystem.Tick(state, context, emitter)
│       Reads:   emitter.DrainPending()  (all events from steps 4–7)
│       Mutates: port.Prosperity, Individual.Authority, port.TradeHealth
│                faction.NavalStrength (via stimuli)
│                port.Profile.ActiveModifiers (production modifiers)
│       Emits:   PortProsperityChanged, GovernorAuthorityChanged, PowerVacuumCreated,
│                HavenOpportunityDetected, TradeRouteDisrupted, etc. (secondary events)
│       Adds to: state.PendingFactionStimuli (for FactionSystem next tick)
│
├─ 9. KnowledgeSystem.Tick(state, context, emitter)
│       Reads:   emitter.PeekAll()  (all events including secondary — NOT drained)
│       Mutates: state.Knowledge (new facts, spreading, degradation, suppression)
│                player.KnownFactIds
│                broker inventories
│       Emits:   (none — KnowledgeSystem does not generate WorldEvents)
│
│  ── POST-TICK ──────────────────────────────────────────────────────────────
│
├─ 10. Clear per-tick transient flags
│        foreach ship: ship.ArrivedThisTick = false
│
├─ 11. Advance date
│        state.Date = state.Date.Advance()
│
└─ 12. Dispatch events to frontend
         foreach event in emitter.DrainAll():
             EventOccurred?.Invoke(event)
```

---

## 5. TickEventEmitter — Clarified Design

The emitter model needs explicit clarification because multiple systems interact with it differently:

```csharp
// Ahoy.Simulation/Engine/TickEventEmitter.cs

public sealed class TickEventEmitter : IEventEmitter
{
    // All events emitted this tick — never cleared mid-tick
    private readonly List<(WorldEvent Event, SimulationLod Lod)> _all = new();

    // Events not yet processed by EventPropagationSystem
    private readonly List<(WorldEvent Event, SimulationLod Lod)> _pending = new();

    public void Emit(WorldEvent worldEvent, SimulationLod sourceLod)
    {
        _all.Emit(worldEvent, sourceLod));
        _pending.Add((worldEvent, sourceLod));
    }

    /// EventPropagationSystem: take unprocessed events and clear the pending buffer
    public IReadOnlyList<(WorldEvent, SimulationLod)> DrainPending()
    {
        var drained = _pending.ToList();
        _pending.Clear();
        return drained;
    }

    /// KnowledgeSystem: read everything emitted this tick (primary + secondary)
    /// Does NOT clear — SimulationEngine calls DrainAll at tick end
    public IReadOnlyList<(WorldEvent, SimulationLod)> PeekAll() => _all;

    /// SimulationEngine: dispatch to frontend, then clear for next tick
    public IReadOnlyList<(WorldEvent, SimulationLod)> DrainAll()
    {
        var all = _all.ToList();
        _all.Clear();
        _pending.Clear();
        return all;
    }
}
```

**The flow:**
- Steps 4–7: systems call `Emit()` → both `_all` and `_pending` grow
- Step 8: EventPropagationSystem calls `DrainPending()` → gets steps 4–7 events; `_pending` cleared. Secondary events from propagation rules are `Emit()`-ed → back into `_pending` AND `_all`. Secondary events of secondary events are processed recursively (up to depth 3) within the same `DrainPending()` invocation.
- Step 9: KnowledgeSystem calls `PeekAll()` → sees everything: steps 4–7 events + all secondary events. Does not clear.
- Step 12: SimulationEngine calls `DrainAll()` → dispatches complete event set to frontend, clears for next tick.

---

## 6. Cross-Cutting Clarifications

### 6.1 Ship.ArrivedThisTick

Added to `Ship` to communicate arrivals from `ShipMovementSystem` to `EconomySystem` within the same tick without coupling through events:

```csharp
// On Ship — transient, cleared at post-tick (step 10)
public bool ArrivedThisTick { get; internal set; }
```

`EconomySystem` queries `state.Ships.Values.Where(s => s.ArrivedThisTick)` in Phase 3 (Arrivals). Clean, fast, no event subscription needed.

### 6.2 PendingFactionStimuli Timing

`EventPropagationSystem` writes to `state.PendingFactionStimuli` during step 8. `FactionSystem` reads and drains it at the start of step 7. Because step 7 runs before step 8, stimuli written in tick N are consumed by `FactionSystem` in tick N+1. This is intentional — propagation effects reach factions one tick later, which is appropriate for cascade latency.

### 6.3 SimulationContext Lifetime

`SimulationContext` is computed at step 3 and is **immutable for the duration of the tick**. If the player moves between regions mid-tick, the LOD tier change takes effect at the start of the next tick. Systems never recompute LOD mid-tick.

### 6.4 WorldState Mutation Discipline

All `WorldState` mutation happens exclusively on the **main simulation thread**, only inside `IWorldSystem.Tick()` or `SimulationEngine`'s pre/post-tick phases. The `DecisionQueue` background thread writes only to its own thread-safe collections. `WorldState` has no locks and requires none.

### 6.5 LOD Transition Detection

`SimulationEngine` tracks the LOD map from the previous tick and compares it to the current tick to detect transitions:

```csharp
private IReadOnlyDictionary<RegionId, SimulationLod> _previousLods = new Dictionary<RegionId, SimulationLod>();

private void DetectLodTransitions(SimulationContext current, WorldState state)
{
    foreach (var (region, lod) in current.AllLods)
    {
        var previous = _previousLods.GetValueOrDefault(region, SimulationLod.Distant);
        if (previous != SimulationLod.Local && lod == SimulationLod.Local)
            _continuitySystem.OnRegionBecameLocal(region, state, _emitter);
    }
    _previousLods = current.AllLods;
}
```

`OnRegionBecameLocal` triggers the Return Sequence from `SDD-SimulationLOD.md` Section 11.3.

---

## 7. Frontend Boundary Summary

Three contact points — nothing else crosses the boundary.

```
┌─────────────────────────────────────────────────────┐
│                  Ahoy.Simulation                    │
│                                                     │
│  SimulationEngine                                   │
│      │                                              │
│      ├── GetSnapshot() → WorldSnapshot              │──→ Frontend READS state
│      │                                              │
│      ├── CommandQueue.Enqueue(PlayerCommand)        │←── Frontend WRITES actions
│      │                                              │
│      └── event EventOccurred(WorldEvent)            │──→ Frontend REACTS to changes
│                                                     │
└─────────────────────────────────────────────────────┘
```

**`WorldSnapshot`** — a point-in-time read of world state. Produced on demand; never holds a live reference to `WorldState`. The frontend can hold a snapshot between ticks without risk.

**`IPlayerCommandQueue`** — the frontend submits `PlayerCommand` records. Applied at the start of the next tick (pre-tick phase, before step 3). Player commands never mutate state mid-tick.

**`EventOccurred`** — a C# event raised after each tick with all `WorldEvent`s that fired. The frontend subscribes to update its state reactively. Events carry enough information that the frontend doesn't need to diff snapshots.

### Current PlayerCommand Hierarchy

```csharp
public abstract record PlayerCommand;

// Navigation
public record SetCourseToPort(PortId Destination)               : PlayerCommand;
public record SetCourseToRegion(RegionId Destination)           : PlayerCommand;

// Trade
public record PurchaseCommodity(PortId Port, TradeGood Good, int Quantity) : PlayerCommand;
public record SellCommodity(PortId Port, TradeGood Good, int Quantity)     : PlayerCommand;

// Interaction
public record InterveneInDecision(
    Guid RequestId, PlayerIntervention Intervention)            : PlayerCommand;
public record RequestAudience(IndividualId Individual)          : PlayerCommand;
public record PurchaseKnowledgeFact(
    KnowledgeFactId Fact, IndividualId FromBroker)              : PlayerCommand;
public record OfferKnowledgeFact(
    KnowledgeFactId Fact, IndividualId ToBroker)                : PlayerCommand;

// Crew & Ship
public record HireCrew(PortId Port, int Count)                  : PlayerCommand;
public record RepairShip(PortId Port, ShipId Ship)              : PlayerCommand;
```

This list grows as mechanics are implemented. The command pattern ensures all player intent is captured as data — easy to log, replay for debugging, and extend.

---

## 8. WorldFactory — Initial State

The Caribbean world is **hand-crafted data** that `WorldFactory` instantiates into an initial `WorldState`. This is content design, not simulation design — but its structure is worth defining.

```csharp
// Ahoy.WorldData/CaribbeanWorldDefinition.cs

public static class CaribbeanWorldDefinition
{
    public static WorldState Build()
    {
        var state = new WorldState
        {
            Date = new WorldDate(0)  // 1 January 1680
        };

        BuildRegions(state);    // 5 regions with adjacency
        BuildPorts(state);      // 4–8 ports per region with EconomicProfiles
        BuildFactions(state);   // 4 colonial + 1–3 pirate brotherhoods
        BuildShips(state);      // Named faction ships; initial merchant fleet
        BuildIndividuals(state);// Governors, faction leaders, notable captains
        BuildWeather(state);    // Initial weather state (dry season defaults)
        BuildRelationships(state); // Initial faction standing (historical approximation)

        return state;
    }
}
```

**What world content design must produce (not yet done):**

| Content | Status |
|---|---|
| 5 region names, geography, and adjacency | Not yet defined |
| ~30 ports with names, archetypes, and economic profiles | Not yet defined |
| Colonial faction identities (Spain, England, France, Netherlands) | Named; not detailed |
| 1–3 pirate brotherhood identities | Not yet defined |
| Initial faction standings | Not yet defined |
| Named starting ships (faction flagships, etc.) | Not yet defined |
| Starting governor pool | Not yet defined |

This is the primary content gap before the simulation can run meaningfully. It is content work, not system work — but it is the next design task after this review.

---

## 9. Serialisation (Save/Load)

Not yet designed in detail. The flat dictionary `WorldState` structure is deliberately serialisation-friendly. Notes for when this is addressed:

- `System.Text.Json` with source generation is the natural fit for a C# backend
- `WorldDate` serialises as a plain `int` (the tick count)
- Entity ID structs serialise as their underlying `Guid`
- `KnowledgeFact.Claim` is a discriminated union — will need a custom converter or a `[JsonDerivedType]` hierarchy
- `PropagationRule` implementations are code, not data — they do not need to be serialised
- `DecisionQueue` is transient — any in-flight LLM requests at save time are discarded; pending decisions restart from their last state on load

**Recommendation:** defer until the simulation runs and produces state worth saving.

---

## 10. Console Frontend

`Ahoy.Console` is the first frontend. Its purpose is not user-facing gameplay — it is an **observation and validation harness** for the simulation.

Minimum viable console:

```
> run 365          — advance simulation by 365 ticks, print summary events
> run              — advance one tick, print all events
> status           — print world state summary (regions, faction standings, trade health)
> region gulf      — print detailed state of The Gulf region
> port nassau      — print port detail (prosperity, inventory, governor, recent events)
> faction spain    — print Spain's goals, resources, relationships
> knowledge        — print player's current knowledge log
> tick 412         — jump to a specific tick (re-run from tick 0 to 412)
```

This harness lets us validate:
- That the self-reinforcing loops actually emerge
- That knowledge propagates believably
- That faction behaviour is rational
- That weather creates meaningful disruptions
- That the economy responds to events

Building this before any graphical frontend keeps the feedback loop tight.

---

## 11. Readiness Assessment

### Ready to implement now

| Component | Confidence | Notes |
|---|---|---|
| `Ahoy.Core` types (IDs, enums, value objects) | High | Fully specified |
| `WorldState` data model | High | All entities defined |
| `SimulationEngine` skeleton | High | Pipeline and wiring clear |
| `TickEventEmitter` | High | Fully specified in this doc |
| `WeatherSystem` | High | Logic complete |
| `ShipMovementSystem` | High | Logic complete |
| `EconomySystem` (structure) | High | Production/consumption/repricing clear |
| `EconomySystem` (merchant routing) | Medium | Algorithm clear; balance needs tuning |
| `FactionSystem` (structure) | High | Goal types and lifecycle clear |
| `FactionSystem` (utility scoring) | Medium | Values need calibration |
| `EventPropagationSystem` | High | Rule structure and core rules clear |
| `KnowledgeSystem` (data model) | High | Types fully specified |
| `KnowledgeSystem` (spreading algorithm) | Medium | Concept clear; hop/decay values need tuning |
| `ActorDecisionSystem` (sync/queue) | High | Fully specified |
| `RuleBasedDecisionProvider` | Medium | Structure clear; individual decisions need design |
| `LlmActorDecisionProvider` | High | Integration pattern clear |
| `SimulationLOD` + `ContinuitySystem` | High | Fully specified |
| `Ahoy.Console` harness | High | Requirements clear |

### Needs more work before implementation

| Component | Gap | Action Required |
|---|---|---|
| **World content** | No ports, regions, factions defined | Content design pass — the most pressing gap |
| **Player interaction layer** | Commands defined; application logic not | Design how commands map to world mutations |
| **`RuleBasedDecisionProvider`** | Decision logic for each trigger type | Needs per-trigger rule design |
| **Individual career simulation** | Named NPC careers referenced but not designed | Design career progression and succession |
| **Serialisation** | Not designed | Defer until first meaningful world state exists |
| **Combat resolution** | Deferred; abstracted as stat-check | Needs at minimum a stat-check spec |

### Known gaps identified across SDDs

- `SDD-WorldState` — `FactionGoal` subtypes defined in `SDD-FactionSystem`; cross-reference needed
- `SDD-EconomySystem` — `MerchantKnowledge` replaced by `KnowledgeStore` queries; the SDD has not been updated to reflect this fully
- `SDD-KnowledgeSystem` — `Individual.MerchantKnowledge` removal noted but `MerchantState` (desperation tracking) not yet specified on `Individual`
- `SDD-ActorDecisionSystem` — `ISyncActorDecisionProvider.ResolveMatrix()` added; the fallback interface needs updating

---

## 12. Recommended Build Order

Given the above, the recommended sequence for initial implementation:

```
Phase 1 — Foundation
    Ahoy.Core (all IDs, enums, value objects)
    WorldState data model (all entity classes, no behaviour)
    SimulationEngine skeleton (tick loop, empty systems, emitter)
    Ahoy.Console harness (run N ticks, print events)

Phase 2 — World Content
    CaribbeanWorldDefinition (regions, ports, factions, initial state)
    WorldFactory (load definition into WorldState)
    Validate: world starts in a plausible state

Phase 3 — Simulation Systems (in pipeline order)
    WeatherSystem
    ShipMovementSystem
    EconomySystem (production/consumption/repricing first, merchant routing second)
    FactionSystem (resources + patrol first, goal evaluation second)
    EventPropagationSystem (core economic + political rules first)
    KnowledgeSystem (origination + basic spreading first)

Phase 4 — Advanced Systems
    SimulationLOD + ContinuitySystem
    ActorDecisionSystem (sync provider first, LLM provider second)
    Full KnowledgeSystem (guarding, trading, broker inventories)
    Full EventPropagationSystem (all rule categories)

Phase 5 — Player Interaction
    PlayerCommand application logic
    Player knowledge queries
    Intervention mechanics
```

Each phase produces a runnable simulation that can be observed in the console harness. Feedback at each phase informs the next.

---

## 13. Open Questions

- [ ] Individual career simulation — governors are appointed, promoted, and die. Named rival captains build careers. This needs a design pass before Phase 3.
- [ ] Combat resolution — currently "abstracted to a stat check." What exactly is the check? Needs a minimum viable spec before `FactionSystem` conquest and player encounter resolution can be implemented.
- [ ] `RuleBasedDecisionProvider` — what are the actual rules for each trigger type? This is the "dumb" decision logic that the LLM makes richer. Needs a design pass.
- [ ] Player starting conditions — blank slate captain. What ship class? How much gold? Which port do they start in?
- [ ] Is there a game loop driver in `Ahoy.Console` that advances time automatically, or is every tick manually triggered? For validation purposes, auto-advance with event filtering is more useful.

---

*Architecture review complete. Recommended next step: World Content Design — defining the five Caribbean regions, their ports, their economic profiles, and the starting state of each faction.*
