# Ahoy — Knowledge-Driven Living World Design Review

You are a senior game systems designer and C# architect reviewing implementation
proposals for **Ahoy**, a C# .NET 10 pirate sandbox simulation (Dwarf Fortress +
Sid Meier's Pirates). The game is backend-first; all mechanics are pure simulation
logic, no rendering or UI.

---

## Architecture quick reference

- **Tick = 1 day.** 8 systems run in order each tick:
  Weather → ShipMovement → Economy → Faction → IndividualLifecycle →
  EventPropagation → Knowledge → Quest

- **`KnowledgeFact`**: Claim (discriminated union), Confidence (0–1, exponential
  decay at 0.9851/tick), HopCount (15% penalty per retelling), IsDisinformation,
  SourceHolder, OriginatingAgentId (nullable), CorroborationCount,
  CorroboratingFactionIds (echo-chamber guard), IsDecayExempt

- **`KnowledgeHolderId`** cases: FactionHolder, PortHolder, ShipHolder,
  IndividualHolder, PlayerHolder

- **Sensitivity levels** (passive propagation chance):
  Public (30%), Restricted (10%), Secret (0%), Disinformation (30%)

- **`KnowledgeConflict`**: Detected and stored when competing non-superseded facts
  exist for the same SubjectKey in a single holder. Fields: SubjectKey,
  CompetingFacts, DominantFact, ConfidenceSpread, IsResolved.
  `KnowledgeConflictDetected` event is emitted. **No system currently reads
  conflicts or acts on them.**

- **Source reliability**: Per-holder float (default 1.0). Adjusted by
  `RecordSourceOutcome(source, wasAccurate)`: +0.05 accurate, -0.15 inaccurate.
  Clamped [0.1..1.5]. **Only the player's direct observations trigger this;
  NPCs never record source outcomes.**

- **16 claim types implemented**:
  PortPriceClaim, PortProsperityClaim, PortControlClaim, ShipLocationClaim,
  ShipStatusClaim, ShipCargoClaim, WeatherClaim, RouteHazardClaim,
  FactionStrengthClaim, FactionIntentionClaim, IndividualWhereaboutsClaim,
  IndividualStatusClaim, IndividualAllegianceClaim, PlayerActionClaim,
  OceanPoiClaim, ContractClaim, CustomClaim

- **`Individual`** fields: Id, Name, Role (Governor/PortMerchant/NavalOfficer/
  PirateCaptain/Smuggler/Informant/KnowledgeBroker/Diplomat/Privateer),
  FactionId, ClaimedFactionId (cover identity for infiltrators), LocationPortId,
  HomePortId, Authority, PlayerRelationship, IsAlive, IsCompromised, CurrentGold,
  PersonalityTraits, CareerHistory

- **`Ship`**: CaptainId (nullable IndividualId), OwnerFactionId, Location
  (AtPort/AtSea/EnRoute/AtPoi), RoutingDestination, Cargo, GoldOnBoard,
  ClaimedOwnerFactionId (false colors), ArrivedThisTick (transient)

- **`Faction.IntelligenceCapability`** (0.10–0.95): Scales disinformation quality
  (0.70 + cap*0.25), gates DeceptionExposed detection, reduces investigation
  yield. Mutated: +0.005/tick on EspionageGoal, -0.05 on DeceptionExposed.

- **Quest system (v0.1)**: Contract quests only. `ContractClaim` facts in
  `PlayerHolder` with confidence > 0.40 + separate intel fact > 0.50 trigger
  `ContractQuestInstance`. Flat state machine: Active → Completed | Expired |
  Failed. Branches produce WorldEvents. LLM renders flavor text only.
  `QuestTemplate` supports chaining via `NextQuestTemplateId`.
  Phase graph model (multi-step quests) described in SDD but not implemented.

- **NPC decisions**: `RuleBasedDecisionProvider` reads `WorldState` directly
  (ground truth). NPCs never consult their IndividualHolder facts for decisions.
  Merchant routing uses random fallbacks or ground-truth port data.

---

## What Groups 1–3 established

All merged and operational:
- Full knowledge propagation: ship-carried gossip, broker skimming, player
  observation, passive absorption, confidence decay, hop penalties
- Investigation commands: `InvestigateLocalCommand` (free, physical presence)
  and `InvestigateRemoteCommand` (gold cost, async resolution gated by
  target faction's IntelligenceCapability)
- Knowledge trading: `BuyKnowledgeCommand`, `SellFactCommand` with brokers
- Burning mechanic: `BurnAgentCommand` traces disinformation to injecting
  agent via OriginatingAgentId, sets IsCompromised, confidence penalty,
  30-tick replacement timer
- FactionIntelligenceCapability: EspionageGoal, disinformation quality
  scaling, counterintelligence gates, capability mutation
- Disinformation seeding: FactionSystem plants false ShipLocationClaims in
  pirate havens (~1%/tick for colonial factions)
- ContractClaim origination: Factions seed bounty and relief contracts into
  port knowledge pools based on active goals

---

## Proposed: Group 5 — Knowledge-Driven Living World

The central thesis: **NPCs must be epistemic agents, not epistemic spectators.**
Every NPC should act on what they know (their IndividualHolder/ShipHolder facts),
not what is objectively true. This single change transforms the simulation from
a player-centric puzzle into a living world where factions, merchants, captains,
and governors all navigate the same fog of information.

---

### 5A — NPC Knowledge-Gated Decisions

**5A-1: Merchant routing via IndividualHolder facts**

Replace ground-truth merchant routing with knowledge-driven routing:
1. Captain reads IndividualHolder for non-superseded PortPriceClaim facts
2. Scores candidate ports by expected margin from known prices
3. Falls back to FactionHolder facts, then HomePortId
4. Conflicting price claims resolved by highest confidence

Requires captain seeding: CaribbeanWorldDefinition must give starting captains
initial PortPriceClaim facts for their home region ports.

**5A-2: Governor tour decisions via knowledge**

Replace flat 2%/tick tour probability:
1. Tour likelihood scales with faction stability (FactionHolder facts)
2. Destination uses IndividualWhereaboutsClaim (visit known allies) and
   PortProsperityClaim (visit struggling ports)
3. Tours suppressed to ports with enemy PortControlClaim

**5A-3: Decision provider knowledge access**

Pass KnowledgeStore into RuleBasedDecisionProvider so all NPC decision triggers
can read holder-specific facts.

**Open question 1:** When an NPC captain holds no PortPriceClaim facts at all
(fresh replacement, or all facts decayed), the fallback is HomePortId. But a
captain who has been at sea for 100+ ticks will have had all their price
knowledge decay to irrelevance. Should ShipHolder facts (crew gossip) serve as
a secondary source before the HomePort fallback? If so, should crew gossip
(ShipHolder) carry less weight than captain knowledge (IndividualHolder)?

**Open question 2:** NPC decision-making currently runs in `RuleBasedDecisionProvider`
which is synchronous and stateless. Adding knowledge lookups per-NPC per-tick
could be expensive with 30+ individuals and hundreds of facts. Should NPC
knowledge queries be cached per-tick, or is the scale small enough that direct
lookups are acceptable? What is the performance boundary where this matters?

---

### 5B — NPC Quest Participation

NPCs with combat-capable roles (PirateCaptain, NavalOfficer, Privateer) should
detect and pursue contract quests through the same knowledge-gated mechanism as
the player.

**5B-1: NPC contract detection**

Each tick, scan eligible NPCs' IndividualHolder for ContractClaim facts with
confidence > 0.40 and supporting intel > 0.50 (same gates as player).

**5B-2: NPC contract pursuit model**

Lightweight `NpcContractPursuit` — not a full QuestInstance:
- PursuerId (IndividualId), Contract (ContractClaim)
- Status: Routing | Engaging | Abandoned
- No branches, no LLM dialogue, no player UI

**5B-3: NPC contract resolution**

- Pursuer's ship routes toward target using knowledge-gated routing (5A)
- Same-region arrival: stat-check resolution (simplified combat)
- Intel confidence drop below 0.30: pursuit abandoned
- Success: ContractFulfilled event, NPC paid, quest expires for all pursuers
- Pursuit events propagate as gossip: "Captain X is hunting Ship Y"

**Open question 3:** If both a player and an NPC are pursuing the same contract,
and the NPC fulfils it first, the player's quest expires. This creates
competitive pressure — good for a living world, but potentially frustrating
if the player invested significant gold in investigation. Should the player
receive partial compensation (e.g., the issuing faction acknowledges the
player's intel contribution), or is "you were too slow" the intended experience?

**Open question 4:** NPC pursuit routing introduces a new category of
knowledge-driven movement: goal-directed navigation toward a known target
location. Currently ShipMovementSystem only handles trade routing and
HomePort fallback. Should pursuit routing be a new RoutingDestination variant
(e.g., `PursuitRoute(ShipId target, RegionId lastKnownRegion)`), or should it
reuse the existing `RoutingDestination` with the target's last-known region as
the destination port?

**Open question 5:** Which NPC roles should be eligible for contract pursuit?
The proposal limits it to PirateCaptain, NavalOfficer, Privateer. Should
Smugglers be eligible (they have ships and motivation)? Should PortMerchants
be excluded even if they hold a ContractClaim (they are traders, not fighters)?
What about Informants — could they "pursue" investigation-type contracts
(TargetDead condition via assassination) rather than naval combat?

---

### 5C — Knowledge Conflict as Gameplay

Knowledge conflicts are detected and stored but never surfaced or acted upon.

**5C-1: Player conflict surfacing**

When KnowledgeConflictDetected fires for PlayerHolder:
- Tag conflict as player-visible
- Console `knowledge` command shows competing claims with confidence bars
- Player can investigate to resolve (existing InvestigateCommand infrastructure)

**5C-2: NPC conflict behaviour**

When an NPC's IndividualHolder contains conflicting facts:
- Dominant fact (highest confidence) drives decisions
- Near-tied conflicts (ConfidenceSpread < 0.15): NPC delays action 1–3 ticks
- Captains with conflicting RouteHazardClaims choose conservative routes

**5C-3: Conflict-triggered quest templates**

New quest category: triggered by KnowledgeConflictDetected rather than
ContractClaim. Examples:
- "Two sources disagree about the Silver Fleet's location — investigate or gamble"
- "Your intelligence contradicts the governor's briefing — who is lying?"

**Open question 6:** Should knowledge conflicts auto-resolve when one fact's
confidence decays below a threshold (e.g., ConfidenceSpread > 0.40 means
the weaker fact is effectively dead), or should they persist until explicitly
superseded by new information? Auto-resolution is simpler but means the player
never needs to actively investigate conflicts — they just wait.

**Open question 7:** Conflict-triggered quests represent a new trigger mechanism
(QuestCondition evaluates KnowledgeConflict records, not individual facts).
Should QuestCondition be extended with a `ConflictCondition` variant, or should
the system synthesize a virtual "ConflictFact" claim type that can be evaluated
by the existing FactCondition predicates? The former is cleaner; the latter
avoids changing the condition tree model.

---

### 5D — Multi-Phase Quest Framework

Replace the flat state machine with a phase graph for complex, multi-step quests.

**5D-1: QuestPhase model**

Each phase has:
- KnowledgeGate (QuestCondition that must be satisfied to advance)
- Optional LocationGate (physical presence requirement)
- Branches (choices available in this phase)
- AutoAdvance flag (advances when gate satisfied, no player choice needed)

QuestTemplate gains ordered Phases list. Single-phase = backward compatible
with existing flat templates.

**5D-2: Phase advancement logic**

Each tick for active multi-phase quests:
1. Evaluate current phase's KnowledgeGate against player facts
2. AutoAdvance phases transition immediately
3. Non-AutoAdvance phases present branches to player
4. Final phase completion resolves quest normally

**5D-3: Two exemplar multi-phase templates**

"The Lost Manifest" (treasure hunt, 3 phases):
- Rumour → Verify → Expedition

"The Governor's Conspiracy" (political intrigue, 3 phases):
- Tip → Gather Evidence → Consequences

**Open question 8:** Multi-phase quests create a problem with knowledge decay:
a quest triggered by a low-confidence rumour in Phase 1 may require
high-confidence verification in Phase 2. If the original trigger fact decays
below the Phase 2 gate while the player is travelling to investigate, the quest
becomes impossible to advance. Should active quest instances "pin" their trigger
facts (exempt from decay while the quest is active), or should the player be
expected to re-acquire the knowledge through investigation? Pinning is
convenient but undermines the decay mechanic. Re-acquisition is thematic but
potentially frustrating.

**Open question 9:** Should NPCs be able to participate in multi-phase quests
(5B), or should NPC pursuit remain limited to flat contract quests? Multi-phase
NPC quests would require NPCs to autonomously gather information across phases,
which implies a planning/goal system more sophisticated than the current
RuleBasedDecisionProvider. Is this a natural extension of 5A, or does it require
a fundamentally different NPC architecture?

---

### 5E — Claim Circulation Improvements

Several claim types are defined but never enter circulation.

**5E-1: RouteHazardClaim seeding**

Storm damage and pirate encounter events seed RouteHazardClaim into the affected
ship's ShipHolder. Propagates to port on next dock.

**5E-2: IndividualAllegianceClaim discovery paths**

Currently only revealed on death. Add:
- Remote investigation of individual with ClaimedFactionId != FactionId can reveal
- Brokers can sell allegiance facts
- Faction counter-intelligence (IntelligenceCapability roll) can detect enemy
  infiltrators and emit allegiance facts into FactionHolder

**5E-3: FactionIntentionClaim leak mechanic**

Secret sensitivity means 0% passive propagation. Add:
- Low IntelligenceCapability (< 0.35): 2%/tick chance that an active intention
  leaks to a random controlled port at Restricted sensitivity
- Creates organic discovery without requiring direct investigation

**Open question 10:** The leak mechanic (5E-3) means low-capability factions
(pirates at 0.25–0.30) almost certainly leak their intentions within 30–40 ticks.
This is thematically appropriate (loose lips) but mechanically one-directional —
only low-cap factions leak. Should high-capability factions also leak under
specific conditions (e.g., treasury crisis, lost war, DeceptionExposed event),
or is "intelligence capability = operational security" a sufficient model?

---

## Evaluation instructions

For each of the five sub-proposals (5A through 5E) and each open question:

1. Identify any architectural conflicts with the existing tick ordering, data
   model, or event flow as described in the architecture reference above
2. Answer each open question with a **concrete decision and rationale**
3. Flag any missing data model additions, new event types, or world seeding
   changes not mentioned in the proposal
4. Rate each sub-proposal as: **Solid** (implement as described), **Revise**
   (specific changes needed, state what), or **Rethink** (fundamental problem)
5. For any proposal rated Revise or Rethink: provide a concrete alternative
   that achieves the same goal within the stated architectural constraints

For each open question, either provide a concrete design decision (with rationale)
or explicitly argue for deliberate deferral with the specific condition that would
trigger revisiting it. Vague deferrals are not acceptable.

Do not propose wholesale rewrites. The architecture is established.
Focus on what is missing or wrong in the specific proposals above.
