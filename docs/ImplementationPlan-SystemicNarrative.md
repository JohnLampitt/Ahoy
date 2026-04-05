# Implementation Plan — Systemic Narrative Engine (Group 6)

Date: 2026-04-05

## Motivation

Groups 1–5 built the epistemic infrastructure and NPC goal pursuit. NPCs act
on what they know, pursue contracts, stall, and leak intel. But all contracts
originate from faction-level strategic goals — no NPC has personal motivations.
There are no grudges, no vendettas, no alliances between individuals.

The Systemic Narrative Engine adds **micro/personal** contract origination
alongside the existing **macro/strategic** path. Both output the same
`ContractClaim` into the KnowledgeStore. The EpistemicResolver handles them
uniformly.

### Dual-Origin Contract Pipeline

```
Macro (Faction AI)  → Strategic goals (War, Famine, Expansion)
                    → ContractClaim seeded into ports
                    ↘
                      KnowledgeStore → EpistemicResolver → GoalPursuit
                    ↗
Micro (Individual)  → Relationship state (Grudge, Debt, Alliance)
                    → ContractClaim seeded from personal wealth
```

---

## Gap Analysis

### G7 — No NPC-to-NPC Relationships (Critical)

**Current state:** `Individual.PlayerRelationship` tracks one NPC's view of
the player. `Faction.Relationships` tracks diplomatic standings between factions.
No individual NPC has opinions about any other individual NPC.

**Problem:** Vendettas, alliances, patronage, and betrayal between NPCs are
impossible. The simulation produces no inter-NPC drama.

### G8 — No Deed Ledger (Critical)

**Current state:** `PlayerActionClaim` records that the player completed a quest
branch. No claim type records "Actor X did Y to Target Z." Events like
`ShipRaided`, `BribeAccepted`, `AgentBurned` fire and modify state directly,
but leave no epistemic trace that other NPCs can discover and react to.

**Problem:** Gossip about deeds can't propagate. An NPC who raids a merchant
in the Gulf of Mexico should become notorious in Havana taverns — but there's
no knowledge artifact to carry that reputation.

### G9 — No Personal Goal Generation (High)

**Current state:** NPC goals come only from scanning for faction-seeded
`ContractClaim` facts (5B-3). No NPC generates a contract from personal
grievance or opportunity.

**Problem:** All narrative flows from faction strategy. No personal stories
emerge between individuals.

### G10 — No Reconciliation Path (Medium)

**Current state:** `Individual.PlayerRelationship` is modified directly by
code in `SimulationEngine.cs` (bribes, burns, fraud). No systemic mechanism
to repair broad reputation damage except per-governor bribery.

**Problem:** A single hostile act can cascade into faction-wide hostility
with no proportionate repair mechanic.

---

## Proposed Implementation

### Group 6A — IndividualActionClaim (Deed Ledger)

**New claim type:**

```csharp
// KnowledgeStore.cs

public enum ActionSeverity { Nuisance = 15, Significant = 35, Severe = 60, Heroic = 100 }
public enum ActionPolarity { Hostile = -1, Friendly = 1 }

public record IndividualActionClaim(
    IndividualId ActorId,
    IndividualId TargetId,
    IndividualId? BeneficiaryId,  // Who ordered/paid for it (faction governor, etc.)
    ActionPolarity Polarity,
    ActionSeverity Severity,
    string Context               // "Raided merchant vessel", "Paid bribe", etc.
) : KnowledgeClaim;
```

**Subject key:** `IndividualAction:{ActorId}:{TargetId}:{Context-hash}`

**Seeding points** — each existing event that implies culpability gets an
IndividualActionClaim emitted alongside the event's existing knowledge effects:

| Event | Actor | Target | Polarity | Severity |
|---|---|---|---|---|
| `ShipRaided` | Attacker captain | Target captain | Hostile | Significant |
| `ShipDestroyed` (with attacker) | Attacker captain | Target captain | Hostile | Severe |
| `BribeAccepted` | Player | Governor | Friendly | Nuisance |
| `AgentBurned` | Player (or burning agent) | Burned agent | Hostile | Severe |
| `ContractFulfilled` | Fulfiller | Issuer | Friendly | Significant |
| `NpcClaimedContract` | NPC pursuer | Issuer | Friendly | Significant |
| Future: `GoodsDelivered` to famine port | Deliverer | Port governor | Friendly | Heroic |

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs` — new cases in
`IngestEvent` switch.

**Propagation:** IndividualActionClaims propagate as Public sensitivity.
They are gossip — "Did you hear what Captain Vane did to that merchant?"
Confidence decays normally; distant deeds become rumours.

### Group 6B — Relationship Matrix

**On WorldState:**

```csharp
// WorldState.cs
// Key: (ObserverId, SubjectId). Value: -100..+100.
// Sparse — only pairs that have interacted or received gossip about each other.
public Dictionary<(IndividualId, IndividualId), float> RelationshipMatrix { get; } = new();
```

**Replaces:** `Individual.PlayerRelationship` is subsumed. The player gets an
`IndividualId` (already exists as a concept via `PlayerState`), and player
relationships become entries in the matrix like any other actor.

**Migration:** Existing `PlayerRelationship` values are seeded into the matrix
at world init. `EffectiveAffinity` in `RuleBasedDecisionProvider` reads from
the matrix instead of `Individual.PlayerRelationship`.

**Performance:** Sparse storage. With ~50 NPCs, worst case is ~2500 entries
(250KB). In practice, most NPCs only have relationships with 5–10 others.
No concern.

**Helper methods on WorldState:**

```csharp
public float GetRelationship(IndividualId observer, IndividualId subject)
    => RelationshipMatrix.TryGetValue((observer, subject), out var r) ? r : 0f;

public void AdjustRelationship(IndividualId observer, IndividualId subject, float delta)
{
    var key = (observer, subject);
    RelationshipMatrix.TryGetValue(key, out var current);
    RelationshipMatrix[key] = Math.Clamp(current + delta, -100f, 100f);
}
```

### Group 6C — Consequence Math (Epistemic Relationship Mutation)

**When:** KnowledgeSystem ingests an `IndividualActionClaim` into any holder.
If the holder is an `IndividualHolder`, the holding individual updates their
relationship with the Actor (and optionally the Beneficiary).

**Formula:**

```
Δ = P × S × C × M_trait
```

Where:
- `P`: ActionPolarity (+1 or -1)
- `S`: ActionSeverity weight (15/35/60/100), normalised to 0..1 by dividing by 100
- `C`: Confidence of the KnowledgeFact (0.0 to 1.0) — distant rumours carry
  less relationship weight
- `M_trait`: Personality modifier:
  - Hostile acts: `1.0 + (observer.Personality.Loyalty * 0.3)` — loyal NPCs
    hold grudges harder
  - Friendly acts: `1.0 + (observer.Personality.Greed * -0.3)` — principled
    NPCs value kind acts more (Greed is negative = principled)

**Special case — Beneficiary:** If the claim has a BeneficiaryId, the observer
also adjusts their relationship with the Beneficiary at 50% of the delta.
"The governor who ordered the hit is half as guilty as the captain who did it."

**Special case — Factional loyalty:** If the observer shares a faction with
the Target, apply a 1.5× multiplier. "He attacked one of ours."

**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs` — new method
`ApplyRelationshipConsequences` called during fact propagation when the
receiving holder is an IndividualHolder.

### Group 6D — Personal Goal Generation

**File:** `src/Ahoy.Simulation/Systems/IndividualLifecycleSystem.cs`

New method `EvaluatePersonalGoals(Individual, WorldState)` called during the
tick, after tour movement.

#### Vengeance Trigger

```
if GetRelationship(npc, nemesis) < -75
   && npc.CurrentGold >= 200
   && npc.Role is Governor or PirateCaptain or NavalOfficer:

   Deduct 200 gold from npc
   Seed ContractClaim into PortHolder(npc.LocationPortId):
     IssuerId = npc.Id
     IssuerFactionId = npc.FactionId
     TargetSubjectKey = "Individual:{nemesisId}" or "Ship:{nemesisShipId}"
     Condition = TargetDead or TargetDestroyed
     GoldReward = 200
     Archetype = PirateRival or UnderworldHit
```

The ContractClaim enters the knowledge economy like any faction contract. Any
NPC (or the player) who discovers it can pursue it through the existing
EpistemicResolver pipeline.

#### Extortion Trigger

```
if npc holds KnowledgeFact with Sensitivity.Secret
   && fact.Claim implicates a wealthy target (IndividualActionClaim or AllegianceClaim)
   && GetRelationship(npc, target) < 20  // not a friend
   && npc.Role is Informant or KnowledgeBroker or PirateCaptain:

   Create ExtortGoal(targetId, secretFactId)
   → EpistemicResolver: route to target's port, emit ExtortionAttempt event
   → Target responds via RuleBasedDecisionProvider (or LLM): pay or refuse
   → Pay: gold transfer + relationship +15 (uneasy truce)
   → Refuse: NPC leaks the secret fact to PortHolder (public exposure)
```

**New NpcGoal subtype:**

```csharp
public record ExtortGoal(
    Guid Id, IndividualId NpcId,
    IndividualId TargetId,
    KnowledgeFactId LeverageFactId) : NpcGoal(Id, NpcId);
```

#### Patronage Trigger

```
if GetRelationship(npc, ally) > +50
   && npc.CurrentGold >= 100
   && npc.Role is Governor or KnowledgeBroker:

   Seed exclusive high-confidence ContractClaim or price intelligence
   directly into IndividualHolder(ally)
   → "The governor slipped you a tip about a vulnerable Spanish convoy"
```

This isn't a new goal — it's a direct knowledge injection during the tick.
The ally receives actionable intel that other NPCs don't have, creating
information advantage as a reward for good relationships.

### Group 6E — Reconciliation Valve

Two systemic paths to repair relationships:

#### PardonClaim (Economic Valve)

**New claim type:**

```csharp
public record PardonClaim(
    IndividualId GrantedBy,      // The governor/official who issued it
    FactionId Faction,            // The faction whose hostilities are pardoned
    IndividualId PardonedActor    // Who receives the pardon
) : KnowledgeClaim;
```

**Mechanic:** Player purchases a pardon from a corrupt governor (gold sink
proportional to accumulated hostility). The `PardonClaim` propagates through
the knowledge system. When an NPC ingests a `PardonClaim`:
- All `IndividualActionClaim` facts about the pardoned actor held by NPCs of
  that faction are marked `IsDecayExempt = false` (accelerated forgetting)
- The NPC's relationship with the pardoned actor shifts toward 0 by 50%
  (not full reset — the grudge fades but doesn't vanish instantly)

**Gate:** Governor's Authority must be > 60 (they need political weight to
issue pardons). Governor relationship with player must be > -20 (won't pardon
someone they personally hate).

#### Heroic Acts (Behavioral Valve)

No new mechanic needed — `IndividualActionClaim` with `ActionPolarity.Friendly`
and `ActionSeverity.Heroic` naturally produces a large positive relationship
delta via the consequence math. The player resolves a crisis affecting their
nemesis, and the math does the rest.

The key is ensuring enough crisis events exist to create heroic opportunities.
This is where the "systemic crises" design pass comes in (deferred to after
implementation).

---

## Sequencing

```
Group 6A (IndividualActionClaim)
  New claim type + seeding in KnowledgeSystem.IngestEvent
  ↓
Group 6B (Relationship Matrix)
  WorldState.RelationshipMatrix + migration from Individual.PlayerRelationship
  ↓
Group 6C (Consequence Math)
  KnowledgeSystem applies relationship deltas on IndividualActionClaim ingestion
  ↓
Group 6D (Personal Goal Generation)
  IndividualLifecycleSystem: vengeance, extortion, patronage triggers
  ↓
Group 6E (Reconciliation Valve)
  PardonClaim + governor purchase mechanic
```

6A and 6B are independent and can be done in parallel. 6C requires both.
6D requires 6C (needs relationship data to trigger). 6E requires 6C
(needs relationship math to apply pardon effects).

---

## Key Design Constraints

1. **IndividualActionClaim is gossip, not court records.** It propagates as
   Public sensitivity, decays normally, and can be wrong (disinformation
   variant: frame someone for a crime they didn't commit). Distant deeds are
   rumours. Only direct witnesses have high confidence.

2. **Relationship Matrix is sparse.** Only populated for pairs that interact
   or receive gossip about each other. No pre-population. Helper methods
   return 0 for unknown pairs.

3. **Consequence math scales with confidence.** A high-confidence eyewitness
   account shifts relationships more than a faint tavern rumour. This creates
   a natural gradient: nearby events have immediate impact, distant events
   create slow-burning reputation effects.

4. **Personal contracts are economically real.** NPCs spend their own gold to
   seed bounties. This creates natural rate-limiting — broke NPCs can't afford
   vendettas. It also creates player opportunity: impoverish your enemies and
   they can't hire assassins.

5. **Tick ordering preserved.** IndividualActionClaim seeding happens in
   KnowledgeSystem (tick 7). Personal goal generation happens in
   IndividualLifecycleSystem (tick 5) — but it reads the *previous tick's*
   relationship state (T-1), which is intentional. "You decided to act on
   yesterday's grudge."

6. **PlayerActionClaim coexists.** `IndividualActionClaim` is the universal
   deed record; `PlayerActionClaim` remains as a quest-branch-specific trace.
   They serve different purposes and don't conflict.

7. **ExtortGoal is the only new NpcGoal subtype needed.** Vengeance and
   patronage work through existing ContractClaim + knowledge injection
   mechanics. Extortion requires a new interaction model (demand/response).

---

## Open Questions (for Competing LLM Review)

1. Should `IndividualActionClaim` track the specific event that caused it
   (e.g., `WorldEvent` reference), or is the `Context` string sufficient
   for both narrative and mechanical purposes?

2. Should the Relationship Matrix be fully symmetric (if A hates B, does B
   automatically know A hates them?), or should it be asymmetric (A hates B
   but B doesn't know)?

3. Should PardonClaim affect all NPCs of a faction uniformly, or should
   individual NPCs with very high grudges be partially resistant (e.g.,
   the captain whose brother you killed doesn't fully forgive just because
   the governor says so)?

4. What is the gold cost formula for pardons? Flat rate, proportional to
   accumulated hostility, or proportional to faction strength?

5. Should extortion success/failure generate its own IndividualActionClaim?
   (It should — the act of extortion is itself a deed that others can gossip
   about.)
