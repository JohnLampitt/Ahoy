# Decision Record — Group 2 & 3 Implementation

Date: 2026-04-03
Status: Approved for implementation

## 2a — InvestigateCommand

- **Two variants**: `InvestigateLocalCommand(string SubjectKey)` — physical presence, HopCount=0, Confidence=0.90, free. `InvestigateRemoteCommand(string SubjectKey, int GoldCost)` — spy network, HopCount=1, fallible.
- **Prior-fact gate**: Player must hold at least one KnowledgeFact for SubjectKey (any confidence, any state including superseded/IsDisinformation) to issue either command.
- **Pricing**: `BaseClaimTypeCost + (DistanceInTicks × SpyTravelRate)`. Distance computed from player's best-known location of the subject (their prior fact). Prices the spy's effort, not the classification.
- **Async delivery**: `PendingInvestigation` list on `WorldState`. `EventPropagationSystem` deposits the result into `PlayerHolder` when `CurrentTick >= TickCompletion`.
- **Failure**: Gated by target faction's `IntelligenceCapability`. On failure: gold consumed, `FailedInvestigationClaim` (HopCount=1, Confidence=1.0) deposited into PlayerHolder, supersedes trigger fact.

## 2b — KnowledgeBroker

- **Inventory**: Broker's `IndividualHolder` facts. No separate data structure.
- **Bootstrapping**: Seeded at world creation in `CaribbeanWorldDefinition` with a snapshot of their faction's high-confidence facts. No special lifecycle hook.
- **Secret gap**: Intentional — static brokers never organically acquire Secret facts (0% propagation). Secrets only via `InvestigateRemoteCommand`.
- **Passive skimming**: KnowledgeSystem skims PortHolder facts above a confidence threshold into broker IndividualHolder each tick. Keeps inventory alive past bootstrap decay.
- **SellFactCommand filters**: Confidence floor (e.g. 0.60), deduplication (broker already holds equal/higher confidence on SubjectKey), sensitivity filter. No Diminishing Returns Ledger — deduplication + confidence floor sufficient.
- **Broker wallet**: Purchases draw from faction `TreasuryGold`. No per-individual wallet.

## 2c — Burning Mechanic

- **OriginatingAgentId**: Add `IndividualId? OriginatingAgentId` to `KnowledgeFact`. Set (immutable) when a faction injects disinformation via an Individual operative. Does not replace `SourceHolder` — hop-count logic unaffected.
- **IsCompromised**: New `bool IsCompromised` field on `Individual`. Set true when InvestigateCommand traces a lie back to them via `OriginatingAgentId`.
- **Inventory penalty**: All facts in burned individual's `IndividualHolder` take immediate -0.30 confidence penalty. Full inventory, not scoped — epistemic contamination is total from receiver's perspective.
- **No cascade**: Penalty does not propagate to copies held by other entities. In-the-wild copies decay naturally.
- **Faction response**: FactionSystem on `IndividualCompromised` event: (a) drop player's faction standing, (b) start 30-tick `PendingAssetReplacement` timer, (c) IndividualLifecycleSystem spawns replacement Informant after timer elapses.
- **Public notification**: `AssetExposedClaim` emitted into local PortHolder so player hears the rumor.
- **Individual state**: `IsAlive=true`, `IsCompromised=true` — burned individuals remain visible but inactive.

## 2d — Merchant Lifecycle

- **PersonalWealth**: `int CurrentGold` on `Individual`. EconomySystem injects income on completed trade run. ShipMovementSystem deducts per-tick crew upkeep.
- **Knowledge-gated departure**: Captain departure decision chain: (1) IndividualHolder has PortPriceClaim for accessible port with better margin → route there. (2) FactionHolder has such a claim → route there. (3) Fallback to `HomePortId`.
- **Pipeline**: `RoutingDestination` is already cleared by ShipMovementSystem on arrival (`ArriveInRegion` line 145). `TryDepartPort` → `AssignNpcRoute` fires on the next tick, after KnowledgeSystem has seeded the captain's IndividualHolder with the new port's prices. The 1-tick delay is built-in naturally — EconomySystem does NOT touch `RoutingDestination`.
- **TicksDockedAtCurrentPort**: Add to `Ship`. Used to compute idle crew expenditure.
- **Bankruptcy → role change**: Deferred. `MerchantBankruptEvent` → role change to PirateCaptain/Smuggler is a later pass once PersonalWealth is stable.

## Group 3 — FactionIntelligenceCapability

- **Field**: `float IntelligenceCapability` (0.0–1.0) on `Faction`.
- **Three effects**: (1) scales BaseConfidence of disinformation the faction seeds; (2) `Random.NextDouble() < IntelligenceCapability` gates whether `DeceptionExposed` reaches FactionHolder; (3) reduces investigation yield (normal=0.90, against high-cap faction → lower).
- **No gold-cost scaling**: Yield reduction achieves the same gameplay fog without leaking the stat.
- **Initialisation**: Seeded per faction in `CaribbeanWorldDefinition` (e.g. Spain=0.8, England=0.65, France=0.65, Netherlands=0.60, Pirates=0.30).
- **Mutation**: FactionSystem owns. `+0.005/tick` while `EspionageGoal` active; `-0.05` on `DeceptionExposed` received. Clamped `[0.10, 0.95]`.
- **EspionageGoal**: New `FactionGoal` subtype needed to trigger the gain path.
- **LLM narrator**: Out of scope until `Ahoy.Simulation.LlmDecisions` is wired.
