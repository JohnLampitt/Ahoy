# Ahoy — Group 4 Design Review: Individual Lifecycle & World Memory

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
  decay), HopCount (15% penalty per retelling), IsDisinformation, SourceHolder,
  OriginatingAgentId (nullable — set at disinformation injection), IsDecayExempt

- **`KnowledgeHolderId`** cases: FactionHolder, PortHolder, ShipHolder,
  IndividualHolder, PlayerHolder

- **`Individual`** fields relevant to this proposal:
  ```
  float Authority          // 0–100; cascade target
  float PlayerRelationship // -100..+100; read by DecisionProvider but NEVER written
  bool  IsAlive            // false = dead
  bool  IsCompromised      // true = burned agent
  int   CurrentGold
  PortId? HomePortId       // null for nomadic individuals
  PortId? LocationPortId
  IndividualRole Role      // Governor, PortMerchant, NavalOfficer, PirateCaptain,
                           // Smuggler, Informant, KnowledgeBroker, Diplomat, Privateer
  ```

- **`Port`** fields relevant to this proposal:
  ```
  IndividualId? GovernorId
  float Prosperity         // 0–100
  float FactionAuthority   // 0–100; written by GovernorAuthorityRule
  float PersonalReputation // -100..+100 player standing; NEVER written
  float InstitutionalReputation // -100..+100 faction view of player; NEVER written
  ```

- **`PlayerState`** fields:
  ```
  float Notoriety          // 0–100; affects NPC recognition; NEVER written
  int   PersonalGold
  ```

- **`PropagationRule`** base class: `Matches(WorldEvent)`, `Apply(event, state, ctx, events)`.
  Rules are listed in `EventPropagationSystem.BuildDefaultRules()`.

- **Current propagation rules** (named by RuleId):
  - B2 `GovernorAuthorityRule` — on `PortProsperityChanged` where NewValue < 30f:
    `governor.Authority -= 5f`, `port.FactionAuthority -= 3f`. Does NOT kill anyone.
  - E1 `GovernorDeathReplacementRule` — `Matches(IndividualDied)`. Body: clears
    `port.GovernorId = null`, emits `GovernorChanged(NewGovernor: null)`.
    **Nothing in the codebase ever emits `IndividualDied`**, so this rule never fires.

- **`IndividualLifecycleSystem`** (system 5): drives governor tour movement only.
  2% chance per tick to start a diplomatic tour, counts down, returns home.
  No mortality pass whatsoever.

- **`RuleBasedDecisionProvider`** actively reads `individual.PlayerRelationship`
  and `port.PersonalReputation` to vary NPC behaviour. These fields are always
  zero because nothing writes them.

---

## What Groups 1–3 established

Groups 1–3 are merged. Key additions relevant to Group 4:
- `BribeGovernorCommand(IndividualId GovernorId, int GoldAmount)` — **defined but
  not yet implemented** in `SimulationEngine.ApplyCommand()`.
- `BurnAgentCommand(IndividualId AgentId)` — **implemented**: sets
  `IsCompromised = true`, applies -0.30f confidence penalty to all that agent's
  facts, emits `AgentBurned` event.
- `SellFactCommand`, `BuyKnowledgeCommand` — implemented.
- `Individual.IsCompromised` — written by BurnAgentCommand; burned agents have a
  30-tick replacement timer in `FactionSystem.TickBurnReplacements`.

---

## Proposed: Group 4A — Player Reputation & Relationship Writes

The player leaves no mark on the world. This proposal wires up the existing
scaffolding.

### 4A-1: Individual.PlayerRelationship writes

In `SimulationEngine.ApplyCommand()`:

| Command | Effect |
|---|---|
| `BribeGovernorCommand` (gold transferred) | `governor.PlayerRelationship += GoldAmount / 20f`, clamped [-100,+100] |
| `BurnAgentCommand` (success) | All Individuals of the same faction at the same port: `individual.PlayerRelationship -= 15f` |
| `SellFactCommand` (success) | `broker.PlayerRelationship += 5f` |
| `BuyKnowledgeCommand` (success) | `broker.PlayerRelationship += 3f` |

Additionally, `BribeGovernorCommand` needs to be **implemented** in
`SimulationEngine` (it currently has no case):
1. Validate: governor exists, is alive, is at same port as player.
2. Validate: player has sufficient gold.
3. Deduct gold from `PlayerState.PersonalGold`.
4. Apply relationship delta (above).
5. Apply `port.PersonalReputation += 5f`.

**Open question 1:** What stops the player from spamming BribeGovernorCommand
indefinitely to max out every governor relationship? Should there be a per-tick
cooldown, a diminishing-returns curve, or a hard cap on how much a single governor
will accept before losing interest (or suspecting a trap)?

### 4A-2: Port.PersonalReputation writes

| Trigger | Effect |
|---|---|
| `EconomySystem` — successful NPC-to-player or player sale at port | `port.PersonalReputation += 1f` |
| `BurnAgentCommand` success, agent's HomePortId == port | `port.PersonalReputation -= 20f` |
| `BribeGovernorCommand` success (see above) | `port.PersonalReputation += 5f` |

**Open question 2:** `Port.PersonalReputation` and `Individual.PlayerRelationship`
on the governor represent overlapping concepts — both affect how the governor
treats the player. Should they stay as separate fields (reputation is structural/
historical, relationship is personal/transactional), or should one drive the other?
If separate, what happens when a governor dies and is replaced — does the new
governor inherit the port's PersonalReputation or start at 0?

### 4A-3: PlayerState.Notoriety writes

Notoriety represents how widely feared/known the player is across the Caribbean.
For now (combat not yet implemented), the only write:
- `BurnAgentCommand` success → `player.Notoriety += 5f`

**Open question 3:** Is Notoriety a single float meaningful across all factions,
or should it be per-faction? A player who is notorious among pirates but unknown
to colonial governors behaves very differently from one known everywhere.
Propose a concrete model that doesn't require a full refactor of PlayerState.

---

## Proposed: Group 4B — Individual Mortality & Replacement

NPCs are currently immortal. The world has no turnover. This closes the loop.

### 4B-1: Emit IndividualDied from IndividualLifecycleSystem

`IndividualDied` is defined as a WorldEvent but never emitted. The `GovernorDeathReplacementRule` already matches it — it just never fires.

**Proposed mortality pass** (added to `IndividualLifecycleSystem.Tick()`, after
the existing tour-movement loop):

```
const double BaseMortalityChance = 0.002;   // ~1% per year (360-tick year)
const double CompromisedMortalityMultiplier = 5.0; // burned agents: ~5% per year

foreach non-player Individual where IsAlive:
    chance = BaseMortalityChance
    if IsCompromised: chance *= CompromisedMortalityMultiplier
    if _rng.NextDouble() < chance:
        individual.IsAlive = false
        emit IndividualDied(Date, Local, individual.Id)
```

### 4B-2: Fix GovernorDeathReplacementRule to spawn a replacement

Current rule (E1) sets `port.GovernorId = null` and emits `GovernorChanged(null)`.
The comment reads "FactionSystem will appoint a new one on next tick (future work)".

**Proposed fix:** The replacement is spawned inline in `GovernorDeathReplacementRule.Apply()`:

```csharp
// Create replacement governor
var newId = IndividualId.New();
var replacement = new Individual {
    Id = newId,
    FirstName = NamePool.RandomFirst(rng),
    LastName = NamePool.RandomLast(rng),
    Role = IndividualRole.Governor,
    FactionId = dead.FactionId,
    LocationPortId = port.Id,
    HomePortId = port.Id,
    Authority = 40f,    // starts weaker than veteran
};
state.Individuals[newId] = replacement;
port.GovernorId = newId;
events.Emit(new GovernorChanged(Date, lod, port.Id, oldGovId, newId), lod);
```

This requires:
1. A **`NamePool`** static class (or inline arrays in `CaribbeanWorldDefinition`)
   with Caribbean-appropriate first and last names.
2. The `GovernorDeathReplacementRule` needs access to a `Random` instance — currently
   it's a stateless `PropagationRule`. Options: inject `Random` via constructor,
   use `Random.Shared`, or pass `Random` through `SimulationContext`.

**Open question 4:** The replacement is spawned in `EventPropagationSystem` (system 6),
meaning ShipMovement (system 2) and Economy (system 3) have already run this tick
without a governor. The new governor starts with `Authority = 40f` but no
`PlayerRelationship` history. Should the replacement inherit any portion of the
dead governor's relationship state (e.g. faction institutional memory), or start
fully cold? What's the right starting Authority — 40f seems low if the faction
immediately fills the vacancy, but high if the new appointment is untested?

### 4B-3: KnowledgeFact cleanup on death

When a governor or informant dies, their `IndividualHolder` facts may still be
circulating. Two options:
- **Option A:** Purge — remove all facts sourced from `IndividualHolder(dead.Id)`
  from `KnowledgeStore`. Clean but destroys information that may have already
  propagated.
- **Option B:** Orphan — leave facts in circulation but mark them `IsDecayExempt = false`
  so they decay to irrelevance naturally. Truer to how gossip works; a rumour
  doesn't disappear just because the person who spread it is dead.

**Open question 5:** Which option (purge vs. orphan) is more consistent with the
existing knowledge propagation philosophy, and does the answer change depending on
whether the dead individual was an Informant (deliberately injected facts) vs. a
Governor (accumulated facts through play)?

---

## Sequencing question

**Open question 6:** The two sub-proposals are independent but interact:
a) 4A-1 (relationship writes) requires `BribeGovernorCommand` to be implemented
   first — it's the most important write path.
b) 4B-2 (governor replacement) creates new Individuals whose `PlayerRelationship`
   starts at 0 — which is fine only after 4A is live.

Is this the right order (4A fully before 4B), or are there dependency inversions
that suggest a different sequencing?

---

## Evaluation instructions

For each of the five open questions above:
1. Identify any architectural conflicts with the existing system (tick ordering,
   data model assumptions, event flow)
2. Answer the open question with a **concrete decision and rationale**
3. Flag any missing pieces not mentioned in the proposal
4. Rate each sub-proposal (4A-1, 4A-2, 4A-3, 4B-1, 4B-2, 4B-3) as:
   **Solid** (implement as described), **Revise** (specific changes needed),
   or **Rethink** (fundamental problem)

Do not propose wholesale rewrites. The architecture is established.
Focus on what is missing or wrong in the specific proposals.
