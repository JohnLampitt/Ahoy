# Ahoy — NPC Agent Model: Individual vs Ship

## Context

You are a senior game systems designer reviewing a C# .NET 10 tick-engine pirate
sandbox called **Ahoy**, inspired by Sid Meier's Pirates and Dwarf Fortress. The
simulation models the 17th-century Caribbean with factions, ships, named governors
(Individual entities), and a full epistemic knowledge system (confidence decay,
corroboration, disinformation, source reliability).

We are about to implement knowledge-gated merchant routing and have hit a
fundamental design question that will affect the architecture of the NPC model
for the rest of the project. We need a concrete decision, not a list of trade-offs.

---

## Current Architecture

**Relevant types:**

- `Individual`: has `IndividualId`, `LocationPortId`, `HomePortId`,
  `TourTicksRemaining`, `FactionId?`, `IsAlive`, `CaptainId?` (a ship they captain).
  Currently only governors are modelled as Individuals. They take 3–10 tick tours
  to faction-controlled ports and return home.

- `Ship`: has `OwnerFactionId?`, `CaptainId?: IndividualId?`, `RoutingDestination`,
  `IsPirate`, `IsPlayerShip`, `OwnerFactionId?`. Ships navigate between ports in
  `ShipMovementSystem`. Routing is currently random (`GetRandomAccessiblePort`).

- `KnowledgeHolderId` discriminated union:
  ```
  FactionHolder(FactionId)
  PortHolder(PortId)
  ShipHolder(ShipId)         ← already wired, ships accumulate facts at port arrivals
  IndividualHolder(IndividualId)  ← already wired (in types and propagation), used by governors
  PlayerHolder
  ```
  All five holder types already exist and are wired into the bidirectional
  port↔ship knowledge propagation in `KnowledgeSystem`.

- `KnowledgeSystem.PropagateViaArrivingShips()`: When a ship docks, all facts
  exchange bidirectionally between `ShipHolder(ship.Id)` and `PortHolder(port.Id)`.
  Ships accumulate personal knowledge from every port they visit.

- **Current merchants**: 4 merchant Brigs defined in `CaribbeanWorldDefinition`,
  all faction-owned. No independent/factionless merchants exist yet.

- **No merchant decision-making exists.** Merchants trade at their current port
  only (EconomySystem). `ShipMovementSystem.AssignNpcRoute()` picks a random
  accessible port — no economic or knowledge logic involved.

---

## The Design Question

We have identified two architectural options for the NPC agent model:

### Option A — Ship as NPC (current implicit model)

Ships are agents. The merchant IS the ship. Routing decisions live in
`ShipMovementSystem.AssignNpcRoute()`. Knowledge lives on `ShipHolder(ship.Id)`.

**Strengths:**
- Simpler — no new entity type needed
- Consistent with how patrol ships and pirates currently work
- `ShipHolder` knowledge is already populated by port-arrival propagation

**Weaknesses:**
- If a captain buys a new ship, their accumulated knowledge disappears with the hull
- "Merchant" and "warship" are ship roles, not first-class entities — you cannot
  model a retired sea captain with contacts but no current ship
- Personality, relationships, and history belong to a person, not a vessel

### Option B — Individual as NPC (individual-first model)

Named Individuals are agents. A merchant captain is an `Individual` entity with a
ship as their current vehicle. Routing decisions happen at the individual layer.
Knowledge accumulates in `IndividualHolder`, following the person across ships.
Ships become voyage logs (`ShipHolder` = where this hull has been, independent of
who sailed it).

**Strengths:**
- Aligns with the GDD goal of "Dwarf Fortress-style named NPC simulation"
- `IndividualHolder` already exists and is wired into knowledge propagation
- `Ship.CaptainId?: IndividualId?` already provides the individual→ship link
- Enables personality traits, faction loyalty, retirement, assassination, succession
- Knowledge genuinely follows the person, not the hull

**Weaknesses:**
- Requires expanding `IndividualLifecycleSystem` beyond governor tour logic
- Introduces a new `IndividualType` or `IndividualRole` concept (governor vs merchant
  captain vs pirate captain vs spy — see also Thread 6 in prior prompts: spy archetypes)
- `AssignNpcRoute()` in ShipMovementSystem would need to delegate to the captain's
  knowledge rather than the ship's

---

## Specific Questions

**1. Which model is the correct long-term foundation?**

Given that `IndividualHolder` and `Ship.CaptainId` are already present, and given
the GDD's stated Dwarf Fortress-like goal, which option — or a hybrid — should we
commit to? Give the explicit reasoning and state any assumptions about the GDD's
NPC simulation ambition.

**2. If Option B (Individual-first), what is the minimum viable addition to
`IndividualLifecycleSystem`?**

The system currently only handles governor tours. What fields need to be added to
`Individual` (role/archetype enum, home port, personal wealth, preferred trade
goods)? What decision trigger replaces the 2% tour-start probability for a merchant
(presumably profit-driven, not diplomatic)? What does the routing logic consult:
`IndividualHolder` facts, `FactionHolder` facts for their owning faction, or both?

**3. What is the correct semantic for `ShipHolder` in Option B?**

If Individual is the agent, `ShipHolder(ship.Id)` facts represent the vessel's
voyage log — where this hull has been, independent of who sailed it. Is this still
useful (e.g. port authorities might track a ship's hull history for contraband
purposes)? Or should `ShipHolder` be deprecated and all ship-arrival knowledge
propagation redirect to `IndividualHolder(captain.Id)`? If both coexist, what is
the explicit distinction in semantics?

**4. Trading companies as a faction type.**

Entities like the Dutch East India Company or the English Royal African Company
operated as commercial powers with fleets, collective intelligence, and treasury —
but no territorial sovereignty. Should these be modelled as a new
`FactionType.TradingCompany`, giving them `FactionHolder` collective knowledge,
treasury, and fleets, but no `ControlledPorts`? Or is a flat list of merchant
Individuals with `OwnerFactionId` referencing an existing colonial faction sufficient?
What gameplay consequences follow from each choice?

**5. What should `AssignNpcRoute()` do as a placeholder today?**

We want to implement something now without foreclosing Option B. The correct
placeholder must:
- Use knowledge rather than random selection (so we don't implement random and have
  to undo it when Individual-first is adopted)
- Not require full Individual-first refactoring to implement
- Be architecturally consistent with either option

Specifically: should the placeholder query `ShipHolder(ship.Id)` facts (Option A
path), or should it query `IndividualHolder(captain.Id)` facts if the ship has a
captain (Option B path), with a `ShipHolder` fallback for uncaptained ships? Which
query is correct regardless of which option we ultimately commit to?

---

## Closing Instruction

Provide concrete decisions for each question above. For any question where the
answer is genuinely "it depends," specify exactly what it depends on and give the
decision rule. Do not present open-ended trade-off lists — we need architectural
commitments that can be implemented.

The answer to Question 5 in particular will be implemented immediately, before the
broader architectural question is resolved. Make sure it is consistent with the
long-term model you recommend in Question 1.
