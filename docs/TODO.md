# Ahoy — Known Technical Debt & TODOs

Tracked issues discovered during development and testing. Each item includes
the system it affects, the symptom, and the intended fix.

---

## Critical — Affects Simulation Correctness

### Knowledge GC: Fact Accumulation During Propagation
- **System:** KnowledgeSystem / `ShareFacts`
- **Symptom:** Over 1825 ticks, individual holders accumulate 10+ non-superseded
  facts for the same subject key (e.g., `Individual:{id}`). Each propagation
  source creates a new fact rather than corroborating/superseding the existing one.
- **Impact:** Memory growth, inflated conflict counts, distorted confidence scores.
- **Invariant:** Currently capped at >10 per subject per holder before flagging.
- **Fix:** `ShareFacts` should check for existing same-subject facts in the target
  holder and either corroborate (if same claim) or supersede (if different claim),
  not blindly create new facts. The `AddAndSupersede` path exists but `ShareFacts`
  bypasses it when the claim content differs slightly (e.g., different confidence).

### Gold Inflation: Ports Have No Treasury
- **System:** EconomySystem / `ExecuteMerchantTrade`
- **Symptom:** Gold is created from nothing when merchants sell goods — there's no
  check that the port can afford to pay. With inelastic essential pricing (up to 10×),
  large amounts of gold enter the economy per tick.
- **Impact:** Total gold grows unbounded; economic velocity invariant becomes
  meaningless.
- **Fix:** Add explicit `Port.Treasury` field. Trade revenue comes from the port's
  treasury, not thin air. Faction taxation replenishes port treasuries. When a port
  can't afford goods, trade doesn't happen (creating real scarcity pressure).

---

## High — Affects Game Balance

### Mean-Reversion Band-Aid Still Active
- **System:** EconomySystem / `ProduceAndConsume`
- **Symptom:** `prosperityDelta += (50f - port.Prosperity) * 0.005f` artificially
  prevents permanent economic collapse.
- **Impact:** Masks underlying economic loop failures. Ports recover from crises
  without merchant intervention.
- **Removal condition:** Remove once the merchant delivery loop (arbitrage routing +
  faction subsidies + external food imports) is proven sufficient via 5-year deep-time
  test WITHOUT the band-aid.
- **Location:** `EconomySystem.cs`, `ProduceAndConsume` method, marked with TODO comment.

### External Food Imports Are a Placeholder
- **System:** EconomySystem / `InjectExternalFood`
- **Symptom:** Fixed food injection per tick represents supply ships from Europe.
  Currently a flat `pop/150 × wealthMultiplier` with no simulation backing.
- **Impact:** Food appears from nowhere; no supply chain to disrupt.
- **Fix:** Replace with a proper inter-regional trade/convoy system where faction
  supply ships physically travel from "off-map" entry points, can be intercepted
  by pirates, and carry finite cargo. Blockades should cut off food imports.

---

## Medium — Affects Feature Completeness

### NPC Merchants Cannot Fulfil Delivery Contracts
- **System:** QuestSystem / `TryFulfillGoodsDelivery`
- **Symptom:** `GoodsDelivered` contracts only resolve for the player's fleet
  (checks `state.Player.FleetIds`). NPC merchants who deliver food to a starving
  port don't fulfil the contract.
- **Impact:** Faction-backed famine relief contracts go unfulfilled even when NPC
  merchants actually deliver the goods.
- **Fix:** Add NPC delivery contract checking — when an NPC merchant docks with
  cargo matching an active `GoodsDelivered` contract at that port, fulfil it.

### ExtortGoal Execution Not Implemented
- **System:** QuestSystem / EpistemicResolver
- **Symptom:** `ExtortGoal` type exists but has no tick logic — NPCs assigned
  extortion goals do nothing.
- **Impact:** Extortion trigger in IndividualLifecycleSystem is effectively dead code.
- **Fix:** Implement demand/response cycle: NPC routes to target's port, emits
  extortion demand event, target responds via RuleBasedDecisionProvider (pay or
  refuse), consequences applied.

### Ship Docking Invariant Not Enforced
- **System:** Ship / Port
- **Symptom:** Setting `ship.Location = new AtPort(portId)` requires also updating
  `port.DockedShips`, `ship.TicksDockedAtCurrentPort`, and `ship.ArrivedThisTick`.
  These are currently coordinated manually by callers.
- **Impact:** World init code and tests may create inconsistent docking state.
- **Fix:** Add `WorldState.DockShip(ShipId, PortId)` method that atomically sets
  all four fields.

### LLM Goal Selection Not Wired
- **System:** Decisions / `INpcGoalSelector`
- **Symptom:** Interface exists but no implementation. NPCs use rule-based goal
  selection only.
- **Impact:** NPC behaviour is rational but predictable — no personality-driven
  goal selection.
- **Fix:** Implement when rule-based path is proven. Requires `IDecisionLog` for
  deterministic replay (persist LLM responses by tick for reproducibility).

---

## Low — Cosmetic / Documentation

### SDD-Architecture Project Tree Outdated
- **File:** `docs/SDD-Architecture.md`
- **Symptom:** Project structure tree doesn't include `Quests/`, `GoalModels.cs`,
  `FactionStimulus.cs`, `OceanPoi.cs`, or the test project.
- **Fix:** Update the tree to match actual file structure.

### WorldContent-Caribbean Still Draft Status
- **File:** `docs/WorldContent-Caribbean.md`
- **Symptom:** Status says "Draft" but the content has been implemented.
- **Fix:** Cross-check against `CaribbeanWorldDefinition.cs` and promote status.
