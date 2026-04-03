# Implementation Plan — Group 2 & 3

Date: 2026-04-03

Sequenced into three groups. Each group compiles cleanly before the next begins.
Within Group B, items are largely independent and can proceed in parallel.

---

## Group A — Pure Data Model Additions

No cross-system behaviour. Do all of these first in a single pass.

### A1 — `KnowledgeFact.OriginatingAgentId`
**File:** `src/Ahoy.Simulation/State/KnowledgeStore.cs`

```csharp
public IndividualId? OriginatingAgentId { get; init; }
```

Immutable; set only at disinformation injection time. Required for burning to trace
lies back to the seeding individual regardless of how many PortHolders the fact
passed through. Null for all organically-observed facts.

### A2 — `Individual.IsCompromised` and `Individual.CurrentGold`
**File:** `src/Ahoy.Simulation/State/Individual.cs`

```csharp
public bool IsCompromised { get; set; }
public int CurrentGold { get; set; }
```

`IsCompromised`: burned agents remain alive but inactive. FactionSystem starts a
30-tick replacement timer when set true.
`CurrentGold`: merchant income/expenditure; broker wallet. Integer gold pieces.

### A3 — `Ship.TicksDockedAtCurrentPort`
**File:** `src/Ahoy.Simulation/State/Ship.cs`

```csharp
public int TicksDockedAtCurrentPort { get; set; }
```

Incremented by ShipMovementSystem each tick while docked. Reset to 0 on arrival
and departure. Used to compute idle crew expenditure.

### A4 — `Faction.IntelligenceCapability` and `EspionageGoal`
**File:** `src/Ahoy.Simulation/State/Faction.cs`

```csharp
public float IntelligenceCapability { get; set; }  // 0.0–1.0, clamped [0.10, 0.95]
```

Add to the FactionGoal hierarchy at the bottom of the file:
```csharp
public record EspionageGoal : FactionGoal;
```

Three effects on IntelligenceCapability:
- Disinformation BaseConfidence = `0.70 + IntelligenceCapability * 0.25` (range 0.70–0.95)
- `DeceptionExposed` detection gate — random roll > capability required
- Remote investigation yield reduced proportionally

### A5 — `PendingInvestigation` and `WorldState.PendingInvestigations`
**File:** `src/Ahoy.Simulation/State/WorldState.cs`

```csharp
public sealed class PendingInvestigation
{
    public required string SubjectKey { get; init; }
    public required int GoldCost { get; init; }
    public required int SubmittedOnTick { get; init; }
    public int ResolvesOnTick => SubmittedOnTick + 5;
}
```

Add to WorldState:
```csharp
public List<PendingInvestigation> PendingInvestigations { get; } = new();
```

Fixed 5-tick resolution delay. EventPropagationSystem resolves when
`CurrentTick >= ResolvesOnTick`.

### A6 — New PlayerCommand variants
**File:** `src/Ahoy.Simulation/Engine/PlayerCommand.cs`

Under the `// ---- Knowledge ----` section, append:

```csharp
public record InvestigateLocalCommand(string SubjectKey) : PlayerCommand;
public record InvestigateRemoteCommand(string SubjectKey, int GoldCost) : PlayerCommand;
public record SellFactCommand(IndividualId BrokerId, KnowledgeFactId FactId) : PlayerCommand;
public record BurnAgentCommand(IndividualId AgentId) : PlayerCommand;
```

### A7 — New WorldEvents
**File:** `src/Ahoy.Simulation/Events/WorldEvent.cs`

```csharp
public record InvestigationResolved(
    WorldDate Date, SimulationLod SourceLod,
    string SubjectKey,
    KnowledgeFactId? ResultFactId,
    bool WasSuccessful) : WorldEvent(Date, SourceLod);

public record AgentBurned(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId AgentId,
    FactionId OwningFactionId) : WorldEvent(Date, SourceLod);

public record AgentReplaced(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId OldAgentId,
    IndividualId NewAgentId,
    FactionId OwningFactionId) : WorldEvent(Date, SourceLod);
```

---

## Group B — System Logic (depends on Group A)

### B1 — `FactionSystem`: IntelligenceCapability mutation + EspionageGoal + burn timers
**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`
**Depends on:** A4

1. Add private field:
   ```csharp
   private readonly Dictionary<IndividualId, int> _burnReplacementTimers = new();
   ```

2. `ScoreGoal`: add `EspionageGoal => faction.IntelligenceCapability < 0.80f ? 0.70f : 0.20f`

3. `GenerateCandidateGoals`: add `EspionageGoal` for Colonial factions.

4. `SeedIntentionFact`: add summary `"EspionageGoal" => "Expanding intelligence operations"`

5. `SeedDisinformation`: replace fixed confidence with
   `0.70f + faction.IntelligenceCapability * 0.25f`; set `OriginatingAgentId = baitShip.CaptainId`

6. Add `TickIntelligence(Faction, FactionId, SimulationContext)`:
   - `+0.005f/tick` if EspionageGoal active (clamp to 0.95)
   - `-0.05f` when DeceptionExposed stimulus received (clamp to 0.10)

7. Add `TickBurnReplacements(WorldState, SimulationContext, IEventEmitter)`:
   - Scan `state.Individuals` for `IsAlive && IsCompromised` individuals whose faction matches
   - On timer expiry: set old agent `IsAlive = false`, spawn replacement Individual with same
     Role/FactionId/LocationPortId/HomePortId, emit `AgentReplaced`

8. Wire both methods into `Tick` after `TickRelationshipDecay`.

### B2 — `KnowledgeSystem`: broker passive skimming
**File:** `src/Ahoy.Simulation/Systems/KnowledgeSystem.cs`
**Depends on:** A2

Add `SkimBrokerFacts(WorldState state, int tick)`, called after `PropagateViaArrivingShips`:

- For each alive `KnowledgeBroker` Individual with a `LocationPortId`:
  - For each PortHolder fact with `Confidence > 0.65f` and not superseded:
    - If broker's IndividualHolder has no non-superseded fact for the same SubjectKey:
      - Copy with `HopCount + 1`, confidence reduced by hop penalty, `SourceHolder = PortHolder`

Also: in `ShareFacts`, when propagating a fact, preserve `OriginatingAgentId`:
```csharp
OriginatingAgentId = sourceFact.OriginatingAgentId,
```

### B3 — `EventPropagationSystem`: remote investigation resolution
**File:** `src/Ahoy.Simulation/Systems/EventPropagationSystem.cs`
**Depends on:** A4, A5, A7

Add `ResolveRemoteInvestigations(WorldState, SimulationContext, IEventEmitter)` called at
start of `Tick`:

- For each `PendingInvestigation` where `ResolvesOnTick <= tickNumber`:
  - Identify target faction from SubjectKey
  - Roll `_rng.NextDouble()`: success if `roll > targetFaction.IntelligenceCapability`
  - Success: find best matching fact in FactionHolder/nearest PortHolder,
    copy to PlayerHolder with `HopCount + 2`, `Confidence * 0.60f`,
    emit `InvestigationResolved(WasSuccessful: true)`
  - Failure: emit `InvestigationResolved(WasSuccessful: false, ResultFactId: null)`
  - Remove from `PendingInvestigations`

Also add `DeceptionExposed` event handler: queue `FactionStimulus("DeceptionExposed", 1f)`
for the deceiving faction when the event propagates.

### B4 — `ShipMovementSystem`: crew upkeep + docked counter + knowledge-gated departure
**File:** `src/Ahoy.Simulation/Systems/ShipMovementSystem.cs`
**Depends on:** A2, A3

1. `ArriveInRegion` (inside dock branch): `ship.TicksDockedAtCurrentPort = 0`

2. `TryDepartPort` (at start): `ship.TicksDockedAtCurrentPort++`

3. Add `DeductCrewUpkeep(Ship ship, WorldState state)` — called once per ship at top
   of tick loop:
   - `totalCost = ship.CurrentCrew * 1` (1g/crew/tick constant)
   - Deduct from `captain.CurrentGold` if captain set, else from `ship.GoldOnBoard`

4. Knowledge-gated departure gate in `TryDepartPort`: only call `AssignNpcRoute` if
   `IndividualHolder(captain)` has any non-superseded `PortPriceClaim` with
   `Confidence > 0.40f` **OR** `ship.TicksDockedAtCurrentPort >= 5` (HomePort fallback
   fires via existing `AssignNpcRoute` random fallback path).

### B5 — `SimulationEngine`: new command handlers
**File:** `src/Ahoy.Simulation/Engine/SimulationEngine.cs`
**Depends on:** A4, A5, A6, A7

Add three cases to `ApplyCommand`:

**`InvestigateLocalCommand`:**
- Require player ship is `AtPort`
- Prior-fact gate: PlayerHolder must have non-superseded fact for SubjectKey
- Scan `PortHolder(currentPort)` for matching SubjectKey
- Copy best match to PlayerHolder with `HopCount + 1`
- Emit `InvestigationResolved`

**`InvestigateRemoteCommand`:**
- Prior-fact gate: same as above
- Deduct `cmd.GoldCost` from `state.Player.PersonalGold`
- Append `PendingInvestigation` to `state.PendingInvestigations`
- No event yet — EventPropagationSystem emits on resolution (B3)

**`SellFactCommand`:**
- Validate broker: `Role == KnowledgeBroker`, at same port as player
- Find fact in PlayerHolder, validate `Confidence >= 0.60f`
- Deduplication: broker must not already hold a non-superseded fact for same SubjectKey
- Draw price from `broker.FactionId` → `faction.TreasuryGold`
- Copy fact to `IndividualHolder(broker)` with `HopCount + 1`
- Credit `state.Player.PersonalGold`

**`BurnAgentCommand`:**
- Validate `Individual.IsAlive && !IsCompromised`
- Require PlayerHolder has any fact with `OriginatingAgentId == cmd.AgentId`
  (player must have traced the lie — prior-fact gate for burning)
- Set `individual.IsCompromised = true`
- Apply -0.30f confidence penalty to all facts in `IndividualHolder(agentId)`
- Emit `AgentBurned`
- Inject `AssetExposedClaim` into `PortHolder(individual.LocationPortId)` with
  `Confidence = 0.85f`, `Sensitivity = Secret`
- FactionSystem picks up burn via `TickBurnReplacements` scan (no direct call needed)

### B6 — `EconomySystem`: captain income on trade completion
**File:** `src/Ahoy.Simulation/Systems/EconomySystem.cs`
**Depends on:** A2

In `ExecuteMerchantTrade`, after the SELL block:

```csharp
private const float CaptainIncomeFraction = 0.10f;

// Inside SELL block, after ship.GoldOnBoard += revenue:
if (ship.CaptainId.HasValue &&
    state.Individuals.TryGetValue(ship.CaptainId.Value, out var captain))
{
    captain.CurrentGold += (int)(revenue * CaptainIncomeFraction);
}
```

`EconomySystem` does NOT touch `RoutingDestination`. The 1-tick lag before re-routing
is a natural consequence of the existing pipeline order (ShipMovement=2, Knowledge=7).

---

## Group C — World Seeding (depends on A4, A2)

### C1 — Seed `IntelligenceCapability` in `CaribbeanWorldDefinition`
**File:** `src/Ahoy.WorldData/CaribbeanWorldDefinition.cs`

Add to each faction initializer in `DefineFactions`:

| Faction | IntelligenceCapability |
|---|---|
| Spain | 0.55f |
| England | 0.45f |
| France | 0.40f |
| Netherlands | 0.50f |
| Brethren of the Blood | 0.30f |
| Silver Coast Rovers | 0.25f |

### C2 — Bootstrap broker and captain starting wealth
**File:** `src/Ahoy.WorldData/CaribbeanWorldDefinition.cs`

In `AddMerchant`: set `captainIndividual.CurrentGold = 150 + rng.Next(0, 100)` (150–250g).

In `AddBroker` (or wherever KnowledgeBroker individuals are created): set
`individual.CurrentGold = 200` and seed their `IndividualHolder` with a
`FactionStrengthClaim` per faction at `Confidence = 0.55–0.70f`, `HopCount = 1`,
`Sensitivity = Restricted`, `SourceHolder = FactionHolder(factionId)`.

---

## Sequencing Summary

```
Group A (data model — all independent)
  ↓
Group B (logic — parallel within group, dependencies noted)
  B4 + B6  (merchant lifecycle — self-contained)
  B1 + B2  (faction intelligence + broker skimming — self-contained)
  B3 + B5  (investigation commands + resolution — ship together for E2E)
  B6       (burning — after OriginatingAgentId has been in production)
  ↓
Group C (world seeding — alongside any B item once A4 + A2 exist)
```

## Key Notes

- **Prior-fact gate**: use `KnowledgeFact.GetSubjectKey` to compare against
  `state.Knowledge.GetFacts(new PlayerHolder())` filtered to non-superseded.
- **OriginatingAgentId propagation**: one-line addition in `ShareFacts` when creating
  propagated copies — add in the same PR as B5 burning logic.
- **Burn replacement timer**: `FactionSystem.TickBurnReplacements` scans
  `state.Individuals` directly rather than listening for events, avoiding cross-system
  event ordering issues.
- **IntelligenceCapability gate**: `_rng.NextDouble() > targetFaction.IntelligenceCapability`.
  Spain (0.55) → 45% success rate. Pirates (0.25) → 75% success rate.
- **Bankruptcy → role change**: deferred. `MerchantBankruptEvent` and
  Merchant→PirateCaptain role change is a later pass once PersonalWealth is stable.
