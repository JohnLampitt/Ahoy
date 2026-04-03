# Ahoy — Knowledge & Quest System: Design Gaps Review Brief

## What This Is

A follow-up to the earlier technical review brief. The core knowledge and quest systems are
now implemented and working. This document describes the **remaining gaps** — areas where the
design intent is clear but the implementation does not yet match it, or where the design
itself is still underspecified. Your task is to propose concrete, implementable designs.

The codebase is C# / .NET 10. We use discriminated unions expressed as C# abstract records.
The simulation runs as a deterministic tick engine (1 tick = 1 day). All world mutations
happen through typed `WorldEvent`s and `IWorldSystem.Tick()` — no side-effects outside
the engine. Keep proposals within this idiom.

---

## Implemented (for context)

- `KnowledgeFact` with Confidence (exponential decay ≈ 0.985/tick), HopCount, SourceHolder,
  BaseConfidence, CorroborationCount, IsSuperseded, IsDisinformation
- `KnowledgeConflict` as first-class object: two contradicting facts on same subject key
  trigger a per-holder conflict entry
- Source reputation: `RecordSourceOutcome(source, wasAccurate)` — accurate +0.05,
  inaccurate -0.15, clamped 0.1..1.5. Applied as a multiplier to incoming confidence
  during propagation
- Passive investigation: player ship arrival → HopCount=0 PortControlClaim and PortPriceClaims
  automatically generated; superseded facts feed back into source reputation
- All 12 quest templates (A1–A6, B1–B6) implemented and triggering
- `KnowledgeNarrator`: qualitative LLM translation layer mapping confidence/hops/conflict
  to prompt instructions

---

## Gap 1: Active Investigation Command

**Design intent:** The player can spend time and/or resources to deliberately investigate a
specific fact. Unlike passive arrival observations (which auto-generate on docking), an
active investigation costs something and targets a specific held fact — particularly useful
for disinformation detection.

**What exists:** `KnowledgeFact.IsDisinformation` (server-side flag, holder cannot see it).
Passive observations are generated on player ship arrival. No `InvestigateCommand` exists.

**What's missing:**
- A command that the player can issue targeting a specific `KnowledgeFactId`
- Resource cost (time? gold? a contact?) to gate the action
- Resolution: produces a new HopCount=0 fact that either corroborates or contradicts
- Discovery of disinformation: when a new direct observation contradicts a held
  `IsDisinformation = true` fact, some feedback should be available to the player
  (without exposing the `IsDisinformation` flag directly)

**Questions:**
1. What is the right resource cost model? Pure time (N ticks at location)? Gold paid to
   a contact? Or an action that requires a specific `IndividualHolder` to be reachable?
2. How should discovered disinformation be surfaced? Options:
   - A `[CONTRADICTED]` tag on the old fact in the UI with no further commentary
   - An in-world event that produces an NPC rumour about someone planting false intelligence
   - A flag on the `KnowledgeConflict` record: `ContainsDisinformation` (server-side only;
     triggers different NPC dialogue)
3. Should targeted investigation produce facts about non-port subjects (e.g. individual
   whereabouts, ship cargo) or only port-observable data (prices, control)?

---

## Gap 2: Knowledge Trading / Information Brokers

**Design intent:** The player can buy facts from NPCs or sell facts to them. Ports have
information brokers (a type of `IndividualHolder`) who aggregate local intelligence and
sell it for gold. The player can also sell intelligence to factions.

**What exists:** `KnowledgeStore.AddFact()` supports adding facts to any holder.
`KnowledgeSensitivity` enum has `Public / Restricted / Secret` values.

**What's missing:**
- A `BuyIntelligenceCommand` and `SellIntelligenceCommand`
- Pricing model for facts (function of Confidence, Sensitivity, HopCount, age)
- Broker availability — not all ports have brokers; broker quality varies
- What happens to the broker's reputation if the sold fact proves wrong?

**Questions:**
1. Pricing formula: what variables should determine the price of a fact? Suggested
   starting point: `BasePrice * Confidence * (1 / (HopCount + 1)) * SensitivityMultiplier`.
   Is this right? What are reasonable values?
2. Should brokers have limited inventory (facts expire from their pool)? Or do they
   hold all non-superseded facts indefinitely?
3. When the player sells a fact to a broker, should it propagate into the port's knowledge
   pool and spread to other ships naturally? Or should sold facts stay in a separate
   "broker inventory" that other players (in a future multiplayer context) can buy?
4. Broker reliability: if a player repeatedly buys inaccurate facts from a broker,
   `RecordSourceOutcome` already tracks this. Should brokers with low reliability
   still sell facts (at a discount), or should they become unavailable for certain
   categories?

---

## Gap 3: `FactionIntentionClaim` Emission

**Design intent:** Factions have military and economic intentions. A claim like "Spain
intends to attack Port Royal within 30 days" should exist as a fact that can be held
by players and NPCs, degrade over time, and trigger quest templates.

**What exists:** `FactionIntentionClaim(FactionId, IntentionSummary: string)` is defined
in the discriminated union. `FactionSystem` tracks faction goals (ExpandTerritory,
BuildNavy, etc.) but never emits this claim.

**What's missing:**
- Trigger condition: when should a claim be emitted? Options:
  - When a faction goal crystallises into a concrete plan (e.g. a target port is selected)
  - When a faction's naval strength crosses a threshold that makes attack viable
  - Periodically, proportional to IntelligenceCapability
- Distribution: who should initially hold this fact? (Faction agents at specific ports?
  All ports in the faction's territory? The faction's own `FactionHolder`?)
- Sensitivity: should `FactionIntentionClaim` always be `Secret` or `Restricted`?

**Questions:**
1. `IntentionSummary` is currently a free string. Should this be a structured claim
   (`FactionIntentionClaim(FactionId, IntentionType, TargetPortId?, TargetFactionId?,
   PlannedExecutionDate?)`) or keep the free string for LLM narrative flexibility?
2. How should a `FactionIntentionClaim` become outdated? If the faction executes the
   attack, the claim should be superseded. If they abandon the goal, it should decay.
   What's the right supersession trigger?
3. Quest trigger: "Spain plans to attack Port Royal — do you warn the English, profit
   from the chaos, or accelerate the attack?" — is this the right quest shape, and
   does the current QuestBranch/OutcomeAction model support it?

---

## Gap 4: Sensitivity-Gated Propagation

**Design intent:** `Restricted` and `Secret` facts should not propagate freely.
They should only travel through relationships — a `Secret` fact shared by a faction
agent requires trust (relationship score ≥ threshold, or payment). Currently all
non-superseded facts above 0.10 confidence propagate at the same 30% base rate.

**What exists:** `KnowledgeSensitivity` enum. `KnowledgeStore.ShareFacts()` uses a flat
30% propagation chance regardless of sensitivity.

**What's missing:**
- A relationship model between holders (or at minimum, a trust flag on the propagation
  edge — e.g. `ShipHolder` at a `FactionHolder`-controlled port can receive `Restricted`
  facts from that port if the faction trusts the ship's captain)
- Propagation rules that gate on sensitivity + relationship
- How to represent "the player paid for this intel" without a full relationship graph

**Questions:**
1. Is a full relationship graph (between all `KnowledgeHolderId` pairs) the right model,
   or can we approximate with simpler rules:
   - `Public`: propagates freely (current behaviour)
   - `Restricted`: only propagates within same faction's holders (PortHolder → FactionHolder
     freely, FactionHolder → ShipHolder only if ship is faction-aligned)
   - `Secret`: never propagates via gossip — only via direct sale or active sharing
2. How should the player gain access to `Restricted` facts? Via faction standing? Via gold
   payment? Via completing quests for that faction?
3. Should disinformation facts always propagate as `Public` regardless of their original
   sensitivity? (The faction injecting them *wants* them to spread.)

---

## Gap 5: NPC Decision Quality Using the Knowledge Store

**Design intent:** Merchant captains route based on their held `PortPriceClaim` facts
(confidence-weighted). Factions evaluate expansion goals using `FactionStrengthClaim`
facts held by their `FactionHolder`. Currently the weighting is rough.

**What exists:**
- `MerchantDecisionProvider` scores destinations using held price facts but doesn't
  fully incorporate confidence as a weight
- `FactionSystem` evaluates goals against world state directly, not against held knowledge

**What's missing:**
- Confidence-weighted destination scoring for merchants:
  `score = expectedPrice * confidence + fallbackPrice * (1 - confidence)`
  where `fallbackPrice` is the faction/holder's prior belief when no fact exists
- Faction decision-making via knowledge facts: e.g. faction evaluates
  `FactionStrengthClaim` for a rival before deciding to pursue `ExpandTerritory` goal

**Questions:**
1. For merchant routing: should confidence enter as a linear multiplier on expected price,
   or as a probability of the price being true (Bayesian expected utility)?
   What's the simplest model that produces interesting emergent merchant behaviour?
2. For faction decisions: factions currently act on ground truth. If we switch them to
   act on their held knowledge (potentially wrong), how do we prevent trivial
   exploits (the player always plants false `FactionStrengthClaim` facts to manipulate
   faction behaviour)?
3. Should NPCs have "curiosity" — a tendency to seek out information they lack?
   E.g. a merchant with no price fact for a port assigns it a high expected value
   and routes there to discover prices.

---

## Gap 6: Contradiction UI Patterns

**Design intent:** When the player holds two contradicting facts about the same subject,
the UI should surface this clearly and the player should be able to reason about which
is true. Currently `KnowledgeConflict` is detected and displayed in the console with
a `[CONFLICT]` tag, but there is no player agency over resolution.

**What exists:** `KnowledgeConflict` with `DominantFact`, `ConfidenceSpread`, `IsResolved`.
Console displays competing facts. No resolution mechanic beyond natural decay.

**Questions:**
1. Beyond natural confidence decay, should the player have an explicit "Trust this source"
   action that boosts one competing fact and marks the other superseded? If so, does this
   interact with `RecordSourceOutcome`?
2. Should contradictions that involve a known `IsDisinformation` fact behave differently
   in the UI? (The server knows one fact is planted — but the player shouldn't see that
   flag directly. Can the UI hint at it without breaking the epistemic model?)
3. Should contradictions block certain quest branches? E.g. "You cannot proceed until you
   resolve the conflicting intelligence about Port Royal's allegiance."
4. At what `ConfidenceSpread` threshold does a conflict become "effectively resolved" from
   the player's perspective? Should `KnowledgeConflict.IsResolved` use a confidence
   threshold rather than the current "count ≤ 1" definition?

---

## Gap 7: Quest Template Parameterisation and Anchoring

**Design intent:** Some quests should be anchored to specific world entities at
instantiation — e.g. "B3: The Vanishing Frigate" should always refer to the same ship
throughout its lifecycle, not re-evaluate the trigger condition. Currently `TitleFactory`
interpolates the entity name into the title but the full entity reference isn't frozen.

**What exists:** `QuestInstance` holds `TriggerFactIds` (the fact IDs at activation time).
`TitleFactory` derives a display title from trigger facts. `AllowDuplicateInstances` controls
whether the same template fires multiple times.

**What's missing:**
- A way to "anchor" a quest to a specific entity (ShipId, IndividualId) at instantiation,
  so that outcome actions and branch availability can reference that entity directly
- Example: B3's "Track the ship and rescue survivors" branch should operate on the
  *specific ship* from the trigger fact, not just re-query the knowledge store

**Questions:**
1. Proposed anchor model: `QuestInstance` gains an `EntityAnchors: Dictionary<string, object>`
   that stores entity IDs extracted from trigger facts at activation. Branches reference
   these by key. Is this the right shape? What are the type-safety trade-offs?
2. `OutcomeEvents: (WorldState → WorldEvent[])` already receives `WorldState`. Could
   `OutcomeEvents: (WorldState, QuestInstance → WorldEvent[])` be the right signature to
   give branches access to instance anchors? Or is a separate `ApplyContext` record cleaner?
3. For quests with `AllowDuplicateInstances = true` (e.g. B5: SilentConvoy, B3),
   how do we prevent the same entity triggering two simultaneous instances? Should there
   be an `AnchorKey` that deduplicates — "one active instance per (TemplateId, ShipId)"?
