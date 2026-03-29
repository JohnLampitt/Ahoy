# Ahoy — System Design: Simulation LOD

> **Status:** Living document.
> **Version:** 0.2 — Region continuity model added
> **Depends on:** SDD-WorldState.md, SDD-KnowledgeSystem.md

---

## 1. The Unifying Principle

LOD and the knowledge system are not two separate concerns. They are the same concern expressed from two angles:

- **From the simulation side:** distant regions receive coarser processing — fewer events, less granularity
- **From the knowledge side:** distant information is less reliable — lower confidence, less precision

These must be the same thing. The reason simulation is coarser at distance is *because* information travels at ship speed. LOD is not a hidden engine optimisation running beneath the knowledge system. **LOD is the simulation-side expression of knowledge fidelity.**

> The fidelity of simulation in a region determines the granularity of events that region generates.
> Events become KnowledgeFacts. Facts flow through the knowledge system normally.
> There is no separate LOD layer — LOD is what happens when knowledge hasn't arrived yet.

This means LOD is visible, consistent, and meaningful to the player — not an invisible performance trick.

---

## 2. LOD Tiers

```csharp
// Ahoy.Simulation/Engine/SimulationLod.cs

public enum SimulationLod
{
    Local,      // Player's current region — full processing, specific events
    Regional,   // One region hop away — full processing, aggregate events
    Distant     // Two or more hops away — coarse processing, major events only
}
```

### Tier Definitions

| Tier | Distance | Processing | Event Granularity | Fact BaseConfidence |
|---|---|---|---|---|
| **Local** | 0 hops | Full every tick | Individual and specific | 0.85 – 1.0 |
| **Regional** | 1 hop | Full every tick | Aggregate and directional | 0.45 – 0.75 |
| **Distant** | 2+ hops | Coarse, key changes only | Summary and major only | 0.10 – 0.40 |

Confidence ranges reflect the inherent precision of events generated at each tier — not just age. A `Local` event is specific and observed; a `Distant` event is a summary inferred from limited signals.

---

## 3. SimulationContext

`SimulationContext` is computed once per tick from the player's current region and passed to every system. Systems use it to determine how to process each region and how to emit events.

```csharp
// Ahoy.Simulation/Engine/SimulationContext.cs

public sealed class SimulationContext
{
    public RegionId PlayerRegion { get; }

    private readonly IReadOnlyDictionary<RegionId, SimulationLod> _regionLods;

    public SimulationContext(RegionId playerRegion, WorldState state)
    {
        PlayerRegion = playerRegion;
        _regionLods  = ComputeAllLods(playerRegion, state);
    }

    public SimulationLod GetLod(RegionId region) =>
        _regionLods.TryGetValue(region, out var lod) ? lod : SimulationLod.Distant;

    private static Dictionary<RegionId, SimulationLod> ComputeAllLods(
        RegionId playerRegion, WorldState state)
    {
        // BFS over region adjacency graph from player's position
        var result   = new Dictionary<RegionId, SimulationLod>();
        var visited  = new HashSet<RegionId>();
        var queue    = new Queue<(RegionId region, int distance)>();

        queue.Enqueue((playerRegion, 0));

        while (queue.Count > 0)
        {
            var (region, distance) = queue.Dequeue();
            if (!visited.Add(region)) continue;

            result[region] = distance switch
            {
                0 => SimulationLod.Local,
                1 => SimulationLod.Regional,
                _ => SimulationLod.Distant
            };

            foreach (var adjacent in state.Regions[region].AdjacentRegionIds)
                if (!visited.Contains(adjacent))
                    queue.Enqueue((adjacent, distance + 1));
        }

        return result;
    }
}
```

The player's region is always defined — even at sea, ships belong to a region. A ship `EnRoute` between two regions is considered to be in the destination region for LOD purposes (it is approaching, its context is forward-looking).

---

## 4. IWorldSystem — Updated Signature

All systems receive the `SimulationContext` alongside `WorldState`:

```csharp
// Ahoy.Simulation/Systems/IWorldSystem.cs

public interface IWorldSystem
{
    void Tick(WorldState state, SimulationContext context, IEventEmitter events);
}
```

Systems iterate over regions (or entities within regions) and branch on `context.GetLod(region)`.

---

## 5. IEventEmitter — LOD-Aware Emission

Events are emitted with their source LOD tier. `KnowledgeSystem` uses this to assign `BaseConfidence` when converting events to `KnowledgeFact`s.

```csharp
// Ahoy.Simulation/Events/IEventEmitter.cs

public interface IEventEmitter
{
    void Emit(WorldEvent worldEvent, SimulationLod sourceLod);
}
```

`SimulationEngine` provides the concrete implementation. `KnowledgeSystem` reads emitted events and uses `sourceLod` as the primary driver of `BaseConfidence`:

```csharp
private float BaseConfidenceForLod(SimulationLod lod) => lod switch
{
    SimulationLod.Local    => 0.95f,
    SimulationLod.Regional => 0.60f,
    SimulationLod.Distant  => 0.25f,
    _                      => 0.25f
};
```

Age-based degradation (from `SDD-KnowledgeSystem`) applies on top of this initial confidence.

---

## 6. Per-System LOD Behaviour

### 6.1 ShipMovementSystem

| LOD | Behaviour |
|---|---|
| **Local** | Individual ships advance day by day; arrival and departure events are specific (named ship, named port); ships can be encountered by the player |
| **Regional** | Ships advance along routes; arrival/departure events fired but without encounter-level detail; ships exist in WorldState but are not encounter-eligible |
| **Distant** | Ships bulk-advanced along their routes; only significant transitions fire (arrived at a major port, route abandoned); no per-ship daily events |

Named faction ships (flagships, notable vessels) always process at their actual LOD tier. Bulk/unnamed faction naval presence is abstract at all tiers — represented as `PatrolStrength` allocation on factions, not as individual ships.

### 6.2 EconomySystem

| LOD | Behaviour |
|---|---|
| **Local** | Per-commodity, per-port price calculations every tick; individual cargo loading/unloading for each merchant; specific `CommodityPriceChanged` events |
| **Regional** | Per-port prices updated; merchant cargo processed in bulk per route; aggregate trade flow events (`TradeVolumeChanged`) rather than per-shipment events |
| **Distant** | Region-level trade health tracked (a single `TradeHealth` value per region, 0–100); only significant threshold crossings fire events (`RegionTradeCollapsed`, `RegionTradeBoom`) |

Merchant routing decisions (`ScoreDestination`) only run at **Local** and **Regional** tiers. Distant merchants follow their last-set route without re-evaluating until their tier improves.

### 6.3 FactionSystem

| LOD | Behaviour |
|---|---|
| **Local** | Full goal evaluation; named NPC actions tracked individually; patrol events specific (patrol spotted in region); individual port prosperity updates |
| **Regional** | Full goal evaluation; actions resolved as aggregate outcomes; patrol events are presence-level (`SpanishPatrolPresenceIncreased`) not ship-level |
| **Distant** | Only major goal completions tracked; only high-threshold events fire (`PortCaptured`, `WarDeclared`, `AllianceFormed`); treasury and naval strength update from summaries |

### 6.4 KnowledgeSystem

| LOD | Behaviour |
|---|---|
| **Local** | Full knowledge spreading via individual ships; port gossip pools active; broker inventories updated; player receives fine-grained facts |
| **Regional** | Knowledge spreading via ship route movements; port pools updated but with reduced fidelity; player receives directional facts |
| **Distant** | Only major facts propagate into the knowledge network; knowledge spreading minimal; player receives summary facts or nothing |

---

## 7. State Model — What Is Always Maintained

Full entity state is maintained everywhere regardless of LOD tier. There is no state compression or materialisation:

- All `Ship` entities exist in `WorldState.Ships` at all times
- All `Port` commodities exist regardless of how precisely they're being updated
- All `Individual` records exist regardless of how granularly they're being ticked

What LOD controls is **processing fidelity and event granularity**, not the existence of state. This avoids any "snap" discontinuities when LOD changes — entities are always there, just more or less actively simulated.

---

## 8. Region Transitions — The Discovery Mechanic

When the player moves from a distant or regional region into a new local region, the LOD tier for that region increases to `Local`. On the next tick, full processing fires for the first time — generating fine-grained, high-confidence events that feed into the player's `KnowledgeStore`.

This is the **discovery moment**: the player's prior knowledge (from distant or regional facts, possibly stale or imprecise) is confronted with ground truth. The knowledge system surfaces this naturally:

- Prior facts about the region get superseded by new, high-confidence local facts
- Where new facts *contradict* old ones, the player's intel was wrong
- Where they *align*, the intel was good

No special catch-up code is required. The LOD tier change drives richer event generation; the knowledge system handles the rest through its normal supersession and contradiction logic.

**Example:**

> The player has a `RegionSafetySnapshot` for the Gulf with `SafetyScore=0.7, Confidence=0.25` — a 60-day-old rumour that the Gulf was relatively safe.
>
> They sail in. First local tick fires. `ShipMovementSystem` generates specific patrol events. `FactionSystem` generates named NPC patrol encounters. The Gulf actually has `SafetyScore=0.2` — the Spanish increased patrols after a war started two months ago.
>
> The old fact is superseded. The player's knowledge now reflects reality. They sailed into much more danger than their intel suggested.

---

## 9. TradeHealth — The Distant Economy Summary

For distant regions, `EconomySystem` maintains a single `TradeHealth` value (0–100) per region rather than per-commodity tracking. This is how distant economic state is represented without full commodity simulation.

```csharp
// Added to Region

public int TradeHealth { get; internal set; } = 50;  // 0 = collapsed, 100 = thriving
```

`TradeHealth` is updated by `EconomySystem` at all LOD tiers:
- At `Local`/`Regional`: derived from the weighted average of commodity supply/demand ratios across the region's ports
- At `Distant`: updated directly from high-level inputs (war presence, faction naval strength, major events)

When a region transitions from Distant to Local LOD, `TradeHealth` is used to seed initial commodity prices for ports that haven't been fully simulated recently — ensuring continuity.

```csharp
// EconomySystem — on LOD transition to Local
private void SeedPricesFromTradeHealth(Region region, WorldState state)
{
    var healthFactor = region.TradeHealth / 100f;  // 0.0 – 1.0

    foreach (var portId in region.PortIds)
    {
        var port = state.Ports[portId];
        foreach (var commodity in port.Inventory.Values)
        {
            // Trade health modulates supply: healthy = ample supply, collapsed = scarce
            commodity.Supply = (int)(commodity.Demand * healthFactor * 1.5f);
            commodity.Price  = Math.Clamp(
                (int)(commodity.BasePrice / healthFactor),
                (int)(commodity.BasePrice * 0.2f),
                (int)(commodity.BasePrice * 5.0f));
        }
    }
}
```

---

## 10. PatrolStrength — The Distant Naval Summary

For distant regions, faction naval presence is represented as an abstract `PatrolStrength` allocation rather than individual ships. This feeds directly into region safety ratings and knowledge facts.

```csharp
// Added to Faction

// How much naval strength is allocated to each region for patrol purposes
public Dictionary<RegionId, int> PatrolAllocations { get; } = new();
```

At `Local` LOD, `PatrolStrength` is expressed as encounter probability for specific patrol ships. At `Regional` and `Distant` LOD, it is expressed as an aggregate `PatrolPresenceClaim` fact:

```csharp
// Regional/Distant event emitted by FactionSystem
public record PatrolPresenceChanged(
    WorldDate OccurredAt,
    RegionId Region,
    FactionId Faction,
    PatrolLevel Level   // None, Light, Moderate, Heavy, Overwhelming
) : WorldEvent(OccurredAt);
```

The player's safety knowledge about a distant region is built from these aggregate claims, not individual ship sightings.

---

## 11. Region Continuity

When the player revisits a region they've been to before, two failure modes must be avoided:

1. **Implausible state** — entities frozen in impossible conditions (a fully-laden ship still docked 60 days later with no explanation)
2. **Unjustified discontinuity** — prices, NPC positions, or port control have shifted in ways no knowledge fact explains; the world feels edited rather than continued

The player has two distinct relationships with regions:

- **Never visited** — no prior mental model; the current state is the only state they know
- **Previously visited** — they have expectations; the world must feel like it *continued*, not like it was *reset*

The continuity model addresses this through three mechanisms: the Plausibility Contract, Entity Anchoring, and the Return Sequence.

---

### 11.1 The Plausibility Contract

When a region drops to `Distant` LOD, the simulation makes a weaker guarantee: it will not leave state in a logically inconsistent condition. It need not be *precise*, but it must be *believable*.

Each system is bound by this contract at `Distant` LOD:

| System | Plausibility Obligation |
|---|---|
| **ShipMovementSystem** | Ships ready to depart must depart or have an explicit reason not to (blocked by weather, waiting for cargo). Ships cannot remain indefinitely docked without explanation. |
| **EconomySystem** | Ports must not sit at zero supply with no event explaining the shortage. `TradeHealth` must reflect the actual flow of goods through the region even when per-commodity tracking is suspended. |
| **FactionSystem** | Factions must continue acting on their active goals, even if resolution is coarse. A faction that was mid-conquest when the player left must either succeed, fail, or still be in progress — not freeze. |
| **KnowledgeSystem** | Facts generated at `Distant` LOD must be sufficient to explain major state changes to the player when they return. No significant change should be unexplained. |

---

### 11.2 Entity Anchoring

The player has richer expectations about entities they have directly observed or interacted with. These entities are **anchored** — the coarse simulation handles them with extra care to preserve continuity.

**Anchor status is derived automatically from the `KnowledgeStore`.** An entity is anchored for a specific player if:

- The player holds a `KnowledgeFact` about that entity with `Confidence >= 0.7` (directly observed or reliably reported)
- The player has a non-zero personal reputation with that `Individual`
- The entity is a port the player has docked at within the last 90 ticks

```csharp
// Ahoy.Simulation/Engine/ContinuityAnchor.cs

public static bool IsAnchored(KnowledgeHolderId entity, WorldState state)
{
    // Check if player holds any high-confidence facts about this entity
    return state.Knowledge
        .FactsKnownBy(new PlayerHolder())
        .Any(f => FactReferencesEntity(f, entity) && f.CurrentConfidence(state.Date) >= 0.7f);
}
```

**What anchoring means for the coarse simulation:**

Anchored entities receive **explicit state transitions** at `Distant` LOD rather than being bulk-advanced silently. Each significant change to an anchored entity generates a `Distant`-confidence knowledge fact that enters the normal propagation pipeline:

> *"Captain Rodrigo's galleon was reported departing Nassau"* (Confidence: 0.25)

The player may or may not receive this fact before they return, depending on how quickly it propagates. But it exists in the world — and when they return, the entity's state is explainable.

Non-anchored entities can be bulk-advanced without generating individual facts. Their state changes are absorbed into regional summaries.

---

### 11.3 The Return Sequence

When the player's `SimulationContext` causes a region's LOD to increase (from `Distant` or `Regional` to `Local`), `SimulationEngine` triggers a **Return Sequence** before the first full tick runs in that region.

```csharp
// Ahoy.Simulation/Engine/SimulationEngine.cs

private void OnLodIncreased(RegionId region, SimulationLod previous, SimulationLod current, WorldState state)
{
    if (current != SimulationLod.Local) return;

    // 1. Seed economic state from TradeHealth summary
    _economySystem.SeedPricesFromTradeHealth(region, state);

    // 2. Verify anchored entities are in plausible states; patch if not
    _continuitySystem.ReconcileAnchoredEntities(region, state);

    // 3. Generate discovery facts — what the player observes on arrival
    _continuitySystem.GenerateReturnDiscoveryFacts(region, state);
}
```

#### Step 1 — Economic Seeding
Already defined in Section 9. Commodity prices and supply are initialised from `TradeHealth` for ports that have been on `Distant` LOD. The full `EconomySystem` then takes over from the next tick.

#### Step 2 — Anchored Entity Reconciliation

`ReconcileAnchoredEntities()` walks anchored entities in the region and checks for implausible states:

```csharp
// Ahoy.Simulation/Engine/ContinuitySystem.cs

public void ReconcileAnchoredEntities(RegionId region, WorldState state)
{
    var anchoredShips = state.Ships.Values
        .Where(s => IsInRegion(s, region, state) && IsAnchored(new IndividualHolder(s.CaptainId ?? default), state));

    foreach (var ship in anchoredShips)
    {
        // Ship was docked and ready to depart — has it been frozen?
        if (ship.Location is AtPort port && DaysSinceDocked(ship, state) > 14)
        {
            // Force a plausible resolution: depart toward the best known destination
            var captain = ship.CaptainId is { } id ? state.Individuals[id] : null;
            if (captain?.MerchantState is not null)
                _economySystem.ForceDeparture(ship, captain, state, _events);
        }
    }
}
```

This is a safety net, not a primary mechanism. If the Plausibility Contract is being honoured by each system, reconciliation finds little to fix. Its presence prevents edge cases from surfacing as jarring bugs.

#### Step 3 — Return Discovery Facts

`GenerateReturnDiscoveryFacts()` compares the player's last high-confidence knowledge of this region against its current state, then generates a batch of `Local`-confidence facts representing what the player observes upon arrival.

```csharp
public void GenerateReturnDiscoveryFacts(RegionId region, WorldState state)
{
    var playerFacts = state.Knowledge
        .FactsKnownBy(new PlayerHolder())
        .Where(f => FactIsAboutRegion(f, region))
        .ToList();

    // Port control changes
    foreach (var portId in state.Regions[region].PortIds)
    {
        var port = state.Ports[portId];
        var lastKnownController = GetLastKnownController(portId, playerFacts);

        if (lastKnownController != null && lastKnownController != port.ControllingFaction)
        {
            _events.Emit(new PortControlChanged(
                state.Date, portId, lastKnownController.Value, port.ControllingFaction),
                SimulationLod.Local);
        }
    }

    // Governor changes, major price divergences, patrol presence shifts...
    // Each generates a Local-confidence fact that supersedes the player's prior knowledge
}
```

The player experiences this as arriving and observing. The facts flow into their `KnowledgeStore` at `Local` confidence. Prior facts that contradict current reality are superseded. The gap between what they expected and what they find is the measure of how much the world moved without them.

---

### 11.4 The Player Experience

The full continuity model produces the following felt experience:

**If the player was away a short time (a few weeks):**
- Prices have shifted slightly
- A merchant they knew has departed
- Minor patrol changes
- The world feels like it ticked on

**If the player was away a long time (months):**
- Significant price divergence — the economy moved
- An NPC they knew may have a new role, or be gone entirely
- Port control may have changed; a war may have concluded
- The player's prior knowledge is substantially stale
- Arriving feels like returning to a place after a long absence — familiar but changed

**If the player had poor prior knowledge of the region:**
- The current state is simply revealed; there is nothing to contradict
- This is the same as a first visit

---

### 11.5 What This Does Not Do

The continuity model does **not** retroactively simulate everything that happened while the player was away. The coarse simulation runs plausible, summarised state changes — the player gets the *outcomes*, not the full history.

This is intentional and consistent with the information delay principle: you don't get a detailed account of everything that occurred while you were gone. You arrive, you observe, you piece it together. Some things you learn immediately. Others you learn by asking around. A few things you may never find out.

---

## 13. Revisions to Prior SDDs

### SDD-WorldState
- `IWorldSystem.Tick()` signature updated to include `SimulationContext`
- `IEventEmitter.Emit()` updated to include `SimulationLod sourceLod`
- `Region` gains `int TradeHealth`
- `Faction` gains `Dictionary<RegionId, int> PatrolAllocations`
- `WorldState` gains `SimulationContext CurrentContext` (updated at the start of each tick)

### SDD-EconomySystem
- `EconomySystem.Tick()` branches on `context.GetLod(region)` for all per-region processing
- Merchant routing (`ScoreDestination`) only runs at Local and Regional tiers
- Distant tier uses `TradeHealth` summary model
- `SeedPricesFromTradeHealth()` runs when a region transitions to Local LOD

### SDD-KnowledgeSystem
- `KnowledgeFact` gains `SimulationLod GeneratedAtLod`
- `BaseConfidence` is initialised from `GeneratedAtLod` in `KnowledgeSystem.OnWorldEvent()`
- Age-based confidence decay applies on top of the LOD-based initial value
- `KnowledgeSystem.Originate()` reads `sourceLod` from emitted events

---

## 14. Open Questions

- [ ] Should `SimulationLod.Regional` be one hop or up to two hops? With only five regions in the Caribbean, one hop means almost everything is either Local or Regional. Two hops for Regional would make Distant genuinely rare.
- [ ] When the player is `EnRoute` between two regions, should LOD be based on origin, destination, or a blend? Destination is proposed here but worth validating during implementation.
- [ ] Should `TradeHealth` be exposed to the player directly as a visible metric, or only ever expressed through price signals and knowledge facts?
- [ ] The `ContinuitySystem` is introduced here as a component of `SimulationEngine` — should it be a named system in the tick pipeline, or remain an engine-internal concern?
- [ ] How long should the anchor window be for ports (currently 90 ticks / 3 months)? This determines how quickly the player "forgets" a port and stops receiving anchored continuity treatment.

---

*Next step: SDD-FactionSystem — goals, relationships, and how factions act on the world each tick, using the unified LOD model.*
