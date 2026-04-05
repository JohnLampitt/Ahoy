# Implementation Plan — Colonial Triage & Defection Cascade (Group 9)

Date: 2026-04-05

## Motivation

Factions currently fund relief for every starving port equally, capped at 50%
of treasury. This is strategically naive — an empire fighting a war shouldn't
bankrupt itself feeding a 300-person outpost. When relief is denied, nothing
happens — the port just starves quietly.

This plan adds **Colonial Triage** (factions intelligently allocate scarce
relief) and a **Defection Cascade** (abandoned ports organically flip to
independent/pirate control). This turns economic failure into one of the game's
richest narrative generators: the systemic birth of Pirate Havens and
Independent Republics.

---

## Current State

- `SeedFamineRelief` in `IndividualLifecycleSystem` caps relief at
  `TreasuryGold / 2` — no strategic evaluation.
- No `CapturePortGoal` faction goal exists.
- `ClaimedFactionId` on Individual is only used for espionage infiltrators,
  not for political defection.
- `PortCaptured` event exists but no `PortDefected` event.
- No Strategic Value Score or triage logic.

---

## Phase 1 — Strategic Value Score (SVS)

### Data Model

No new fields needed. SVS is calculated on-the-fly from existing data:

```
SVS = (Population / 1000)
    × M_relationship    (faction leader ↔ governor: -50→0.5, 0→1.0, +50→1.5)
    × M_threat          (Blockaded→0.2, AtWar region→0.5, Peaceful→1.0)
    × M_production      (has unique goods like Gold/Silver→1.5, GeneralTrade→1.0)
```

A Havana-sized capital (pop 10K, allied governor, producing gold) scores ~15.
A remote outpost (pop 500, neutral governor, no unique goods) scores ~0.5.

### Triage Budget Cap

**File:** `src/Ahoy.Simulation/Systems/IndividualLifecycleSystem.cs`

Replace the current flat `TreasuryGold / 2` cap with SVS-based cap:

```csharp
float maxTreasuryPercent = Math.Clamp(svs * 0.02f, 0.01f, 0.25f);
int maxApprovedRelief = (int)(faction.TreasuryGold * maxTreasuryPercent);
```

- High-value capital (SVS 15): up to 25% of treasury
- Marginal outpost (SVS 0.5): limited to 1%
- If requested relief exceeds cap: **deny relief**

---

## Phase 2 — Relief Denial & Defection Cascade

### New Events

**File:** `src/Ahoy.Simulation/Events/WorldEvent.cs`

```csharp
/// <summary>Faction refused to fund relief for a starving port.</summary>
public record FactionReliefDenied(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, FactionId FactionId) : WorldEvent(Date, SourceLod);

/// <summary>A port's governor has renounced their faction allegiance.</summary>
public record PortDefected(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, FactionId OldFaction, FactionId? NewFaction,
    IndividualId GovernorId) : WorldEvent(Date, SourceLod);
```

### Defection Trigger

**File:** `src/Ahoy.Simulation/Systems/IndividualLifecycleSystem.cs`

When `FactionReliefDenied` fires (or is detected next tick):

1. **Deed ledger entry:** `IndividualActionClaim(FactionLeader, Governor,
   null, Hostile, Severe, "Abandoned colony to starvation")`. This drives
   the governor's relationship with the faction leader to -100 via
   consequence math.

2. **Governor evaluates options** (new method `EvaluateDefection`):
   - If governor relationship with own faction leader < -75 AND port is
     starving AND no relief coming:
   - Governor flips: `Port.ControllingFactionId` changes to null (Independent)
     or to a pirate faction if pirates have haven presence in the region.
   - `ClaimedFactionId` on the governor remains the old faction (cover for
     now — they're still pretending to be loyal while secretly independent).
   - Emit `PortDefected` event.

3. **The broadcast:** Governor seeds `ContractClaim(GoodsDelivered, Food)`
   using the port's local gold (whatever's left) and routing through any
   faction's knowledge pools — "buying food from anyone, even pirates."

### Narrative Propagation

`PortDefected` event generates:
- `PortControlClaim` updated in all nearby port holders
- `IndividualActionClaim(Governor, FactionLeader, null, Hostile, Severe,
  "Defected from Crown")` — the governor's deed is gossip too
- `FactionIntentionClaim` at Public sensitivity: "Port X has declared
  independence from [Faction]"

---

## Phase 3 — Faction Response to Defection

### New Faction Goal

**File:** `src/Ahoy.Simulation/State/Faction.cs`

```csharp
public record RecapturePort(PortId TargetPort) : FactionGoal;
```

### Rule-Based Default Response

**File:** `src/Ahoy.Simulation/Systems/FactionSystem.cs`

When `PortDefected` event is processed as a `FactionStimulus`:

```
if faction.AtWarWith.Count == 0 AND faction.TreasuryGold > recaptureCost:
    adopt RecapturePort(defectedPort) goal
    seed ContractClaim(TargetDead) on the defecting governor
else:
    adopt IgnoreDefection — write off the port for now
    (the faction has bigger problems)
```

### LLM Override (Pondering State)

The faction leader enters `Pondering` state on defection. The LLM receives:

**Payload:**
- Leader traits (Proud/Vengeful vs Pragmatic/Cautious)
- Treasury state, active wars, naval strength
- SVS of the lost port
- Relationship with the defecting governor

**LLM returns one of:**
- `RecapturePort` — retake the port militarily
- `AssassinateGovernor` — can't afford reconquest, but the traitor must die
- `IgnoreDefection` — pragmatic triage, focus on the war

**Fallback:** Rule-based default fires immediately. LLM can override on
the next tick. World never stalls.

### RecapturePort Execution

**New NpcGoal subtype:**

```csharp
public record RecapturePortGoal(
    Guid Id, IndividualId NpcId,
    PortId TargetPort) : NpcGoal(Id, NpcId);
```

Assigned to a NavalOfficer. EpistemicResolver: route to the defected port's
region, dock, stat-check for reconquest. On success: `PortCaptured` event,
port returns to faction control.

---

## Phase 4 — Pirate Haven Emergence

When a port defects and no faction recaptures it within 30 ticks:

1. If a pirate faction has `HavenPresence > 0` in the region, the port
   becomes a pirate haven: `Port.IsPirateHaven = true`,
   `Port.ControllingFactionId = pirateFactionId`.
2. The pirate faction gains a new port without fighting — they just moved
   in to fill the power vacuum.
3. The port starts receiving pirate trade (smuggled goods, fenced loot).
4. `EstablishHaven` goal for the pirate faction is automatically fulfilled.

If no pirate faction has presence: the port stays Independent, slowly
recovers via external food imports, and becomes a neutral trade hub that
any faction can try to claim.

---

## Sequencing

```
Phase 1 — Strategic Value Score + triage budget cap
  ↓
Phase 2 — Relief denial event + defection cascade
  ↓
Phase 3 — Faction response (RecapturePort goal + LLM override)
  ↓
Phase 4 — Pirate haven emergence from power vacuum
```

---

## Key Design Decisions

1. **SVS is calculated, not stored.** No new field on Port — computed from
   population, relationships, threats, and production each time it's needed.

2. **Defection uses PortCaptured event internally.** A defection is
   mechanically a capture (new controlling faction). The `PortDefected` event
   is the narrative wrapper; the simulation state change reuses existing code.

3. **Governor ClaimedFactionId stays as old faction during transition.** The
   governor is "secretly independent" — other factions don't immediately know
   about the defection until gossip propagates. This creates an intelligence
   window where the player can exploit the confusion.

4. **LLM decides faction response, not defection itself.** The defection
   trigger is deterministic (relationship < -75 + starving + no relief).
   Only the parent faction's *response* to the defection uses the LLM.
   This respects the invariant: simulation never depends on LLM.

5. **Pirate haven emergence is time-gated (30 ticks).** Gives factions a
   window to respond before pirates move in. If the faction is at war and
   broke, 30 ticks isn't enough — the pirates win. If the faction is at
   peace, they have time to recapture.

---

## Resolved Questions

### Q1: Independent port food imports → Reduced to 50%
Independent ports lose faction supply convoys but attract opportunistic
merchants. Set `InjectExternalFood` to 50% of normal rate for ports with
no `ControllingFactionId`. Creates a survival window without making
independence free. The player or pirate merchants filling the gap is the
gameplay.

### Q2: Player-triggered defection → Yes, with consequences
The player can deliberately blockade a port until the faction denies relief
and the governor defects. But existing mechanics connect the dots: the
player's blockade generates `ShipLocationClaim` placing them in the region
+ `IndividualActionClaim(Hostile, Severe, "Blockaded port")`. The faction
leader sees both deeds in their knowledge. The player's relationship with
the original faction tanks — they trade one enemy for another.

### Q3: Multi-port republics → Individual defection only (for now)
Each port defects individually. Multi-port republics require a new faction
entity and unified AI — deferred to a future "Federation" mechanic where
adjacent independent ports voluntarily merge. Individual defections produce
more narrative variety: one port goes pirate, another stays independent,
a third gets recaptured.

### Q4: Governor retains gold and authority, with decay
The governor keeps both — they're the same person, they just switched sides.
Gold funds initial independent relief contracts. Authority determines
governance stability (high = smooth transition, low = chaos → easy pirate
prey). Add 0.5/tick authority drain for independent ports with no faction
backing. When authority hits 0, the governor is powerless — pirates or any
faction can walk in unopposed.
