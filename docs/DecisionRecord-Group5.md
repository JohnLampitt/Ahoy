# Decision Record — Group 5 (Knowledge-Driven Living World)

Date: 2026-04-04 (initial review), 2026-04-05 (architectural pivot)
Status: Revised — EpistemicResolver architecture adopted, 5D dropped

## Architectural Pivot (2026-04-05)

After the competing LLM review, further discussion revealed a critical lesson
from the project's own history: **quest templates were already tried and
deliberately removed.** The surviving `ContractQuestInstance` is the
stripped-down replacement that actually works — knowledge-triggered, no
predefined structure.

This validates the competing LLM's concerns about 5D more strongly than
initially credited. Building multi-phase quest templates would repeat a
known failure.

### What changed

| Area | Before | After |
|---|---|---|
| 5D (Multi-Phase Quests) | Phase graph on QuestTemplate | **Dropped entirely** — templates were tested and rejected in practice |
| Quest architecture | Template-driven state machines | **EpistemicResolver** — phases emerge from knowledge gaps, not predefined structures |
| NPC goal model | NpcContractPursuit (contract-only) | **NpcGoal hierarchy** — generalised goal pursuit for any knowledge-driven objective |
| NPC failure | Silent expiry | **Stall & Leak** — NPC failure/stalling creates player opportunity through leaked intel |
| LLM role | Narrative flavour on quest text | **Goal selection** — LLM chooses NPC goals (the "why"), EpistemicResolver executes deterministically (the "how"), simulation validates outcomes (the "what happened") |

### EpistemicResolver — Core Concept

The player's "quest" is already how ContractQuestInstance works:
1. Hear a rumour (low confidence fact enters holder)
2. Investigate to verify (raise confidence through investigation commands)
3. Act on verified intelligence (fulfil contract condition)

The EpistemicResolver generalises this pattern for both NPCs and players.
There are no predefined phases — the knowledge system's confidence thresholds
*are* the phase gates. A fact at 0.30 confidence is a rumour. At 0.60 it's
actionable intelligence. At 0.80 it's verified. The "phases" emerge from
the epistemic state, not from a template.

### NpcGoal Hierarchy — Core Concept

Replaces the contract-only `NpcContractPursuit` with a general goal model.
An NPC goal is any knowledge-driven objective: pursue a bounty, investigate
a rumour, trade at a profitable port, flee a dangerous region. Goals are
lightweight routing directives with resolution conditions — same footprint
as `NpcContractPursuit` but not limited to contracts.

### Stall & Leak — Core Concept

When an NPC stalls on a goal (intel decays, route blocked, target lost),
instead of silent expiry, the stall generates observable side effects:
the NPC's knowledge about why they stalled leaks into port gossip on next
dock. This creates player opportunity — "Captain Vane abandoned his hunt
for the Silver Galleon near Jamaica" is actionable intelligence.

### LLM Invariant — Clarified

The constraint is not "LLM never affects gameplay." It is **"simulation
never depends on LLM."** The DecisionQueue pattern already embodies this.

- **Without LLM:** Rule-based goal selection. Deterministic,
  personality-weighted scoring. Every NPC gets a sensible goal every tick.
  The world is alive but predictable — NPCs behave rationally given their
  knowledge and role.
- **With LLM:** Goal selection gains nuance. A pirate captain considers
  personal grudges, weighs risk against personality, maybe pursues a vendetta
  instead of the highest-value bounty. NPCs become characters rather than
  optimisers. The world is alive and surprising.

The line: **LLM selects goals (the "why"). EpistemicResolver executes them
deterministically (the "how"). Simulation validates outcomes mechanically
(the "what happened").** The LLM can influence what an NPC *wants* to do.
It never influences what *actually happens* when they try.

---

## Original Review Summary (2026-04-04)

Competing LLM rated: 5A Revise, 5B Revise, 5C Solid, 5D Solid, 5E Solid.
5A, 5B (revised), 5C, and 5E survive. 5D dropped per architectural pivot above.

---

## 5A — NPC Knowledge-Gated Decisions (REVISE → Adopted with modifications)

### T-1 Knowledge Concern — Acknowledged, no action needed

The competing LLM correctly notes ShipMovement (system 2) reads knowledge that
was last updated by KnowledgeSystem (system 7) on the previous tick. This is
**already the intended design** — the 1-tick lag is a natural consequence of the
tick pipeline and maps to "yesterday's intelligence" in a 1-day tick. No change
needed; this is a feature, not a bug.

### PortLocationClaim — **Push back**

The competing LLM suggests we need a `PortLocationClaim` so merchants know
where ports are. This is **incorrect for our architecture**. Port geography
(RegionId, adjacency, BaseTravelDays) is structural data on `Region` and `Port`
entities — it represents the physical map, not intelligence. A captain knows
where Havana is; they don't know what prices Havana is offering today. Geography
is innate ground truth; prices, control, prosperity are epistemic. No new claim
type needed.

### OQ1 — ShipHolder as secondary source: **Adopt**

Union IndividualHolder + ShipHolder facts, select highest confidence. Crew
gossip (ShipHolder) is the navigator's log and tavern chatter — captains would
absolutely consult this. Implementation: in the routing query, merge facts from
both holders before scoring candidates.

### OQ2 — No caching: **Adopt**

Direct lookups. ~30-50 NPCs × ~100-200 facts per holder is sub-millisecond on
.NET 10. Premature caching introduces stale-state bugs for negligible gain.
Revisit only if profiler shows RuleBasedDecisionProvider exceeding 2-3ms/tick.

---

## 5B — NPC Quest Participation (REVISE → Revised with NpcGoal architecture)

### NpcContractPursuit → NpcGoal hierarchy

The original proposal used a contract-specific `NpcContractPursuit`. The
architectural pivot replaces this with a general **NpcGoal hierarchy** that
handles any knowledge-driven objective (bounties, trade routing, investigation,
fleeing danger). Same lightweight footprint — a routing directive with a
resolution condition — but not limited to contracts.

Lives on WorldState for cross-system visibility (same tick-ordering rationale
as the original proposal):

```csharp
// WorldState.cs
public Dictionary<IndividualId, NpcGoal> NpcGoals { get; } = new();
```

### Stall & Leak mechanic — **New addition**

When an NPC stalls on a goal (intel confidence decays below threshold, route
blocked, target lost), instead of silent expiry:
1. Goal status transitions to Stalled
2. On next dock, the NPC's knowledge about the stalled goal leaks into port
   gossip (ShipHolder → PortHolder propagation)
3. This creates player opportunity: "Captain Vane abandoned his hunt for the
   Silver Galleon near Jamaica" is actionable intelligence

This is the bridge between NPC failure and player discovery.

### NpcClaimedContract event — **Adopt** (unchanged)

When an NPC successfully resolves a contract goal, emit `NpcClaimedContract`
event. Player's quest expires with status `ClaimedByNpc`.

### OQ3 — No player compensation: **Adopt** (unchanged)

"You were too slow" is the correct experience.

### OQ4 — ShipRoute union type: **Adopt** (unchanged)

```csharp
public abstract record ShipRoute;
public record PortRoute(PortId Destination) : ShipRoute;
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;
public record PoiRoute(OceanPoiId Poi) : ShipRoute;

public ShipRoute? Route { get; set; }  // replaces RoutingDestination + PoiDestination
```

Now also used by the NpcGoal system — a goal determines the ship's Route.

### OQ5 — Eligible roles: **Adopt with modification** (unchanged)

PirateCaptain, NavalOfficer, Privateer + Informant (TargetDead only).
Informant resolution gated by IntelligenceCapability. Smugglers and
PortMerchants excluded.

---

## 5C — Knowledge Conflict as Gameplay (SOLID → Confirmed)

### KnowledgeConflictResolved event — **Adopt**

Good catch. Emit `KnowledgeConflictResolved(SubjectKey, WinningFactId)` when
a conflict auto-resolves or is resolved by investigation. Console and future
UI need this to clean up conflict displays.

### OQ6 — Auto-resolve at spread > 0.40: **Adopt**

When the dominant fact leads by 0.40+ confidence, the weaker fact is discredited
noise. Auto-resolve prevents tedious manual investigation of dead rumours. The
player still needs to investigate *close* conflicts — which is where the
gameplay value actually is.

### OQ7 — ConflictCondition variant: **Adopt**

Extend `QuestCondition` with:

```csharp
public record ConflictCondition(
    Func<KnowledgeConflict, bool> Predicate,
    string Description) : QuestCondition;
```

This queries `KnowledgeConflict` records directly rather than polluting
`KnowledgeStore` with synthetic meta-facts. Clean separation between world
knowledge and meta-knowledge about contradictions.

---

## 5D — Multi-Phase Quest Framework (DROPPED)

**Dropped in architectural pivot (2026-04-05).** Quest templates were already
tried in the codebase and deliberately removed. The surviving
`ContractQuestInstance` — knowledge-triggered, no predefined structure — is the
approach that actually works.

The EpistemicResolver replaces this entirely. "Phases" emerge from knowledge
confidence thresholds, not from a template state machine. See the Architectural
Pivot section above for the full rationale.

OQ8 (fact pinning) and OQ9 (NPC multi-phase eligibility) are moot — there are
no phases to pin facts to, and NPC goal pursuit is handled by the NpcGoal
hierarchy instead of template-gated participation.

---

## 5E — Claim Circulation Improvements (SOLID → Confirmed)

### OQ10 — Event-driven leaks for high-cap factions: **Adopt**

This is a significant improvement over the original RNG-based proposal.
High-capability factions leak intentions **only** under systemic stress:

1. `TreasuryGold < ExpenditurePerTick * 5` → "can't pay their spies" leak
2. `DeceptionExposed` event → "operational security compromised" leak
3. `NavalStrength` drops by > 50% in 10 ticks → "faction in disarray" leak

Each trigger: 15% chance per tick (while condition persists) that one active
Secret `FactionIntentionClaim` downgrades to Restricted in a random controlled
port.

**Player agency loop:** Player raids Spanish gold → treasury crisis → Spain's
secret invasion plans leak to Havana taverns → player discovers and acts. This
is emergent storytelling through mechanics, exactly what the GDD demands.

Low-capability factions (< 0.35) keep the original passive leak mechanic (2%/tick)
as a baseline — they're structurally leaky regardless of events.

---

## Missing Pieces Flagged by Review

| Item | Source | Action |
|---|---|---|
| PortLocationClaim | Competing LLM | **Rejected** — geography is structural, not epistemic |
| NpcGoal on WorldState | Pivot (was NpcContractPursuit) | **Adopted** — generalised goal hierarchy, tick ordering requires WorldState |
| ShipRoute union type | Integration | **Adopted** — replaces PortId? RoutingDestination + PoiDestination |
| NpcClaimedContract event | Integration | **Adopted** — player communication for lost bounties |
| Stall & Leak mechanic | Pivot | **Adopted** — NPC stalls leak intel into port gossip, creates player opportunity |
| EpistemicResolver | Pivot | **Adopted** — unified quest architecture, replaces template state machines |
| LLM goal selection invariant | Pivot | **Adopted** — LLM selects goals, never determines outcomes |
| KnowledgeConflictResolved event | Competing LLM | **Adopted** — UI cleanup signal |
| ConflictCondition variant | Competing LLM | **Adopted** — clean quest trigger for conflicts |
| ~~CurrentPhaseIndex on quest instance~~ | ~~Competing LLM~~ | **Moot** — 5D dropped |
| ~~IsNpcEligible on QuestTemplate~~ | ~~Competing LLM~~ | **Moot** — 5D dropped, NpcGoal handles eligibility |
| Informant assassination mechanic | Integration | **Adopted** — non-naval contract resolution path |
| Event-driven leak triggers | Competing LLM | **Adopted** — replaces flat RNG for high-cap factions |
