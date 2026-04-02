# Ahoy — System Design: Quest System

> **Status:** Living document.
> **Version:** 0.1
> **Depends on:** SDD-KnowledgeSystem.md, SDD-WorldState.md, SDD-Architecture.md

---

## 1. Overview

Quests are the primary vehicle for emergent narrative in Ahoy. Rather than scripted story arcs, quests are **knowledge-triggered story beats** — short, self-contained opportunities that surface when the player's information about the world reaches a threshold of credibility or urgency.

The quest system sits at the intersection of the simulation and narrative layers:

- **The simulation defines what is true** (world state, faction standings, ship positions, port control)
- **The knowledge system defines what the player knows** (facts with confidence scores that decay)
- **The quest system bridges the two** — detecting when the player's knowledge creates a meaningful choice, presenting it, and writing the outcome back into the world as new `WorldEvent`s

### Design Goals

- **Knowledge-gated, not timer-gated** — quests open and close based on the player's information state, not arbitrary timers
- **Outcomes are simulation-real** — every branch choice produces `WorldEvent`s that propagate through the full simulation pipeline; the world reacts
- **Disinformation is first-class** — a quest can be triggered by a planted lie; acting on false intelligence has real consequences
- **Expiry is natural** — if the player ignores a quest, confidence decay closes the window without special expiry logic
- **LLM-augmentable but not LLM-dependent** — all structural logic is deterministic; the LLM renders flavour text and is called at most twice per quest (instantiation + dialogue)

---

## 2. Architecture

The quest system is **System 7** in the simulation tick pipeline, running after `KnowledgeSystem`:

```
Weather(1) → ShipMovement(2) → Economy(3) → Faction(4) →
EventPropagation(5) → Knowledge(6) → Quest(7)
```

This ordering ensures quest trigger evaluation sees fully-updated player knowledge for the current tick, including propagated gossip and newly ingested events.

### Project boundaries

| Component | Project |
|---|---|
| `QuestModels.cs` — templates, instances, condition tree | `Ahoy.Simulation` |
| `QuestStore.cs` — active/history store | `Ahoy.Simulation` |
| `QuestSystem.cs` — tick evaluation | `Ahoy.Simulation` |
| `CaribbeanQuestTemplates.cs` — hardcoded template definitions | `Ahoy.WorldData` |
| Console `quests` / `choose` commands | `Ahoy.Console` |

`Ahoy.WorldData` already depends on `Ahoy.Simulation`, so template definitions can reference all simulation types. `Ahoy.Simulation` does not reference `Ahoy.WorldData` — templates are injected at construction time via `SimulationEngine.BuildEngine(questTemplates: ...)`.

---

## 3. Data Model

### 3.1 QuestTemplate

A `QuestTemplate` is a static definition — the blueprint for a type of quest. It is created once at startup.

```csharp
public sealed class QuestTemplate
{
    public required QuestTemplateId Id { get; init; }
    public required string Title { get; init; }
    public required string Synopsis { get; init; }

    // Condition tree evaluated against the player's non-superseded facts each tick
    public required QuestCondition TriggerCondition { get; init; }

    // Selects the specific facts that caused activation (for display)
    public required Func<IReadOnlyList<KnowledgeFact>, IReadOnlyList<KnowledgeFact>>
        TriggerFactSelector { get; init; }

    public required IReadOnlyList<QuestBranch> Branches { get; init; }

    // Returns true if this instance should expire given current world state
    public required Func<QuestInstance, WorldState, bool> ExpiryPredicate { get; init; }

    // Fallback flavour text (overridden by LLM-generated content on QuestInstance)
    public string? NpcName { get; init; }
    public string? DefaultNpcDialogue { get; init; }

    public FactionId? AssociatedFactionId { get; init; }
    public bool AllowDuplicateInstances { get; init; }
}
```

### 3.2 QuestCondition tree

Trigger conditions are a composable tree of predicates evaluated against the player's live facts:

```
QuestCondition
  ├── FactCondition(Func<KnowledgeFact, bool> Predicate, string Description)
  ├── AndCondition(IReadOnlyList<QuestCondition> Children)
  └── OrCondition(IReadOnlyList<QuestCondition> Children)
```

Examples:
```csharp
// A1 — Bait and Bloodhound
new FactCondition(
    f => f.Claim is ShipLocationClaim && f.Confidence >= 0.60f,
    "ShipLocationClaim with confidence >= 0.60"
)

// Compound example: faction weakened AND player knows their port
new AndCondition([
    new FactCondition(f => f.Claim is FactionStrengthClaim fs
        && fs.NavalStrength < 3, "Faction strength critical"),
    new FactCondition(f => f.Claim is PortControlClaim, "Port control known"),
])
```

### 3.3 QuestBranch

Each branch represents one player choice. The `OutcomeEvents` lambda is evaluated at resolution time (not at trigger time) so it can capture current world state.

```csharp
public sealed class QuestBranch
{
    public required string BranchId { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }

    // Called at resolution time — returns events injected into the tick emitter
    public required Func<WorldState, IReadOnlyList<WorldEvent>> OutcomeEvents { get; init; }

    // If non-null, activates this template on the next tick after resolution
    public QuestTemplateId? NextQuestTemplateId { get; init; }
}
```

### 3.4 QuestInstance

A `QuestInstance` is a live, running quest — one activation of a template.

```csharp
public sealed class QuestInstance
{
    public QuestInstanceId Id { get; init; }         // short display ID
    public required QuestTemplate Template { get; init; }
    public QuestStatus Status { get; set; }           // Active | Completed | Expired | Failed
    public required WorldDate ActivatedDate { get; init; }
    public required IReadOnlyList<KnowledgeFact> TriggerFacts { get; init; }

    // LLM-filled flavour (null until wired — falls back to template defaults)
    public string? LlmNpcName { get; set; }
    public string? LlmDialogue { get; set; }

    public QuestBranch? ChosenBranch { get; set; }
    public WorldDate? ResolvedDate { get; set; }
}
```

### 3.5 QuestStore

`WorldState` holds a `QuestStore` — pure data, mutated by `QuestSystem`.

```csharp
public sealed class QuestStore
{
    public IReadOnlyList<QuestInstance> ActiveQuests { get; }
    public IReadOnlyList<QuestInstance> History { get; }

    public void AddActive(QuestInstance instance);
    public void Resolve(QuestInstance instance);         // moves to history
    public bool HasActiveInstance(QuestTemplateId id);  // prevents re-triggering
    public bool HasCompleted(QuestTemplateId id);       // for chain guards
}
```

---

## 4. Tick Lifecycle

Each tick, `QuestSystem` runs in two phases:

### Phase 1: Expiry check

```
for each active instance:
    if ExpiryPredicate(instance, state) → mark Expired, move to history
```

Expiry predicates typically check:
- `state.Date > instance.ActivatedDate.Advance(N)` — time limit
- `player's trigger fact confidence has decayed below threshold` — natural knowledge decay

### Phase 2: Trigger evaluation

```
for each registered template:
    skip if already has active instance (unless AllowDuplicateInstances)
    evaluate TriggerCondition against player's non-superseded facts
    if true → create QuestInstance, add to ActiveQuests
```

### Branch resolution (pre-tick command processing)

Branch choices are applied as `ChooseQuestBranchCommand` at the **start of the next tick** (standard command pipeline):

```
ChooseQuestBranchCommand(QuestInstanceId, BranchId)
    → find active instance
    → find branch
    → emit branch.OutcomeEvents(state) into TickEventEmitter
    → mark instance Completed, move to history
```

Outcome events flow through the full event pipeline (EventPropagation → Knowledge), ensuring all NPCs, factions, and ports react to the consequence normally.

---

## 5. Quest Templates

### 5.1 Hardcoded templates (v0.1)

Templates are currently static C# definitions in `Ahoy.WorldData/CaribbeanQuestTemplates.cs`. The two implemented templates are:

**A1 — Bait and Bloodhound**
- Trigger: `ShipLocationClaim` with confidence ≥ 0.60
- Branches: intercept / sell intel / ignore
- Expiry: 15 ticks after activation
- Disinformation angle: the ship may be a Q-ship trap (not yet wired — `IsDisinformation` seeding pending)

**A3 — The Governor's Quill**
- Trigger: `IndividualWhereaboutsClaim` with confidence < 0.40 (rumour-grade)
- Branches: deliver to French / sell to English / blackmail
- Expiry: 30 ticks after activation
- Note: trigger requires `IndividualWhereaboutsClaim` facts — not yet seeded by any system (see §8)

### 5.2 Template registry

Templates are registered at composition root (`Program.cs`):

```csharp
var engine = SimulationEngine.BuildEngine(world,
    questTemplates: CaribbeanQuestTemplates.All);
```

`SimulationEngine.BuildEngine` accepts `IReadOnlyList<QuestTemplate>?` and defaults to empty if null, keeping the engine decoupled from world content.

---

## 6. LLM Integration (future)

The quest system is designed to accept LLM-generated content without changing its structural logic.

### Integration point

When `QuestSystem` creates a new `QuestInstance`, it immediately has valid `Template.NpcName` and `Template.DefaultNpcDialogue` fallbacks. Optionally, before adding the instance to `ActiveQuests`, an async LLM call can be made:

```
QuestSystem triggers
    → build prompt: template + current world snapshot (port names, faction standings, prices)
    → LLM call (async, non-blocking)
    → on completion: set instance.LlmNpcName, instance.LlmDialogue
    → instance added to ActiveQuests (with or without LLM content)
```

The LLM receives:
- `QuestTemplate.Synopsis` — the structural intent
- Live world snapshot — actual port names, faction standings, relevant prices, named ships
- Player's trigger facts — what specifically caused the quest to fire

The LLM returns:
- A named NPC (e.g. "María de Alcázar, a half-drunk cartographer")
- Opening dialogue specific to the world state
- Optionally: branch-specific dialogue variations

### Constraint

**The LLM never determines world truth.** It renders flavour text only. All causal consequences flow back as `WorldEvent`s through deterministic code. The LLM is a content renderer, not a world engine.

---

## 7. Composability

### 7.1 Quest chaining

A branch's `NextQuestTemplateId` field allows chains: completing one quest can directly trigger another template's evaluation next tick. This is used for political arcs where one decision opens a follow-on opportunity.

### 7.2 Condition composability

`AndCondition` and `OrCondition` allow complex multi-fact triggers without per-template boilerplate. For example, a quest requiring both knowledge of a port's weakness AND a faction's naval disposition:

```csharp
new AndCondition([
    new FactCondition(f => f.Claim is PortControlClaim pc
        && state.Ports[pc.Port].Prosperity < 30, "Port in crisis"),
    new FactCondition(f => f.Claim is FactionStrengthClaim fs
        && fs.NavalStrength < 5, "Faction weakened"),
])
```

### 7.3 Phase graph (future)

The current model is a flat state machine: `Active → Completed | Expired | Failed`. A future phase graph would model quests as a directed graph of phases (`Verify → Contact → Deliver → Resolve`), with knowledge-conditioned transitions between phases. This is not implemented in v0.1 — flat templates cover the first 12 designed quests adequately.

---

## 8. Known Gaps (v0.1)

| Gap | Impact | Resolution |
|---|---|---|
| `IndividualWhereaboutsClaim` never seeded | Quest A3 never triggers | Seed initial governor location facts in `CaribbeanWorldDefinition`, or emit `IndividualMoved` events when governors travel |
| `IsDisinformation` never set on facts | Disinformation quests can't distinguish planted from genuine facts | Wire disinformation seeding into FactionSystem intelligence operations |
| No `QuestEvent` emitted on trigger/resolution | Frontend can't react to quest lifecycle | Add `QuestActivated` / `QuestResolved` world events, or expose via separate callback |
| Quest ID display uses short prefix | `choose` command requires knowing the ID | Add `choose <n>` shorthand for the nth active quest |
| Superseded facts pruned immediately | LLM can't see "what the player used to believe" | Optionally separate IsSuperseded from pruning; keep superseded for one tick |
| No quest persistence | World restart clears quest history | Requires serialisation layer (not scoped for v0.1) |

---

## 9. Design Reference

The 12 LLM-generated quest examples that drove these design decisions are preserved in `docs/QuestExamples-LLMGenerated.md`. The coverage matrix at the end of that document maps each quest to the claim types, disinformation patterns, and expiry mechanics it exercises.

---

## 10. Open Questions

- Should the player have a persistent quest log showing expired and completed quests with their outcomes?
- Can factions *react* to the player's quest choices — e.g., a faction learns the player sold their governor's letters and retaliates?
- Should quest templates have prerequisite checks beyond knowledge (e.g., player notoriety ≥ threshold, or faction standing)?
- At what point does the phase graph model become necessary? (Likely when quests require multi-day investigation before a choice is presented.)
- Should disinformation quests visually signal their uncertainty, or should the player always be surprised? (Affects UI design significantly.)
