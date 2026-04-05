# Implementation Plan — Systemic Crises (Group 7)

Date: 2026-04-05

## Motivation

Groups 5–6 built the epistemic infrastructure and dual-origin contract pipeline.
NPCs act on knowledge, pursue goals, form grudges, and seed personal bounties.
But the world lacks the systemic pressure events that force actors into distress
and create the high-stakes contracts that drive emergent narrative.

This plan adds 6 major crises that organically generate contracts, shift
relationships, and create player opportunity through existing systems.

---

## Data Model Additions (Phase 1)

All additions are additive — no existing fields change.

### Individual.cs

```csharp
/// <summary>Non-null when this individual is held captive by another.</summary>
public IndividualId? CaptorId { get; set; }
```

### Ship.cs

```csharp
/// <summary>True when crew is carrying an infectious disease. Spreads to ports on dock.</summary>
public bool HasInfectedCrew { get; set; }

/// <summary>
/// A physical packet of secret documents carried by this ship.
/// When the ship is boarded/sunk, the package transfers to the attacker.
/// Kept in the knowledge economy, not the commodity loop.
/// </summary>
public KnowledgeFactId? CarriedIntelPackage { get; set; }
```

### Faction.cs

```csharp
/// <summary>
/// Discrete war state. Prevents float-oscillation (R bouncing around -80).
/// Requires a formal Peace Treaty event to clear. Seeded by DeclareWar goal.
/// </summary>
public HashSet<FactionId> AtWarWith { get; } = new();
```

New FactionGoal subtypes:

```csharp
public record DeclareWar(FactionId TargetFaction) : FactionGoal;
public record SeekPeace(FactionId TargetFaction) : FactionGoal;
```

### KnowledgeStore.cs

New claim type:

```csharp
public record PortConditionClaim(
    PortId Port,
    PortConditionFlags Condition) : KnowledgeClaim;
```

New contract condition:

```csharp
public enum ContractConditionType
{
    TargetDestroyed,
    TargetDead,
    GoodsDelivered,
    TargetRescued,    // VIP abduction resolution
}
```

### GoalModels.cs

New NpcGoal subtypes:

```csharp
public record RansomGoal(
    Guid Id, IndividualId NpcId,
    IndividualId CaptiveId,
    FactionId TargetFactionId,
    int DemandGold) : NpcGoal(Id, NpcId);

public record PatrolRegionGoal(
    Guid Id, IndividualId NpcId,
    RegionId Region) : NpcGoal(Id, NpcId);

// ExtortGoal already planned in Group 6D
```

---

## Crisis 1 — VIP Abduction (The Ransom Loop)

### Trigger

When a ship is sunk/surrendered in combat and carries a VIP captain
(Governor, Diplomat, NavalOfficer), a stat-check determines capture vs death.
If captured: set `Individual.CaptorId = attackerCaptainId`.

### Knowledge Propagation

- `IndividualStatusClaim(Individual, Role, FactionId, IsAlive: true)` with
  context indicating captivity — seeded into attacker's ShipHolder
- `IndividualActionClaim(Captor, Victim, null, Hostile, Severe, "Abducted VIP")`

### AI Reactions

- **Victim's faction:** Relationship hits floor. Governor/faction seeds
  `ContractClaim(Condition: TargetRescued, TargetSubjectKey: "Individual:{captiveId}")`.
  High gold reward proportional to VIP rank.
- **Captor's AI:** `RansomGoal` — route toward victim's faction port, emit
  ransom demand. If no response within 20 ticks, escalate (threaten execution).

### Resolution

- Player/NPC intercepts captor, defeats them → `CaptorId` cleared, contract
  fulfilled, `IndividualActionClaim(Rescuer, Captive, null, Friendly, Heroic, "Rescued VIP")`
- Ransom paid → gold transfer, `CaptorId` cleared, captor relationship improves

---

## Crisis 2 — Epidemic Outbreak (The Quarantine Loop)

### Trigger

Random event on tropical ports (2% per tick for qualifying ports). Sets
`PortConditionFlags.Plague` on the port.

### Tick Logic

- Ships docked at infected port: 10% chance per tick to gain `HasInfectedCrew`
- Infected ships that dock at clean ports: 5% chance to spread epidemic
- Epidemic ports: prosperity decays 2×, production halts, population drain
- **Cure:** Epidemic clears after 30 ticks naturally, OR immediately when
  `GoodsDelivered(Medicine)` contract is fulfilled at that port. Medicine
  delivery also sets `HasInfectedCrew = false` on all ships docked there
  and reduces transmission chance to 0% for 10 ticks.

### Knowledge Propagation

- `PortConditionClaim(Port, Plague)` seeded into PortHolder
- `PortProsperityClaim` reflects crashing prosperity

### AI Reactions

- **Merchants:** Knowledge-gated routing (5A) reads `PortConditionClaim` and
  avoids infected ports — organic quarantine, no special system needed
- **Governor:** Seeds urgent `ContractClaim(GoodsDelivered, Medicine)` and
  `ContractClaim(GoodsDelivered, Food)` at massive premiums

### Resolution

Medicine delivery fulfils contract AND mutates epidemic state.

---

## Crisis 3 — Chokehold Blockade (The Economic Siege)

### Trigger

**Blockade detection (FactionSystem):** Sum naval strength of hostile ships in
a port's region. If `hostileStrength > portDefenseRating`, set
`PortConditionFlags.Blockaded`. Based on combat tonnage, not raw ship count —
three unarmed pirate sloops don't blockade fortified Havana.

### Tick Logic

- Blockaded ports: import prices skyrocket, export prices crash (stockpile)
- RouteHazardClaim seeded with high severity for the blockaded region
- Blockade lifts when hostile strength drops below threshold

### Knowledge Propagation

- `PortConditionClaim(Port, Blockaded)` seeded into PortHolder
- `RouteHazardClaim` spreads via normal gossip
- Price distortions propagate as `PortPriceClaim`

### AI Reactions

- **Merchants:** EpistemicResolver routes around blockaded ports
- **Governor:** Empties treasury for `ContractClaim(TargetDestroyed)` on
  blockading flagship
- **Player opportunity:** Blockade running (deliver goods at inflated prices)
  or take the bounty

---

## Crisis 4 — Bloodless Coup / Mutiny (The Subversion Loop)

### Trigger

When a governor or naval officer's relationship with their faction drops
critically (faction mistreatment, personal grudge) AND a rival faction's
relationship is positive, they flip allegiance.

### Mechanics

Uses existing infiltrator model: `Individual.FactionId` changes to new
faction, `ClaimedFactionId` set to original (operating in secret).

### Knowledge Propagation

- If IntelligenceCapability check fails: `IndividualAllegianceClaim` exposed
  (existing 5E-2 counter-intelligence)
- `IndividualActionClaim(Traitor, OriginalFaction, NewFaction, Hostile, Severe, "Defected")`

### AI Reactions

- **Original faction:** R → -100. Immediate `ContractClaim(TargetDead)` on traitor
- **New faction:** Seeds `ContractClaim(GoodsDelivered, Weapons)` to fortify
  their new asset

### Resolution

Player chooses: assassin for original faction, or arms smuggler for the traitor.

---

## Crisis 5 — Intelligence Compromise (The Blackmail Loop)

### Trigger

When an Informant or Diplomat ship is sunk/boarded, `CarriedIntelPackage`
(a `KnowledgeFactId?`) transfers to the attacker's ship.

### Knowledge Propagation

- When ship docks carrying intel package: `ShipCargoClaim`-equivalent fact
  leaks into PortHolder (tavern gossip: "that ship is carrying secret papers")
- The fact referenced by the package is a `FactionIntentionClaim` or similar
  high-value secret

### AI Reactions

- **Finder:** Generates `ExtortGoal` against the faction that owns the intel
- **Owner faction:** Seeds covert `ContractClaim(TargetDestroyed)` against the
  carrier ship

### Resolution

Player tracks extortionist, retrieves intel. Choice: complete the contract
(return it) or keep it and extort the faction themselves.

---

## Crisis 6 — Colonial War (The Privateer Rush)

### Trigger

When `Faction.Relationships` drops below -80 AND faction has `DeclareWar`
goal, add target to `AtWarWith` set. War is a discrete state — requires
formal `SeekPeace` goal + treaty event to end.

### Tick Logic

- `FactionIntentionClaim(War)` seeded at **Public** sensitivity across all
  controlled ports
- Governors stop seeding trade contracts, start seeding mass
  `ContractClaim(TargetDestroyed)` — Letters of Marque targeting enemy ships
- Privateers adopt `PatrolRegionGoal` on enemy trade routes

### Knowledge Propagation

- War declaration propagates as Public gossip — everyone hears fast
- Enemy ship locations become high-value intelligence

### AI Reactions

- **Merchants:** EpistemicResolver avoids enemy-controlled ports entirely
- **Privateers:** Mass goal adoption — hunt enemy shipping
- **Player:** War is opportunity — Letters of Marque pay well, enemy ships
  are legitimate targets, but the player's factional relationships determine
  which side they serve

### Resolution

When one faction's treasury hits zero OR naval strength drops below 20%
of pre-war levels, `SeekPeace` goal fires. Peace treaty clears `AtWarWith`,
removes outstanding Letters of Marque (contract expiry).

---

## Sequencing

```
Phase 1 — Data Models
  Individual.CaptorId
  Ship.HasInfectedCrew, Ship.CarriedIntelPackage
  Faction.AtWarWith, DeclareWar/SeekPeace goals
  PortConditionClaim, ContractConditionType.TargetRescued
  RansomGoal, PatrolRegionGoal

Phase 2 — Crisis Triggers (tick logic)
  Epidemic propagation (ship↔port infection, cure via medicine delivery)
  Blockade detection (hostile strength vs port defense, not ship count)
  War declaration (relationship threshold → AtWarWith discrete state)
  VIP capture (combat resolution → CaptorId stat check)

Phase 3 — Crisis-Driven Contract Seeding
  Governor panic contracts (epidemic → Medicine/Food delivery)
  Ransom demands (CaptorId → RansomGoal → gold demand)
  Letters of Marque (AtWar → mass TargetDestroyed contracts)
  Blockade bounties (Blockaded → TargetDestroyed on flagship)

Phase 4 — EpistemicResolver Extensions
  ExtortGoal execution (demand/response cycle)
  RansomGoal execution (route to victim's port, demand gold)
  PatrolRegionGoal execution (loiter in region, intercept enemies)
  TargetRescued condition check (CaptorId cleared)
```

---

## Key Design Constraints

1. **VIPs are relational, not cargo.** `CaptorId` on Individual keeps captives
   in the sociopolitical engine. No risk of "selling the governor at market."

2. **Intel is epistemic, not cargo.** `CarriedIntelPackage` references a
   `KnowledgeFactId`, keeping secrets in the knowledge economy rather than
   the commodity loop.

3. **Blockade detection uses strength, not count.** Naval tonnage vs port
   defense rating prevents trivial blockades.

4. **War is discrete, not continuous.** `AtWarWith` HashSet prevents
   float-oscillation. Requires formal treaty to end.

5. **Medicine delivery mutates state.** Fulfilling the GoodsDelivered(Medicine)
   contract clears the epidemic flag — the cure is mechanically real, not just
   a payout.

6. **All crises output ContractClaims.** Every crisis ultimately generates
   contracts that enter the unified knowledge economy. The EpistemicResolver
   handles them uniformly. No crisis-specific quest logic.

7. **4 EpistemicResolver verbs cover all 6 crises.** Investigate, Intercept,
   Deliver, Extort/Communicate. No crisis requires a new execution paradigm.
