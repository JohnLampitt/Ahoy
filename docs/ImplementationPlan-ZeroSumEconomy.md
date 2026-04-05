# Implementation Plan — Zero-Sum Colonial Economy (Group 10)

Date: 2026-04-05

## Motivation

The current economy has three gold printers (faction income from prosperity,
trade revenue from thin air, captain's double-count) and insufficient sinks.
Total gold grows ~4,000-13,000/tick net. Simply switching to zero-sum trade
would cause deflationary collapse — crew upkeep and naval maintenance destroy
gold every tick with no replacement.

The fix requires three layers: ground all trade in physical Port.Treasury,
introduce a controlled "Export Mint" to replace destroyed gold, and add
consequence-driven Royal Stipends to prevent bailout abuse.

---

## Phase 1 — Grounding the Economy (Zero-Sum Trade)

### 1A: Port.Treasury

**File:** `src/Ahoy.Simulation/State/Port.cs`

```csharp
/// <summary>Local gold balance. Merchants are paid from this. Faction taxes drain it.</summary>
public int Treasury { get; set; }
```

Seeded at world init proportional to population × prosperity.

### 1B: Trade is a Transfer

**File:** `src/Ahoy.Simulation/Systems/EconomySystem.cs`

When a merchant sells goods to a port:
```
int cost = price * sellQty;
if (port.Treasury < cost)
    cost = port.Treasury;   // port can only pay what it has
    sellQty = cost / price; // partial sale
if (cost <= 0) return;      // port is broke — merchant refuses

port.Treasury -= cost;
ship.GoldOnBoard += cost;
```

When a merchant buys goods from a port:
```
ship.GoldOnBoard -= cost;
port.Treasury += cost;      // port receives payment for its goods
```

**Result:** Every trade is a gold transfer. No gold created or destroyed.

### 1C: Captain's Cut is Redistribution

```csharp
var grossRevenue = price * sellQty;
var captainCut = (int)(grossRevenue * CaptainIncomeFraction);
ship.GoldOnBoard += grossRevenue - captainCut;  // ship gets 90%
captain.CurrentGold += captainCut;               // captain gets 10%
// Total: grossRevenue. No double-count.
```

### 1D: Faction Taxation Replaces Income Printing

**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`

Remove: `faction.TreasuryGold += IncomePerTick` (the prosperity printer).

Replace with physical taxation:
```
foreach port in faction.ControlledPorts:
    taxRate = 0.10f  // 10% of port treasury per tick
    tax = (int)(port.Treasury * taxRate)
    port.Treasury -= tax
    faction.TreasuryGold += tax
```

Faction income is now directly proportional to how wealthy their ports are.
A starving, broke port contributes nothing. A thriving trade hub funds the
empire.

### 1E: Pirate Income from Physical Sources

Remove: `RaidingMomentum * 5 + HavenPresence * 0.8` printer.

Replace with:
- Tax their haven ports' treasuries (same as colonial factions)
- Captain's cut from raid events (`ShipRaided` gold already transfers
  from victim to attacker — just ensure the faction gets its share)

### 1F: Player Trade Fix

**File:** `src/Ahoy.Simulation/Engine/SimulationEngine.cs`

Player sells goods: `port.Treasury -= revenue; player.PersonalGold += revenue`
Player buys goods: `player.PersonalGold -= cost; port.Treasury += cost`

---

## Phase 2 — The Hub-and-Spoke Export Model (Controlled Mint)

### 2A: Export Flag

**File:** `src/Ahoy.Simulation/State/EconomicProfile.cs`

```csharp
/// <summary>True for faction capitals and major trade hubs. These ports can
/// export high-value goods to Europe, converting them into fresh gold.</summary>
public bool CanExportToEurope { get; set; }
```

Set to true only for 4-5 ports: Havana, Port Royal, Willemstad, Bridgetown,
Veracruz. Minor ports cannot export — they sell to merchants who carry goods
to the hubs.

### 2B: The Export Mint

**File:** `src/Ahoy.Simulation/Systems/EconomySystem.cs`

New method `TickExports(Port)` runs after production/consumption:

```
if (!port.Economy.CanExportToEurope) return;

foreach exportableGood in [Gold, Silver, Sugar, Tobacco, Indigo, Rum]:
    surplus = supply[good] - targetSupply[good]
    if surplus <= 0: continue

    exportQty = min(surplus, maxExportPerTick)
    exportRevenue = exportQty * worldPrice[good]  // fixed European price

    supply[good] -= exportQty    // goods leave the simulation
    port.Treasury += exportRevenue // fresh gold enters from Europe
```

**This is the only gold faucet in the entire economy.** Gold enters the
Caribbean exclusively through export hubs selling goods to Europe. The rate
is controlled by:
- How many goods reach the hub (depends on merchant traffic)
- The hub's surplus (consumed goods can't be exported)
- A per-tick export cap (prevents infinite minting)

### 2C: The Emergent Treasure Fleet

Because minor ports produce raw goods (silver, sugar) but can't export them,
supply piles up and local prices crash. Merchants see the arbitrage: buy
cheap silver at Veracruz, sell at Havana where it's exported for European
gold. This organically creates the treasure fleet shipping lanes — highly
predictable, lucrative routes that pirates can hunt.

---

## Phase 3 — Sovereign Debt & Consequences

### 3A: Royal Stipend with Cooldown

**File:** `src/Ahoy.Simulation/State/Faction.cs`

```csharp
/// <summary>Accumulated debt to the Crown. Garnishes future export revenue.</summary>
public int RoyalDebt { get; set; }

/// <summary>Ticks until next stipend request is allowed. Simulates Atlantic crossing.</summary>
public int StipendCooldownTicks { get; set; }
```

When a faction's treasury hits 0 and it has critical obligations (war,
relief, naval maintenance):

```
if StipendCooldownTicks > 0: cannot request — must wait
else:
    stipendAmount = max(2000, min(10000, estimatedNeed))
    faction.TreasuryGold += stipendAmount
    faction.RoyalDebt += (int)(stipendAmount * 1.2f)  // 20% interest
    faction.StipendCooldownTicks = 100  // ~3 months Atlantic delay
```

### 3B: Debt Garnishment

During `TickExports`, when fresh gold enters the hub port's treasury and
the faction taxes it:

```
if faction.RoyalDebt > 0:
    garnishment = (int)(taxRevenue * 0.50f)  // Crown takes 50%
    faction.TreasuryGold -= garnishment
    faction.RoyalDebt -= garnishment
```

Heavy debt cripples future economic growth — the faction is paying back
the Crown instead of funding military operations.

### 3C: Viceroy Recall (Debt Crisis)

When `RoyalDebt > 50,000`:

1. Emit `ViceroyRecalled` event
2. Debt resets to 0 (Crown writes it off — fresh start)
3. Current viceroy: `Role = DisgracedOfficial`, removed from governor post
4. New viceroy spawned (Dynamic Promotion pattern)
5. Disgraced viceroy stays in simulation with nemesis relationship toward
   whoever caused the bankrupting crisis (blockading player, rival faction)

---

## Sequencing

```
Phase 1 — Zero-sum trade (Port.Treasury, transfer-based trade,
           captain's cut fix, faction taxation, pirate income fix)
  ↓
Phase 2 — Export Mint (CanExportToEurope, hub export tick,
           world price table)
  ↓
Phase 3 — Sovereign debt (Royal Stipend, garnishment, Viceroy Recall)
```

Phase 1 is the critical fix — eliminates all gold printing. Phase 2
prevents deflationary collapse by providing a controlled faucet. Phase 3
adds political consequences to economic failure.

---

## Key Design Decisions

1. **One faucet, many sinks.** Gold enters the Caribbean only through export
   hubs. It leaves through crew upkeep, naval maintenance, investigations,
   bribes, agent replacement, and unresolved contracts. The balance between
   faucet rate and sink rate determines inflation/deflation.

2. **Port.Treasury creates real scarcity.** A broke port can't buy food even
   if merchants are willing to sell. This makes port wealth a genuine
   strategic resource and creates the famine→defection cascade naturally.

3. **Hub-and-spoke creates treasure fleets.** Raw goods flow from periphery
   to capitals; gold flows back. The shipping lanes are predictable, creating
   piracy gameplay. Blockading a capital cuts off the gold supply for the
   entire empire.

4. **Sovereign debt has teeth.** Stipends prevent immediate collapse but
   garnish 50% of future export revenue. Factions that rely on bailouts
   slowly suffocate. The Viceroy Recall is the ultimate consequence — a
   named character is ruined and becomes a narrative actor.

5. **Export prices are fixed (European market).** The Caribbean doesn't
   set world prices — Europe does. This is historically accurate and
   provides a stable anchor for the faucet rate.

6. **InjectExternalFood becomes a ReliefMission.** The background food import
   placeholder is removed entirely. Food enters the Caribbean as cargo on
   faction ships (ReliefMissions) or merchant ships, paid for with real gold.

---

## Tuning Parameters

| Parameter | Initial Value | Effect |
|---|---|---|
| Port tax rate | 10%/tick | Higher = richer factions, poorer ports |
| Export cap per good per tick | 20 units | Controls faucet rate |
| European prices | Gold=800, Silver=400, Sugar=60, etc. | Controls gold injection per export |
| Stipend cooldown | 100 ticks | Longer = fewer bailouts |
| Stipend interest | 20% | Higher = more punishing debt |
| Garnishment rate | 50% of tax revenue | Higher = slower debt paydown |
| Viceroy recall threshold | 50,000g debt | Lower = more frequent political crises |

---

## Tests Needed

**Tier 1 — Petri Dish:**
- Zero-sum trade: merchant sells food, assert port.Treasury decreased by
  exactly the gold the ship received
- Broke port: port.Treasury = 0, merchant docks with food, assert trade
  doesn't happen (no gold created)
- Export mint: hub port with surplus sugar, run ticks, assert port.Treasury
  increased and sugar supply decreased
- Faction taxation: faction taxes wealthy port, assert port.Treasury decreased
  and faction.TreasuryGold increased by same amount

**Tier 2 — Deep-Time:**
- Run 5 years, assert total gold in economy stays within 0.5×-2× of starting
  gold (no hyperinflation or total deflation)
- Assert at least one export hub has positive treasury throughout

**Tier 4 — Chaos Monkey:**
- Zero all port treasuries. Assert economy recovers via export mint within
  200 ticks (hubs produce goods, export them, generate fresh gold, merchants
  redistribute)
