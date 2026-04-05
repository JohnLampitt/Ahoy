# Implementation Plan — Knowledge-Driven Living World

Date: 2026-04-04 (initial), 2026-04-05 (revised — EpistemicResolver pivot)

## Motivation

The GDD states two foundational pillars: "The world is alive" and "Information is power." Groups 1-3 built the epistemic infrastructure — facts propagate, decay, contradict, and get faked. Contract quests trigger from knowledge state. Factions plant disinformation. Agents get burned.

But the world is not yet alive in the way the GDD demands. The critical gap: **NPCs are epistemic spectators, not epistemic agents.** They hold knowledge but don't act on it. Only the player navigates uncertainty; NPCs read ground truth. Quests are player-only; no NPC pursues a bounty, investigates a rumour, or acts on bad intelligence. The knowledge system is a one-way mirror.

This plan addresses the structural gaps that prevent a living, breathing world where both player and NPCs participate in knowledge-driven emergent gameplay.

## Architectural Pivot — Why Templates Are Dead

Quest templates (predefined phase graphs with state machines) were **already
tried in this codebase and deliberately removed.** The surviving
`ContractQuestInstance` is the stripped-down replacement that actually works:
knowledge-triggered, no predefined structure.

The new architecture uses an **EpistemicResolver** — a unified model where
"phases" emerge from knowledge confidence thresholds, not template definitions.
A fact at 0.30 is a rumour. At 0.60 it's actionable. At 0.80 it's verified.
The player's journey through a "quest" is simply: hear rumour → investigate →
act. This is already how `ContractQuestInstance` works; the resolver generalises
it for both NPCs and players.

**LLM Invariant:** The simulation never depends on the LLM. The LLM selects
NPC goals (the "why" — adding personality, grudges, risk assessment). The
EpistemicResolver executes goals deterministically (the "how"). The simulation
validates outcomes mechanically (the "what happened"). Without the LLM,
rule-based scoring produces valid goals. With it, NPCs become characters rather
than optimisers.

---

## Gap Analysis

### G1 — NPC Omniscience (Critical)

**Current state:** `RuleBasedDecisionProvider` reads `WorldState` directly for all NPC decisions. Merchant routing uses random fallbacks or ground-truth port data. Governors start diplomatic tours at a flat 2% probability with no context.

**Problem:** The knowledge system's core promise — "the same rules govern player knowledge and NPC knowledge; no omniscient NPCs" (SDD-KnowledgeSystem §1) — is violated. Information asymmetry is one-directional. NPCs never act on stale intel, never get tricked by disinformation, never make bad decisions because their IndividualHolder facts are wrong.

**Impact:** Disinformation planted by factions has no NPC victims. The player is the only actor who can be misled. The world feels mechanical, not alive.

### G2 — NPC Quest Participation (Critical)

**Current state:** Only the player can activate and fulfil contract quests. `QuestSystem.ScanForContractQuests()` exclusively reads `PlayerHolder`. NPCs with ContractClaims in their IndividualHolder ignore them entirely.

**Problem:** A pirate captain who hears about a bounty should pursue it. A merchant who learns of a famine port should reroute for profit. A naval officer who receives intelligence about an enemy fleet should act on it. None of this happens.

**Impact:** The world has no NPC-driven story beats. All narrative flows through the player.

### G3 — Knowledge Conflicts Are Inert (High)

**Current state:** `KnowledgeStore` detects and tracks `KnowledgeConflict` records. `KnowledgeConflictDetected` events are emitted. But no system reads conflicts, no UI surfaces them, and no NPC or player mechanic resolves them.

**Problem:** Contradictory intelligence is a core gameplay driver. When two sources disagree about a ship's location, the player should face a decision: which to trust? When an NPC holds conflicting facts, their behaviour should reflect indecision or the dominant fact.

**Impact:** Disinformation creates conflicts that silently resolve via confidence decay rather than creating meaningful gameplay moments.

### G4 — Flat Quest State Machine (Medium — resolved by EpistemicResolver, not templates)

**Current state:** Quests are `Active -> Completed | Expired | Failed`. No phases.

**Problem:** "Expansive quests" require investigation phases, information gathering, intermediary contacts, and branching mid-quest based on what the player learns.

**Resolution:** Templates were tried and removed. The EpistemicResolver approach recognises that the knowledge system's confidence thresholds *already are* the phase gates. A treasure hunt is: hear rumour (0.35 confidence) → investigate (0.65) → sail there (direct observation, 0.95). No predefined phase graph needed — the existing mechanics of propagation, investigation, decay, and corroboration create the quest structure emergently.

**Impact:** Quests gain depth without adding a template system. The same epistemic model applies to both player quests and NPC goals.

### G5 — Claim Type Gaps (Medium)

**Current state:** 16+ claim types exist, but several are structurally orphaned:
- `IndividualAllegianceClaim`: Only revealed on death. No living NPC's allegiance is discoverable.
- `RouteHazardClaim`: Defined, never seeded by any system.
- `FactionIntentionClaim`: Seeded by FactionSystem on goal adoption, but at Secret sensitivity — effectively invisible unless investigated.

**Problem:** The claim vocabulary is rich but underutilised. NPCs and players can't trade or act on half the claim types because they never enter circulation.

### G6 — Source Reliability Underused (Low-Medium)

**Current state:** `KnowledgeStore.RecordSourceOutcome()` tracks per-source accuracy. The player's direct observations validate or invalidate prior beliefs. But NPCs never call `RecordSourceOutcome` — they have no mechanism to discover they were lied to.

**Problem:** Source reliability should be the NPC equivalent of "learning who to trust." A captain who repeatedly receives bad intel from a port should become skeptical. This mechanic exists in code but has zero NPC consumers.

---

## Proposed Implementation Groups

### Group 5A — NPC Knowledge-Gated Decisions

**Goal:** NPCs consult their IndividualHolder (or their ship's ShipHolder) instead of ground truth when making decisions.

#### 5A-1: Merchant Routing via IndividualHolder + ShipHolder

**File:** `src/Ahoy.Simulation/Systems/ShipMovementSystem.cs`

Replace the current `AssignNpcRoute` random/ground-truth logic:
1. Union facts from `IndividualHolder(captain)` and `ShipHolder(ship)` — crew gossip is the navigator's log
2. Filter for non-superseded `PortPriceClaim` facts; select highest confidence per port
3. Score candidate ports by expected margin (known buy vs. known sell prices)
4. If no useful price knowledge: fall back to `FactionHolder` facts
5. If still nothing: fall back to `HomePortId`

Port geography (RegionId, adjacency, BaseTravelDays) is structural ground truth on `Region`/`Port` entities — captains know where ports are, they just don't know current prices. No `PortLocationClaim` needed.

**Key constraint:** Captain must have been seeded with initial knowledge at world creation (`CaribbeanWorldDefinition`) or have docked at ports to accumulate facts. Without seeding, all merchants route home on tick 1.

**Performance:** Direct KnowledgeStore lookups per NPC per tick. No caching layer — ~30-50 NPCs × ~100-200 facts is sub-millisecond on .NET 10. Revisit only if profiler shows > 2-3ms/tick.

#### 5A-2: Governor Tour Decisions via Knowledge

**File:** `src/Ahoy.Simulation/Systems/IndividualLifecycleSystem.cs`

Replace flat 2% tour probability:
1. Score tour likelihood based on `FactionHolder` facts (faction under pressure -> governor stays home; faction stable -> governor travels)
2. Tour destination selection uses known `IndividualWhereaboutsClaim` (visit allied governors) and `PortProsperityClaim` (visit struggling ports)
3. Suppress tours to ports where `PortControlClaim` shows enemy control

#### 5A-3: Decision Provider Knowledge Access

**File:** `src/Ahoy.Simulation/Decisions/RuleBasedDecisionProvider.cs`

Pass `KnowledgeStore` into decision evaluation so all NPC decision triggers can read holder-specific facts rather than world state. This is the infrastructure change; individual decision rules are updated incrementally.

### Group 5B — NPC Goal Pursuit (revised from "NPC Quest Participation")

**Goal:** NPCs detect, pursue, and resolve knowledge-driven goals through the
same epistemic mechanisms as the player. Not limited to contracts — any
knowledge-driven objective (bounties, trade, investigation, flight).

#### 5B-1: NPC Goal Assignment (replaces "Contract Detection")

**File:** `src/Ahoy.Simulation/Systems/QuestSystem.cs`

Goal assignment triggers when an NPC's `GoalPursuit` is in the `Pondering`
state — which occurs when an NPC spawns, finishes a goal, or abandons a
stalled goal.

**Part 1 — Rule-based fallback (always available, synchronous):**

For each Pondering NPC with a combat role (PirateCaptain, NavalOfficer,
Privateer, Informant), scan their `IndividualHolder` for high-confidence
`ContractClaim` facts (> 0.40) with supporting intel (> 0.50). Score
candidates by personality-weighted utility (e.g., Greedy captains weight
gold reward higher; Cautious captains penalise low-confidence intel).
Assign the highest-scoring goal as `FulfillContractGoal`. Informants
eligible only for `TargetDead` contracts.

If no actionable contract: assign a default goal appropriate to role
(trade routing for merchants, patrol for naval officers, etc.).

**Part 2 — LLM strategic goal selection (optional, async):**

When QuestSystem (tick 7) detects a Pondering NPC, it dispatches an async
request to the LLM service via the existing `DecisionQueue` infrastructure.

**The prompt payload** — the engine synthesises the NPC's epistemic reality
into a structured payload:
- **Identity:** Role, PersonalityTraits (Greed, Boldness, Cunning, Loyalty)
- **Knowledge:** Top 5 highest-confidence facts from their IndividualHolder
  (known bounties, port wealth, ship locations, faction intentions, etc.)
- **Resources:** Ship status (hull, crew, cargo), CurrentGold
- **Context:** Current location, active conflicts in held knowledge

**The async callback** — the LLM evaluates the payload and returns a
structured decision (e.g., `{"GoalType": "FulfillContract",
"TargetSubject": "Ship:1234"}`). The callback safely queues this mutation
for the next tick, assigning the new `NpcGoal` and transitioning the
pursuit state from Pondering to Active.

**Fallback guarantee:** The rule-based path assigns a goal immediately on
the same tick. If the LLM responds before the next tick, it can override
the rule-based goal. If the LLM is slow or unavailable, the NPC already
has a valid goal — the world never stalls waiting for LLM responses. This
is the same pattern as the existing `DecisionQueue` for player-facing NPC
interactions.

#### 5B-2: Goal Pursuit & Resolver Model (replaces "Contract Pursuit Model")

**File:** `src/Ahoy.Simulation/State/GoalModels.cs` (new)

The goal (the behavioural objective) is separated from the pursuit (the
execution state machine):

```csharp
// ---- The Behavioural Objective ----

public abstract record NpcGoal(Guid Id, IndividualId NpcId);

public record FulfillContractGoal(
    Guid Id, IndividualId NpcId,
    ContractClaim Contract) : NpcGoal(Id, NpcId);

// Future: TradeGoal, InvestigateGoal, FleeGoal, EscortGoal, etc.

// ---- The State Machine ----

public enum PursuitState { Pondering, Active, Stalled, Completed, Abandoned }

public sealed class GoalPursuit
{
    public required NpcGoal ActiveGoal { get; init; }
    public PursuitState State { get; set; } = PursuitState.Active;
    public int TicksStalled { get; set; }
    public required int ActivatedOnTick { get; init; }
}
```

**File:** `src/Ahoy.Simulation/State/WorldState.cs`

```csharp
public Dictionary<IndividualId, GoalPursuit> NpcPursuits { get; } = new();
```

Lives on WorldState so ShipMovementSystem (system 2) can read pursuits when
setting NPC routes, even though QuestSystem (system 7) creates them.

#### 5B-3: ShipRoute Union Type

**File:** `src/Ahoy.Simulation/State/Ship.cs`

Replace `PortId? RoutingDestination` + `OceanPoiId? PoiDestination` with a
discriminated union:

```csharp
public abstract record ShipRoute;
public record PortRoute(PortId Destination) : ShipRoute;
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;
public record PoiRoute(OceanPoiId Poi) : ShipRoute;

// On Ship:
public ShipRoute? Route { get; set; }  // replaces RoutingDestination + PoiDestination
```

`PursuitRoute` navigates to `LastKnownRegion` and performs proximity check
on arrival for interception. The active `GoalPursuit` determines which Route
variant to set.

#### 5B-4: Epistemic Execution & Stall/Leak (replaces "Contract Resolution")

**Execution — EpistemicResolver pattern:**

**File:** `src/Ahoy.Simulation/Decisions/RuleBasedDecisionProvider.cs`

For each active `GoalPursuit`, the resolver evaluates the NPC's current
knowledge state against the goal's requirements and returns the immediate
primitive action:
- NPC knows target location (high confidence `ShipLocationClaim`) → set
  `PursuitRoute`, state remains Active
- NPC lacks target location → trigger `InvestigateRemoteCommand` if NPC has
  gold, otherwise Stall
- NPC in same region as target → attempt resolution (stat check)
- Informant `TargetDead` resolution: abstracted stat-check gated by faction's
  `IntelligenceCapability` on arrival at target's port. Failure emits
  `AgentBurned`.
- Target destroyed/dead by NPC → emit `NpcClaimedContract`, pay NPC, state
  Completed. Player receives no compensation — "you were too slow."

The resolver doesn't know what kind of goal it's executing. It follows the
epistemic breadcrumb trail: what does the NPC know? What do they need to know?
What's the next action to close that gap?

**Stalling:**

If the NPC lacks the gold, capability, or knowledge to execute the next
breadcrumb, increment `TicksStalled`. If `TicksStalled > 14`, set state to
Abandoned.

**Stall & Leak mechanic:**

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs`

When a `GoalPursuit` is marked Abandoned:
1. Emit `NpcPursuitAbandoned` event
2. KnowledgeSystem identifies the highest-confidence fact the NPC held
   regarding that goal
3. Inject that fact into the `PortHolder` where the NPC is currently docked
4. This generates tavern gossip: "Captain Vane abandoned his hunt for the
   Silver Galleon near Jamaica"
5. Player (or other NPCs) can discover and act on this leaked intelligence

**AtSea edge case:** If abandonment triggers while the NPC is AtSea or
EnRoute, the leak is deferred — the fact is held in ShipHolder and injected
into the next PortHolder the NPC docks at. This is consistent with existing
ship-carried gossip propagation: sailors don't shout their frustrations into
the ocean, they grumble about it in the next tavern. The existing
`ArrivedThisTick` flag on Ship provides the trigger point — KnowledgeSystem
already processes arrival gossip using this flag.

This is the bridge between NPC failure and player opportunity. Silent expiry
is replaced with emergent storytelling.

**Player interaction:** The player competes with NPCs for bounties. If an NPC
fulfils a contract first, the player's quest expires with status `ClaimedByNpc`.
The player can also sell intel to NPCs to point them at targets (or misdirect
them with bad intel).

### Group 5C — Knowledge Conflict Resolution

**Goal:** Conflicts become visible gameplay moments for both player and NPCs.

#### 5C-1: Player Conflict Surfacing

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs`

When `KnowledgeConflictDetected` fires for `PlayerHolder`:
- Tag the conflict as `PlayerVisible = true`
- Console `knowledge` command shows active conflicts with competing claims
- Player can issue `InvestigateLocalCommand` or `InvestigateRemoteCommand` to resolve

#### 5C-2: NPC Conflict Behaviour

**File:** `src/Ahoy.Simulation/Decisions/RuleBasedDecisionProvider.cs`

When an NPC holds conflicting facts:
- Use dominant fact (highest confidence) for decisions
- If `ConfidenceSpread < 0.15` (near-tied): NPC hesitates (delays action by 1-3 ticks)
- Captains with conflicts about route safety may choose conservative routes

#### 5C-3: KnowledgeConflictResolved Event

**File:** `src/Ahoy.Simulation/Events/WorldEvent.cs`

```csharp
public record KnowledgeConflictResolved(
    WorldDate Date, SimulationLod SourceLod,
    string SubjectKey,
    KnowledgeFactId WinningFactId) : WorldEvent(Date, SourceLod);
```

Emitted when auto-resolve fires (ConfidenceSpread > 0.40) or when investigation
supersedes one competing fact. Console and future UI use this to clean up
conflict displays.

#### 5C-4: Conflict-Triggered Quests

Extend `QuestCondition` with:

```csharp
public record ConflictCondition(
    Func<KnowledgeConflict, bool> Predicate,
    string Description) : QuestCondition;
```

Queries `KnowledgeConflict` records directly — no synthetic "ConflictFact"
polluting the KnowledgeStore.

New quest templates that trigger on conflicts:
- "Two captains disagree about the location of the Silver Fleet — who do you believe?"
- "Your intelligence contradicts the governor's briefing — investigate or comply?"

### ~~Group 5D — Multi-Phase Quest Framework~~ (DROPPED)

**Dropped.** Quest templates were already tried in this codebase and deliberately
removed. The surviving `ContractQuestInstance` — knowledge-triggered, no
predefined structure — is the approach that works.

**Replacement: EpistemicResolver.** The "phases" that templates tried to
predefine emerge naturally from knowledge confidence thresholds:

| Confidence | Epistemic State | Player Experience |
|---|---|---|
| < 0.30 | Noise | Not actionable — below awareness threshold |
| 0.30–0.50 | Rumour | "You've heard whispers…" — investigation available |
| 0.50–0.70 | Actionable intel | Quest activation threshold — can act, but risk of bad info |
| 0.70–0.90 | Verified intelligence | High confidence — reliable basis for action |
| > 0.90 | Direct observation | Ground truth equivalent — player witnessed it |

A "treasure hunt" is not Phase 1 → Phase 2 → Phase 3. It is:
1. `OceanPoiClaim` enters PlayerHolder at 0.35 confidence (rumour heard in tavern)
2. Player investigates → confidence rises to 0.65 (corroborated by second source)
3. Player sails to region → direct observation → confidence 0.95

No template defined these steps. The knowledge system's existing mechanics
(propagation, investigation, decay, corroboration) *are* the quest structure.
The LLM narrative layer describes what's happening; it doesn't drive it.

The same model applies to NPCs via the NpcGoal hierarchy (5B). An NPC
"pursuing a treasure" is an NPC whose IndividualHolder contains an
`OceanPoiClaim` at actionable confidence, whose NpcGoal routes them toward
the POI region. If their confidence decays, the goal stalls. No phase graph
needed.

### Group 5E — Claim Circulation Improvements

**Goal:** Ensure underused claim types enter the knowledge ecosystem.

#### 5E-1: RouteHazardClaim Seeding

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs`

When `ShipMovementSystem` generates storm damage or pirate encounter events, seed `RouteHazardClaim` into the affected ship's `ShipHolder`. On next dock, this propagates to port naturally.

#### 5E-2: IndividualAllegianceClaim Circulation

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs`

Currently only revealed on death. Add:
- Investigation of an individual with `ClaimedFactionId != FactionId` can reveal allegiance
- Brokers who hold allegiance facts can sell them (already supported by `SellFactCommand` infrastructure)
- Faction counter-intelligence (IntelligenceCapability roll) can detect and emit allegiance facts about enemy infiltrators into FactionHolder

#### 5E-3: FactionIntentionClaim Downgrade Path

**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`

Currently seeded at Secret sensitivity (0% propagation). Two leak mechanics:

**Low-capability baseline (passive):** Factions with IntelligenceCapability < 0.35
have a 2%/tick chance that an active Secret intention leaks to a random
controlled port at Restricted sensitivity. Structurally leaky organisations.

**High-capability stress leaks (event-driven):** All factions leak under
systemic stress, regardless of capability:
1. `TreasuryGold < ExpenditurePerTick * 5` → "can't pay their spies"
2. `DeceptionExposed` event → "operational security compromised"
3. `NavalStrength` drops > 50% in 10 ticks → "faction in disarray"

Each trigger: 15%/tick chance (while condition persists) that one active Secret
`FactionIntentionClaim` downgrades to Restricted in a random controlled port.

**Player agency loop:** Player raids Spanish gold → treasury crisis → Spain's
secret invasion plans leak to Havana taverns → player discovers and acts. This
turns leaks into a mechanic the player can actively provoke.

---

## Sequencing

```
Group 5A (NPC knowledge-gated decisions)
  5A-3 first (infrastructure), then 5A-1 + 5A-2 in parallel
  ↓
Group 5E (claim circulation — makes 5A meaningful by giving NPCs facts to act on)
  All items independent, parallel
  ↓
Group 5B (NPC goal pursuit �� needs 5A for NPC routing + 5E for claim flow)
  5B-2 + 5B-3 first (GoalPursuit model + ShipRoute union), then 5B-1 + 5B-4
  ↓
Group 5C (conflict resolution — benefits from all above being live)
  5C-1 first (player-facing), then 5C-2, then 5C-3 + 5C-4
```

5D is dropped. Groups 5C can proceed once 5A+5E are stable.

---

## Key Design Constraints

1. **No system reads ground truth for NPC decisions after 5A.** All NPC behaviour flows from held facts. This is the single most important invariant.

2. **NPC goal pursuit is lightweight.** `GoalPursuit` wraps an `NpcGoal` (the objective) with an execution state machine (Active/Stalled/Completed/Abandoned). No branches, no predefined phases, no player-facing UI. The EpistemicResolver determines the next primitive action from the NPC's knowledge state. Lives on WorldState for cross-system visibility.

3. **ShipRoute union type replaces PortId? RoutingDestination.** `PortRoute`, `PursuitRoute`, `PoiRoute` — consolidates routing intent into a single discriminated union. `PursuitRoute` enables interception at sea.

4. **No templates, no phase graphs.** "Phases" emerge from knowledge confidence thresholds. The knowledge system's existing mechanics (propagation, investigation, decay, corroboration) *are* the quest structure. This applies equally to player quests and NPC goals.

5. **Stall & Leak replaces silent expiry.** When NPC goals stall, the context leaks as gossip. NPC failure creates player opportunity rather than disappearing silently.

6. **LLM selects goals, never determines outcomes.** The simulation never depends on the LLM. Rule-based fallback always produces valid goals. The LLM adds judgement and personality to goal *selection* only. The EpistemicResolver executes goals deterministically; the simulation validates outcomes mechanically.

7. **Claim circulation changes are additive.** No existing claim types change semantics. New seeding paths are layered on. High-cap faction leaks are event-driven (player agency), not RNG.

8. **Tick ordering is preserved.** No new systems are added. All changes extend existing systems within their current tick slots. Ships route on T-1 knowledge — this is intentional (yesterday's intelligence).
