# Decision Record — Group 5 (Knowledge-Driven Living World)

Date: 2026-04-04
Status: Approved for implementation (post competing-LLM review)

## Review Summary

Competing LLM rated: 5A Revise, 5B Revise, 5C Solid, 5D Solid, 5E Solid.
All sub-proposals survive review. Two revisions adopted with modifications,
three solid ratings confirmed. No fundamental rethinks required.

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

## 5B — NPC Quest Participation (REVISE → Adopted with modifications)

### NpcContractPursuit on WorldState — **Adopt**

The competing LLM correctly identifies the tick-ordering problem: if
`NpcContractPursuit` lives only in QuestSystem (system 8), ShipMovementSystem
(system 2) can't route based on it. Move to WorldState:

```csharp
// WorldState.cs
public Dictionary<IndividualId, NpcContractPursuit> NpcContractPursuits { get; } = new();
```

QuestSystem writes pursuits. ShipMovementSystem reads them to set routing.

### ContractFailed event — **Adopt with modification**

Add `NpcClaimedContract` event (not `ContractFailed` — the contract didn't fail,
it succeeded for someone else). The event carries the NPC's identity so the
console can surface: "Captain Vane claimed the bounty on the Silver Galleon."

### OQ3 — No player compensation: **Adopt**

"You were too slow" is the correct experience. The world does not wait. Clear
communication via `NpcClaimedContract` event and `QuestResolved` with status
`ClaimedByNpc` ensures the player understands what happened.

### OQ4 — PursuitRoute variant: **Adopt with modification**

New ShipLocation variant is overkill — `ShipLocation` is a position union
(where you *are*), not a routing intent. The correct place is a new
`RoutingDestination` alternative. Currently `Ship.RoutingDestination` is
`PortId?` — too narrow. Refactor to:

```csharp
// Replace: public PortId? RoutingDestination { get; set; }
// With:
public abstract record ShipRoute;
public record PortRoute(PortId Destination) : ShipRoute;
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;
public record PoiRoute(OceanPoiId Poi) : ShipRoute;  // consolidates existing PoiDestination

public ShipRoute? Route { get; set; }
```

This also lets us retire `PoiDestination` as a separate field. ShipMovementSystem
handles `PursuitRoute` by navigating to `LastKnownRegion` and performing a
proximity check on arrival.

### OQ5 — Eligible roles: **Adopt with modification**

Agree: PirateCaptain, NavalOfficer, Privateer + Informant.

**Informant detail:** Informants are eligible for `TargetDead` contracts only.
Resolution is an abstracted stat-check when the Informant arrives at the target's
port (assassination/sabotage), not naval combat. This is gated by
`IntelligenceCapability` of the Informant's faction — higher capability = higher
success chance. Failure emits `AgentBurned` for the Informant.

Smugglers and PortMerchants excluded — economically motivated, combat-averse.

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

## 5D — Multi-Phase Quest Framework (SOLID → Confirmed)

### CurrentPhaseIndex — **Adopt**

Add `int CurrentPhaseIndex { get; set; }` to quest instance model. Obvious
omission from the original proposal.

### OQ8 — Pin trigger facts: **Adopt with modification**

Agree that re-acquisition is frustrating gameplay. Pin trigger facts by setting
`IsDecayExempt = true` when the quest activates. Revert on quest completion,
failure, or expiry.

**Modification:** Only pin facts that are directly referenced as phase gate
conditions (trigger facts and phase-specific knowledge gates). Don't pin all
related facts — if the player hears a *secondary* rumour about the treasure
site, that rumour should still decay normally. The captain's log records the
quest-critical details, not every overheard whisper.

### OQ9 — Defer NPC multi-phase quests: **Adopt**

Add `bool IsNpcEligible` to `QuestTemplate` (default `false` for multi-phase,
`true` for flat contracts). Autonomous phase navigation requires GOAP or HTN,
which is a fundamentally different NPC architecture. Defer until
`RuleBasedDecisionProvider` is proven insufficient for the game's needs.

**Deferral condition:** Revisit when > 50% of contract quests are being
completed by NPCs and the simulation needs richer NPC-driven narrative beats.

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
| NpcContractPursuit on WorldState | Competing LLM | **Adopted** — tick ordering requires it |
| ShipRoute union type | Integration | **Adopted** — replaces PortId? RoutingDestination + PoiDestination |
| NpcClaimedContract event | Integration | **Adopted** — player communication for lost bounties |
| KnowledgeConflictResolved event | Competing LLM | **Adopted** — UI cleanup signal |
| ConflictCondition variant | Competing LLM | **Adopted** — clean quest trigger for conflicts |
| CurrentPhaseIndex on quest instance | Competing LLM | **Adopted** — obvious model gap |
| IsNpcEligible on QuestTemplate | Competing LLM | **Adopted** — gates NPC quest participation |
| Informant assassination mechanic | Integration | **Adopted** — non-naval contract resolution path |
| Event-driven leak triggers | Competing LLM | **Adopted** — replaces flat RNG for high-cap factions |
