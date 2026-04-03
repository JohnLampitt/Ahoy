
---

## The Prompt

---

# Ahoy — Knowledge & Quest System: Technical Review Brief

## What This Is

Ahoy is a C# / .NET 10 pirate sandbox simulation inspired by Sid Meier's Pirates and Dwarf
Fortress. It is backend-first and frontend-agnostic. The simulation runs as a deterministic
tick engine (1 tick = 1 day). An LLM layer sits above the engine as a *content renderer* —
it generates flavour text and NPC dialogue, but has **no causal authority over the world**.
All mechanical consequences flow through typed WorldEvents and WorldState mutations inside
the engine.

This document describes the current state of two systems — Knowledge and Questing — and
asks for your design critique, suggested improvements, and alternative approaches.

---

## 1. The Knowledge System

### Core Concept

Knowledge is a first-class resource. Every actor in the world — player, merchant captain,
governor, pirate, faction — operates on what they *know*, not on objective truth. Facts
travel with ships, degrade over time and distance, and can be falsified.

### KnowledgeFact

Every piece of world-knowledge is a `KnowledgeFact`:

```
KnowledgeFact {
  Claim            : KnowledgeClaim       // what the fact asserts (discriminated union)
  Sensitivity      : KnowledgeSensitivity // Public / Restricted / Secret
  Confidence       : float [0..1]         // decays 0.015/tick; degrades 0.10/hop
  BaseConfidence   : float                // confidence at creation (immutable reference)
  ObservedDate     : WorldDate            // when the underlying event occurred
  HopCount         : int                  // 0 = directly witnessed; N = N-th retelling
  IsSuperseded     : bool                 // newer fact about same subject exists
  IsDisinformation : bool                 // server-side only; holders cannot see this
  SourceHolder     : KnowledgeHolderId?   // who passed this copy (null = self-witnessed)
  CorroborationCount : int                // independent confirmations (not duplicates)
}
```

### KnowledgeClaim Types (discriminated union)

Eleven claim types exist; six are actively emitted by simulation systems today:

| Claim | Emitted by | Trigger event |
|---|---|---|
| `PortPriceClaim(Port, Good, Price)` | KnowledgeSystem | PriceShifted |
| `PortProsperityClaim(Port, Value)` | KnowledgeSystem | PortProsperityChanged |
| `PortControlClaim(Port, FactionId?)` | KnowledgeSystem | PortCaptured |
| `ShipLocationClaim(Ship, Location)` | KnowledgeSystem + FactionSystem | ShipArrived + disinformation |
| `WeatherClaim(Region, Wind, Storm)` | KnowledgeSystem | StormFormed |
| `IndividualWhereaboutsClaim(Individual, Port?)` | World seeding | (world init + decay) |
| `FactionStrengthClaim(Faction, Naval, Gold)` | FactionSystem | strength change ≥10% |

Five claim types are defined but not yet emitted: `ShipCargoClaim`, `FactionIntentionClaim`,
`RouteHazardClaim`, `CustomClaim`, and more can be added.

### KnowledgeHolderId (who holds knowledge)

```
KnowledgeHolderId =
  | PlayerHolder
  | PortHolder(PortId)        // port's population knowledge pool
  | ShipHolder(ShipId)        // ship's captain/crew collective
  | IndividualHolder(IndividualId)
  | FactionHolder(FactionId)
```

The player is **not special-cased**. `PlayerHolder` is just another holder in the same
union. SourceHolder uses this same type — if the player tells a port something, the port
records `SourceHolder = PlayerHolder`.

### How Facts Spread

Ships carry knowledge like cargo. On **arrival** at a port, the ship deposits facts into
the port's population pool (sensitivity-filtered) and picks up facts from the pool.

Propagation rules:
- 30% chance per fact per ship arrival event
- Each hop: confidence -= 0.10 (stops propagating below 0.10)
- **Corroboration** (new in v0.2): if same claim arrives from a *different* source,
  `CorroborationCount++` instead of creating a duplicate fact
- Supersession: a newer fact about the same subject (matched by subject key) marks the
  old one `IsSuperseded = true` with a 1-tick grace period before pruning

### Disinformation

Factions with high `IntelligenceCapability` can inject `IsDisinformation = true` facts
into port pools. These look identical to genuine facts from the holder's perspective.
Discovery is through:
- Contradiction (player holds two conflicting claims about the same subject)
- Failed action (sailed to the "unguarded" port, found a patrol)
- Direct investigation (player physically travels to the location → self-witnessed
  `HopCount=0, SourceHolder=null` fact that either corroborates or contradicts)

### How Knowledge Impacts World Reality

This is the key design goal — facts don't just sit in a store. They drive behaviour:

1. **NPC routing**: Merchant captains query their held `PortPriceClaim` facts when
   scoring destinations. A merchant who has heard (even inaccurately) that Port Royal
   has high sugar prices will sail there. Confidence-weighted scoring.

2. **Faction decisions**: FactionSystem evaluates goals (ExpandTerritory, BuildNavy, etc.)
   using world state *and* knowledge facts. A faction that believes (via low-confidence
   FactionStrengthClaim) that an enemy is weak may pursue expansion goals.

3. **Quest triggers**: The QuestSystem (System 7 in the tick pipeline) evaluates
   registered templates against the *player's* non-superseded knowledge. A quest only
   activates when the player knows something actionable.

4. **Player snapshot**: The WorldSnapshot shown to any frontend filters observable ships
   and ports by the player's held facts. The player only "sees" ships they have
   knowledge of (above a confidence threshold).

---

## 2. The Quest System

### Architecture

QuestSystem is System 7, running after KnowledgeSystem each tick. It:
1. Expires active quests whose `ExpiryPredicate` fires
2. Evaluates all registered `QuestTemplate` trigger conditions against player facts
3. Instantiates `QuestInstance` for any newly triggered templates

Branch resolution (player choice) is handled by `SimulationEngine.ApplyCommand` outside
the tick pipeline.

### Condition Tree

Trigger conditions are a composable tree:

```
QuestCondition =
  | FactCondition(Predicate: KnowledgeFact → bool, Description: string)
  | AndCondition(Children: QuestCondition[])
  | OrCondition(Children: QuestCondition[])
```

Examples:
- A1 fires when the player holds *any* `ShipLocationClaim` with `Confidence >= 0.60`
- B3 fires when the player holds a `ShipLocationClaim` AND a `FactionStrengthClaim`
  (two-fact AND condition)
- B6 fires when player holds `IndividualWhereaboutsClaim` OR `WeatherClaim`

### QuestBranch

```
QuestBranch {
  BranchId             : string
  Label                : string
  Description          : string
  AvailabilityCondition: (playerFacts → bool)?    // hide branch if condition is false
  OutcomeEvents        : (WorldState → WorldEvent[])  // immediate world events
  OutcomeActions       : QuestOutcomeAction[]         // declarative world-state mutations
}

QuestOutcomeAction =
  | SupersedeTriggerFacts()                           // mark trigger facts superseded
  | AddKnowledgeFact(Claim, Confidence, Sensitivity, Holders[])
  | EmitRumourAction(Text, PortSelector)
```

### Variety Mechanism

`QuestTemplate.TitleFactory: (triggerFacts → string)?` lets a template derive its
display title from the specific facts that triggered it. Example:
- Template "Bait and Bloodhound" produces "Bait and Bloodhound — San Cristóbal"
  when the triggering `ShipLocationClaim.Ship` resolves to that ship name.

Combined with `AllowDuplicateInstances = true`, a single template can fire multiple
times for different entities (different ships, different governors), giving combinatorial
variety from a small set of templates.

### The 12 Quest Templates (April 2026 design)

All 12 are now implemented:

| ID | Title | Trigger claim type | Condition | Disinformation? |
|---|---|---|---|---|
| A1 | Bait and Bloodhound | ShipLocation ≥ 0.60 | FactCondition | ✓ central |
| A2 | A Squeeze on Sweetness | PortPrice > 200 (Sugar) | FactCondition | ✓ |
| A3 | The Governor's Quill | IndividualWhereabouts < 0.40 | FactCondition | — |
| A4 | Rot in the Timbers | FactionStrength low | FactCondition | ✓ |
| A5 | Eye of the Tempest | WeatherClaim ≥ 0.50 | FactCondition | ✓ |
| A6 | The Bloodless Coup | PortControl < 0.45 conf | FactCondition | ✓ |
| B1 | The Governor's Shadow | PortControl (high) AND IndividualWhereabouts (low) | AndCondition | ✓ |
| B2 | The Sugar Windfall | PortPrice > 200 (Sugar), 3-branch | FactCondition | — |
| B3 | The Vanishing Frigate | ShipLocation AND FactionStrength | AndCondition | ✓ |
| B4 | A Cure in Doubt | IndividualWhereabouts < 0.35 | FactCondition | — |
| B5 | The Silent Convoy | ShipLocation < 0.55 (low conf) | FactCondition | ✓ central |
| B6 | The Map That Wasn't | IndividualWhereabouts OR WeatherClaim | OrCondition | ✓ central |

---

## 3. LLM Integration Point

The LLM's role is deliberately constrained:

- **Input**: a `KnowledgeFact` bundle (the quest's trigger facts with provenance: confidence,
  hops, source holder, corroboration count) + template metadata + world name context
- **Output**: NPC display name, dialogue/flavour text for the quest hook
- **Not allowed**: the LLM cannot choose branches, emit events, or mutate world state
- **Wiring**: `QuestInstance.LlmNpcName` and `QuestInstance.LlmDialogue` are nullable
  slots filled asynchronously; the sync fallback (`Template.NpcName`, `Template.Synopsis`)
  always renders immediately

This means: the LLM sees the *epistemic state* of the player (what they know, how
confidently, via what chain) and produces narrative that reflects that uncertainty. A
0.20-confidence rumour should sound like a rumour. A 0.90-confidence direct observation
should sound like firsthand knowledge.

---

## 4. Known Gaps and Open Questions

For your review, these are the currently identified gaps:

1. **Investigation mechanic not yet wired**: The design says sailing to a relevant
   location should produce a `HopCount=0, SourceHolder=null` observation that corroborates
   or contradicts held facts. Not yet triggered by any system.

2. **Knowledge trading not implemented**: Players cannot buy/sell facts from NPCs or
   information brokers yet. The `KnowledgeStore` model supports it but no UI/command exists.

3. **Contradiction flagging incomplete**: When two conflicting active facts exist for
   the same subject key, both coexist but are not yet explicitly flagged as contradicting
   each other (planned `IsContradicted` field).

4. **NPC decision quality**: Merchant routing uses knowledge facts (PortPriceClaim) but
   the weighting is rough (confidence not fully incorporated). Faction decisions don't
   yet query the knowledge store directly.

5. **`FactionIntentionClaim` never emitted**: The claim type exists but no system
   produces it. Could enable quests around "I know the Spanish plan to attack Port Royal."

6. **Sensitivity-gated spreading**: The design calls for `Restricted`/`Guarded`/`Secret`
   facts to spread only with relationship context. Currently all facts above `0.10`
   confidence propagate with the same 30% base rate.

---

## 5. Questions for Review

1. **Confidence curve**: We use linear decay (0.015/tick) and linear hop penalty (0.10).
   Should either be non-linear? What decay function best models information entropy in a
   gossip network?

2. **Corroboration mechanics**: Currently corroboration just increments a counter. Should
   it boost `Confidence` directly? If so, by how much, and should there be diminishing
   returns?

3. **Contradiction resolution**: When a player holds two contradicting facts (same subject,
   different claims), how should the system help them reason about which is true? Options:
   - Simple display flag (`[CONTRADICTED]`)
   - Confidence-weighted resolution (higher confidence "wins" in display)
   - Force investigation before the contradiction resolves
   - Surface to LLM as a narrative opportunity ("you've heard conflicting reports…")

4. **Quest condition expressivity**: The current condition tree is purely knowledge-based
   (player's fact store). Should conditions also be able to test world state directly
   (e.g. "player is in region X", "player has > 500 gold")? Or does mixing knowledge
   and world-state conditions undermine the epistemic model?

5. **Disinformation detection design**: How should a player ever *knowingly* verify that
   a fact is disinformation, beyond "I sailed there and it was wrong"? Is there a
   satisfying mechanical loop that doesn't break the simulation's epistemic honesty?

6. **Template parameterisation vs procedural generation**: We currently use `TitleFactory`
   to interpolate entity names into a fixed template. Could the condition tree itself be
   parameterised (e.g. "trigger on any FactionStrengthClaim where Faction matches the
   faction that controls the player's home port")? What's the right level of abstraction
   before templates become too complex to author?

7. **LLM prompt shape**: Given a trigger fact bundle like:
   ```
   ShipLocationClaim(San Cristóbal, AtSea(Western Caribbean))
   Confidence: 0.67  BaseConfidence: 0.72  Hops: 2  Corroboration: 1
   Source: PortHolder(Tortuga)  ObservedDate: 1683-03-29
   ```
   What narrative framing produces the best quest hook? How much mechanical detail
   (confidence numbers, hop count) should be exposed to the LLM vs. translated to
   qualitative language ("a well-travelled rumour", "secondhand intelligence")?
