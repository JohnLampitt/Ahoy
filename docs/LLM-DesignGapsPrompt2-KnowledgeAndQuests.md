# Ahoy — Knowledge & Quest System: Design Gaps Review Brief (Part 2)

## What This Is

A second review brief for Project Ahoy, a C# .NET 10 pirate sandbox simulation.
A first brief (covering active investigation, knowledge trading, FactionIntentionClaim
emission, sensitivity-gated propagation, NPC decision quality, contradiction UI, and quest
entity anchoring) has already been sent to competing LLMs and their feedback incorporated.

This brief covers a **different set of gaps** — areas where the gap between the SDD design
intent and the current implementation is structural or architecturally significant, and
where design input is needed before building.

The codebase uses discriminated union records, a tick engine (1 tick = 1 day), and all world
mutations via typed `WorldEvent`s and `IWorldSystem.Tick()`. Keep proposals within this idiom.

---

## Implemented (current state)

- `KnowledgeFact`: Confidence (exponential decay ≈ 0.985/tick), HopCount, SourceHolder,
  BaseConfidence, CorroborationCount, IsSuperseded, IsDisinformation
- `KnowledgeConflict` as first-class object per holder
- Source reputation: `RecordSourceOutcome(source, wasAccurate)` modulates propagation weight
- Passive investigation: player ship arrival → HopCount=0 observations
- All 12 quest templates (A1–A6, B1–B6) defined and triggering
- `QuestActivated` and `QuestResolved` world events emitted
- `KnowledgeNarrator`: maps fact state to qualitative LLM prompt instructions
- Disinformation injection: `FactionSystem` plants `IsDisinformation=true` facts at ~1% chance
  per tick per faction, seeded into pirate haven knowledge pools
- `IndividualWhereaboutsClaim` seeded at world startup for named individuals

---

## Gap 1: Individual Movement and Whereabouts Lifecycle

**Design intent:** Quests A3 (Governor's Quill), B4 (Cure in Doubt), and B6 (Map That Wasn't)
trigger on `IndividualWhereaboutsClaim`. The SDD calls for `IndividualMoved` events when
governors and named individuals travel between ports, generating fresh whereabouts facts.

**Current state:**
- `IndividualWhereaboutsClaim` is seeded once at world startup (static initial location).
- No individual movement system exists — governors and named NPCs never travel.
- No `IndividualMoved` world event exists.
- A3, B4, B6 trigger only on the low-confidence stale startup fact (confidence decays below
  0.35 after ~60 ticks). They never fire on *fresh* intelligence.

**What's missing:**
- A movement cadence: how often do named individuals travel, and how is the destination chosen?
- A world event (`IndividualMoved` or `IndividualLocationChanged`) that `KnowledgeSystem`
  converts into a new `IndividualWhereaboutsClaim`, superseding the old one
- Whether individual movement is driven by the faction system (governors follow faction orders),
  random walks, or personality-influenced decisions

**Questions:**

1. Should individual movement be driven by the same `ActorDecisionSystem` (LLM inflection
   points) as faction decisions, or should it use a simple probability model
   (e.g. 5% chance per tick to relocate to a faction-controlled port)?
   The design goal is for governors to feel alive and purposeful, not random.

2. When a governor moves, who initially holds the new `IndividualWhereaboutsClaim`?
   Only witnesses at the departure port? Only witnesses at the arrival port? Both?
   The current model seeds startup facts into "all ports in the faction's territory" —
   should movement facts be seeded more narrowly (arrival port only) to create
   genuine information asymmetry?

3. Should governors have a home port they return to? Or should they be in permanent motion?
   A home port would create predictable "safe" knowledge that decays into uncertainty when
   the governor leaves — which directly creates quest triggers. Is this the right mechanic?

4. The design distinguishes `IndividualHolder` (named NPC) from `PortHolder` (port
   population pool). When a governor arrives at a port, do they deposit their knowledge
   into the port pool, and pick up the port pool's facts — the same as ships? Or should
   named individuals have a separate, more curated knowledge exchange?

---

## Gap 2: Disinformation Detection and Player Feedback

**Design intent:** Disinformation is first-class. Players discover false intelligence through
contradiction or failed action — never through a direct `IsDisinformation` flag. The SDD
says: "Discovery is through contradiction (conflicting claim from a different source) or
failed action (the claimed ship was not there)."

**Current state:**
- `FactionSystem` injects `IsDisinformation=true` facts at pirate havens at low frequency.
- `IsDisinformation` propagates through the system (correctly marked on all copies).
- `KnowledgeConflict` fires when two facts with different claims share the same subject key.
- But there is no feedback mechanism when a player acts on a false fact and it fails.
  If the player chooses a quest branch based on a false `ShipLocationClaim`, the outcome
  resolves exactly the same as a true one — nothing signals the deception.

**What's missing:**
- A "failed action" detection path: when branch resolution produces no matching entity
  (the ship doesn't exist at the claimed location), what should happen?
- Player feedback that doesn't break epistemic integrity — they shouldn't see `IsDisinformation`
  but they should feel the consequences
- Whether factions should be able to tell they successfully deceived the player, and act on that

**Questions:**

1. **Failed action mechanic:** When A1's "intercept the galleon" branch resolves and the
   ship isn't there (because the fact was planted), what is the right event?
   Options:
   - A `QuestFailedOnFalseIntel` event that marks the instance `Failed` and emits a
     new `IsDisinformation=true` discovery fact with HopCount=0 for the player
   - The player's trigger facts are superseded by a contradicting empty-handed observation,
     letting them piece it together from the resulting `KnowledgeConflict`
   - A `RumourRefuted` world event that spreads from the location: "word is the tip was
     false — there was nothing there"
   Which of these best serves "the world reacts to your actions" without over-signalling?

2. **Faction awareness:** Should the injecting faction learn (via a knowledge fact of their
   own) that their disinformation was acted upon? If so, what should they do with that
   knowledge — gloat, escalate, target the player specifically? This connects to
   whether factions can have *personal* intelligence goals targeting the player.

3. **Disinformation seeding rate and variety:** Currently 1% per tick per faction, always
   a `ShipLocationClaim`. The SDD mentions fleet movements, trade route safety, and
   governor locations as also viable disinformation targets. How should factions choose
   what to plant? Should the claim type be chosen to match a current player quest?
   (A faction that knows the player is hunting a governor could plant a false whereabouts.)

4. **The corroboration trap:** A faction could in theory plant the *same false claim* from
   multiple ports, causing the player's `CorroborationCount` to rise on a false fact —
   making it *more* convincing. Is this an intended mechanic (sophisticated deception) or
   an exploit to close? If intended, should it be seeded deliberately (multiple injection
   points for the same disinformation campaign)?

---

## Gap 3: Knowledge Guarding and Suppression

**Design intent:** The SDD describes an `ActiveGuard` system: factions register guard interest
on sensitive facts, and `KnowledgeSystem` runs a suppression pass each tick that slows spread
and can "burn" a fact for specific holders. This is what makes `Secret` and `Guarded` facts
genuinely rare and drives the knowledge economy.

**Current state:**
- `ActiveGuard` and `GuardedFacts` do not exist in code.
- `KnowledgeSensitivity.Restricted/Secret` values are defined but not used to gate propagation.
- All facts propagate at the same 30% base rate regardless of sensitivity.
- `Faction.IntelligenceCapability` is referenced in the SDD but the field doesn't exist.

**This is the largest structural gap between the SDD and the implementation.**

**Questions:**

1. **Is full suppression worth the complexity?** The SDD's model — a per-fact guard with
   intensity 1–10 and a per-tick suppression pass — is powerful but expensive.
   A simpler approximation: sensitivity directly sets propagation probability (Public=30%,
   Restricted=10%, Guarded=3%, Secret=0%). No per-tick pass, no guard registry.
   Does this adequately serve the design goal of "meaningful secrecy," or does the game
   require that factions *actively burn* compromised facts to feel alive?

2. **Guard registration trigger:** If we implement the full `ActiveGuard` model, what
   triggers a faction to register a guard? Options:
   - Automatically, whenever a `Secret`/`Guarded` fact is created for their `FactionHolder`
   - Only when the faction's `IntelligenceCapability` exceeds a threshold
   - Only for specific claim types (fleet movements, treasury state, governor location)
   How granular should the guard registry be?

3. **Burning facts:** The SDD says suppression can "burn" a fact for specific holders —
   the holder's belief is marked as known-to-be-false. This is distinct from supersession
   (which requires a contradicting fact). What is the right mechanical representation?
   A new `IsBurned` flag on `KnowledgeFact`? Or should burning be modelled as the faction
   injecting a contradicting fact (forcing a `KnowledgeConflict`) rather than directly
   mutating the holder's knowledge?

4. **Counter-intelligence and the player:** If the player is holding a `Secret` fact about
   a faction, and that faction has high `IntelligenceCapability`, should they be able to
   target the player's copy specifically? What are the in-world mechanics — does the
   faction send an agent, or does it happen probabilistically? This connects directly to
   whether selling sensitive intel is genuinely risky.

---

## Gap 4: Natural Quest Expiry vs. Timer-Based Expiry

**Design intent:** The SDD is explicit: "Expiry is natural — if the player ignores a quest,
confidence decay closes the window without special expiry logic."

**Current state:**
- All 12 quest templates use `state.Date.CompareTo(instance.ActivatedDate.Advance(N)) > 0`.
- Quests expire after a fixed number of ticks regardless of the state of the trigger facts.
- This violates "knowledge-gated, not timer-gated."

**The tension:** A pure decay-based model has a failure mode. If the trigger fact is held by
the player but never decays below threshold (e.g. it gets re-corroborated), the quest stays
open indefinitely. A hard time limit provides a backstop. But a hybrid needs clear rules.

**Questions:**

1. **Pure model:** Replace all timer predicates with a check on the trigger facts directly:
   ```csharp
   ExpiryPredicate = (instance, state) =>
   {
       var facts = state.Knowledge.GetFacts(new PlayerHolder());
       return !instance.Template.TriggerCondition.Evaluate(facts);
   }
   ```
   This is clean and consistent. If the player's trigger fact decays, the quest closes.
   If they receive a corroborating fact and confidence rises, the quest stays open.
   **Does this fully replace timers, or is there a category of quest where a hard time limit
   is also appropriate** (e.g. a political window that closes regardless of what the player knows)?

2. **Re-triggering after expiry:** In the pure model, if the trigger fact is restored above
   threshold after the quest expired (a new rumour arrives), should the template re-trigger?
   The current `HasActiveInstance` guard prevents this while a quest is active, but after
   expiry it clears. Re-triggering feels natural but could flood the player with
   re-activated stale quests. What's the right guard?

3. **Mixed model:** Keep timer as a hard backstop but shorten it significantly and add the
   decay check as the primary condition:
   ```csharp
   ExpiryPredicate = (instance, state) =>
       state.Date > instance.ActivatedDate.Advance(45) ||  // hard backstop
       !instance.Template.TriggerCondition.Evaluate(facts); // natural decay
   ```
   Is 45 ticks too long? The design intent is that the player should feel urgency from
   decay, not from a countdown.

---

## Gap 5: LLM Quest Instantiation — Connecting KnowledgeNarrator to the Call

**Design intent:** SDD §6 says when `QuestSystem` creates a `QuestInstance`, an async LLM
call generates `LlmNpcName` and `LlmDialogue` specific to the live world state.
`QuestInstance.LlmNpcName` and `LlmDialogue` fields exist; `KnowledgeNarrator` exists and
generates qualitative context; but the actual call is not wired.

**Current state:**
- `QuestInstance.DisplayNpcName` falls back to `Template.NpcName ?? "Unknown Contact"`.
- `QuestInstance.DisplayDialogue` falls back to template default dialogue.
- `KnowledgeNarrator.Describe()` produces `EpistemicContext` but nothing calls it for quests.

**Questions:**

1. **What should the LLM receive for quest instantiation?**
   Proposed context bundle:
   - `QuestTemplate.Synopsis` (structural intent — 1 sentence)
   - `EpistemicContext` for each trigger fact (from `KnowledgeNarrator.Describe()`)
   - Live world snapshot: port names, faction standings, current date
   - Player's recent quest history (last 3 completed/expired) — for NPC continuity
   Is this the right set? What is missing that would significantly improve output quality?

2. **Output format:** The SDD says the LLM returns an NPC name and opening dialogue.
   Should it also return branch-specific dialogue variations (different NPC lines for
   each branch the player could choose)? Or does branch dialogue come from a second,
   separate call at resolution time? The matrix approach from `ActorDecisionSystem`
   (one call covering all scenarios) could apply here too.

3. **Non-blocking instantiation:** The `ActorDecisionSystem` SDD describes a stalling
   mechanic for NPC decisions while inference completes. Quests don't have a natural
   stall equivalent — the quest appears immediately in the player's log.
   Options:
   - Quest appears immediately with fallback text; LLM text replaces it when ready
     (the player may read the fallback if they check immediately)
   - Quest is queued for one tick before appearing, giving LLM time to respond
   - No async at all for quests — use a dedicated fast/cheap model call inline
   Which of these best fits the "LLM-augmentable but not LLM-dependent" design principle?

4. **NPC continuity:** If the same named NPC (e.g. "María the Broker") triggers multiple
   quests over time, should the LLM be given their prior dialogue to maintain character
   consistency? How do we track named NPCs across `QuestInstance` lifetimes without
   coupling `QuestTemplate` to a specific `IndividualId`?

---

## Gap 6: Quest History as Knowledge

**Design intent:** The SDD says LLM rationale from actor decisions "enters the knowledge
system as a narrative fact." This same principle should apply to quests: a resolved quest
should generate `KnowledgeFact`s that spread through the world and let factions/NPCs
react to the player's choices.

**Current state:**
- `QuestActivated` and `QuestResolved` events are emitted correctly.
- `KnowledgeSystem` ingests world events and converts them to facts — but there is no
  handler for `QuestActivated`/`QuestResolved`.
- No quest outcome generates a `KnowledgeFact` via the event pipeline (only via the
  `EmitRumourAction` outcome action, which is manually specified per branch).

**Questions:**

1. **What should QuestResolved generate as a knowledge fact?**
   Options:
   - A `CustomClaim` with a narrative description of the outcome: automatically generated
     from the branch label and the quest title
   - A structured `PlayerActionClaim(QuestTemplateId, BranchId, PortId)` that other
     systems can pattern-match on
   - Nothing automatically; rely entirely on per-branch `OutcomeActions` (current approach)
   The risk of automatic generation: every quest completion floods the world with facts.
   The risk of manual-only: templates are tedious to write and easy to forget.

2. **Who should initially hold a QuestResolved fact?**
   - Only the player (they know what they did)
   - The port where the quest resolved (witnesses)
   - The faction the quest concerned (if any `AssociatedFactionId` is set)
   How should this fact's sensitivity be set? If the player sold out a governor, the
   faction almost certainly wants that information `Secret`.

3. **Faction reaction to player quest history:** A faction that holds a `PlayerActionClaim`
   showing the player sided against them should update their standing with the player.
   This requires `FactionSystem` to read `KnowledgeFacts` about the player — not just
   direct events. Is this the right architecture, or should faction reputation be updated
   directly by `SimulationEngine` when resolving quest branches, without going through
   the knowledge layer?

4. **The player's own quest log as knowledge:** Currently `QuestStore.History` is a
   separate list. Should it instead be derived from the player's held `KnowledgeFacts`
   (facts about their own completed quests)? This would unify the data model and let
   the player's history degrade and be superseded like any other knowledge — which
   creates interesting possibilities (the player "forgets" or gets misinformation about
   their own past if their facts are corrupted) but may be too strange in practice.
