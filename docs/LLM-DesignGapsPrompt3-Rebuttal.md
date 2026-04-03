# Ahoy — Competing Planner Rebuttal: Knowledge & Quest Systems (Part 3)

## Context

You are a senior game systems designer and C# architect reviewing an evolving
knowledge-and-quest simulation for a pirate sandbox game (C# .NET 10). The game
is called **Ahoy** — a backend-first simulation inspired by Sid Meier's Pirates
and Dwarf Fortress.

Two earlier rounds of design-gap prompts were given to a competing LLM, and the
resulting feedback was incorporated into the codebase. **Part 2 is now fully
implemented and merged.** What follows are seven threads from that feedback that
were either deferred without satisfactory rationale, answered too shallowly, or
contain internal tension that was not resolved.

For each thread below:
- We describe what was implemented
- We describe what the competing LLM said
- We state specifically what remains unresolved
- We ask a concrete design question

**Your task:** For each thread, provide either:
(a) A concrete design decision with rationale, or
(b) An explicit argument for deliberate deferral, with the specific condition that
would trigger revisiting it.

Vague deferrals ("we'll handle this later") are not acceptable answers here.

---

## Architecture Quick Reference

- **Tick = 1 day.** 8 systems run in order: Weather → ShipMovement → Economy →
  Faction → IndividualLifecycle → EventPropagation → Knowledge → Quest.
- **KnowledgeFact** has: Claim (discriminated union), Confidence (0–1, decays
  0.9851^tick), BaseConfidence, HopCount (15% penalty per retelling),
  IsDisinformation, SourceHolder (FactionHolder | PortHolder | PlayerHolder | null),
  CorroborationCount.
- **KnowledgeStore** tracks per-holder fact pools, KnowledgeConflict records,
  and source reliability (_sourceReliability dict keyed by SourceHolder).
- **Sensitivity levels**: Public (propagates 30%/tick), Restricted (10%),
  Secret (0% — never spreads passively), Disinformation (30%).
- **QuestSystem** runs last. Quests trigger when player's fact pool meets a
  condition, expire on natural decay or per-template timer, and have a 20-tick
  cooldown after creation or resolution.
- **PlayerActionClaim**: auto-emitted on quest resolution to PlayerHolder +
  PortHolder (Restricted sensitivity).
- **IndividualLifecycleSystem**: governors start tours at 2% per tick, travel to a
  random faction-controlled port for 3–10 ticks, then return home.

---

## Thread 1 — Individual Movement Is Context-Blind

**What was implemented:** A flat 2% per-tick probability triggers a tour for any
governor who is at their HomePort. Destination is a random faction-controlled port
that isn't the HomePort. Duration is 3–10 ticks. No faction state, war status, or
prosperity level is consulted.

**What the competing LLM said:** The lightweight probability-based state machine
was proposed as "good enough for a first pass."

**What is unresolved:** The competing LLM never addressed when "first pass" ends
and what should replace it. Concretely:
- A governor traveling to an enemy-controlled port during active war is nonsensical.
- High-prosperity factions logically conduct more diplomatic travel.
- All governors have identical travel frequency regardless of their faction's
  strategic situation.

**Design question:** Should the 2% flat probability be retained as a permanent
simplification (if so, give the explicit gameplay justification for ignoring
faction context), OR provide a concrete design for at least one contextual
modifier — e.g. a war-suppression rule or prosperity-scaled probability — using
only data already present in WorldState (FactionState, PortState)?

---

## Thread 2 — Corroboration Amplifies Disinformation

**What was implemented:** The KnowledgeSystem uses a Bayesian-style corroboration
update: each new fact agreeing with an existing claim raises confidence above the
base via a diminishing-returns formula. CorroborationCount is incremented.
IsDisinformation is stored but does not gate the corroboration calculation.

**What the competing LLM said:** "The corroboration trap is a feature not an
exploit — sophisticated disinformation by multiple colluding sources is intentionally
dangerous."

**What is unresolved:** The answer accepts the *outcome* but ignores the *mechanic*.
If a faction plants the same disinformation at 5 ports and those facts propagate
and corroborate each other, the player's held confidence in the false claim can
reach 0.95+ — higher than most genuine facts ever achieve. The player has no signal
that corroborated high-confidence facts might be coordinated fabrication.
IsDisinformation exists in the data model but is never checked before corroboration.

**Design questions:**
1. Should `IsDisinformation = true` on an incoming fact block or dampen the
   corroboration upward adjustment on the existing fact? (i.e., two disinformation
   facts do not corroborate each other unless at least one comes from a distinct
   SourceHolder with high reliability)
2. What is the player's mechanical counterplay beyond "don't trust high-confidence
   facts"? The active investigation mechanic (Gap 1 from Part 1 prompt) was
   proposed but never designed in detail — is that the answer here?
3. If corroboration of disinformation is intentionally unrestricted, state explicitly
   what in-world signal tells the player that coordinated deception happened, without
   requiring the player to simply distrust everything.

---

## Thread 3 — Faction Has No Awareness of Successful Deception

**What was implemented:** When `InjectDisinformationCorrection` fires, the
deceiving faction's source reliability is degraded via `RecordSourceOutcome(wasAccurate: false)`.
The correcting KnowledgeFact is seeded to the PlayerHolder. No event is emitted to
signal that the deception was discovered.

**What the competing LLM said:** Degrading source reliability is sufficient signal.

**What is unresolved:** Source reliability is an internal float — the deceiving
faction has no mechanic by which they discover their deception was detected. The
player has no way to signal "I know you planted this." The adversarial loop is
one-directional: the faction acts; the player discovers; the faction never learns
the ruse failed.

**Design questions:**
1. Should `InjectDisinformationCorrection` seed a `CustomClaim("DeceptionDiscovered", ...)`
   fact to the deceiving faction's FactionHolder, so their decision-making can react?
   Or is faction ignorance of discovery intentional (keeping the player in the
   strategic advantage position)?
2. If factions should be able to discover their deception was caught, what WorldEvent
   carries this — a new `DeceptionExposed` event, or reuse of an existing type?
3. What gameplay consequence should follow from the faction knowing? Options:
   (a) faction immediately increases disinformation sensitivity-gating in that
   region; (b) faction marks the player as "epistemically hostile" via a new
   RelationshipClaim; (c) faction escalates with a counter-mission. Pick one and
   justify it within the existing architecture.

---

## Thread 4 — NPC Decision Quality: Always Deferred, Never Designed

**What was implemented:** Nothing. FactionSystem and EconomySystem read ground-truth
WorldState for all decisions. Ships patrol based on actual ship positions. Merchants
price goods on actual port prosperity.

**What the competing LLM said (Part 1):** "NPC knowledge-gated decisions require
a full decision layer rewrite — out of scope."

**What the competing LLM said (Part 2):** No mention.

**Why this is unacceptable:** The knowledge system exists to create *gameplay* —
information asymmetry that the player can exploit or be deceived by. If NPCs never
consult their KnowledgeStore, the asymmetry only runs one way (player is uncertain,
NPCs are omniscient). This is a fundamental design conflict with the GDD's stated
goal of a "fog of war over the social layer."

**Design question:** Identify **one specific, minimal NPC decision** in the existing
FactionSystem or EconomySystem that could be redirected to query the faction's held
KnowledgeFacts rather than WorldState directly, without restructuring the system.
The requirements:
- The decision must already exist in the codebase
- The change must be expressible as a targeted substitution (read KnowledgeStore
  instead of WorldState for one field in one decision path)
- Describe the specific code seam: which method, which WorldState field currently
  read, what KnowledgeFact type and holder to query instead
- Describe the fallback: what happens when the faction has no relevant fact held?

"Defer until decision layer rewrite" is explicitly not an acceptable answer here.

---

## Thread 5 — KnowledgeConflict Is Never Surfaced

**What was implemented:** `KnowledgeStore.AddFact()` detects when an incoming fact
contradicts an existing non-superseded fact on the same claim subject and records
a `KnowledgeConflict`. These records are stored but never read by any system, never
emitted as a WorldEvent, and not shown in the console `knowledge` command output.

**What the competing LLM said:** "Design the conflict UI in a later frontend pass."

**What is unresolved:** The data model for surfacing conflicts is unspecified. "UI
later" doesn't mean "data model later" — the event and query interface need to be
designed independently of which frontend renders them.

**Design questions:**
1. Should a `KnowledgeConflictDetected` WorldEvent be emitted by KnowledgeSystem
   when a conflict is recorded? If yes, what fields does it carry (fact A, fact B,
   holder, claim subject)?
2. What is the minimal console `knowledge` command extension to show conflicts?
   Proposal: add a "Conflicts:" section listing pairs of contradictory held facts
   with their confidence values side-by-side. Is this sufficient?
3. Is "conflict" itself an active mechanic (player chooses which fact to trust,
   invalidating the other) or purely informational (both facts persist, player
   reasons from them)? The answer determines whether a `ResolveConflict` command
   is needed.

---

## Thread 6 — The "Burning" Mechanic Is Referenced but Undesigned

**What is referenced:** `SDD-Knowledge.md` mentions that when a Secret fact
propagates beyond its permitted pool, the agent who was guarding it is "burned"
— their identity as a counter-intelligence asset is revealed to the faction who
now holds the secret.

**What was implemented:** Sensitivity-gated propagation ensures Secret facts
propagate at 0% — they never escape passively. There is no active mechanic for
a Secret to escape, and no "burning" outcome exists in the code.

**What the competing LLM said (Part 2 plan):** "'Burning' via counter-intel
injection reuses existing SeedDisinformation." This is architecturally incorrect.
SeedDisinformation plants *false* facts. Burning is about the consequence of a
*true* secret escaping — the guard's identity is revealed. These are different
mechanics.

**Design questions:**
1. What is the concrete trigger for a burn event? Options:
   - (a) A Secret fact is accessed via an active `InvestigateCommand` the player
     executes successfully
   - (b) A Secret fact is deliberately "leaked" by a faction as a political move
   - (c) An internal faction betrayal (individual loyalty mechanic) causes a Secret
     to escape
   Which trigger is consistent with the current architecture and design goals?
2. What is the concrete outcome? Specifically: what KnowledgeFact type is created,
   who are the SourceHolder and TargetHolder, and what WorldEvent is emitted?
3. How does this feed back into IndividualLifecycle? Options:
   - (a) The burned individual gains a flag, modifying their tour behavior or
     suppressing them from being a TriggerFact for quests
   - (b) The individual is simply removed from play (IndividualDied)
   - (c) No lifecycle consequence — only the knowledge consequence

---

## Thread 7 — PlayerActionClaim Decay Creates Faction Amnesia

**What was implemented:** `PlayerActionClaim` facts are seeded at `Confidence=1.0`
to `PlayerHolder` and `PortHolder(currentPort)` with `Sensitivity=Restricted`.
They decay at the standard rate (0.9851^tick). After ~130 ticks the player's
own `PlayerHolder` copy falls below 0.15 (near-threshold).

**What the competing LLM said:** Standard decay is correct — factions forgetting
player actions is realistic and creates opportunities for reputation rehabilitation.

**What is unresolved:** The competing LLM conflated two distinct holders:
- `PlayerHolder`: the player's own record of their actions. Should this decay?
- `FactionHolder` / `PortHolder`: third parties' knowledge of what the player did.
  These decaying is realistic and desirable.

If `PlayerHolder` copies decay, the player's own quest log diverges from the
knowledge system's representation of "what the player did" — a confusing UX split
where the quest log says "Completed" but the knowledge system says "confidence: 0.08."

**Design questions:**
1. Should `PlayerHolder` copies of `PlayerActionClaim` be exempt from decay (stored
   with `BaseConfidence=1.0` and a custom decay rate of 1.0), while PortHolder and
   FactionHolder copies decay normally?
2. If yes, how is this implemented — a per-holder decay rate override, a
   special-case in the decay loop, or a flag on the fact itself?
3. If no (all copies decay at the same rate), what is the gameplay rationale for
   the player's own memory fading? Is this a deliberate "unreliable narrator" design
   element, and if so, how is it surfaced in the UX without being confusing?

---

## Closing Instruction

For each of the seven threads above, provide one of:

**(A) Concrete design decision:** State the choice, give the rationale, and — where
a code change is implied — identify the specific file and method to modify.

**(B) Deliberate deferral with conditions:** Argue explicitly why the decision
should not be made now, and state a specific triggering condition (a feature, a
milestone, or an observable problem in playtest) that would require revisiting it.

Vague deferrals and "we'll figure it out later" responses will be treated as
non-answers. This is a rebuttal round — shallow answers from prior rounds are being
challenged, not re-asked.
