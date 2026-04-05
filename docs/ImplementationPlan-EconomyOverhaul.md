# Implementation Plan — Economy Overhaul (Group 8)

Date: 2026-04-05

## Motivation

The current economy is a flat production/consumption loop with no population
pressure, linear pricing, and routing that ignores travel time. This creates
three failure modes:

1. **Death spirals** — low-prosperity ports can't attract merchants, requiring
   an artificial mean-reversion band-aid `((50 - prosperity) * 0.005)`
2. **No famine cascade** — food depletion only raises prices slightly; there's
   no starvation, no population decline, no crisis generation
3. **Passive merchants** — routing scores by raw margin, not profit-per-day,
   so merchants ignore distant crises even when rewards are astronomical

This plan adds explicit population, non-linear pricing for essential goods,
faction relief subsidies, and profit-per-day routing. The goal: remove the
mean-reversion band-aid entirely by making the "invisible hand" actually work.

---

## Phase 1 — Population & Demographic Engine

### 1A: Port Population Field

**File:** `src/Ahoy.Simulation/State/Port.cs`

```csharp
/// <summary>
/// Resident population. Drives consumption (demand), production capacity,
/// and tax revenue. Range: 100 (abandoned outpost) to 15,000 (major capital).
/// </summary>
public int Population { get; private set; }

public void AdjustPopulation(int delta)
{
    Population = Math.Max(100, Population + delta); // floor at 100 — never fully empty
}
```

**World data seeding** — `CaribbeanWorldDefinition` assigns population by port type:

| Port Type | Population Range | Examples |
|---|---|---|
| Capital | 8,000–15,000 | Havana, Port Royal, Santo Domingo |
| Major port | 3,000–7,000 | Cartagena, Nassau, Bridgetown |
| Minor port | 1,000–2,500 | Montserrat, Curaçao |
| Outpost | 300–800 | Pirate havens, remote settlements |

### 1B: TargetSupply — Dynamic Consumption

**File:** `src/Ahoy.Simulation/State/EconomicProfile.cs`

Replace flat `BaseConsumption` with population-driven calculation:

```csharp
/// <summary>
/// How much of each good this port needs per tick to sustain its population.
/// Food: 1 unit per 100 population. Medicine: 1 unit per 500 population.
/// Other goods: based on BaseConsumption rates scaled by population/1000.
/// </summary>
public Dictionary<TradeGood, int> TargetSupply { get; } = new();
```

**Tick calculation** in EconomySystem:

```csharp
// Essential goods — fixed ratio to population
targetSupply[Food] = population / 100;
targetSupply[Medicine] = population / 500;

// Production goods — scaled by population and prosperity
foreach (var (good, baseRate) in BaseConsumption)
    if (good is not Food and not Medicine)
        targetSupply[good] = (int)(baseRate * (population / 1000f) * prosperity);
```

### 1C: Survival Tick — The Famine Cascade

**File:** `src/Ahoy.Simulation/Systems/EconomySystem.cs`

New method `TickSurvival(Port)` runs before `ProduceAndConsume`:

```
1. Calculate foodNeeded = population / 100
2. If supply[Food] >= foodNeeded:
   - Consume food
   - Organic growth: population += population * 0.001 (0.1% per day, ~44% per year)
   - Cap at port's max capacity (based on original seeded population × 1.5)
3. If supply[Food] < foodNeeded:
   - Consume all remaining food
   - starvationRatio = 1 - (foodAvailable / foodNeeded)
   - Population loss: population -= population * 0.05 * starvationRatio
   - Floor at 100 (port never fully depopulates)
   - Prosperity drops: prosperity -= 5 * starvationRatio
   - Emit PortStarvation event (triggers governor crisis contracts)
```

### 1D: Production Scaling

Replace flat `BaseProduction` with population-scaled output:

```
actualProduction = baseProductionRate * (population / 1000f) * (prosperity / 100f)
```

A starving port with 2,000 population and 20% prosperity produces at
`baseRate * 2.0 * 0.2 = 40%` of full capacity. A thriving capital with
12,000 population and 80% prosperity produces at `baseRate * 12.0 * 0.8 = 960%`.

This creates the cascade: starvation → population drops → production drops →
less goods to trade → less merchant traffic → deeper starvation.

---

## Phase 2 — Non-Linear Pricing (Inelastic Essentials)

### 2A: Essential Good Classification

**File:** `src/Ahoy.Core/Enums/TradeGood.cs` or `EconomicProfile.cs`

```csharp
public static bool IsEssential(TradeGood good) => good is TradeGood.Food or TradeGood.Medicine or TradeGood.Water;
```

### 2B: Pricing Formula Upgrade

**File:** `src/Ahoy.Simulation/State/EconomicProfile.cs`

Replace the linear `EffectivePrice` with exponential scaling for essentials:

```csharp
public int EffectivePrice(TradeGood good)
{
    if (!BasePrice.TryGetValue(good, out var basePrice)) return 0;

    var supply = Supply.GetValueOrDefault(good, 0);
    var target = TargetSupply.GetValueOrDefault(good, 1);
    var ratio = (float)target / Math.Max(supply, 1);

    // Essentials scale exponentially when scarce — people pay everything they have
    var exponent = IsEssential(good) && ratio > 1.0f ? 2.0f : 1.0f;
    var multiplier = MathF.Pow(ratio, exponent);

    // Apply modifiers
    foreach (var mod in ActiveModifiers)
        if (mod.Good == good) multiplier *= mod.Multiplier;

    // Wider price band for essentials: 10%–5000% (vs 20%–500% for luxuries)
    var minMult = IsEssential(good) ? 0.10f : 0.20f;
    var maxMult = IsEssential(good) ? 50.0f : 5.0f;
    return (int)Math.Clamp(basePrice * multiplier, basePrice * minMult, basePrice * maxMult);
}
```

**Result:** A port at half its food target pays 4× base price. At quarter
target, 16×. At 10% target, 100×. This makes relief runs mathematically
irresistible to profit-seeking merchants.

---

## Phase 3 — Faction Relief Subsidies

### 3A: Treasury Deadlock Detection

**File:** `src/Ahoy.Simulation/Systems/IndividualLifecycleSystem.cs`

When a governor detects starvation (food supply < 50% of target), check if the
port can actually afford to buy food at current prices. If not, escalate to
the parent faction:

```
if port cannot afford food at effective prices:
    faction.TreasuryGold -= reliefBudget
    seed ContractClaim(GoodsDelivered, Food, port) with faction gold as reward
```

The faction treasury — not the port's local economy — backs the contract.
Merchants know the payout is guaranteed by the Crown.

### 3B: PortStarvation Event

**File:** `src/Ahoy.Simulation/Events/WorldEvent.cs`

```csharp
public record PortStarvation(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, int PopulationLoss, float StarvationRatio) : WorldEvent(Date, SourceLod);
```

This event:
- Triggers governor crisis contract seeding (existing Phase 3 from Group 7)
- Propagates as `PortConditionClaim(Famine)` via KnowledgeSystem
- Generates `IndividualActionClaim` against the governor if population loss
  exceeds 10% (political consequence — "the governor let the port starve")

---

## Phase 4 — Profit-Per-Day Routing

### 4A: Travel Time in Routing Score

**File:** `src/Ahoy.Simulation/Systems/ShipMovementSystem.cs`

Update `ScoreMerchantDestination` to divide expected profit by estimated
travel time:

```csharp
// Current: score = price × confidence × cargoQty
// New:     score = (price × confidence × cargoQty) / estimatedTravelDays

var portRegion = state.Ports.TryGetValue(claim.Port, out var p) ? p.RegionId : currentRegion;
var travelDays = Math.Max(1f, GetTravelDays(currentRegion, portRegion, state));
var score = (claim.Price * fact.Confidence * ship.Cargo[claim.Good]) / travelDays;
```

**Result:** A port offering 16× food prices 3 days away massively outscores
a port offering 2× sugar prices 1 day away. Relief runs become the rational
choice.

### 4B: Pass Current Region to Scoring

`ScoreMerchantDestination` currently doesn't know where the ship is. Add
`RegionId currentRegion` parameter and thread it through from `AssignNpcRoute`.

---

## Phase 5 — Remove the Band-Aid

Once Phases 1–4 are implemented and deep-time tests pass:

1. Remove `prosperityDelta += (50f - port.Prosperity) * 0.005f;`
2. Run the 5-year deep-time test
3. Verify: ports that starve trigger famine cascades, attract merchants via
   high prices, and recover organically without mean-reversion
4. Verify: the economy doesn't permanently collapse (at least some ports
   should maintain >20% prosperity across 1825 ticks)

If the economy still collapses, the issue is in merchant gold supply
(merchants need starting capital to buy food) or in the faction subsidy
pipeline (factions need to spend treasury on relief).

---

## Sequencing

```
Phase 1 — Population & Demographics (Port.Population, TargetSupply,
           famine cascade, production scaling)
  ↓
Phase 2 — Non-Linear Pricing (essential good classification,
           exponential pricing for scarce essentials)
  ↓
Phase 3 — Faction Relief (treasury deadlock detection, PortStarvation
           event, faction-backed contracts)
  ↓
Phase 4 — Profit-Per-Day Routing (travel time in scoring, current
           region threading)
  ↓
Phase 5 — Remove mean-reversion band-aid + deep-time validation
```

Each phase is independently testable. Phase 1 alone should reduce the
severity of death spirals. Phase 2 should make relief runs profitable.
Phase 3 ensures merchants can actually get paid. Phase 4 ensures they
choose the most urgent destination.

---

## Key Design Decisions

1. **Population floor at 100.** A port never fully depopulates — it becomes
   an abandoned outpost that can be resettled. This prevents permanent map
   holes and maintains the possibility of recovery.

2. **Growth cap at 1.5× initial population.** Prevents runaway population
   growth in prosperous ports. A capital seeded at 10,000 caps at 15,000.

3. **Essential goods exponent = 2.0.** Food at half supply = 4× price.
   Quarter supply = 16×. This is aggressive enough to redirect merchant
   traffic but not so extreme that a single shortage bankrupts the economy.

4. **Faction relief uses faction treasury, not port treasury.** This breaks
   the treasury deadlock: a broke port can't buy food, but the Crown can.
   The cost comes from the central faction treasury, which is replenished
   by port taxes across all controlled ports.

5. **Prosperity becomes an efficiency multiplier, not a survival proxy.**
   Population handles survival (people live/die). Prosperity handles
   economic efficiency (production rates, trade volume). They're correlated
   but not identical: a recently starved port can have low population but
   recovering prosperity as food arrives.

6. **PortStarvation event drives political consequences.** A governor who
   lets their port starve gets an `IndividualActionClaim` deed against them.
   Other NPCs' relationship with the governor worsens. If it crosses the
   nemesis threshold, someone seeds a bounty on the governor. This is the
   systemic narrative engine feeding on economic failure.

---

## New Tests Needed

**Tier 1 — Petri Dish:**
- Famine Cascade: Remove all food from a 5,000-population port. Run 30 ticks.
  Assert population declined, PortStarvation event emitted, governor seeded
  relief contract.
- Relief Run: Starving port with 16× food prices. Merchant with food cargo
  3 days away. Assert merchant routes there (profit-per-day beats closer
  alternatives).
- Faction Subsidy: Port treasury at 0, faction treasury at 5,000. Assert
  faction-backed contract seeded when port can't afford food.

**Tier 2 — Deep-Time:**
- Run 5 years WITHOUT mean-reversion. Assert average prosperity > 15%
  and at least 80% of ports have population > 500.

**Tier 4 — Chaos Monkey:**
- Global famine (set all food to 0). Assert world recovers within 200 ticks
  via the famine cascade → high prices → merchant relief → population recovery
  loop. This is the acid test for removing the band-aid.
