# Implementation Plan — Knowledge-Driven Living World

Date: 2026-04-04

## Motivation

The GDD states two foundational pillars: "The world is alive" and "Information is power." Groups 1-3 built the epistemic infrastructure — facts propagate, decay, contradict, and get faked. Contract quests trigger from knowledge state. Factions plant disinformation. Agents get burned.

But the world is not yet alive in the way the GDD demands. The critical gap: **NPCs are epistemic spectators, not epistemic agents.** They hold knowledge but don't act on it. Only the player navigates uncertainty; NPCs read ground truth. Quests are player-only; no NPC pursues a bounty, investigates a rumour, or acts on bad intelligence. The knowledge system is a one-way mirror.

This plan addresses the structural gaps that prevent a living, breathing world where both player and NPCs participate in knowledge-driven emergent gameplay.

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

### G4 — Flat Quest State Machine (Medium)

**Current state:** Quests are `Active -> Completed | Expired | Failed`. No phases. SDD-QuestSystem §7.3 explicitly defers multi-phase quests ("not implemented in v0.1").

**Problem:** "Expansive quests" require investigation phases, information gathering, intermediary contacts, and branching mid-quest based on what the player learns. A treasure hunt needs: hear rumour -> verify location -> navigate hazards -> discover site. A political conspiracy needs: receive tip -> investigate -> choose allegiance -> act.

**Impact:** Quests feel transactional rather than adventurous.

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

### Group 5B — NPC Quest Participation

**Goal:** NPCs can detect, pursue, and resolve contract quests through the same knowledge-triggered mechanism as the player.

#### 5B-1: NPC Contract Detection

**File:** `src/Ahoy.Simulation/Systems/QuestSystem.cs`

New method `ScanForNpcContractOpportunities(WorldState)`:
- For each alive Individual with role in {PirateCaptain, NavalOfficer, Privateer, Informant}:
  - Read their `IndividualHolder` for `ContractClaim` facts with confidence > 0.40
  - Check supporting intel (same gate as player: separate fact about TargetSubjectKey)
  - Informants eligible only for `TargetDead` contracts (assassination/sabotage, not naval combat)
  - If conditions met: create `NpcContractPursuit` on `WorldState.NpcContractPursuits`

#### 5B-2: NPC Contract Pursuit Model

**File:** `src/Ahoy.Simulation/State/QuestModels.cs`

```csharp
public sealed class NpcContractPursuit
{
    public required IndividualId PursuerId { get; init; }
    public required ContractClaim Contract { get; init; }
    public NpcPursuitStatus Status { get; set; }  // Routing | Engaging | Abandoned
    public required int ActivatedOnTick { get; init; }
}
```

**File:** `src/Ahoy.Simulation/State/WorldState.cs`

```csharp
public Dictionary<IndividualId, NpcContractPursuit> NpcContractPursuits { get; } = new();
```

Lives on WorldState so ShipMovementSystem (system 2) can read pursuits when
setting NPC routes, even though QuestSystem (system 8) creates them.

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
on arrival for interception. Standard port routing would miss targets at sea.

#### 5B-4: NPC Contract Resolution

**File:** `src/Ahoy.Simulation/Systems/QuestSystem.cs`

Each tick, for active `NpcContractPursuit`:
- If pursuer's ship is in same region as target: attempt resolution (stat check)
- Informant `TargetDead` resolution: abstracted stat-check gated by faction's
  `IntelligenceCapability` on arrival at target's port. Failure emits `AgentBurned`.
- If pursuer's intel confidence drops below 0.30: status -> Abandoned
- If target destroyed/dead: emit `NpcClaimedContract` event, pay NPC
- Player receives no compensation — "you were too slow" is intentional
- Emit `NpcContractPursuitStarted` / `NpcContractAbandoned` as WorldEvents so the knowledge system can propagate "Captain X is hunting Ship Y" as gossip

**Player interaction:** The player competes with NPCs for bounties. If an NPC fulfils a contract first, the player's quest expires with status `ClaimedByNpc`. The player can also sell intel to NPCs to point them at targets (or misdirect them with bad intel).

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

### Group 5D — Multi-Phase Quest Framework

**Goal:** Replace flat state machine with a phase graph for complex quests.

#### 5D-1: QuestPhase Model

**File:** `src/Ahoy.Simulation/State/QuestModels.cs`

```
QuestPhase:
  PhaseId: string
  Description: string
  KnowledgeGate: QuestCondition (must be satisfied to advance)
  LocationGate: Func<WorldState, bool>? (optional physical presence requirement)
  Branches: IReadOnlyList<QuestBranch> (choices available in this phase)
  AutoAdvance: bool (advances immediately when gate satisfied, no player choice)
```

QuestTemplate gains `Phases: IReadOnlyList<QuestPhase>` (ordered). Existing flat templates map to a single phase (backward compatible).

Quest instance gains `int CurrentPhaseIndex { get; set; }` to track progress.

**Trigger fact pinning:** When a multi-phase quest activates, set
`IsDecayExempt = true` on facts directly referenced as phase gate conditions.
Revert on quest completion/failure/expiry. Only pin gate-critical facts, not
every related fact — the captain's log records quest details, not every whisper.

**NPC eligibility:** Add `bool IsNpcEligible` to `QuestTemplate` (default
`false` for multi-phase, `true` for flat contracts). Autonomous phase navigation
requires GOAP/HTN architecture; defer until RuleBasedDecisionProvider proves
insufficient.

#### 5D-2: Phase Advancement Logic

**File:** `src/Ahoy.Simulation/Systems/QuestSystem.cs`

Each tick for active multi-phase quests:
1. Evaluate current phase's `KnowledgeGate` against player facts
2. If satisfied and `AutoAdvance`: move to next phase, emit `QuestPhaseAdvanced`
3. If satisfied and not `AutoAdvance`: present branches to player
4. If final phase completed: resolve quest normally

#### 5D-3: Exemplar Multi-Phase Templates

**File:** `src/Ahoy.WorldData/CaribbeanQuestTemplates.cs`

Two new templates exercising the phase system:

**"The Lost Manifest"** (treasure hunt):
- Phase 1 (Rumour): Trigger on `OceanPoiClaim` with confidence < 0.50. Auto-advance.
- Phase 2 (Verify): Requires `OceanPoiClaim` confidence > 0.70 (player must investigate/corroborate). Choice: pursue alone or sell location.
- Phase 3 (Expedition): Location gate — player ship in POI region. Choice: salvage or set ambush for rivals.

**"The Governor's Conspiracy"** (political):
- Phase 1 (Tip): Trigger on `IndividualAllegianceClaim` showing infiltrator. Auto-advance.
- Phase 2 (Gather Evidence): Requires 2+ corroborating facts about the individual. Choice: confront governor, sell to rival faction, or blackmail.
- Phase 3 (Consequences): Knowledge gate on faction response. Resolution branches vary by Phase 2 choice.

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
Group 5B (NPC quest participation — needs 5A for NPC routing + 5E for claim flow)
  5B-2 + 5B-3 first (models), then 5B-1 + 5B-4
  ↓
Group 5C (conflict resolution — benefits from all above being live)
  5C-1 first (player-facing), then 5C-2, then 5C-3 + 5C-4
  ↓
Group 5D (multi-phase quests — independent of 5B/5C but richer with them)
  5D-1 first (model), then 5D-2, then 5D-3
```

Groups 5C and 5D are independent of each other and can proceed in parallel once 5A+5E are stable.

---

## Key Design Constraints

1. **No system reads ground truth for NPC decisions after 5A.** All NPC behaviour flows from held facts. This is the single most important invariant.

2. **NPC quest pursuit is lightweight.** `NpcContractPursuit` is not a full `QuestInstance` — it has no branches, no LLM dialogue, no player-facing UI. It's a routing directive with a resolution condition. Lives on WorldState for cross-system visibility.

3. **ShipRoute union type replaces PortId? RoutingDestination.** `PortRoute`, `PursuitRoute`, `PoiRoute` — consolidates routing intent into a single discriminated union. `PursuitRoute` enables interception at sea.

4. **Phase graph is backward compatible.** Existing flat templates are single-phase quests. No migration needed. `IsNpcEligible` defaults to `false` for multi-phase.

5. **Trigger fact pinning is scoped.** Only facts directly referenced as phase gate conditions are pinned. Secondary/ambient facts decay normally.

6. **Claim circulation changes are additive.** No existing claim types change semantics. New seeding paths are layered on. High-cap faction leaks are event-driven (player agency), not RNG.

7. **Tick ordering is preserved.** No new systems are added. All changes extend existing systems within their current tick slots. Ships route on T-1 knowledge — this is intentional (yesterday's intelligence).
