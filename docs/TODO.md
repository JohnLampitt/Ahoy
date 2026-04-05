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

### Caribbean Food Economy Collapse
- **System:** EconomySystem / `InjectExternalFood`, world data archetypes
- **Symptom:** At 365 ticks, most major ports hit 0% prosperity. Small
  `GeneralTrade` ports have zero food supply despite producing it. Only ports
  receiving external food imports survive. Three ports reach population floor
  (100) with Famine flag.
- **Diagnosis from report-365.txt:**
  - Major ports (Havana, Cartagena, Bridgetown) have food supply but prosperity
    still crashes — food imports cover ~67% of need, local production covers
    the rest only at high prosperity. Once prosperity dips, production drops
    below need and the spiral starts.
  - Small GeneralTrade ports (San Juan, Santo Domingo) have `BaseProduction[Food]=8`
    but zero food on hand — production at population-scaled rate is consumed
    immediately with nothing left over.
  - Merchant arbitrage routing works (merchants buy cheap food and deliver it)
    but there aren't enough food-producing ports or merchants to serve the
    entire Caribbean. The food deficit is structural.
- **Root cause:** The Caribbean historically imported most of its food. The
  `InjectExternalFood` placeholder partially models this but the numbers
  aren't tuned. Additionally, only `GeneralTrade` archetype produces food —
  all other archetypes (TreasurePort, SugarProducer, Entrepot, PirateHaven,
  TimberPort) have food BaseProduction added but at rates too low for their
  population.
- **Fix options (choose one or combine):**
  1. Increase external food import rate to fully cover the deficit (simple but
     removes food scarcity as a gameplay mechanic)
  2. Add a proper supply convoy system where faction ships physically carry
     food from off-map, can be intercepted by pirates (best long-term)
  3. Increase food BaseProduction rates at each archetype to make more ports
     self-sufficient (reduces need for trade, less interesting)
  4. Add food production as a function of port hinterland/farmland — a new
     field on Port representing agricultural capacity independent of archetype
- **Workaround:** Mean-reversion band-aid `(50 - prosperity) * 0.005` prevents
  total collapse but masks the structural deficit.

### Export Cap Is a Hack — Needs Physical Convoy Model
- **System:** EconomySystem / `TickExports`
- **Symptom:** Export hubs are capped at 3 units per good per tick via a hard
  constant `MaxExportPerGoodPerTick`. This prevents gold hyperinflation but
  is an arbitrary throttle with no simulation backing.
- **Problem:** The cap doesn't respond to world conditions — a blockaded hub
  still exports 3 units/tick because the cap is checked against local supply,
  not shipping lane safety. A booming hub with massive surplus can't export
  more than a struggling one.
- **Fix:** Replace the hard cap with a physical convoy system. Export hubs
  load surplus goods onto faction ships that physically sail to an off-map
  "Europe" waypoint. Gold returns on the next convoy. Export rate is naturally
  limited by: number of available ships, travel time, pirate interception.
  Blockading a hub's shipping lanes physically prevents exports.
- **Interim:** The 3-unit cap is tuned to prevent inflation at current
  European price levels (~50g for Gold, 8g for Sugar). If prices or port
  count change, this needs retuning.

### Remove InjectExternalFood Once Export Mint Sustains Population
- **System:** EconomySystem / `InjectExternalFood`
- **Symptom:** Food is magically injected into ports each tick with no economic
  cost. This bypasses the zero-sum economy — food appears from nowhere.
- **Current state:** Kept at `pop/150` rate as a safety net. Independent ports
  get 50%. Removing prematurely causes mass starvation because the export mint
  + merchant delivery loop isn't yet sufficient to sustain all ports.
- **Removal condition:** Run the 5-year deep-time test WITHOUT `InjectExternalFood`.
  If average prosperity stays above 15% and population stays above 80% of
  starting levels, the export loop is self-sustaining and the placeholder can go.
- **Fallback:** If removal fails, keep a minimal "fishing/foraging" baseline
  (~20% of current rate) representing local food production that doesn't
  require gold or trade. This is economically free but represents subsistence.

### ~~Gold Inflation: Ports Have No Treasury~~ (RESOLVED — Group 10)
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

## Future — Design Ideas

### Independent Port Federation / Republic Mechanic
- **Context:** Group 9 (Colonial Triage) introduces individual port defection.
  When a faction denies relief, a governor can flip their port to Independent.
  Currently each port defects individually.
- **Idea:** Adjacent independent ports could voluntarily merge into a new
  "Independent Republic" faction entity. This would require:
  - A new faction type (Republic) with elected leadership, shared treasury
  - Mechanic for independent governors to propose federation (relationship > +30
    between governors, both independent, adjacent regions)
  - Unified AI for the republic: collective defense, shared trade policy
  - Diplomatic recognition from colonial factions (treaties, trade agreements)
- **Gameplay value:** Creates a third force in the Caribbean — not colonial, not
  pirate. The player could be the broker who unifies scattered independent ports
  into a viable republic, or the privateer hired to break one apart.
- **Deferred until:** Group 9 is implemented and individual defection is proven.
  Federation adds significant faction system complexity that isn't justified
  until we see how often multi-port independence clusters emerge organically.

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
