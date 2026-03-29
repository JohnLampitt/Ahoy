# Ahoy — System Design: EventPropagationSystem

> **Status:** Living document.
> **Version:** 0.1
> **Depends on:** SDD-WorldState.md, SDD-SimulationLOD.md, SDD-KnowledgeSystem.md, SDD-FactionSystem.md

---

## 1. Overview

`EventPropagationSystem` is the fifth system in the tick pipeline — it runs after all simulation systems have acted and before `KnowledgeSystem` converts events to facts.

Its role is to take the raw `WorldEvent`s emitted by systems 1–4 during the current tick and **cascade them into downstream state mutations and secondary events**. It is the mechanism by which cause and effect ripples through the world:

> *Hurricane destroys a warehouse → food supply collapses → prosperity falls → governor loses authority → power vacuum → pirates establish a foothold → regional safety drops → merchants divert → trade income falls → faction treasury pressured*

No individual system owns this chain. Each step is a `PropagationRule` reacting to the previous event. The cascade plays out across multiple ticks, driven by the normal simulation cycle.

`EventPropagationSystem` is not a general event bus or pub/sub system. It has a specific, bounded role: **translate events into world state mutations**. Coordination between systems happens through shared `WorldState`, not through this system.

---

## 2. New State — Intermediary Variables

EventPropagation requires two intermediary state variables not established in prior SDDs. These are key nodes in cascading chains — many events affect them, and they in turn affect many outputs.

### 2.1 Port Prosperity

Already present on `Port` as `int Prosperity { get; internal set; }` (0–100). Formalised here with its full input/output contract.

**Inputs (what drives prosperity up/down):**

| Cause | Delta |
|---|---|
| Active trade volume through port | +1 per threshold per tick |
| Food supply shortage | −8 per tick while shortage persists |
| General commodity shortage | −3 per tick |
| War active in region | −5 per tick |
| Hurricane or severe weather | −15 to −40 (one-time) |
| Faction provides support (treasury spend) | +5 one-time |
| Player trades regularly at port | +1 per N visits |
| Pirates raid nearby shipping | −2 per tick while active |

**Outputs (what prosperity drives):**

| Prosperity Level | Effect |
|---|---|
| > 70 | Bonus tax income for controlling faction; attracts more merchants |
| 40–70 | Normal operation |
| 20–40 | Governor authority begins declining; garrison understrength |
| < 20 | Pirate haven establishment becomes easy; garrison near collapse; faction income severely impacted |
| < 5 | Port may become effectively ungoverned — no controlling faction functions |

### 2.2 Governor Authority

Added to `Individual` for `IndividualRole.Governor`:

```csharp
// Added to Individual (Governor only)
public int Authority { get; internal set; } = 75;  // 0–100
```

**Inputs:**

| Cause | Delta |
|---|---|
| Port prosperity drops below 40 | −3 per tick while below threshold |
| Port prosperity above 60 | +1 per tick recovery |
| Controlling faction sends support | +10 one-time |
| Successful defence of port | +15 one-time |
| Corruption event exposed | −20 one-time |
| Governor replaced (new appointment) | Reset to 65 |

**Outputs:**

| Authority Level | Effect |
|---|---|
| > 60 | Normal; garrison at full strength |
| 40–60 | Garrison at 80% strength; minor pirate tolerance |
| 20–40 | Garrison at 50%; port bribes become cheaper; pirate haven growth unsuppressed |
| < 20 | Power vacuum conditions met; faction loses effective control without formal port capture |
| < 10 | `PowerVacuumCreated` event fires (once per governor tenure) |

---

## 3. The Propagation Model

### 3.1 Rule-Based Cascade

`EventPropagationSystem` maintains an ordered list of `PropagationRule`s. Each rule:

- **Matches** a specific event type and optional world-state condition
- **Applies** a state mutation and optionally emits secondary events
- **Inherits LOD** from its trigger event — secondary events carry the same (or lower) confidence as what triggered them

```csharp
// Ahoy.Simulation/Systems/Propagation/PropagationRule.cs

public abstract class PropagationRule
{
    // Can this rule fire for the given event in the current world state?
    public abstract bool Matches(WorldEvent worldEvent, WorldState state, SimulationContext context);

    // Apply the rule: mutate state and/or emit secondary events
    public abstract void Apply(
        WorldEvent     trigger,
        WorldState     state,
        SimulationContext context,
        IEventEmitter  events,
        int            depth);
}
```

Rules are registered in `EventPropagationSystem` at startup. New rules can be added without touching existing ones — the system is open to extension.

### 3.2 Cascade Depth

Secondary events emitted by a rule are processed in the same tick, up to a maximum cascade depth of **3**. At depth 3, rules still apply state mutations but do not emit further secondary events. This prevents unbounded cascade chains within a single tick.

```csharp
private void ProcessEvent(WorldEvent worldEvent, WorldState state, SimulationContext context, int depth)
{
    if (depth > MaxCascadeDepth) return;

    foreach (var rule in _rules.Where(r => r.Matches(worldEvent, state, context)))
    {
        var emitter = depth < MaxCascadeDepth
            ? _events                              // can emit secondary events
            : new SuppressedEmitter(_events);      // applies mutations, suppresses further emission

        rule.Apply(worldEvent, state, context, emitter, depth);
    }
}
```

Chains that need more than 3 steps to propagate complete naturally across subsequent ticks — the simulation cycle continues the cascade for free.

### 3.3 Loop Prevention

Two guards prevent circular cascades:

1. **Type guard:** a rule cannot emit an event of the same type as its trigger
2. **Entity guard:** a rule cannot emit an event about the same entity it just processed at the same depth

These are enforced by the `IEventEmitter` wrapper at each depth level.

---

## 4. Core Propagation Rules

### Category A — Economic Consequences

**A1: CommodityShortage → ProsperityDecline**
```
Trigger:  CommodityShortage(port, Food, amount)
Condition: shortage > 0
Mutation: port.Prosperity -= 8
Emits:    PortProsperityChanged(port, -8)
```

**A2: CommodityShortage (non-food) → ProsperityDecline**
```
Trigger:  CommodityShortage(port, good, amount) where good != Food
Mutation: port.Prosperity -= 3
Emits:    PortProsperityChanged(port, -3)
```

**A3: CommodityPriceChanged (major spike) → MerchantRouteAwareness**
```
Trigger:  CommodityPriceChanged(port, good, old, new) where new > old * 1.5
Emits:    TradeOpportunitySignal(port, good)   [enters knowledge system as a high-value economic fact]
```

**A4: TradeVolumeGrew → ProsperityIncrease**
```
Trigger:  TradeVolumeGrew(port, delta)
Mutation: port.Prosperity += clamp(delta / 100, 0, 3)
```

**A5: PortControlChanged → TradeDisruption**
```
Trigger:  PortControlChanged(port, fromFaction, toFaction)
Condition: fromFaction != toFaction
Mutation: port.Inventory foreach good: supply -= supply * 0.3  [conquest disrupts supply]
Emits:    TradeRouteDisrupted(port.Region, duration: 14 days)
```

---

### Category B — Political Consequences

**B1: PortProsperityChanged → GovernorAuthorityDecline**
```
Trigger:  PortProsperityChanged(port, delta) where delta < 0
Condition: port.Prosperity < 40 && port.GovernorId != null
Mutation: governor.Authority -= 3
Emits:    GovernorAuthorityChanged(governor, -3)  [if authority crosses threshold]
```

**B2: GovernorAuthorityChanged → PowerVacuum**
```
Trigger:  GovernorAuthorityChanged(governor, delta) where governor.Authority < 10
Condition: !VacuumAlreadyActive(port)
Mutation: (none — structural change handled by FactionSystem next tick via pending stimulus)
Emits:    PowerVacuumCreated(port)
```

**B3: PowerVacuumCreated → HavenOpportunity**
```
Trigger:  PowerVacuumCreated(port)
Emits:    HavenOpportunityDetected(port)  [pirate brotherhoods receive this as a knowledge fact]
```

**B4: WarDeclared → TradeDisruption**
```
Trigger:  WarDeclared(factionA, factionB)
Foreach:  region where both factions have presence
Emits:    TradeRouteDisrupted(region, duration: while war persists)
Mutation: factionA-factionB trade volume → 0
```

**B5: GovernorAppointed → AuthorityReset**
```
Trigger:  GovernorAppointed(port, newGovernor, previousGovernor)
Mutation: newGovernor.Authority = 65
          port.InstitutionalReputation = lerp(port.InstitutionalReputation, 0, 0.3)
          [slight institutional memory fade on succession]
```

---

### Category C — Military Consequences

**C1: ShipRaided → RegionSafetyPressure**
```
Trigger:  ShipRaided(ship, raidingFaction, region)
Emits:    PirateActivityIncreased(region, raidingFaction)
[FactionSystem consumes this next tick when computing RegionSafety]
```

**C2: PatrolPresenceChanged (increased) → MerchantConfidence**
```
Trigger:  PatrolPresenceChanged(region, faction, level) where level increased
Emits:    RegionSafetyImproved(region)
[enters knowledge network — merchants update their safety knowledge]
```

**C3: ShipDestroyed (faction naval) → NavalStrengthDecline**
```
Trigger:  ShipDestroyed(ship) where ship.OwningFaction != null && ship is named
Mutation: faction.NavalStrength -= ShipClass.StrengthValue(ship.Class)
Emits:    FactionNavalStrengthChanged(faction, -delta)
```

**C4: FactionTreasuryCollapsed → NavalDecay**
```
Trigger:  FactionTreasuryCollapsed(faction)
Mutation: faction.NavalStrength = (int)(faction.NavalStrength * 0.85f)  [-15% per tick while collapsed]
Emits:    FactionNavalStrengthChanged(faction, -decayAmount)
```

---

### Category D — Weather and Natural Events

**D1: HurricaneEvent → Production Collapse**
```
Trigger:  HurricaneEvent(region, severity)  [emitted by WeatherSystem]
Foreach:  port in region
Mutation: port.Prosperity -= severity * 25
          port.Profile.ActiveModifiers["Hurricane"] = new ProductionModifier(
              AffectedGood: null,           // all goods
              Multiplier: 1f - severity * 0.6f,
              ExpiresAt: state.Date.Advance(30))
Emits:    PortProsperityChanged(port, -delta) foreach port
```

**D2: ProductionModifierExpired → ProsperityRecovery**
```
Trigger:  ProductionModifierExpired(port, modifier) where modifier was Hurricane
Mutation: port.Prosperity += 10  [partial recovery — full recovery takes ongoing trade]
```

---

### Category E — Social Consequences

**E1: ProsperityDecline (sustained) → PopulationUnrest**
```
Trigger:  PortProsperityChanged(port, delta) where delta < 0
Condition: port.Prosperity < 20 && DaysBelowThreshold(port, 20) > 30
Emits:    PopulationUnrest(port)  [raises pirate tolerance, reduces garrison effectiveness]
```

**E2: BrotherhoodFractured → RegionalInstability**
```
Trigger:  BrotherhoodFractured(originalFaction, factionA, factionB)
Foreach:  region where originalFaction had haven presence
Mutation: region.TradeHealth -= 10
Emits:    RegionalInstabilityRaised(region)
```

---

## 5. The Within-Tick Processing Loop

```csharp
// Ahoy.Simulation/Systems/EventPropagationSystem.cs

public sealed class EventPropagationSystem : IWorldSystem
{
    private const int MaxCascadeDepth = 3;
    private readonly IReadOnlyList<PropagationRule> _rules;

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Drain events emitted by systems 1–4 this tick
        var primaryEvents = events.DrainPending();

        foreach (var worldEvent in primaryEvents)
            ProcessEvent(worldEvent, state, context, depth: 1);

        // All events (primary + secondary) now pass to KnowledgeSystem via IEventEmitter
    }

    private void ProcessEvent(
        WorldEvent worldEvent, WorldState state, SimulationContext context, int depth)
    {
        foreach (var rule in _rules)
        {
            if (!rule.Matches(worldEvent, state, context)) continue;

            // At max depth: mutations allowed, secondary emission suppressed
            var emitter = depth >= MaxCascadeDepth
                ? new DepthLimitedEmitter(_events, worldEvent.GetType())
                : new PropagationEmitter(_events, worldEvent.GetType(),
                    secondary => ProcessEvent(secondary, state, context, depth + 1));

            rule.Apply(worldEvent, state, context, emitter, depth);
        }
    }
}
```

`DrainPending()` is added to `IEventEmitter` — it returns all events collected so far this tick, clearing the buffer. `EventPropagationSystem` processes them, then `KnowledgeSystem` receives the final complete set (primary + all secondary events) through the same emitter.

---

## 6. Multi-Tick Cascades

Propagation intentionally does not attempt to resolve full causal chains within a single tick. The simulation cycle handles continuation:

| Tick | What happens |
|---|---|
| N | `HurricaneEvent` fires → `ProductionModifier` applied → `PortProsperityChanged` emitted |
| N+1 | `EconomySystem` sees reduced production → `CommodityShortage` emitted → `PortProsperityChanged` (further decline) |
| N+2 | `EventPropagationSystem` processes sustained shortage → `GovernorAuthorityDecline` |
| N+3 | Governor authority crosses threshold → `PowerVacuumCreated` |
| N+5 | `FactionSystem` evaluates `HavenOpportunityDetected` fact → `EstablishHavenGoal` adopted |
| N+15 | Haven established → `RegionSafetyChanged` (declined) → merchants begin routing around the region |
| N+20 | Reduced merchant traffic → trade income falls → faction treasury pressured |

Each step is a normal tick with no special behaviour. The cascade emerges from the system interactions.

The gap between cause and full effect is itself a gameplay mechanic — the player can observe early warning signs (falling prices, reduced patrol sightings, rumours of unrest) and act before the situation deteriorates. Or they can be the cause of the cascade and watch the consequences unfold.

---

## 7. LOD Considerations

`EventPropagationSystem` does not directly query LOD tiers — it processes whatever events the upstream systems emitted. Since those systems already filtered their event granularity by LOD, `EventPropagationSystem` naturally operates at the appropriate fidelity.

However, secondary events inherit the LOD of their trigger:

```csharp
// PropagationEmitter ensures secondary events carry the trigger's LOD
public void Emit(WorldEvent secondary, SimulationLod lod) =>
    _inner.Emit(secondary, Min(_triggerLod, lod));  // secondary events never more confident than trigger
```

A `PowerVacuumCreated` event originating from a `Distant`-LOD `PortProsperityChanged` is emitted at `Distant` confidence — appropriately imprecise. The knowledge system will convert it to a low-confidence rumour, not a definitive fact.

---

## 8. Pending Stimulus Queue

Some propagation outcomes require `FactionSystem` action — for example, a `PowerVacuumCreated` event should trigger a pirate brotherhood to evaluate an `EstablishHavenGoal`. But `FactionSystem` has already run this tick.

These outcomes are placed in a **pending stimulus queue** on `WorldState`, consumed by `FactionSystem` at the start of its next tick before goal evaluation:

```csharp
// Added to WorldState
public Queue<FactionStimulus> PendingFactionStimuli { get; } = new();
```

```csharp
// Ahoy.Simulation/State/FactionStimulus.cs
public abstract record FactionStimulus;
public record PowerVacuumStimulus(PortId Port)              : FactionStimulus;
public record HavenOpportunityStimulus(PortId Port)         : FactionStimulus;
public record NavalDecayStimulus(FactionId Faction, int Amount) : FactionStimulus;
public record TreasuryCollapsedStimulus(FactionId Faction)  : FactionStimulus;
```

`FactionSystem.Tick()` drains `PendingFactionStimuli` first, injecting stimuli directly into goal evaluation scoring before running the normal evaluation pass.

---

## 9. Revisions to Prior SDDs

### SDD-WorldState
- `WorldState` gains `Queue<FactionStimulus> PendingFactionStimuli`
- `Individual` (Governor) gains `int Authority`
- `IEventEmitter` gains `IReadOnlyList<WorldEvent> DrainPending()`

### SDD-FactionSystem
- `FactionSystem.Tick()` drains and processes `PendingFactionStimuli` before goal evaluation
- `PowerVacuumCreated` and `HavenOpportunityDetected` raise `EstablishHavenGoal` utility scores significantly

### SDD-EconomySystem
- `TradeRouteDisrupted` events received by `EconomySystem` next tick reduce merchant routing scores for the affected region (safely score drops sharply)

### SDD-KnowledgeSystem
- `TradeOpportunitySignal` enters the knowledge network as a high-value `Economic` fact — brokers actively sell these
- `RegionSafetyImproved` / `PirateActivityIncreased` facts feed directly into merchant `RegionSafetySnapshot` updates

---

## 10. Open Questions

- [ ] Should `PropagationRule`s be data-driven (loaded from config/JSON at startup) or code-defined (compiled in)? Data-driven would allow world designers to add/tune cascades without recompiling. Code-defined is simpler and type-safe. Given the C# backend-first approach, code-defined is proposed for now.
- [ ] `PopulationUnrest` is emitted but its downstream effects (pirate tolerance, reduced garrison effectiveness) need a home — does it live as a flag on `Port`, or as a timed modifier similar to `ProductionModifier`? Timed modifier is more consistent.
- [ ] Should the player ever be able to intentionally trigger a cascade? (e.g. sabotage a port's food supply to engineer a power vacuum). Strong gameplay hook — the rules engine makes this natural to implement once the mechanics exist.
- [ ] Maximum cascade depth of 3 is an arbitrary safety value. The right number depends on playtesting — too low and cascades feel truncated; too high and a single event could mutate too much state in one tick.

---

*Next step: WeatherSystem and ShipMovementSystem, or a review of the full system architecture before implementation begins.*
