# Implementation Plan — Faction Missions & Colonial Triage (Group 9)

Date: 2026-04-05 (revised)

## Motivation

Two structural gaps need filling:

**1. Factions can't command their ships.** NPC ships route via merchant
knowledge or bounty pursuit. There's no concept of a faction issuing orders
to a ship it owns — "carry this message to Havana," "deliver food to Port
Royal," "reinforce the garrison at Cartagena." This means faction relief is
magically injected (`InjectExternalFood`), military responses can't be
dispatched, and diplomatic communications are instant.

**2. Factions fund relief naively.** Every starving port gets up to 50% of
the faction treasury. No triage, no strategic evaluation, no consequences
when relief is denied.

This plan adds **Faction Missions** (the command infrastructure for faction
ships) and **Colonial Triage** (intelligent relief allocation with defection
consequences). Faction missions are independently useful and are a prerequisite
for the triage system's courier-based plea/response flow.

---

## Current State

- NPC ships route via merchant arbitrage (ShipMovementSystem) or goal pursuit
  (QuestSystem). No faction-directed routing.
- `SeedFamineRelief` caps relief at `TreasuryGold / 2` — no strategic evaluation.
- `InjectExternalFood` magically creates food at ports each tick.
- No courier/communication ship mechanic.
- No `PortDefected` event or defection cascade.

---

## Phase 1 — Faction Mission Model

### Data Model

**File:** `src/Ahoy.Simulation/State/Ship.cs`

```csharp
/// <summary>
/// An order from a faction to one of its ships. Ships on mission ignore
/// merchant routing — they follow orders. Resolves on arrival at destination.
/// </summary>
public abstract record FactionMission(PortId Destination);

/// <summary>Carry a knowledge fact to a specific port. The diplomatic pouch.</summary>
public record CourierMission(PortId Destination, KnowledgeFactId CarriedFact) : FactionMission(Destination);

/// <summary>Deliver supplies to a port. Cargo loaded at origin, unloaded on arrival.</summary>
public record ReliefMission(PortId Destination, TradeGood Cargo, int Quantity) : FactionMission(Destination);

/// <summary>Reinforce a port's defense. Ship's guns contribute to port defense rating on arrival.</summary>
public record ReinforceMission(PortId Destination) : FactionMission(Destination);

/// <summary>Patrol a region. Ship loiters in the region intercepting enemies.</summary>
public record PatrolMission(PortId Destination, RegionId PatrolRegion) : FactionMission(Destination);
```

**On Ship:**
```csharp
/// <summary>Active faction order. When set, ship ignores merchant routing and follows the mission.</summary>
public FactionMission? Mission { get; set; }
```

### Ship Routing Priority

**File:** `src/Ahoy.Simulation/Systems/ShipMovementSystem.cs`

Update `AssignNpcRoute` to check for mission before merchant routing:

```
if ship.Mission is not null:
    ship.Route = new PortRoute(ship.Mission.Destination)
    return   // mission overrides all other routing
// ... existing merchant/bounty routing below
```

### Mission Resolution on Arrival

**File:** `src/Ahoy.Simulation/Systems/ShipMovementSystem.cs` or new `MissionSystem`

When a mission ship docks at its destination (`ArrivedThisTick` + `Mission` set):

- **CourierMission:** Inject `CarriedFact` into the destination port's
  `PortHolder` and into the governor's `IndividualHolder` (if present).
  Clear the mission.
- **ReliefMission:** Transfer `Cargo × Quantity` from ship hold to
  `port.Economy.Supply`. If ship doesn't have enough (intercepted en route,
  cargo raided), deliver what remains. Clear the mission.
- **ReinforceMission:** Ship stays docked at port. Its `Guns` contribute to
  port defense rating (blockade detection uses this). Mission stays active
  until FactionSystem reassigns.
- **PatrolMission:** Ship departs toward `PatrolRegion`. On arrival in region,
  loiters (similar to existing `PatrolRegionGoal` but faction-directed).

### Mission Assignment

**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`

New method `AssignMissions(Faction, WorldState)` called during the faction tick:

1. **Identify idle faction ships** — ships with `OwnerFactionId == faction`
   and `Mission == null` and docked at a faction-controlled port.
2. **Evaluate mission queue** — faction maintains a priority queue of
   pending missions (courier responses, relief orders, reinforcement requests).
3. **Assign highest-priority mission** to the nearest idle ship.
4. **Load cargo** for ReliefMissions — deduct from the origin port's supply
   (or from faction treasury for purchased goods).

---

## Phase 2 — Replace External Food Imports

With faction missions, the `InjectExternalFood` placeholder can be partially
replaced by actual `ReliefMission` ships:

1. **Faction food supply tick:** Each tick, FactionSystem evaluates controlled
   ports' food levels. Ports below 50% of `TargetSupply[Food]` get a
   `ReliefMission` dispatched from the nearest food-surplus faction port.
2. **Loading:** The origin port's food supply is deducted. The ship physically
   carries the food. It can be intercepted by pirates en route.
3. **Delivery:** On arrival, food is added to the destination port's supply.

`InjectExternalFood` remains as a reduced baseline (representing off-map
supply from Europe — not faction-directed, just background trade). Faction
relief missions supplement it for ports in crisis.

---

## Phase 3 — Courier-Based Plea & Response

### The Information Flow

1. **Governor detects famine.** Port food supply < 50% of target for 5+ ticks.
2. **Governor dispatches plea courier.** FactionSystem creates a `CourierMission`
   carrying a `ReliefRequestFact` (new fact type: port ID, severity, gold
   needed). Assigns to an idle faction ship at the starving port.
3. **Courier travels to capital.** Physical ship, physical travel time. Can be
   intercepted.
4. **Viceroy receives plea.** CourierMission resolves: `ReliefRequestFact`
   enters the viceroy's IndividualHolder and the capital's PortHolder.
5. **FactionSystem evaluates via SVS triage** (see Phase 4).
6. **Response dispatched by courier.** Either:
   - **Approved:** `ReliefMission` ship dispatched with food + `CourierMission`
     carrying approval fact. Governor receives both food and confirmation.
   - **Denied:** `CourierMission` carrying `FactionReliefDenied` fact dispatched
     back. Governor receives denial and evaluates defection.

### Gossip Parallel Path

Famine gossip also propagates via normal merchant traffic. The viceroy may
hear rumours of starvation via `PortConditionClaim(Famine)` before the
official courier arrives. A proactive viceroy (high Loyalty trait) might
dispatch relief based on gossip alone — without waiting for the formal plea.

This means:
- **Well-connected ports** (lots of merchant traffic) get faster informal help
- **Remote ports** rely on the courier — if it's intercepted, they're alone
- **Player opportunity:** intercept the courier to manipulate the information flow

---

## Phase 4 — Strategic Value Score & Triage

### SVS Calculation

When a `ReliefRequestFact` arrives at the capital, FactionSystem calculates:

```
SVS = (Population / 1000)
    × M_relationship    (viceroy ↔ governor: uses RelationshipMatrix)
    × M_threat          (Blockaded→0.2, AtWar region→0.5, Peaceful→1.0)
    × M_production      (produces Gold/Silver→1.5, essential exporter→1.2, GeneralTrade→1.0)
```

### Triage Budget Cap

```csharp
float maxTreasuryPercent = Math.Clamp(svs * 0.02f, 0.01f, 0.25f);
int maxApprovedRelief = (int)(faction.TreasuryGold * maxTreasuryPercent);
```

If the relief request exceeds the cap: **denied.** Emit `FactionReliefDenied`
fact, dispatch denial courier back to the port.

**Viceroy proxy:** The governor of the faction's highest-population port
serves as the viceroy. No new `FactionLeaderId` field needed.

---

## Phase 5 — Defection Cascade

### Trigger

Governor's IndividualHolder receives `FactionReliefDenied` fact (delivered by
courier ship). Consequence math fires:

1. `IndividualActionClaim(Viceroy, Governor, null, Hostile, Severe,
   "Abandoned colony to starvation")` — drives relationship to nemesis.
2. **Governor evaluates defection** (`EvaluateDefection` method):
   - Relationship with viceroy < -75 AND port is starving AND no relief coming
   - Governor flips: `Port.ControllingFactionId → null` (Independent) or
     pirate faction if they have regional presence.
   - Emit `PortDefected` event.
3. **The broadcast:** Governor seeds `ContractClaim(GoodsDelivered, Food)`
   through any available knowledge pool — "buying from anyone, even pirates."

### Narrative Propagation

`PortDefected` generates:
- `PortControlClaim` updated in nearby holders
- `IndividualActionClaim(Governor, Viceroy, null, Hostile, Severe, "Defected")`
- `FactionIntentionClaim` at Public sensitivity: "Port X declared independence"

---

## Phase 6 — Faction Response & Pirate Haven Emergence

### Faction Response

`PortDefected` arrives at the capital (via gossip or courier). FactionSystem
evaluates:

**Rule-based default:**
```
if AtWarWith.Count == 0 AND TreasuryGold > recaptureCost:
    adopt RecapturePort goal, dispatch reinforcement mission
    seed ContractClaim(TargetDead) on the defecting governor
else:
    write off the port (bigger problems right now)
```

**LLM override:** Viceroy enters Pondering state. LLM receives leader traits,
treasury state, active wars, SVS of lost port. Returns one of: RecapturePort,
AssassinateGovernor, IgnoreDefection. Rule-based default fires immediately;
LLM overrides next tick.

### New Goal & Mission Types

```csharp
// Faction goal
public record RecapturePort(PortId TargetPort) : FactionGoal;

// NPC goal (assigned to NavalOfficer)
public record RecapturePortGoal(Guid Id, IndividualId NpcId, PortId TargetPort) : NpcGoal(Id, NpcId);
```

Execution: `ReinforceMission` dispatched to a NavalOfficer's ship. On arrival,
stat-check for reconquest. Success: `PortCaptured` event, port returns to
faction control.

### Pirate Haven Emergence

If no faction recaptures within 30 ticks AND a pirate faction has
`HavenPresence > 0` in the region: port becomes a pirate haven. Pirates fill
the power vacuum without fighting. `EstablishHaven` goal auto-fulfilled.

If no pirates: port stays Independent, recovers via reduced external imports
(50%) and merchant trade, becomes a neutral hub any faction can claim.

---

## Sequencing

```
Phase 1 — FactionMission model + ship assignment + routing priority
  ↓
Phase 2 — Replace InjectExternalFood with ReliefMission ships
  ↓
Phase 3 — Courier-based plea/response flow (physical information travel)
  ↓
Phase 4 — SVS triage (evaluate plea, approve or deny)
  ↓
Phase 5 — Defection cascade (denial → deed → relationship → flip)
  ↓
Phase 6 — Faction response + pirate haven emergence
```

Phases 1-2 are independently useful (factions command ships, food supply
has a physical basis). Phases 3-6 build the triage/defection narrative.

---

## Key Design Decisions

1. **FactionMission is on Ship, not NpcGoal.** Missions are faction orders to
   ships, not individual NPC goals. A ship follows orders regardless of its
   captain's personal goals. This is a separate command channel from the
   EpistemicResolver's goal pursuit.

2. **Couriers are physical ships.** Plea and response travel by sea, take real
   time, and can be intercepted. Distance from the capital directly affects
   response time. This is the knowledge system's core promise applied to
   faction command.

3. **Gossip provides a parallel intelligence path.** The viceroy may hear about
   a famine via merchant gossip before the courier arrives. Proactive leaders
   can act on rumour; cautious leaders wait for the official request. Both
   are valid strategies.

4. **SVS is calculated, not stored.** Computed from population, relationships,
   threats, and production. No new field on Port.

5. **Defection trigger is deterministic.** Governor defects when: relationship
   with viceroy < -75 AND starving AND relief denied. Only the parent
   faction's *response* to defection uses the LLM. Simulation never depends
   on LLM.

6. **Authority is condition-driven, not flag-driven.** Independent governors
   follow the same authority rules as faction governors. No artificial drain.
   The cost of independence is the absence of faction support, not a timer.

7. **Viceroy = governor of highest-population faction port.** No new
   `FactionLeaderId` field. Simple proxy that works with existing data.

---

## Resolved Questions

### Q1: Independent port food imports → Reduced to 50%
Independent ports lose faction supply convoys (ReliefMission ships stop
coming) but keep 50% of baseline external imports (off-map European trade).
Opportunistic merchants still visit. The player or pirate merchants filling
the gap is the gameplay.

### Q2: Player-triggered defection → Yes, with consequences
The player can blockade a port until the faction denies relief and the
governor defects. Existing mechanics connect the dots: `ShipLocationClaim`
places the player in the blockade region, `IndividualActionClaim` records
the hostile act. The faction blames the player if they have the intelligence.

### Q3: Multi-port republics → Individual defection only (for now)
Each port defects individually. Federation mechanic deferred (see TODO.md).

### Q4: Governor retains gold and authority — no artificial drain
Authority is condition-driven: grows when fed, drops on starvation/blockade.
Independence costs the absence of faction support, not a magic timer. A
competent independent governor can survive indefinitely.
