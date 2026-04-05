# Ahoy — System Design: FactionSystem

> **Status:** Living document.
> **Version:** 0.1
> **Depends on:** SDD-WorldState.md, SDD-SimulationLOD.md, SDD-KnowledgeSystem.md

---

## 1. Overview

`FactionSystem` is the fourth system in the tick pipeline. It is responsible for:

- Updating faction resources (treasury, naval strength) each tick
- Evaluating and updating faction goals using utility scoring
- Acting on active goals (deploying naval strength, initiating conquests, pursuing diplomacy)
- Updating relationships between factions in response to world events
- Maintaining patrol allocations across regions, which drive regional safety ratings
- Emitting events for significant faction actions

Factions are **autonomous actors** — they pursue rational self-interest within their constraints, not scripted storylines. Wars start because conditions made war rational. Alliances form because shared threats made cooperation valuable. The player is one more actor in this system, not the cause of all motion.

---

## 2. Resource Model

### 2.1 Colonial Factions

Colonial factions derive income from the ports they control.

**Income sources:**
```
TaxIncome(port)     = port.Prosperity × port.TradeVolume × TaxRate
TradeDuties(region) = TradeVolumeThrough(region) × DutyRate
```

`TaxRate` and `DutyRate` are faction-level constants (can be modified by events — a desperate faction raises taxes, risking prosperity decline).

**Costs:**
```
NavalMaintenance = NavalStrength × CostPerStrengthUnit
GarrisonUpkeep   = sum(port.GarrisonStrength) × CostPerGarrisonUnit
```

**Naval strength decay:** if treasury falls below zero, `NavalStrength` decays by a percentage each tick (crews desert, ships decommission). This cannot be reversed immediately — rebuilding takes time and money. This is the mechanism behind the self-reinforcing decline loop.

```csharp
// Added to Faction
public int GarrisonStrength { get; internal set; }  // aggregate across all held ports
```

### 2.2 Pirate Brotherhoods

Pirate brotherhoods have a fundamentally different and more volatile revenue model.

**Income sources:**
```
RaidIncome  = sum(successful raids this tick × CapturedCargoValue × RaidCutRate)
HavenFees   = sum(merchant ships using haven ports this tick) × FeePerShip
```

`RaidCutRate` is the fraction of captured cargo value that flows to the brotherhood treasury (the rest goes to individual crews). `HavenFees` are paid by merchant captains who have purchased safe passage — not all merchants pay; it depends on their relationship with the brotherhood.

**Costs:**
```
FleetUpkeep = NavalStrength × CostPerStrengthUnit  // lower than colonial — pirate crews are cheaper
HavenUpkeep = sum(haven ports) × CostPerHaven
```

Pirate naval strength is more elastic than colonial: successful raiding attracts opportunistic crews (strength grows), prolonged failure scatters them (strength shrinks). This is modelled as a `MomentumModifier` on strength change:

```csharp
// Added to Faction (PirateBrotherhood only)
public float RaidingMomentum { get; internal set; }  // -1.0 to 1.0
// Positive: recent raiding success, strength grows faster
// Negative: recent losses or dry spells, strength decays faster
```

---

## 3. FactionGoal System

### 3.1 Goal Types

```csharp
// Ahoy.Simulation/State/FactionGoal.cs

public abstract record FactionGoal
{
    public int        Priority    { get; init; }    // 1–10; higher = more resources allocated
    public float      Progress    { get; internal set; }  // 0.0–1.0
    public GoalStatus Status      { get; internal set; } = GoalStatus.Active;
    public WorldDate  InitiatedAt { get; init; }
}

public enum GoalStatus { Active, Completed, Abandoned }

// Colonial + Pirate
public record ConquerPortGoal(PortId Target)                        : FactionGoal;
public record DefendPortGoal(PortId Port)                           : FactionGoal;
public record FormAllianceGoal(FactionId Target)                    : FactionGoal;
public record DeclareWarGoal(FactionId Target)                      : FactionGoal;

// Colonial only
public record ExpandTradeGoal(RegionId Region)                      : FactionGoal;
public record SuppressPiracyGoal(RegionId Region)                   : FactionGoal;

// Pirate only
public record RaidShippingGoal(FactionId Target, RegionId Region)   : FactionGoal;
public record EstablishHavenGoal(PortId Target)                     : FactionGoal;
public record RaiseRaidingMomentumGoal(RegionId Region)             : FactionGoal;
```

A faction may hold multiple active goals simultaneously but is penalised in utility scoring for overextension — pursuing too many high-cost goals dilutes their effectiveness.

### 3.2 Goal Lifecycle

```
Evaluation → Adoption → Execution → Resolution (Completed | Abandoned)
```

Each tick, `FactionSystem` scores all *potential* goals for a faction. Goals above the adoption threshold are added to the active list; goals already active but whose score has fallen below the abandonment threshold are marked `Abandoned`. Goals that complete their progress reach `Completed`.

---

## 4. Goal Evaluation — Utility Scoring

Goals are scored using simple utility functions against the faction's current state. Scores are normalised to 0.0–1.0. The adoption threshold is `0.4`; the abandonment threshold is `0.2` (hysteresis prevents thrashing).

### 4.1 ConquerPort

```csharp
float ScoreConquerPort(Faction faction, Port target, WorldState state)
{
    var strategicValue    = target.Prosperity / 100f
                          + TradeVolumeScore(target, state)
                          + GeographicImportance(target, state);  // hand-authored per port

    var strengthRatio     = NavalStrengthInRegion(faction, target.Region, state)
                          / Math.Max(1f, DefenceStrength(target, state));

    var attackEstimate    = Math.Clamp(strengthRatio / 2f, 0f, 1f);  // needs 2:1 to be confident

    var overextension     = faction.Goals.Count(g => g is ConquerPortGoal && g.Status == GoalStatus.Active) * 0.25f;
    var treasuryPressure  = 1f - Math.Clamp(faction.Treasury / ComfortableTreasury(faction), 0f, 1f);

    return (strategicValue * attackEstimate) / (1f + overextension + treasuryPressure);
}
```

### 4.2 SuppressPiracy

```csharp
float ScoreSuppressPiracy(Faction faction, RegionId region, WorldState state)
{
    var pirateActivity    = GetPirateActivity(region, state);         // 0–1
    var tradeAtRisk       = GetFactionTradeIncome(faction, region, state) / MaxRegionalIncome;
    var patrolCost        = RequiredNavalStrength(region) / Math.Max(1f, faction.NavalStrength);

    return (pirateActivity * tradeAtRisk) / Math.Max(0.1f, patrolCost);
}
```

### 4.3 FormAlliance

```csharp
float ScoreFormAlliance(Faction faction, Faction target, WorldState state)
{
    var currentStanding  = faction.Relationships[target.Id].Standing;
    if (currentStanding < -30) return 0f;  // too hostile to consider

    var sharedThreat     = SharedEnemyThreatScore(faction, target, state);
    var complementarity  = TerritoryComplementarity(faction, target, state);
                           // high if territories don't overlap; low if competing for same ports
    var willingness      = (currentStanding + 100f) / 200f;  // 0 at -100, 1 at +100

    return sharedThreat * complementarity * willingness;
}
```

### 4.4 RaidShipping (Pirate Brotherhood)

```csharp
float ScoreRaidShipping(Faction brotherhood, FactionId target, RegionId region, WorldState state)
{
    var targetTrafficVolume  = GetFactionTrafficVolume(target, region, state);
    var expectedProfit       = targetTrafficVolume * AverageCargValue * RaidSuccessRate(brotherhood, region, state);
    var patrolRisk           = GetPatrolStrength(target, region, state) / Math.Max(1f, brotherhood.NavalStrength);
    var momentum             = (brotherhood.RaidingMomentum + 1f) / 2f;  // 0–1

    return (expectedProfit / MaxExpectedProfit) * momentum / (1f + patrolRisk);
}
```

---

## 5. Relationship System

### 5.1 Standing Updates

Faction relationships update in response to `WorldEvent`s processed during the current tick. Each event type carries implicit standing deltas:

| Event | Affected Relationship | Delta |
|---|---|---|
| `FactionShipAttacked(attacker, victim)` | attacker → victim | −15 |
| `FactionShipAttacked(attacker, victim)` | victim → attacker | −20 |
| `PortControlChanged(from, to)` | to → from | −35 |
| `PortControlChanged(from, to)` | neutral factions → to | −5 (unease) |
| `AllianceFormed(a, b)` | a ↔ b | +35 |
| `WarDeclared(a, b)` | a ↔ b | −50 |
| `TradeVolumeGrew(a, b)` | a ↔ b | +3 per threshold |
| `SharedThreatEmerged(a, b, threat)` | a ↔ b | +10 |
| `PlayerRaidsFactionShip(player, faction)` | player → faction | −(cargo value / 100) |

Standing is clamped to `[−100, 100]`. Changes are applied in the `RelationshipUpdatePhase` after all events for the tick have been collected.

### 5.2 Threshold Crossings

Crossing certain standing thresholds triggers automatic goal creation:

| Threshold | Direction | Effect |
|---|---|---|
| Standing < −60 | Crossed downward | `DeclareWarGoal` adopted (if not already at war) |
| Standing > 50 | Crossed upward | `FormAllianceGoal` evaluated |
| Standing recovers to > −20 | After war | Peace negotiation becomes possible |

These are utility-scored before adoption — a faction will not declare war if it cannot afford to fight, even if standing is sufficiently low.

### 5.3 Relationship Decay

Relationships drift toward a neutral baseline over time if not reinforced by events:

```csharp
// Applied each tick — very slow drift
var decay = standing > 0 ? -0.05f : +0.05f;
relationship.Standing = Math.Clamp(relationship.Standing + decay, -100f, 100f);
```

This models the natural cooling of wars and friendships over time. Active conflict or active trade counteracts decay.

### 5.4 Relationship History

`FactionRelationship.History` stores a bounded list of significant events that shaped the relationship. This is used by the knowledge system (a broker can explain *why* Spain and England are hostile) and by the frontend to present relationship context to the player.

```csharp
public record RelationshipEvent(
    WorldDate   OccurredAt,
    string      Description,  // human-readable summary
    int         StandingDelta
);
```

---

## 6. Naval Allocation

### 6.1 Desired vs. Actual Allocation

Factions cannot instantly redeploy naval strength. Each tick, `FactionSystem` computes a `DesiredAllocation` from active goals, then moves `ActualAllocation` (the `PatrolAllocations` dictionary) toward it.

```csharp
private Dictionary<RegionId, int> ComputeDesiredAllocation(Faction faction, WorldState state)
{
    var desired = new Dictionary<RegionId, int>();

    foreach (var goal in faction.Goals.Where(g => g.Status == GoalStatus.Active))
    {
        switch (goal)
        {
            case ConquerPortGoal g:
                var region = state.Ports[g.Target].Region;
                desired[region] = desired.GetValueOrDefault(region) + (goal.Priority * 5);
                break;

            case SuppressPiracyGoal g:
                desired[g.Region] = desired.GetValueOrDefault(g.Region) + (goal.Priority * 3);
                break;

            case DefendPortGoal g:
                var defRegion = state.Ports[g.Port].Region;
                desired[defRegion] = desired.GetValueOrDefault(defRegion) + (goal.Priority * 4);
                break;
        }
    }

    // Normalize to NavalStrength total
    var total = desired.Values.Sum();
    if (total > faction.NavalStrength)
    {
        var scale = faction.NavalStrength / (float)total;
        foreach (var key in desired.Keys.ToList())
            desired[key] = (int)(desired[key] * scale);
    }

    return desired;
}
```

**Convergence rate:** actual allocation moves toward desired at a rate of 20% per tick — representing the time it takes to physically move naval forces.

```csharp
foreach (var (region, desired) in desiredAllocation)
{
    var current = faction.PatrolAllocations.GetValueOrDefault(region, 0);
    var delta   = (int)((desired - current) * 0.2f);
    faction.PatrolAllocations[region] = current + delta;
}
```

### 6.2 Region Safety Derivation

Regional safety (consumed by the knowledge system and merchant routing) is derived from patrol allocations each tick:

```csharp
// Ahoy.Simulation/Systems/FactionSystem.cs

public float ComputeRegionSafety(RegionId region, WorldState state)
{
    var totalPatrol  = state.Factions.Values
                           .Sum(f => f.PatrolAllocations.GetValueOrDefault(region, 0));

    var pirateRaiding = state.Factions.Values
                            .Where(f => f.Type == FactionType.PirateBrotherhood)
                            .Sum(f => GetActiveRaidingStrength(f, region, state));

    var warConflict  = ActiveConflictsInRegion(region, state) * 15;  // open war degrades safety

    var rawSafety    = (totalPatrol * 2f) / Math.Max(1f, totalPatrol * 2f + pirateRaiding + warConflict);
    return Math.Clamp(rawSafety, 0f, 1f);
}
```

This value is emitted as a `RegionSafetyChanged` event when it crosses meaningful thresholds, feeding into the knowledge system's `RegionSafetySnapshot` facts.

---

## 7. Port Conquest

### 7.1 Conquest at Local / Regional LOD

Port conquest is a **multi-tick process**. When a `ConquerPortGoal` is active and sufficient naval strength is allocated to the target's region, the assault begins.

```csharp
private void AdvanceConquest(Faction faction, ConquerPortGoal goal, WorldState state, IEventEmitter events)
{
    var target      = state.Ports[goal.Target];
    var deployed    = faction.PatrolAllocations.GetValueOrDefault(target.Region, 0);
    var defence     = DefenceStrength(target, state);

    if (deployed < defence * 0.5f) return;  // insufficient force — assault stalls

    var progressDelta = (deployed - defence * 0.5f) / (defence * 2f);
    goal = goal with { Progress = goal.Progress + progressDelta };

    events.Emit(new ConquestInProgress(state.Date, goal.Target, faction.Id, goal.Progress),
                context.GetLod(target.Region));

    if (goal.Progress >= 1.0f)
        CompleteConquest(faction, goal.Target, state, events);
}

private void CompleteConquest(Faction faction, PortId portId, WorldState state, IEventEmitter events)
{
    var port           = state.Ports[portId];
    var previousOwner  = port.ControllingFaction;

    port.ControllingFaction = faction.Id;
    faction.ControlledPorts.Add(portId);
    state.Factions[previousOwner].ControlledPorts.Remove(portId);

    events.Emit(new PortControlChanged(state.Date, portId, previousOwner, faction.Id),
                SimulationLod.Local);
}
```

`DefenceStrength` is the sum of the port's `GarrisonStrength` and any naval strength the controlling faction has allocated to the region:

```csharp
private float DefenceStrength(Port port, WorldState state) =>
    port.GarrisonStrength
    + state.Factions[port.ControllingFaction]
           .PatrolAllocations.GetValueOrDefault(port.Region, 0);
```

A defending faction that receives intel about an imminent assault (via the knowledge system) can adopt a `DefendPortGoal` and reallocate naval strength to slow or repel the attack.

### 7.2 Conquest at Distant LOD

At `Distant` LOD, multi-tick conquest is collapsed into a **single probability check** resolved at the tick when the goal is evaluated:

```csharp
private void ResolveDistantConquest(Faction faction, ConquerPortGoal goal, WorldState state, IEventEmitter events)
{
    var target     = state.Ports[goal.Target];
    var odds       = NavalStrengthInRegion(faction, target.Region, state)
                   / (float)Math.Max(1, DefenceStrength(target, state));
    var rolls      = _rng.NextSingle();

    if (rolls < Math.Clamp(odds / 3f, 0.05f, 0.85f))
        CompleteConquest(faction, goal.Target, state, events);
    else
        goal = goal with { Status = GoalStatus.Abandoned };  // failed; re-evaluate next cycle
}
```

The result emits a `PortControlChanged` event at `Distant` confidence — a low-confidence fact that propagates through the knowledge system at the appropriate fidelity.

---

## 8. Pirate Brotherhood Mechanics

### 8.1 Raiding

When a `RaidShippingGoal` is active, the brotherhood attacks a fraction of the target faction's merchant traffic in the region each tick:

```csharp
private void ExecuteRaiding(Faction brotherhood, RaidShippingGoal goal, WorldState state, IEventEmitter events)
{
    var targetShips = state.Ships.Values
        .Where(s => s.OwningFaction == goal.Target
                 && IsInRegion(s, goal.Region, state)
                 && s.Location is AtSea or EnRoute)
        .ToList();

    var raidCapacity = GetActiveRaidingStrength(brotherhood, goal.Region, state);
    var raided       = targetShips
        .OrderBy(_ => _rng.Next())
        .Take(raidCapacity / 10)  // rough conversion: 10 strength points ≈ 1 intercept per tick
        .ToList();

    foreach (var ship in raided)
    {
        var outcome = ResolveRaidOutcome(brotherhood, ship, state);
        ApplyRaidOutcome(outcome, brotherhood, ship, state, events);
    }

    // Update raiding momentum
    var successRate = raided.Count > 0
        ? (float)raided.Count(r => r /* successful */ != null) / raided.Count
        : 0f;
    brotherhood.RaidingMomentum = Math.Clamp(
        brotherhood.RaidingMomentum * 0.9f + (successRate - 0.5f) * 0.2f, -1f, 1f);
}
```

A successful raid transfers cargo to the brotherhood's treasury (via `RaidIncome`) and emits a `ShipRaided` event.

### 8.2 Haven Establishment

`EstablishHavenGoal` targets a port the brotherhood wants to use as an informal base. It does not require full conquest — instead, it builds informal **haven presence** over time through bribery, intimidation, and repeated use.

```csharp
// Added to Port
public Dictionary<FactionId, int> HavenPresence { get; } = new();  // 0–100 per brotherhood
```

Haven presence grows each tick the brotherhood has ships in the port and the controlling faction doesn't actively suppress it:

```csharp
private void AdvanceHavenEstablishment(Faction brotherhood, EstablishHavenGoal goal, WorldState state)
{
    var port       = state.Ports[goal.Target];
    var controller = state.Factions[port.ControllingFaction];
    var suppression = controller.IntelligenceCapability / 100f
                    + controller.PatrolAllocations.GetValueOrDefault(port.Region, 0) / 50f;

    var growth = 2f * (1f - suppression);
    var current = port.HavenPresence.GetValueOrDefault(brotherhood.Id, 0);
    port.HavenPresence[brotherhood.Id] = (int)Math.Clamp(current + growth, 0, 100);

    if (port.HavenPresence[brotherhood.Id] >= 60)
    {
        goal = goal with { Status = GoalStatus.Completed };
        // Port now functions as a haven — merchants can pay fees, brotherhood ships can dock
    }
}
```

Haven presence is **contested** — the controlling faction can suppress it by increasing patrol allocations in the region. High presence (>80) creates tension and may eventually trigger a `ConquerPortGoal` from the controlling faction.

### 8.3 Brotherhood Cohesion

Pirate brotherhoods have a `Cohesion` value (0–100) representing how unified they are under their current leadership. Cohesion affects:
- How effectively the brotherhood pursues goals (low cohesion = goals execute poorly)
- How attractive the brotherhood is to new recruits (high cohesion = stronger)

```csharp
// Added to Faction (PirateBrotherhood only)
public int Cohesion { get; internal set; } = 70;
```

Cohesion decreases when:
- The brotherhood suffers significant losses (major ships sunk, haven lost)
- Treasury runs dry (crews don't get paid)
- The brotherhood has been inactive for too long (no raids, no momentum)

Cohesion increases when:
- Raiding momentum is positive
- A charismatic leader individual is active (high `Boldness` + `Loyalty` traits)
- The brotherhood achieves major goals

A brotherhood that drops below `Cohesion < 20` may **fracture** — splitting into two smaller factions, each with a share of the naval strength and territory.

---

## 9. The Self-Reinforcing Loop

This is the mechanism by which the living world generates emergent stories of faction rise and fall.

```
Treasury pressure
  ↓ (less money)
NavalStrength decays
  ↓ (fewer ships)
PatrolAllocations reduced
  ↓ (weaker patrols)
RegionSafety falls
  ↓ (more pirate activity)
Merchant ships avoid region
  ↓ (less trade volume)
TaxIncome and TradeDuties fall
  ↓ (less money)
[loop]
```

The reverse loop applies for growing factions. Natural limiters prevent permanent dominance:

| Limiter | Mechanism |
|---|---|
| **Over-extension** | Conquering additional ports increases `GarrisonUpkeep` and spreads `NavalStrength` thin |
| **Alliance formation** | Weaker factions score `FormAllianceGoal` highly when a dominant faction emerges — shared threat logic |
| **Pirate opportunity** | Prosperous faction shipping is highly profitable to raid — pirate scoring is drawn to wealthy targets |
| **Governance strain** | Very high prosperity ports generate governance events (unrest, corruption) — managed via `EventPropagationSystem` |

These limiters mean no single faction reaches a stable dominant position for long without active maintenance.

---

## 10. LOD-Aware Processing

```csharp
public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
{
    foreach (var faction in state.Factions.Values)
    {
        // Resources update at all LOD tiers — always needed for consistency
        UpdateResources(faction, state, context, events);

        var relevantLod = GetFactionRelevantLod(faction, context, state);

        switch (relevantLod)
        {
            case SimulationLod.Local:
            case SimulationLod.Regional:
                EvaluateGoals(faction, state, context, events);
                UpdateRelationships(faction, state, events);
                RecalculateNavalAllocation(faction, state, context);
                ExecuteActiveGoals(faction, state, context, events);
                break;

            case SimulationLod.Distant:
                EvaluateGoalsCoarse(faction, state, events);     // major threshold checks only
                UpdateRelationshipsCoarse(faction, state, events); // crossing thresholds only
                ExecuteActiveGoalsCoarse(faction, state, events); // single-tick probability resolution
                break;
        }
    }

    EmitSafetyEvents(state, context, events);
}
```

**`GetFactionRelevantLod`** returns the best (closest) LOD tier of any region the faction is active in. A faction with ports in both Local and Distant regions is processed at `Local` fidelity — the higher-detail tier dominates.

---

## 11. Events Emitted

| Event | LOD Tier | Trigger |
|---|---|---|
| `PortControlChanged` | All | Port conquest completed |
| `WarDeclared(a, b)` | All | Standing crosses war threshold and goal adopted |
| `AllianceFormed(a, b)` | All | Alliance goal completed |
| `PeaceNegotiated(a, b)` | All | Standing recovers above peace threshold |
| `PatrolPresenceChanged` | Regional/Distant | Patrol allocation crosses a level threshold |
| `ShipRaided` | Local | Successful pirate raid |
| `ConquestInProgress` | Local/Regional | Active assault ongoing |
| `HavenEstablished` | Local/Regional | Haven presence reaches threshold |
| `BrotherhoodFractured` | All | Cohesion drops below fracture threshold |
| `RegionSafetyChanged` | All (varying conf.) | Safety score crosses a threshold |
| `FactionTreasuryCollapsed` | All | Treasury hits zero |

---

## 12. Open Questions

- [ ] How many active goals can a faction hold simultaneously before overextension penalties become prohibitive? A hard cap vs. a soft penalty via utility scoring — the latter is proposed but the balance needs tuning.
- [ ] Should faction AI be aware of the player's reputation and treat the player as a quasi-faction? (e.g. Spain evaluates the player's raiding impact the same way it evaluates a pirate brotherhood's raiding). Likely yes — but the mechanics need fleshing out.
- [ ] Brotherhood fracture: when a brotherhood splits, how are assets divided? By naval strength only, or do specific named individuals pull crews with them based on loyalty traits?
- [ ] Diplomacy beyond alliances and wars — can factions negotiate tribute, non-aggression pacts, or trade agreements? Deferred, but the `FactionRelationship` model should accommodate these.
- [ ] Should the player be able to directly influence faction goal adoption? (e.g. providing intel to a faction about a vulnerable port to trigger a `ConquerPortGoal` they wouldn't have adopted otherwise). Strong gameplay hook — flagged for later.

---

## 13. Post-v0.1 Additions

### Group 5E — Intention Leaks & Counter-Intelligence (implemented)

- **Low-capability factions** (IntelligenceCapability < 0.35): 2%/tick passive
  leak of Secret `FactionIntentionClaim` to Restricted in a random controlled port.
- **Stress-driven leaks** (all factions): 15%/tick when treasury < expenditure × 5
  or naval strength dropped > 50%.
- **Counter-intelligence**: Factions with IntelligenceCapability > 0.40 detect
  enemy infiltrators at cap × 0.01/tick, seeding `IndividualAllegianceClaim`
  into `FactionHolder`.

### Group 7 — War Declaration & Blockade Detection (implemented)

- **`DeclareWar` / `SeekPeace`** goal subtypes drive discrete `AtWarWith` state.
  War triggers at relationship < -80. Peace requires mutual `SeekPeace` goals.
  War intentions seeded at Public sensitivity.
- **Blockade detection**: Each tick, sums hostile ship guns in each port's region
  vs port defense rating (patrol allocation × 10 + base 20). Sets/clears
  `PortConditionFlags.Blockaded`. Uses combat tonnage, not ship count.

---

*Next step: EventPropagationSystem — how world events cascade into downstream state changes.*
