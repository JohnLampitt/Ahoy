# Ahoy — System Design: EconomySystem

> **Status:** Living document.
> **Version:** 0.1
> **Depends on:** SDD-WorldState.md

---

## 1. Overview

`EconomySystem` is the third system in the tick pipeline. It is responsible for:

- Producing goods at ports each tick
- Consuming goods at ports (population demand, production inputs)
- Deciding what cargo departing merchant ships load, and where they sail next
- Processing arriving merchant ships (unloading cargo, updating merchant knowledge)
- Recalculating commodity prices from current supply and demand
- Emitting events for significant economic changes

Goods move between ports **via simulated merchant ships**. There are no abstract trade flows — supply increases at a destination only when a ship physically arrives and unloads. The economy is a direct consequence of ship traffic.

This makes economic cause and effect **visible and traceable**: prices are high because no ships have come; ships stopped coming because a merchant heard the route was dangerous; the route became dangerous because the player raided it twice last month.

---

## 2. New Data Structures

### 2.1 Port: EconomicProfile

A static configuration set at world creation. Defines what a port produces and needs. Modified by dynamic world events (hurricanes, wars, prosperity changes) but not recalculated from scratch each tick.

```csharp
// Ahoy.Simulation/State/EconomicProfile.cs

public sealed class EconomicProfile
{
    // Goods this port produces per tick, at base prosperity
    public Dictionary<TradeGood, int> BaseProduction { get; init; } = new();

    // Goods this port consumes per tick (unmet demand drives scarcity)
    public Dictionary<TradeGood, int> BaseConsumption { get; init; } = new();

    // Production modifiers applied by current world state (e.g. -0.5 after a hurricane)
    // Keyed by a string tag so events can add/remove them cleanly
    public Dictionary<string, ProductionModifier> ActiveModifiers { get; } = new();

    public int EffectiveProduction(TradeGood good)
    {
        var base_ = BaseProduction.GetValueOrDefault(good, 0);
        var multiplier = ActiveModifiers.Values
            .Where(m => m.AffectedGood == good || m.AffectedGood == null)
            .Aggregate(1f, (acc, m) => acc * m.Multiplier);
        return (int)(base_ * multiplier);
    }
}

public record ProductionModifier(
    TradeGood? AffectedGood,   // null = affects all goods
    float Multiplier,
    WorldDate ExpiresAt        // modifiers are temporary; they expire
);
```

`Port` gains an `EconomicProfile Profile` property.

### Port Economic Archetypes

Ports are hand-crafted at world creation with profiles reflecting their geography and purpose:

| Archetype | Produces | Consumes |
|---|---|---|
| **Plantation** | Sugar, Tobacco, Cotton, Coffee | Food, Cloth |
| **Distillery** | Rum | Sugar, Food |
| **Logging & Rope Walk** | Timber, Rope | Food, Cloth |
| **Armory** | Cannon, Powder | Timber |
| **Trade Hub** | — (redistribution only) | Mixed |
| **Pirate Haven** | — (fences stolen cargo) | Food, Timber, Rope |
| **Fishing Settlement** | Food | Cloth, Timber |

A port can span multiple archetypes (a prosperous colonial capital might produce Rum *and* serve as a Trade Hub).

---

### 2.2 MerchantKnowledge

Merchant captains navigate by **what they know**, not by what is true. Their knowledge of prices and route safety ages, degrades in confidence, and is updated through direct experience and port gossip.

This is the same information delay principle applied to NPC actors.

```csharp
// Ahoy.Simulation/State/MerchantKnowledge.cs

public sealed class MerchantKnowledge
{
    // Last known prices at each port, and when the merchant knew them
    public Dictionary<PortId, PortPriceSnapshot> KnownPrices { get; } = new();

    // Last known safety of each region, and when the merchant assessed it
    public Dictionary<RegionId, RegionSafetySnapshot> KnownSafety { get; } = new();

    // Days since last profitable trade — drives desperation
    public int DaysSinceLastProfitableTrade { get; internal set; }

    // Gold on hand — low funds increase desperation
    public int CurrentGold { get; internal set; }

    public float DespérationLevel =>
        Math.Clamp(DaysSinceLastProfitableTrade / 60f + (CurrentGold < 200 ? 0.3f : 0f), 0f, 1f);
}

public record PortPriceSnapshot(
    Dictionary<TradeGood, int> Prices,
    WorldDate AsOf
);

public record RegionSafetySnapshot(
    float SafetyScore,   // 0.0 (extremely dangerous) to 1.0 (very safe)
    WorldDate AsOf
);
```

`MerchantKnowledge` lives on `Individual` for merchant captains — it belongs to the person, not the ship. A captain retains knowledge if they change vessels.

`Individual` gains: `MerchantKnowledge? MerchantKnowledge` (null for non-merchant individuals).

---

### 2.3 MerchantRoutingScore

An intermediate value type used during routing decisions. Not persisted to world state.

```csharp
// Ahoy.Simulation/Systems/Economy/MerchantRoutingScore.cs

internal record MerchantRoutingScore(
    PortId Destination,
    float EstimatedProfit,
    float SafetyFactor,
    float FamiliarityBonus,
    float PersonalityModifier,
    float Total
);
```

---

## 3. Tick Phases

`EconomySystem.Tick()` runs in five sequential phases:

```
1. Production      — ports generate goods
2. Consumption     — ports consume goods; unmet demand noted
3. Arrivals        — docked ships unload; merchant knowledge updated
4. Departures      — idle merchant ships evaluate routes and load cargo
5. Repricing       — all port prices recalculated from current supply/demand
```

Events are emitted throughout and collected by `IEventEmitter`.

---

### Phase 1 — Production

For each port, each tick:

```csharp
foreach (var good in port.Profile.BaseProduction.Keys)
{
    var produced = port.Profile.EffectiveProduction(good);
    var commodity = port.Inventory[good];
    commodity.Supply += produced;
}
```

Production is scaled by `Prosperity` (0–100):

```
EffectiveProductionRate = BaseRate × (0.5 + Prosperity / 200f)
```

A port at 100 prosperity produces at 100% base rate. A port at 0 produces at 50%. Prosperity changes over time via faction and event systems.

Expired `ProductionModifier`s are removed during this phase.

---

### Phase 2 — Consumption

Ports consume goods each tick. If supply is insufficient, a **shortage** is recorded.

```csharp
foreach (var (good, demand) in port.Profile.BaseConsumption)
{
    var commodity = port.Inventory[good];
    var consumed = Math.Min(demand, commodity.Supply);
    commodity.Supply -= consumed;

    if (consumed < demand)
    {
        var shortage = demand - consumed;
        events.Emit(new CommodityShortage(state.Date, port.Id, good, shortage));
        // Unmet demand drives prosperity decline (handled by EventPropagationSystem)
    }
}
```

---

### Phase 3 — Arrivals

Ships that arrived at a port this tick (flagged by `ShipMovementSystem` via `ShipLocation.AtPort` transition) unload their cargo.

```csharp
foreach (var ship in shipsArrivingThisTick)
{
    var port = state.Ports[((AtPort)ship.Location).Port];

    foreach (var (good, qty) in ship.Cargo)
    {
        port.Inventory[good].Supply += qty;
    }
    ship.Cargo.Clear();

    // Update merchant captain's knowledge of this port's current prices
    if (ship.CaptainId is { } captainId)
    {
        var captain = state.Individuals[captainId];
        if (captain.MerchantKnowledge is { } knowledge)
            UpdatePortKnowledge(knowledge, port, state.Date);
    }
}
```

**Port gossip:** while a merchant is in port, they also absorb safety knowledge from other ships present. `InformationPropagationSystem` handles the knowledge-sharing mechanic between merchants at the same port — `EconomySystem` flags that the merchant is docked and available to receive intel.

---

### Phase 4 — Departures

Idle merchant ships (docked, no current orders) evaluate destinations and load cargo. This is the core of the dynamic routing model.

#### 4a. Destination Scoring

For each candidate destination port, a score is calculated:

```csharp
internal MerchantRoutingScore ScoreDestination(
    Ship ship,
    Individual captain,
    PortId destination,
    WorldState state)
{
    var knowledge  = captain.MerchantKnowledge!;
    var origin     = ((AtPort)ship.Location).Port;
    var destPort   = state.Ports[destination];
    var destRegion = destPort.Region;

    // --- Estimated Profit ---
    // Based on known prices at destination vs. current port supply/prices
    // Knowledge age degrades confidence in the estimate
    var priceSnapshot = knowledge.KnownPrices.GetValueOrDefault(destination);
    var knowledgeAge  = priceSnapshot is null ? int.MaxValue
                      : state.Date.DaysElapsedSince(priceSnapshot.AsOf);
    var confidenceFactor = KnowledgeConfidence(knowledgeAge);  // 1.0 → 0.2 over 60 days

    var estimatedProfit = EstimateProfit(ship, origin, destination, priceSnapshot, state)
                        * confidenceFactor;

    // --- Safety Factor ---
    var safetySnapshot = knowledge.KnownSafety.GetValueOrDefault(destRegion);
    var safetyAge      = safetySnapshot is null ? int.MaxValue
                       : state.Date.DaysElapsedSince(safetySnapshot.AsOf);
    var rawSafety      = safetySnapshot?.SafetyScore ?? 0.5f;  // unknown = assume average
    var agedSafety     = rawSafety * KnowledgeConfidence(safetyAge) + 0.5f * (1 - KnowledgeConfidence(safetyAge));

    // Desperation reduces the weight given to danger
    var desperation  = knowledge.DespérationLevel;
    var safetyFactor = MathF.Pow(agedSafety, 1f - desperation * 0.7f);

    // --- Familiarity Bonus ---
    // Slight preference for ports the merchant has visited before
    var visited      = priceSnapshot is not null;
    var familiarity  = visited ? 1.1f : 1.0f;

    // --- Personality ---
    // Bold captains discount danger further; greedy captains amplify profit weight
    var boldness = captain.Personality.Boldness / 100f;
    var greed    = captain.Personality.Greed    / 100f;
    var personalityMod = (1f + greed * 0.3f) * MathF.Pow(safetyFactor, 1f - boldness * 0.4f);

    var total = estimatedProfit * safetyFactor * familiarity * personalityMod;

    return new MerchantRoutingScore(destination, estimatedProfit, safetyFactor, familiarity, personalityMod, total);
}
```

**Knowledge confidence decay:**

```csharp
private static float KnowledgeConfidence(int ageInDays) =>
    ageInDays switch
    {
        <= 7   => 1.0f,
        <= 30  => 0.8f,
        <= 60  => 0.5f,
        <= 120 => 0.2f,
        _      => 0.1f   // very old knowledge is barely useful
    };
```

#### 4b. Destination Selection

The highest-scoring destination wins, with a small random perturbation to prevent all merchants converging on the same port:

```csharp
var scores  = candidatePorts.Select(p => ScoreDestination(ship, captain, p, state));
var chosen  = scores
    .Select(s => (score: s, jitter: s.Total * (0.9f + _rng.NextSingle() * 0.2f)))
    .MaxBy(x => x.jitter)
    .score;
```

#### 4c. Cargo Loading

Once a destination is chosen, the merchant loads cargo that is likely to be profitable at that destination:

```csharp
// Load goods that are cheap here and (known to be) expensive at destination
var originPort = state.Ports[origin];
var knownDestPrices = knowledge.KnownPrices.GetValueOrDefault(destination)?.Prices
                   ?? new Dictionary<TradeGood, int>();

var candidates = originPort.Inventory
    .Where(kv => kv.Value.Supply > 0)
    .Select(kv => new {
        Good     = kv.Key,
        BuyPrice = kv.Value.Price,
        SellEstimate = knownDestPrices.GetValueOrDefault(kv.Key, kv.Value.BasePrice),
        Margin   = knownDestPrices.GetValueOrDefault(kv.Key, kv.Value.BasePrice) - kv.Value.Price
    })
    .Where(c => c.Margin > 0)
    .OrderByDescending(c => c.Margin);

var remainingCapacity = ship.CargoCapacity;
foreach (var candidate in candidates)
{
    var qty = Math.Min(originPort.Inventory[candidate.Good].Supply, remainingCapacity);
    if (qty <= 0) continue;

    ship.Cargo[candidate.Good] = qty;
    originPort.Inventory[candidate.Good].Supply -= qty;
    remainingCapacity -= qty;

    if (remainingCapacity <= 0) break;
}

// Set the ship's destination
ship.Location = new EnRoute(origin, destination, EstimateTravelDays(origin, destination, state));
knowledge.DaysSinceLastProfitableTrade = 0;
```

If no profitable cargo exists at origin, the merchant still departs toward the highest-scoring destination (hoping conditions have changed) — desperation overrides perfect rationality.

---

### Phase 5 — Repricing

After all production, consumption, arrivals, and departures, prices are recalculated for every commodity at every port:

```csharp
foreach (var port in state.Ports.Values)
foreach (var commodity in port.Inventory.Values)
{
    var oldPrice = commodity.Price;
    var ratio    = commodity.Demand > 0
                 ? (float)commodity.Supply / commodity.Demand
                 : commodity.Supply > 0 ? 2f : 1f;

    var rawPrice = (int)(commodity.BasePrice / ratio);
    commodity.Price = Math.Clamp(rawPrice,
        (int)(commodity.BasePrice * 0.2f),   // floor: 20% of base
        (int)(commodity.BasePrice * 5.0f));  // ceiling: 500% of base

    if (Math.Abs(commodity.Price - oldPrice) > commodity.BasePrice * 0.1f)
    {
        events.Emit(new CommodityPriceChanged(
            state.Date, port.Id, commodity.Good, oldPrice, commodity.Price));
    }
}
```

Price events are only emitted when the change exceeds 10% of base price — avoids event spam from minor fluctuations.

---

## 4. Merchant Safety Ratings

Region safety scores used in merchant routing are maintained separately and fed into `MerchantKnowledge` via `InformationPropagationSystem`. They are a derived property of world state — not authored directly.

Safety score per region is computed from:

| Factor | Effect |
|---|---|
| Active pirate activity (recent raids) | Reduces safety |
| Faction naval patrol strength in region | Increases safety |
| Active war between factions in region | Reduces safety |
| Time since last incident | Gradually restores safety |

```csharp
// Computed by InformationPropagationSystem, not EconomySystem
public record RegionSafetyRating(RegionId Region, float Score, WorldDate ComputedAt);
```

When a merchant departs a port, `InformationPropagationSystem` supplies updated safety ratings for regions the merchant is aware of. `EconomySystem` consumes these during departure scoring.

---

## 5. LOD Considerations

| LOD Tier | EconomySystem Behaviour |
|---|---|
| **Local** (player's region) | Full simulation — all phases run, all merchant ships active |
| **Regional** (1 region away) | Full simulation — player may encounter these merchants at sea |
| **Distant** (2+ regions away) | Simplified — production/consumption run; merchant ships are advanced in bulk; individual routing skipped; prices updated from summary flow rates |

For distant regions, merchant ship movement is approximated: a ship on a multi-day voyage in a distant region is advanced by its remaining days without individual routing recalculation. This preserves economic coherence (goods still arrive at distant ports) without the per-ship routing cost.

---

## 6. System Interactions

| System | Interaction |
|---|---|
| `ShipMovementSystem` | Runs before `EconomySystem`; sets `ShipLocation` transitions that Phase 3 (Arrivals) reads |
| `EventPropagationSystem` | Consumes `CommodityShortage` events to drive prosperity decline at ports |
| `InformationPropagationSystem` | Supplies merchant captains with updated safety ratings; handles port gossip (knowledge sharing between docked merchants) |
| `FactionSystem` | Updates `NavalStrength` per region, which feeds into safety ratings consumed by merchants |
| `WeatherSystem` | Emits events that trigger `ProductionModifier`s on affected ports (e.g. hurricane halves production for 30 ticks) |

---

## 7. Open Questions

- [ ] Should a desperate merchant ever take on illegal/pirate cargo at a pirate haven? Interesting gameplay hook — deferred.
- [ ] Price floors/ceilings — are the 20%/500% bounds right, or should they vary by good type (e.g. Cannon should have a higher floor due to scarcity)?
- [ ] Knowledge sharing at port — how much do merchants share with the player? Should the player be able to buy intel from docked merchants, or does it flow passively?
- [ ] How many merchant ships exist at world start — is this a fixed world-creation parameter, or does it grow/shrink with faction prosperity over time?

---

## 8. Post-v0.1 Additions

### Group 5A — Knowledge-Driven Merchant Routing (implemented)

Merchant routing now unions `IndividualHolder` + `ShipHolder` facts and scores
ports by expected trade margin (cargo → high sell prices, empty → low buy prices).
`ShouldDepartPort` checks both holders. No system reads ground truth for NPC
routing decisions. See `SDD-WeatherAndMovement` for routing details.

### Group 7 — Epidemic Propagation (implemented)

EconomySystem now ticks epidemic state:
- 0.2% spawn chance per port per tick → sets `PortConditionFlags.Plague`
- Docked ships gain `HasInfectedCrew` at 10%/tick
- Infected ships spread epidemic to clean ports on dock (5%)
- Epidemic ports: prosperity decays 2×, production halts
- Natural clearing after 30 ticks, or immediately on Medicine delivery
- `MerchantKnowledge` replaced by `KnowledgeStore` queries — merchants avoid
  known epidemic/blockaded ports via `PortConditionClaim` routing (5A)

---

*Next step: FactionSystem — goals, relationships, and how factions act on the world each tick.*
