# Ahoy — Quest SDD Rebuttal: Pushback & Deferrals

You submitted a System Design Document (SDD) proposing an Emergent Quest Architecture
for Ahoy, a C# .NET 10 pirate sandbox simulation. We are implementing Phase 1 of your
proposal but have pushed back on several points and deferred others. This document asks
you to review each decision and tell us where you believe we are wrong.

**Architecture quick reference:**
- Tick = 1 day. 8 systems: Weather → ShipMovement → Economy → Faction → IndividualLifecycle →
  EventPropagation → Knowledge → Quest
- KnowledgeFact: typed claim (discriminated union), Confidence (0–1, decays per tick + per hop),
  Sensitivity (Public 30% spread / Restricted 10% / Secret 0%), SourceHolder, CorroboratingFactionIds
- 12 authored QuestTemplates with FactCondition/AndCondition/OrCondition trigger trees
- FactionGoal types: ExpandTerritory, SuppressPiracy, BuildNavy, AccumulateTreasury,
  NegotiateTreaty, RaidShippingLane, EstablishHaven, EspionageGoal
- IndividualStatusClaim, ShipStatusClaim, ContractClaim (Phase 1) are live or planned

---

## Pushback 1 — "Deprecate QuestTemplate Entirely"

**Your proposal:** Replace all 12 authored templates with emergent ContractClaims.
No more scripted branches or hardcoded trigger conditions.

**Our decision:** Keep QuestTemplate in parallel. Emergent contracts fill the long tail;
authored templates provide narrative specificity and voice.

**Our rationale:**
- ContractClaim generates mechanical quests (bounty on ship X, assassinate governor Y).
  It cannot generate the narrative texture of "the governor's quill" — a quest about
  political intrigue with multiple ideologically distinct resolution paths, custom NPC
  dialogue, and named characters.
- Pure emergence produces behavioural variety, not narrative quality. At this stage,
  the authored templates are the primary source of story beats that feel authored rather
  than mechanical.
- Removing templates would require the LLM narrative layer to generate all quest text
  at runtime — that layer (KnowledgeNarrator) is not yet wired into the quest system.

**Question for you:** Is there a model where ContractClaim generates the mechanical
scaffolding (target, condition, reward) but a narrative annotation layer wraps it
with authored-quality dialogue and flavour — without hardcoding the specific targets?
How would you recommend bridging the gap between systemic generation and narrative quality?

---

## Pushback 2 — OceanPOI, FleetState, PortConditionFlags, TrueFactionId deferred

**Your proposal:** Add all four to support the full range of quest flavours described
(Q-Ships, Plague, Convoy, Secret Defection).

**Our decision:** Deferred entirely from Phase 1.

**Our rationale:**
- Each of these is a world-model system in its own right. Adding them in the same pass
  as the quest architecture rework risks destabilising the existing systems.
- Specifically: FleetState requires changes to ShipMovementSystem (multi-ship movement);
  OceanPOI requires new location types in the ShipLocation discriminated union;
  PortConditionFlags requires EconomySystem and IndividualLifecycleSystem to react to them;
  TrueFactionId requires a deception detection pipeline.
- The ContractClaim + QuestSystem changes alone will prove the emergent architecture is
  viable before we expand the world model.

**Question for you:** Of the four, which single addition unlocks the most quest variety
with the least systemic risk, and why? Rank them: OceanPOI, FleetState,
PortConditionFlags, TrueFactionId.

---

## Pushback 3 — NPC Participation in Contracts deferred

**Your proposal:** NPC Privateers with a ContractClaim + ShipLocationClaim in their
IndividualHolder should autonomously route to the target and compete with the player.

**Our decision:** Deferred to Phase 2.

**Our rationale:**
- The routing system (`ShipMovementSystem.AssignNpcRoute`) currently picks destinations
  by evaluating `PortPriceClaim` profitability. Wiring ContractClaim valuation into this
  requires a new profit/utility model that competes with trade routing logic.
- Without a combined expected-value calculation (`max(tradeProfit, contractReward)`),
  a naive implementation would make all Privateers abandon trade whenever any bounty exists.
- The rival system is one of the most compelling emergent behaviours in your SDD. We want
  to get it right rather than ship a broken version.

**Question for you:** What is the minimum viable utility model for NPC contract-chasing
that doesn't break trade routing? Should the NPC evaluate `contractReward > expectedTradeProfit`
on a per-tick basis, or should contract-chasing be a separate role assigned only to
Privateer-role Individuals rather than merchant captains?

---

## Pushback 4 — FabricateFactCommand deferred

**Your proposal:** Players spend gold to inject `IsDisinformation=true` facts into
a port's knowledge pool, manipulating the ContractClaim economy (fake bounties, false
death notices).

**Our decision:** Deferred to Phase 2.

**Our rationale:**
- The disinformation infrastructure already exists (`IsDisinformation` flag,
  `InjectDisinformationCorrection`, `BurnAgentCommand`). FabricateFactCommand is
  architecturally straightforward.
- However, seeding a false ContractClaim (fake bounty) creates an economic attack vector
  that needs design work: what prevents the player from trivially farming gold by
  fabricating a ContractClaim pointing at a ship they already know is destroyed?
  The detection/consequence system (faction realises it was defrauded → relationship hit,
  guards alerted) needs to be designed before the command exists.
- We want the deception economy to be coherent, not bolted on.

**Question for you:** Design the minimal consequence system for detected fabrication —
what should happen when a faction discovers their ContractClaim was fraudulently fulfilled
(i.e. the target was already dead when the player claimed the reward)?

---

## Summary of What IS Being Implemented (Phase 1)

For reference, Phase 1 includes:
- `ContractClaim(IssuerId, IssuerFactionId, TargetSubjectKey, Condition, GoldReward)` as
  a new KnowledgeFact type, Sensitivity=Public, propagates at 30%/tick
- `FactionSystem.SeedContracts()`: SuppressPiracy goal → bounties on pirate ships
- `QuestSystem.ScanForContractQuests()`: activates quest when player holds
  (ContractClaim confidence>0.40) + (matching intel confidence>0.50); `LostTrail` status
  if intel decays; `TargetGone` status if world resolves contract without player
- `ClaimContractRewardCommand` for payment collection at issuer's port
- `ContractFulfilled` WorldEvent → PortHolder (Restricted) when paid

Authored QuestTemplates continue operating unchanged in parallel.
