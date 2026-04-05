# Ahoy — System Design: Quest System

> **Status:** Living document.
> **Version:** 0.2 — rewritten for EpistemicResolver + NpcGoal architecture (v0.1 template system removed)
> **Depends on:** SDD-KnowledgeSystem.md, SDD-WorldState.md, SDD-Architecture.md

---

## 1. Overview

Quests are the primary vehicle for emergent narrative in Ahoy. Rather than scripted story arcs or predefined templates, quests are **knowledge-triggered story beats** — opportunities that surface when an actor's knowledge about the world reaches a threshold of credibility or urgency.

The quest system sits at the intersection of the simulation and narrative layers:

- **The simulation defines what is true** (world state, faction standings, ship positions, port control)
- **The knowledge system defines what actors know** (facts with confidence scores that decay)
- **The quest system bridges the two** — detecting when knowledge creates a meaningful opportunity, enabling both players and NPCs to act on it

### Design Goals

- **Knowledge-gated, not timer-gated** — opportunities open and close based on epistemic state, not arbitrary timers
- **Outcomes are simulation-real** — every action produces `WorldEvent`s that propagate through the full simulation pipeline
- **Unified player/NPC model** — the same epistemic mechanisms drive both player quests and NPC goal pursuit
- **Disinformation is first-class** — a quest can be triggered by a planted lie; acting on false intelligence has real consequences
- **Expiry is natural** — if an actor ignores an opportunity, confidence decay closes the window without special expiry logic
- **LLM-augmentable but not LLM-dependent** — all structural logic is deterministic; the LLM influences NPC goal *selection* and renders narrative flavour

### Design History

v0.1 used a template-based architecture (`QuestTemplate`, `QuestBranch`, `QuestCondition` tree, `QuestInstance`, `QuestStore`). This was implemented, tested, and **deliberately removed** — templates produced predictable, scripted-feeling behaviour rather than emergent narrative. The surviving `ContractQuestInstance` (knowledge-triggered, no predefined structure) demonstrated that the knowledge system's confidence thresholds are sufficient to create quest-like progression without templates.

v0.2 generalises this insight: the EpistemicResolver pattern replaces templates entirely. "Phases" emerge from knowledge confidence gaps, not predefined state machines.

---

## 2. Architecture

The quest system is **System 8** in the simulation tick pipeline, running after `KnowledgeSystem`:

```
Weather(1) → ShipMovement(2) → Economy(3) → Faction(4) →
IndividualLifecycle(5) → EventPropagation(6) → Knowledge(7) → Quest(8)
```

This ordering ensures quest evaluation sees fully-updated knowledge for the current tick, including propagated gossip and newly ingested events.

### Project boundaries

| Component | Project |
|---|---|
| `QuestModels.cs` — contract quest instances | `Ahoy.Simulation/Quests` |
| `QuestStore.cs` — active/history store with cooldowns | `Ahoy.Simulation/Quests` |
| `GoalModels.cs` — NpcGoal hierarchy + GoalPursuit state machine | `Ahoy.Simulation/State` (new) |
| `QuestSystem.cs` — tick evaluation, player quests + NPC goal assignment | `Ahoy.Simulation/Systems` |
| Console `knowledge` / `quests` commands | `Ahoy.Console` |

---

## 3. Data Model

### 3.1 Player Quests — ContractQuestInstance

Player quests remain `ContractQuestInstance` — knowledge-triggered, flat lifecycle:

```csharp
public sealed class ContractQuestInstance
{
    public QuestInstanceId Id { get; init; } = QuestInstanceId.New();
    public required KnowledgeFactId ContractFactId { get; init; }
    public required ContractClaim Contract { get; init; }
    public required WorldDate ActivatedDate { get; init; }
    public ContractQuestStatus Status { get; set; } = ContractQuestStatus.Active;
    public string? NarrativePromptFragment { get; set; }
    public WorldDate? ResolvedDate { get; set; }
}

public enum ContractQuestStatus { Active, LostTrail, Fulfilled, TargetGone, Expired, ClaimedByNpc }
```

**Activation:** `ContractClaim` in `PlayerHolder` with confidence > 0.40 + separate intel fact about `TargetSubjectKey` with confidence > 0.50. `GoodsDelivered` contracts are self-validating (the contract *is* the intel).

**Lifecycle:** `Active → Fulfilled | Expired | LostTrail → Active | ClaimedByNpc`. No predefined phases — the player's progression is driven by their knowledge state:

| Confidence | Epistemic State | Player Experience |
|---|---|---|
| < 0.30 | Noise | Below awareness threshold |
| 0.30–0.50 | Rumour | Investigation available, not yet actionable |
| 0.50–0.70 | Actionable intel | Quest activates — can act, but risk of bad info |
| 0.70–0.90 | Verified intelligence | High confidence — reliable basis for action |
| > 0.90 | Direct observation | Ground truth equivalent |

### 3.2 NPC Goals — NpcGoal + GoalPursuit

NPC objectives are modelled as a separated goal/pursuit pair:

```csharp
// ---- The Behavioural Objective (immutable) ----

public abstract record NpcGoal(Guid Id, IndividualId NpcId);

public record FulfillContractGoal(
    Guid Id, IndividualId NpcId,
    ContractClaim Contract) : NpcGoal(Id, NpcId);

// Future: TradeGoal, InvestigateGoal, FleeGoal, EscortGoal, etc.

// ---- The Execution State Machine ----

public enum PursuitState { Pondering, Active, Stalled, Completed, Abandoned }

public sealed class GoalPursuit
{
    public required NpcGoal ActiveGoal { get; init; }
    public PursuitState State { get; set; } = PursuitState.Active;
    public int TicksStalled { get; set; }
    public required int ActivatedOnTick { get; init; }
}
```

**WorldState:**
```csharp
public Dictionary<IndividualId, GoalPursuit> NpcPursuits { get; } = new();
```

Lives on `WorldState` so `ShipMovementSystem` (system 2) can read pursuits when setting NPC routes, even though `QuestSystem` (system 8) creates them.

### 3.3 ShipRoute Union Type

Consolidates routing intent into a single discriminated union, replacing `PortId? RoutingDestination` + `OceanPoiId? PoiDestination`:

```csharp
public abstract record ShipRoute;
public record PortRoute(PortId Destination) : ShipRoute;
public record PursuitRoute(ShipId Target, RegionId LastKnownRegion) : ShipRoute;
public record PoiRoute(OceanPoiId Poi) : ShipRoute;

// On Ship:
public ShipRoute? Route { get; set; }
```

`PursuitRoute` navigates to `LastKnownRegion` and performs a proximity check on arrival for interception. The active `GoalPursuit` determines which Route variant to set.

### 3.4 QuestStore

`WorldState` holds a `QuestStore` — pure data, mutated by `QuestSystem`:

```csharp
public sealed class QuestStore
{
    public IReadOnlyList<ContractQuestInstance> ActiveContractQuests { get; }
    public IReadOnlyList<ContractQuestInstance> History { get; }

    public void AddActive(ContractQuestInstance instance);
    public void Resolve(ContractQuestInstance instance);
    public bool HasActiveContractQuest(string targetSubjectKey);
    public bool IsOnCooldown(string targetSubjectKey, WorldDate now);
    public void RecordCooldown(string targetSubjectKey, WorldDate until);
}
```

---

## 4. Tick Lifecycle

Each tick, `QuestSystem` runs in three phases:

### Phase 1: Player quest maintenance

```
for each active ContractQuestInstance:
    check fulfillment (TargetDestroyed, TargetDead, GoodsDelivered)
    check LostTrail (intel confidence < 0.50)
    check Expired (contract confidence < 0.20)
    check ClaimedByNpc (NPC completed same contract)
```

### Phase 2: Player quest activation

```
for each ContractClaim in PlayerHolder with confidence > 0.40:
    skip if active quest exists for this target
    skip if on cooldown
    check intel gate (separate fact about target with confidence > 0.50)
    if satisfied → create ContractQuestInstance
```

### Phase 3: NPC goal assignment & pursuit tick

```
for each NPC with GoalPursuit in Pondering state:
    assign goal via rule-based scoring (immediate)
    dispatch async LLM request for potential override (see §6)

for each NPC with GoalPursuit in Active state:
    evaluate via EpistemicResolver (see §5)

for each NPC with GoalPursuit in Stalled state:
    increment TicksStalled
    if TicksStalled > 14 → Abandoned, emit NpcPursuitAbandoned
```

---

## 5. EpistemicResolver — Goal Execution

The EpistemicResolver is the pattern by which NPC goals are executed. It follows the epistemic breadcrumb trail: **what does the NPC know? What do they need to know? What's the next primitive action to close that gap?**

The resolver doesn't know what kind of goal it's executing — it evaluates the NPC's knowledge state against the goal's requirements and returns the next action.

### For FulfillContractGoal:

1. **NPC knows target location** (high confidence `ShipLocationClaim`) → set `PursuitRoute`, state remains Active
2. **NPC lacks target location** → trigger `InvestigateRemoteCommand` if NPC has gold, otherwise Stall
3. **NPC in same region as target** → attempt resolution (stat check)
4. **Informant TargetDead resolution:** abstracted stat-check gated by faction's `IntelligenceCapability`. Failure emits `AgentBurned`.
5. **Target destroyed/dead by NPC** → emit `NpcClaimedContract`, pay NPC, state Completed
6. **Intel confidence drops below 0.30** → state Stalled

### Stall & Leak mechanic

When a `GoalPursuit` transitions to Abandoned:

1. Emit `NpcPursuitAbandoned` event
2. `KnowledgeSystem` identifies the highest-confidence fact the NPC held regarding that goal
3. Inject that fact into the `PortHolder` where the NPC is currently docked
4. This generates tavern gossip: "Captain Vane abandoned his hunt for the Silver Galleon near Jamaica"
5. Player (or other NPCs) can discover and act on this leaked intelligence

**AtSea edge case:** If abandonment triggers while the NPC is AtSea or EnRoute, the leak is deferred — the fact is held in ShipHolder and injected into the next PortHolder the NPC docks at. This is consistent with existing ship-carried gossip propagation: the `ArrivedThisTick` flag provides the trigger point.

---

## 6. LLM Integration

The LLM participates at two distinct points, neither of which the simulation depends on.

### 6.1 NPC Goal Selection (Pondering state)

When an NPC enters `Pondering` state (spawn, goal completed, goal abandoned), QuestSystem dispatches an async request via `DecisionQueue`.

**Prompt payload** — the engine synthesises the NPC's epistemic reality:
- **Identity:** Role, PersonalityTraits (Greed, Boldness, Cunning, Loyalty)
- **Knowledge:** Top 5 highest-confidence IndividualHolder facts
- **Resources:** Ship status (hull, crew, cargo), CurrentGold
- **Context:** Current location, active knowledge conflicts

**Async callback:** LLM returns structured decision (e.g., `{"GoalType": "FulfillContract", "TargetSubject": "Ship:1234"}`). Callback queues the goal mutation for the next tick, transitioning Pondering → Active.

**Fallback guarantee:** Rule-based scoring assigns a goal immediately on the same tick (synchronous). LLM response can override on the next tick. World never stalls.

### 6.2 Player Quest Narrative (optional)

When `QuestSystem` creates a new `ContractQuestInstance`, it generates a `NarrativePromptFragment` from the contract's archetype and world context. This is a structural description the LLM can use to generate flavour text. The LLM is a content renderer — it does not determine quest structure or outcomes.

### 6.3 LLM Invariant

**The LLM selects goals (the "why"). The EpistemicResolver executes them deterministically (the "how"). The simulation validates outcomes mechanically (the "what happened").** The LLM can influence what an NPC *wants* to do. It never influences what *actually happens* when they try.

Without LLM: NPCs behave rationally given their knowledge and role. The world is alive but predictable.
With LLM: NPCs become characters rather than optimisers. The world is alive and surprising.

---

## 7. NPC Competition

The player competes with NPCs for bounties. If an NPC fulfils a contract first, the player's quest expires with status `ClaimedByNpc`. The `NpcClaimedContract` event carries the NPC's identity so the console can surface: "Captain Vane claimed the bounty on the Silver Galleon."

Player receives no compensation — "you were too slow" is the intended experience. The world does not wait.

The player can also sell intel to NPCs (via existing `SellFactCommand`) to point them at targets — or misdirect them with bad intel.

### Eligible NPC roles

| Role | Contract Types | Resolution |
|---|---|---|
| PirateCaptain | TargetDestroyed | Naval combat (stat check) |
| NavalOfficer | TargetDestroyed | Naval combat (stat check) |
| Privateer | TargetDestroyed | Naval combat (stat check) |
| Informant | TargetDead only | Assassination stat-check gated by IntelligenceCapability |

Smugglers and PortMerchants excluded — economically motivated, combat-averse.

---

## 8. Known Gaps

| Gap | Impact | Resolution |
|---|---|---|
| `GoalModels.cs` not yet created | NPC goal pursuit not functional | Implement as part of Group 5B |
| `ShipRoute` union type not yet implemented | Routing still uses `PortId? RoutingDestination` + `OceanPoiId?` | Implement as part of Group 5B |
| `NpcPursuitAbandoned` event not defined | Stall & Leak mechanic not functional | Add to WorldEvent hierarchy |
| `NpcClaimedContract` event not defined | Player not notified of NPC competition | Add to WorldEvent hierarchy |
| `ClaimedByNpc` not in `ContractQuestStatus` enum | Player quest can't reflect NPC completion | Add enum variant |
| LLM goal selection prompt not implemented | NPCs use rule-based scoring only | Deferred — implement after rule-based path proven |
| No quest persistence | World restart clears quest history | Requires serialisation layer (not scoped) |

---

## 9. Design Reference

The 12 LLM-generated quest examples that informed early design are preserved in `docs/QuestExamples-LLMGenerated.md`. While the template system they were designed for has been removed, the quest *themes* (disinformation, competing intelligence, faction manipulation) remain valid design targets for the EpistemicResolver approach.

---

## 10. Open Questions

- Should the player have a persistent quest log showing expired and completed quests with their outcomes?
- Can factions react to NPC goal pursuit outcomes — e.g., a faction learns its bounty was claimed and adjusts strategy?
- Should NPC goal selection consider faction-level strategic context (e.g., faction under threat → defensive goals preferred)?
- Should disinformation quests visually signal their uncertainty, or should the player always be surprised?
- What additional NpcGoal subtypes are needed beyond `FulfillContractGoal`? (TradeGoal, InvestigateGoal, FleeGoal are candidates.)
