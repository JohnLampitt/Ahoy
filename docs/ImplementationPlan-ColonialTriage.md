# Implementation Plan — Faction Missions & Colonial Triage (Group 9)

Date: 2026-04-05 (revised — incorporated competing review feedback)

## Motivation

Two structural gaps need filling:

**1. Factions can't command their ships.** NPC ships route via merchant
knowledge or bounty pursuit. There's no concept of a faction issuing orders
to a ship it owns — "carry this message to Havana," "deliver food to Port
Royal," "reinforce the garrison at Cartagena." This means faction relief is
magically injected, military responses can't be dispatched, and diplomatic
communications are instant.

**2. Factions fund relief naively.** Every starving port gets up to 50% of
the faction treasury. No triage, no strategic evaluation, no consequences
when relief is denied.

---

## Phase 1 — Faction Mission Model & ExecuteOrdersGoal

### Mission Types

**File:** `src/Ahoy.Simulation/State/Ship.cs`

```csharp
public abstract record FactionMission(PortId Destination);
public record CourierMission(PortId Destination, KnowledgeFactId CarriedFact) : FactionMission(Destination);
public record ReliefMission(PortId Destination, TradeGood Cargo, int Quantity) : FactionMission(Destination);
public record ReinforceMission(PortId Destination) : FactionMission(Destination);
public record PatrolMission(PortId Destination, RegionId PatrolRegion) : FactionMission(Destination);

// On Ship:
public FactionMission? Mission { get; set; }
```

### Captain Agency — ExecuteOrdersGoal

Missions don't bypass the captain. The faction assigns the mission to the
ship AND pushes an `ExecuteOrdersGoal` into the captain's `NpcPursuits`:

**File:** `src/Ahoy.Simulation/State/GoalModels.cs`

```csharp
public record ExecuteOrdersGoal(
    Guid Id, IndividualId NpcId,
    FactionMission Mission) : NpcGoal(Id, NpcId);
```

**EpistemicResolver behaviour for ExecuteOrdersGoal:**
- Captain validates the route against their knowledge (RouteHazardClaim,
  enemy territory, known blockades).
- If route is clear: set `PortRoute(mission.Destination)`, proceed.
- If high-confidence hazard in the way: captain may stall (cautious captains)
  or proceed anyway (bold captains) — personality-driven.
- **Mutiny:** If the captain's relationship with the viceroy drops to < -75
  (Nemesis), they abandon the `ExecuteOrdersGoal`, refuse the mission, and
  become a free agent. This is systemic mutiny — the captain deserts with
  the ship and its cargo.

### Headless Ships (No Captain)

Faction ships without a captain (`CaptainId == null`) execute missions
mechanically — direct `PortRoute` with no knowledge validation or mutiny
check. They're unnamed vessels following standing orders. See Phase 6 for
how headless ships earn names through remarkable actions.

### Mission Assignment

**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`

`AssignMissions(Faction, WorldState)` called during the faction tick:
1. Identify idle faction ships (no mission, docked at controlled port).
2. Evaluate pending mission requests (courier queue, relief needs, patrols).
3. Assign highest-priority mission to nearest idle ship.
4. For captained ships: push `ExecuteOrdersGoal` into captain's NpcPursuits.
5. Load cargo for ReliefMissions from origin port's supply (see Phase 3C).

### Mission Resolution on Arrival

When a mission ship docks at its destination:
- **CourierMission:** Inject `CarriedFact` into port's PortHolder + governor's
  IndividualHolder. Clear mission.
- **ReliefMission:** Transfer cargo from ship to `port.Economy.Supply`. Deliver
  whatever remains (cargo may have been raided en route). Clear mission.
- **ReinforceMission:** Ship stays docked. Guns contribute to port defense
  rating. Mission stays active until FactionSystem reassigns.
- **PatrolMission:** Ship departs toward PatrolRegion, loiters intercepting
  enemies.

---

## Phase 2 — Replace External Food Imports

With faction missions, `InjectExternalFood` becomes partially replaced:

1. **FactionSystem food supply tick:** Each tick, evaluate controlled ports'
   food levels. Ports below 50% TargetSupply[Food] get a `ReliefMission`
   dispatched from the nearest food-surplus faction port.
2. **Physical loading:** Origin port's food supply is deducted. Ship carries
   real cargo. Can be intercepted.
3. **Delivery:** On arrival, food added to destination's supply.

`InjectExternalFood` remains at reduced rate as background off-map European
trade (not faction-directed). Faction relief missions supplement it.

---

## Phase 3 — Courier Plea/Response & SVS Triage

### 3A: Information Flow

1. **Governor detects famine.** Food < 50% of target for 5+ ticks.
2. **Governor dispatches plea courier.** `CourierMission` carrying a
   `ReliefRequestFact` (port ID, severity, gold needed, **CrisisId**).
3. **Courier travels to capital.** Physical ship, physical travel time.
4. **Viceroy receives plea.** Fact enters viceroy's IndividualHolder.
5. **SVS triage** (Phase 3B). Approve or deny.
6. **Response dispatched by courier.** Approval: `ReliefMission` + courier
   with approval fact. Denial: courier with `FactionReliefDenied` fact.

**Gossip parallel path:** Famine gossip propagates via merchant traffic.
Proactive viceroys (high Loyalty) may dispatch relief on rumour alone.

### 3B: Double-Spend Prevention (CrisisId)

Each `PortStarvation` event generates a unique `Guid CrisisId`. This ID
is attached to the `ReliefRequestFact` and the dispatched mission. The
viceroy ignores new requests matching an active CrisisId — prevents
funding the same famine twice (once from rumour, once from courier).

### 3C: Dual SVS — Economic vs Military

```
Economic SVS = (Population / 1000) × M_relationship × M_threat
Military SVS = (Population / 1000) × M_relationship  (ignores threat)
```

- **Economic SVS** caps gold spent on ReliefMissions. A blockaded port has
  low economic SVS (threat = 0.2) — sending food ships into a blockade is
  wasteful.
- **Military SVS** determines whether to dispatch ReinforceMission to break
  the blockade. A strategically important blockaded port gets warships, not
  food ships.

### 3D: Physical Cargo Sourcing (Conservation of Mass)

Factions do not magically spawn food. To dispatch a ReliefMission, the
viceroy must physically purchase food using `TreasuryGold` from the
capital's `PortEconomy.Supply`:

```csharp
var capitalFood = capitalPort.Economy.Supply.GetValueOrDefault(TradeGood.Food);
var canSource = Math.Min(capitalFood, requestedQuantity);
if (canSource < minimumViable) return; // capital can't spare the food

capitalPort.Economy.Supply[TradeGood.Food] -= canSource;
faction.TreasuryGold -= canSource * capitalPort.Economy.EffectivePrice(TradeGood.Food);
// Load onto relief ship
```

If the capital itself is starving, it physically cannot dispatch relief.
This creates empire-wide cascading collapse: capital starves → can't feed
colonies → colonies defect → empire shrinks → fewer tax ports → capital
starves harder.

---

## Phase 4 — Defection Cascade

### Trigger

Governor's IndividualHolder receives `FactionReliefDenied` fact (delivered
by courier). Consequence math fires:

1. `IndividualActionClaim(Viceroy, Governor, null, Hostile, Severe,
   "Abandoned colony to starvation")` — relationship hits nemesis.
2. **EvaluateDefection:** relationship < -75 AND starving AND no relief →
   governor flips port to Independent (or pirate if regional presence).
3. Emit `PortDefected` event.

### The Broadcast

Governor seeds `ContractClaim(GoodsDelivered, Food)` through all knowledge
pools — "buying from anyone, even pirates."

### Pirate Takeover — No Auto-Flip

Pirates do **not** automatically inherit independent ports. Instead:

1. Pirate AI detects the independent port (via PortControlClaim gossip).
2. Pirate faction seeds `ContractClaim(TargetDead, Governor)` — a bounty
   on the independent governor.
3. A pirate captain (NPC or player) sails to the port and assassinates the
   governor via stat-check.
4. **On success:** port flips to pirate control. `PortCaptured` event.
5. **Player opportunity:** the player can intercept the assassination contract
   and defend the independent governor — protecting the fragile republic.

This is dramatically richer than an auto-flip timer. The pirate takeover is
a visible, interceptable act that creates genuine player decision points.

---

## Phase 5 — Faction Response to Defection

When `PortDefected` arrives at the capital (via gossip or courier):

**Rule-based default:**
```
if AtWarWith.Count == 0 AND TreasuryGold > recaptureCost:
    adopt RecapturePort goal, dispatch ReinforceMission
    seed ContractClaim(TargetDead) on defecting governor
else:
    write off the port
```

**LLM override:** Viceroy enters Pondering state. LLM receives traits,
treasury, wars, SVS. Returns: RecapturePort, AssassinateGovernor, or
IgnoreDefection. Rule-based default fires immediately; LLM overrides
next tick.

**New types:**
```csharp
public record RecapturePort(PortId TargetPort) : FactionGoal;
public record RecapturePortGoal(Guid Id, IndividualId NpcId, PortId TargetPort) : NpcGoal(Id, NpcId);
```

---

## Phase 6 — Dynamic Promotion (The Nemesis Engine)

Headless faction ships (no captain) that perform remarkable actions
organically generate named characters.

### Promotion Triggers

ShipMovementSystem and combat resolution listen for extreme outcomes
involving headless ships:

- A headless ReliefMission ship docks at a port with 0 food, breaking a
  critical famine.
- A headless CourierMission ship is attacked by the player or pirates but
  escapes with the message.
- A headless patrol ship successfully intercepts and damages an enemy vessel.

### Execution

When triggered:
1. Create a new `Individual` with role `NavalOfficer`, random personality,
   random name.
2. Assign as `Ship.CaptainId` — the ship is now captained.
3. **Relational injection:** Seed an `IndividualActionClaim` into the new
   captain's knowledge based on the triggering event:
   - Escaped the player → relationship with player starts at -100 (Nemesis).
     "Captain Rodriguez has a score to settle."
   - Broke a famine → relationship with local governor starts at +100
     (Blood Brother). "Captain Martinez saved Port Royal."
   - Intercepted an enemy → relationship with enemy faction captain negative.

### The Narrative Result

The player attacks a nameless Spanish mail ship. It escapes. Two weeks later,
**Captain Alejandro Rodriguez** appears — a named NavalOfficer with a personal
vendetta, born entirely from systemic mechanics. He's hunting the player across
the Caribbean, and the player's knowledge system tells them why: "Rodriguez
was the courier you tried to sink near Havana."

This replaces hand-crafted rival captains with emergent ones. The player's
own actions create their nemeses.

---

## Sequencing

```
Phase 1 — FactionMission model + ExecuteOrdersGoal + captain agency/mutiny
  ↓
Phase 2 — Replace InjectExternalFood with physical ReliefMission ships
  ↓
Phase 3 — Courier plea/response + CrisisId + dual SVS + physical sourcing
  ↓
Phase 4 — Defection cascade + pirate assassination (no auto-flip)
  ↓
Phase 5 — Faction response (RecapturePort + LLM override)
  ↓
Phase 6 — Dynamic Promotion (headless ships → named nemeses)
```

Phases 1-2 are independently useful. Phases 3-5 are the triage/defection
narrative. Phase 6 is independent of triage but enriches the entire faction
ship ecosystem.

---

## Key Design Decisions

1. **Missions go through captain agency.** `ExecuteOrdersGoal` in NpcPursuits
   means captains validate routes against their knowledge and can mutiny.
   Headless ships bypass this — they follow orders mechanically.

2. **CrisisId prevents double-spend.** Each starvation event gets a unique ID.
   Viceroy ignores duplicate requests for the same crisis.

3. **Dual SVS separates economic and military value.** Blockaded ports are
   economic dead weight but may be military priorities. Don't send food into
   a blockade — send warships to break it.

4. **Conservation of mass for relief.** Factions buy food from capital's supply
   with treasury gold. Capital starvation prevents all colonial relief —
   empire-wide cascade.

5. **Pirate takeover requires assassination, not a timer.** Pirates must earn
   the port. The player can defend the independent governor. Creates genuine
   decision points.

6. **Dynamic promotion creates emergent nemeses.** Headless ships that survive
   remarkable events generate named characters with pre-seeded relationships.
   The player's actions create their own rivals.

7. **Couriers are physical ships.** Plea and response travel by sea. Distance
   from capital directly affects response time. The player can intercept
   communications to manipulate the information flow.

8. **Authority is condition-driven.** Independent governors follow the same
   rules as faction governors. No artificial drain.

9. **Viceroy = highest-population governor.** Simple proxy, no new field.

---

## Resolved Questions

### Q1: Independent port food imports → Reduced to 50%
Independent ports lose faction ReliefMission ships but keep 50% of baseline
external imports. Opportunistic merchants still visit.

### Q2: Player-triggered defection → Yes, with consequences
Existing mechanics trace the blockade to the player via ShipLocationClaim
and IndividualActionClaim. The faction blames the player.

### Q3: Multi-port republics → Individual defection only (for now)
Federation mechanic deferred (see TODO.md).

### Q4: Governor retains gold and authority — no artificial drain
Authority is condition-driven. Independence costs the absence of faction
support, not a timer.
