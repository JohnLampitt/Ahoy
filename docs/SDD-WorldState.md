# Ahoy — System Design: World State

> **Status:** Living document. Updated as implementation validates or challenges design decisions.
> **Version:** 0.1

---

## 1. Overview

The World State is the **single source of truth** for the entire simulation. Every system reads from it and writes to it. Nothing else owns authoritative game data.

### Design Principles

- **Pure data** — `WorldState` and its entities are data containers with no behaviour. Logic lives in Systems.
- **Systems as behaviour** — stateless classes that accept `WorldState` and produce mutations plus events.
- **Events as side-effects** — systems emit events during a tick as a by-product of mutation; events drive downstream systems and the frontend.
- **Frontend-agnostic** — the simulation layer has no knowledge of any UI. The frontend reads snapshots and submits commands; it never touches live state.
- **No threading (yet)** — the tick pipeline is sequential and deterministic. Threading slots in later via the snapshot boundary with no rearchitecting required.

---

## 2. Project Structure

```
Ahoy/
├── Ahoy.Core/                  # Shared types: IDs, enums, value objects
│   ├── Ids/                    # Typed entity ID structs
│   └── Enums/                  # FactionType, ShipClass, etc.
│
├── Ahoy.Simulation/            # Core simulation library
│   ├── State/                  # WorldState and all entity data models
│   ├── Systems/                # IWorldSystem implementations (one per domain)
│   ├── Events/                 # WorldEvent hierarchy and IEventEmitter
│   └── Engine/                 # SimulationEngine, tick orchestration
│
└── Ahoy.Console/               # (Future) Console frontend — reads snapshots, sends commands
    (or Ahoy.UI, Ahoy.Web, etc.)
```

The simulation library has **zero dependencies** on any frontend project. Frontends depend on `Ahoy.Simulation`; never the reverse.

---

## 3. Entity Identity

All entities are identified by **typed ID structs**. Systems hold IDs, never direct object references. This keeps entities flat, makes serialisation trivial, and prevents accidental coupling between entity graphs.

```csharp
// Ahoy.Core/Ids/

public readonly record struct RegionId(Guid Value);
public readonly record struct PortId(Guid Value);
public readonly record struct FactionId(Guid Value);
public readonly record struct ShipId(Guid Value);
public readonly record struct IndividualId(Guid Value);
```

New IDs are created with `new PortId(Guid.NewGuid())`. Factory helpers can be added later if needed.

---

## 4. Core Data Structures

### 4.1 WorldState

The top-level mutable container. Owned exclusively by `SimulationEngine` during a tick; exposed read-only to the frontend via snapshots.

```csharp
// Ahoy.Simulation/State/WorldState.cs

public sealed class WorldState
{
    public WorldDate Date { get; internal set; }

    public Dictionary<RegionId, Region>       Regions     { get; } = new();
    public Dictionary<PortId, Port>           Ports       { get; } = new();
    public Dictionary<FactionId, Faction>     Factions    { get; } = new();
    public Dictionary<ShipId, Ship>           Ships       { get; } = new();
    public Dictionary<IndividualId, Individual> Individuals { get; } = new();
    public Dictionary<OceanPoiId, OceanPoi>   OceanPois   { get; } = new();

    public PlayerState Player { get; } = new();

    // Cross-cutting systems
    public KnowledgeStore Knowledge { get; } = new();
    public QuestStore Quests { get; } = new();
    public Dictionary<RegionId, RegionWeather> Weather { get; } = new();

    // NPC-to-NPC relationship matrix (Group 6B) — sparse, -100..+100
    public Dictionary<(IndividualId, IndividualId), float> RelationshipMatrix { get; } = new();

    // NPC goal pursuit (Group 5B) — cross-system visibility
    public Dictionary<IndividualId, GoalPursuit> NpcPursuits { get; } = new();
}
```

Flat dictionaries give O(1) lookups by ID and map directly to serialisation. No deep entity graphs.

---

### 4.2 WorldDate

A tick counter that presents as a calendar date. The game world starts at a defined date (e.g. 1 January 1680); each tick advances by one day.

```csharp
// Ahoy.Simulation/State/WorldDate.cs

public readonly record struct WorldDate(int Tick)
{
    public static readonly WorldDate Epoch = new(0);

    // Configured at world creation — e.g. new DateOnly(1680, 1, 1)
    private static DateOnly _epochCalendarDate;

    public DateOnly ToCalendarDate() =>
        _epochCalendarDate.AddDays(Tick);

    public WorldDate Advance(int days = 1) => new(Tick + days);

    public int DaysElapsedSince(WorldDate earlier) => Tick - earlier.Tick;

    public override string ToString() => ToCalendarDate().ToString("d MMM yyyy");
}
```

All date comparisons in the sim use `WorldDate` (tick arithmetic). Calendar display is for the frontend only.

---

### 4.3 Region

One of the five hand-crafted Caribbean regions. Regions are the primary unit of the **Simulation LOD** system — a region's sim fidelity is determined by its distance from the player's current region.

```csharp
// Ahoy.Simulation/State/Region.cs

public sealed class Region
{
    public RegionId Id           { get; init; }
    public string   Name         { get; init; } = string.Empty;

    // Port IDs within this region
    public List<PortId> PortIds  { get; } = new();

    // Adjacency — which regions are directly reachable from this one
    // Used for routing and LOD distance calculations
    public List<RegionId> AdjacentRegionIds { get; } = new();

    // Dominant faction presence (does not imply full control)
    public FactionId? DominantFaction { get; internal set; }
}
```

LOD tier (Local / Regional / Distant) is computed at tick time from the player's current region, not stored. Distance is the number of region hops to the player.

---

### 4.4 Port

A location in the world. The primary interface between the player and the living world.

```csharp
// Ahoy.Simulation/State/Port.cs

public sealed class Port
{
    public PortId    Id       { get; init; }
    public string    Name     { get; init; } = string.Empty;
    public RegionId  Region   { get; init; }

    // Faction control
    public FactionId  ControllingFaction { get; internal set; }
    public IndividualId? GovernorId      { get; internal set; }

    // Economic state
    public int        Prosperity         { get; internal set; }  // 0–100
    public Dictionary<TradeGood, PortCommodity> Inventory { get; } = new();

    // Facilities present at this port
    public PortFacilities Facilities     { get; internal set; }

    // Reputation — institutional memory of the player, persists through governor changes
    // Personal reputation (per-governor) lives on the Individual record
    public int InstitutionalReputation   { get; internal set; }  // -100 to 100
}

[Flags]
public enum PortFacilities
{
    None      = 0,
    Tavern    = 1 << 0,
    Shipyard  = 1 << 1,
    Market    = 1 << 2,
    Garrison  = 1 << 3,
}
```

---

### 4.5 TradeGood & PortCommodity

Trade goods are a closed enum at this stage. `PortCommodity` tracks supply, demand, and the current price.

```csharp
// Ahoy.Core/Enums/TradeGood.cs

public enum TradeGood
{
    Sugar, Rum, Tobacco, Coffee, Cotton,
    Timber, Rope, Cannon, Powder,
    Food, Cloth, Spices
}
```

```csharp
// Ahoy.Simulation/State/PortCommodity.cs

public sealed class PortCommodity
{
    public TradeGood Good     { get; init; }
    public int       Supply   { get; internal set; }   // units in stock
    public int       Demand   { get; internal set; }   // units wanted per tick
    public int       BasePrice { get; init; }          // gold per unit (world average)
    public int       Price    { get; internal set; }   // current price, driven by supply/demand
}
```

Price is recalculated each tick by `EconomySystem` based on the ratio of supply to demand. A port that produces sugar will have high supply and low local price; if ships stop coming, its imports run dry and import prices spike.

---

### 4.6 Faction

An actor in the living world with goals, resources, and relationships.

```csharp
public sealed class Faction
{
    public FactionId Id { get; init; }
    public string Name { get; init; }
    public FactionType Type { get; init; }   // Colonial | PirateBrotherhood

    // Resources
    public int TreasuryGold { get; set; }
    public int IncomePerTick { get; set; }
    public int ExpenditurePerTick { get; set; }
    public int NavalStrength { get; set; }

    // Relationships — continuous float, -100..+100
    public Dictionary<FactionId, float> Relationships { get; } = new();

    // Discrete war state — prevents float-oscillation, requires formal treaty to clear
    public HashSet<FactionId> AtWarWith { get; } = new();

    // Ports and territories
    public List<PortId> ControlledPorts { get; } = new();
    public List<RegionId> ClaimedRegions { get; } = new();

    // Active goals — evaluated each tick by FactionSystem
    public List<FactionGoal> ActiveGoals { get; } = new();

    // Intelligence
    public float IntelligenceCapability { get; set; } = 0.30f;  // 0.10..0.95

    // Pirate-specific
    public float Cohesion { get; set; } = 70f;
    public float RaidingMomentum { get; set; } = 50f;
}
```

`FactionGoal` subtypes: `ExpandTerritory`, `ConsolidateTerritory`, `SuppressPiracy`,
`BuildNavy`, `AccumulateTreasury`, `NegotiateTreaty`, `RaidShippingLane`,
`EstablishHaven`, `EspionageGoal`, `DeclareWar`, `SeekPeace`.

`AtWarWith` is a discrete state set via `DeclareWar` goal when relationship < -80.
Cleared only by mutual `SeekPeace` goals + treaty event. Prevents the
oscillation that would occur if war were tied to a floating-point threshold.

---

### 4.7 Ship

A vessel in the world. Ships are the primary mobile entity — they belong to
factions or the player and move between ports and sea regions.

```csharp
// Routing intent — discriminated union (Group 5B)
public abstract record ShipRoute;
public record PortRoute(PortId Destination) : ShipRoute;
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;
public record PoiRoute(OceanPoiId Poi) : ShipRoute;

public sealed class Ship
{
    public ShipId Id { get; init; }
    public string Name { get; set; }
    public ShipClass Class { get; init; }

    // Ownership
    public FactionId? OwnerFactionId { get; set; }
    public IndividualId? CaptainId { get; set; }
    public FactionId? ClaimedOwnerFactionId { get; set; }  // false colours

    // Condition
    public float HullIntegrity { get; set; } = 1.0f;  // 0..1
    public int CurrentCrew { get; set; }
    public int MaxCrew { get; init; }
    public int Guns { get; init; }

    // Cargo
    public Dictionary<TradeGood, int> Cargo { get; } = new();
    public int MaxCargoTons { get; init; }
    public int GoldOnBoard { get; set; }

    // Location
    public ShipLocation Location { get; set; }
    public ShipRoute? Route { get; set; }  // routing intent
    public bool ArrivedThisTick { get; set; }  // transient
    public int TicksDockedAtCurrentPort { get; set; }

    // Crisis state
    public bool HasInfectedCrew { get; set; }        // epidemic propagation
    public KnowledgeFactId? CarriedIntelPackage { get; set; }  // captured secret documents

    // Flags
    public bool IsPlayerShip { get; init; }
    public bool IsPirate { get; set; }
}

// Location — discriminated union
public abstract record ShipLocation;
public record AtPort(PortId Port) : ShipLocation;
public record AtSea(RegionId Region) : ShipLocation;
public record EnRoute(RegionId From, RegionId To, float ProgressDays, float TotalDays) : ShipLocation;
public record AtPoi(OceanPoiId Poi, RegionId Region) : ShipLocation;
```

---

### 4.8 Individual

A named NPC — governors, captains, brokers, informants, diplomats. Generated
by the world at startup and through faction replacement mechanics.

```csharp
public sealed class Individual
{
    public IndividualId Id { get; init; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public IndividualRole Role { get; set; }
    public FactionId? FactionId { get; set; }

    public PortId? LocationPortId { get; set; }     // null = at sea
    public PortId? HomePortId { get; set; }          // null for nomadic (pirates, brokers)
    public int? TourTicksRemaining { get; set; }     // governor inspection tours

    public float Authority { get; set; } = 50f;      // 0–100
    public PersonalityTraits Personality { get; init; }
    public List<CareerEntry> CareerHistory { get; } = new();

    // Player relationship (legacy — being subsumed by RelationshipMatrix)
    public float PlayerRelationship { get; set; }

    // Flags
    public bool IsAlive { get; set; } = true;
    public bool IsPlayerKnown { get; set; }
    public bool IsCompromised { get; set; }          // burned agent

    // Hidden loyalty — infiltrator model
    public FactionId? ClaimedFactionId { get; set; }  // cover story
    public bool IsInfiltrator => ClaimedFactionId.HasValue && ClaimedFactionId != FactionId;

    // Wealth
    public int CurrentGold { get; set; }

    // Captivity (Crisis 1: VIP Abduction)
    public IndividualId? CaptorId { get; set; }
}

public enum IndividualRole
{
    Governor, PortMerchant, NavalOfficer, PirateCaptain,
    Smuggler, Informant, KnowledgeBroker, Diplomat, Privateer
}
```

```csharp
public sealed record PersonalityTraits
{
    public float Greed { get; init; }      // -1..+1 (principled ↔ self-interested)
    public float Boldness { get; init; }   // -1..+1 (cautious ↔ reckless)
    public float Loyalty { get; init; }    // -1..+1 (opportunistic ↔ keeps word)
    public float Cunning { get; init; }    // -1..+1 (straightforward ↔ deceptive)
    public float Ambition { get; init; }   // -1..+1 (anonymous ↔ craves status)
}
```

---

### 4.9 PlayerState

The player's position in the world — their assets, reputation, and knowledge.

```csharp
// Ahoy.Simulation/State/PlayerState.cs

public sealed class PlayerState
{
    public string CaptainName { get; set; }
    public IndividualId CaptainIndividualId { get; set; }  // for RelationshipMatrix

    public ShipId? FlagshipId { get; set; }
    public List<ShipId> FleetIds { get; } = new();
    public RegionId? CurrentRegionId { get; set; }

    public int PersonalGold { get; set; }
    public float Notoriety { get; set; }  // 0–100

    // Player knowledge held in KnowledgeStore via PlayerHolder.
    // NPC-to-player relationships held in WorldState.RelationshipMatrix.
}
```

---

## 5. The Event Model

Events are the mechanism by which systems communicate state changes — to each other and to the frontend.

### 5.1 WorldEvent Hierarchy

```csharp
// Ahoy.Simulation/Events/WorldEvent.cs

public abstract record WorldEvent(WorldDate OccurredAt);

// Examples of concrete events
public record PortControlChanged(
    WorldDate OccurredAt,
    PortId Port,
    FactionId PreviousFaction,
    FactionId NewFaction
) : WorldEvent(OccurredAt);

public record FactionRelationshipChanged(
    WorldDate OccurredAt,
    FactionId FactionA,
    FactionId FactionB,
    int PreviousStanding,
    int NewStanding
) : WorldEvent(OccurredAt);

public record ShipDestroyed(
    WorldDate OccurredAt,
    ShipId Ship,
    ShipId? DestroyedBy
) : WorldEvent(OccurredAt);

public record IndividualDied(
    WorldDate OccurredAt,
    IndividualId Individual,
    string Cause
) : WorldEvent(OccurredAt);

public record CommodityPriceChanged(
    WorldDate OccurredAt,
    PortId Port,
    TradeGood Good,
    int OldPrice,
    int NewPrice
) : WorldEvent(OccurredAt);

public record GovernorAppointed(
    WorldDate OccurredAt,
    PortId Port,
    IndividualId NewGovernor,
    IndividualId? PreviousGovernor
) : WorldEvent(OccurredAt);
```

### 5.2 IEventEmitter

Systems emit events through an interface, keeping them decoupled from dispatch logic.

```csharp
// Ahoy.Simulation/Events/IEventEmitter.cs

public interface IEventEmitter
{
    void Emit(WorldEvent worldEvent);
}
```

The `SimulationEngine` provides a concrete implementation that collects events during a tick, then dispatches them after all systems have run.

### 5.3 ~~KnownEvent~~ (Removed — replaced by KnowledgeStore)

> **v0.2 note:** `KnownEvent` and `PlayerState.KnowledgeLog` have been replaced
> by the unified `KnowledgeStore` with `KnowledgeFact` entries held in
> `PlayerHolder`. The information delay mechanic (confidence decay, hop penalties,
> disinformation) is now handled by `KnowledgeFact.Confidence` and the
> propagation pipeline. See `SDD-KnowledgeSystem` §2 for the full model.
>
> The same `KnowledgeStore` also drives NPC decisions via `IndividualHolder` and
> `ShipHolder` — no actor reads ground truth. See `SDD-QuestSystem` §5
> (EpistemicResolver) for how NPCs act on held knowledge.

---

## 6. The Tick Pipeline

### 6.1 IWorldSystem

```csharp
// Ahoy.Simulation/Systems/IWorldSystem.cs

public interface IWorldSystem
{
    void Tick(WorldState state, IEventEmitter events);
}
```

Systems are stateless. All state lives in `WorldState`. A system processes the world, mutates state as needed, and emits events. It does not know what other systems will do with those events.

### 6.2 System Execution Order

Order matters — each system sees the state left by those before it.

| Order | System | Responsibility |
|---|---|---|
| 1 | `WeatherSystem` | Update weather state per region; flag severe events |
| 2 | `ShipMovementSystem` | Advance ships along routes; handle arrivals; NPC routing reads NpcPursuits |
| 3 | `EconomySystem` | Update supply/demand; recalculate prices; move trade goods |
| 4 | `FactionSystem` | Evaluate faction goals; update relationships; take actions |
| 5 | `IndividualLifecycleSystem` | NPC career events, governor tours, agent replacement |
| 6 | `EventPropagationSystem` | Cascade world events into downstream state changes |
| 7 | `KnowledgeSystem` | Propagate facts, decay confidence, detect conflicts, Stall & Leak gossip |
| 8 | `QuestSystem` | Player quest lifecycle, NPC goal assignment + EpistemicResolver execution |

### 6.3 SimulationEngine

```csharp
// Ahoy.Simulation/Engine/SimulationEngine.cs

public sealed class SimulationEngine
{
    private readonly WorldState _state;
    private readonly IReadOnlyList<IWorldSystem> _systems;

    public SimulationEngine(WorldState initialState, IReadOnlyList<IWorldSystem> systems)
    {
        _state   = initialState;
        _systems = systems;
    }

    public void Tick()
    {
        var emitter = new TickEventEmitter();

        foreach (var system in _systems)
            system.Tick(_state, emitter);

        _state.Date = _state.Date.Advance();

        DispatchEvents(emitter.DrainEvents());
    }

    // Called by the frontend layer at any time — never mid-tick
    public WorldSnapshot GetSnapshot() => new(_state);

    private void DispatchEvents(IReadOnlyList<WorldEvent> events)
    {
        // Raise to external subscribers (frontend event stream)
        foreach (var e in events)
            EventOccurred?.Invoke(e);
    }

    public event Action<WorldEvent>? EventOccurred;
}
```

`Tick()` is synchronous. The engine never calls itself — it is driven externally (by a game loop, a test harness, or a console command).

---

## 7. Frontend Boundary

The simulation has three contact points with any future frontend. Nothing else crosses the boundary.

### 7.1 WorldSnapshot — Reading State

```csharp
// Ahoy.Simulation/Engine/WorldSnapshot.cs

public sealed class WorldSnapshot
{
    // Shallow snapshot — sufficient for a frontend to render current state
    // Deep clone only needed if the frontend holds state across ticks
    public WorldDate Date                                      { get; }
    public IReadOnlyDictionary<RegionId, Region> Regions       { get; }
    public IReadOnlyDictionary<PortId, Port> Ports             { get; }
    public IReadOnlyDictionary<FactionId, Faction> Factions    { get; }
    public IReadOnlyDictionary<ShipId, Ship> Ships             { get; }
    public PlayerState Player                                  { get; }

    internal WorldSnapshot(WorldState state)
    {
        Date     = state.Date;
        Regions  = state.Regions;
        Ports    = state.Ports;
        Factions = state.Factions;
        Ships    = state.Ships;
        Player   = state.Player;
    }
}
```

### 7.2 IPlayerCommandQueue — Writing Actions

The frontend submits player decisions as commands. The engine applies them at the **start of the next tick**, not immediately, ensuring the sim is never mutated mid-tick from outside.

```csharp
// Ahoy.Simulation/Engine/IPlayerCommandQueue.cs

public interface IPlayerCommandQueue
{
    void Enqueue(PlayerCommand command);
}

public abstract record PlayerCommand;
public record SailToPort(PortId Destination)          : PlayerCommand;
public record PurchaseCommodity(TradeGood Good, int Qty) : PlayerCommand;
public record SellCommodity(TradeGood Good, int Qty)   : PlayerCommand;
public record AcceptContract(ContractId Contract)      : PlayerCommand;
```

### 7.3 EventOccurred — Reactive Updates

The frontend subscribes to `SimulationEngine.EventOccurred` to receive a stream of `WorldEvent`s as they fire. This allows the UI to react to world changes (animate a ship sinking, update a price display) without polling the snapshot on every frame.

---

## 8. Open Questions

- [ ] Serialisation format for save/load — JSON (human-readable, easy debugging) vs. binary (compact, fast). Deferred; the flat dictionary structure is serialisation-ready either way.
- [ ] `Individual` memory model — should `InteractionMemory` store references to `WorldEvent` IDs, or maintain its own summarised representation? The former is more powerful; the latter is simpler.
- [ ] `FactionGoal` subtypes — full enumeration deferred to `FactionSystem` design, but the base type and list ownership is established here.
- [ ] Weather as a first-class system or a modifier on other systems? (e.g. does `WeatherSystem` own a `WeatherState` per region, or just emit events that `ShipMovementSystem` and `EconomySystem` react to?)

---

*Next step: System design for individual systems, starting with `EconomySystem` or `FactionSystem`.*
