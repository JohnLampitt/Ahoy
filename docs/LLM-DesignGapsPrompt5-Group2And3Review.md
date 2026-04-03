# Ahoy — Group 2 & 3 Design Review

You are a senior game systems designer and C# architect reviewing implementation
proposals for **Ahoy**, a C# .NET 10 pirate sandbox simulation (Dwarf Fortress +
Sid Meier's Pirates). The game is backend-first; all mechanics are pure simulation
logic.

**Architecture quick reference:**
- Tick = 1 day. 8 systems: Weather → ShipMovement → Economy → Faction →
  IndividualLifecycle → EventPropagation → Knowledge → Quest
- `KnowledgeFact`: Claim (discriminated union), Confidence (0–1, exponential decay),
  HopCount (15% penalty per retelling), IsDisinformation, SourceHolder,
  CorroboratingFactionIds (faction echo-chamber guard), IsDecayExempt
- `KnowledgeHolderId` cases: FactionHolder, PortHolder, ShipHolder, IndividualHolder, PlayerHolder
- Sensitivity: Public (30% propagation), Restricted (10%), Secret (0%), Disinformation (30%)
- `BuyKnowledgeCommand(IndividualId BrokerId, KnowledgeFactId FactId, int Price)` already exists
- `IndividualRole` enum: Governor, PortMerchant, NavalOfficer, PirateCaptain, Smuggler,
  Informant, KnowledgeBroker, Diplomat, Privateer — all defined, not all implemented
- `Individual` has: `HomePortId`, `IsAlive`, `LocationPortId`, `PlayerRelationship`,
  `FactionId`, `TourTicksRemaining` — does NOT yet have `IsCompromised` or `PersonalWealth`
- Active investigation, broker mechanics, and burning are NOT YET IMPLEMENTED
- 4 named merchant captains exist as Individual entities (PortMerchant role),
  each captaining a faction-owned Brig. Their knowledge accumulates via IndividualHolder.

**Proposed designs for evaluation:**

---

## 2a — InvestigateCommand

Two variants:

**InvestigateLocalCommand(string SubjectKey)** — player is physically present at the
subject's current location. Yields HopCount=0, Confidence=0.90. No gold cost (direct
observation). Requires `player.LocationPortId` matches the subject's current port.

**InvestigateRemoteCommand(string SubjectKey, int GoldCost)** — player pays a spy
network. Yields HopCount=1, lower confidence, and may fail (returns null fact).
Both variants: the player must already hold at least one KnowledgeFact for the
SubjectKey (even superseded, IsDisinformation, or Confidence=0.05) to issue the
command. Cannot investigate a subject you have never heard of.

**Resolved: prior-fact gate.** Player must hold a prior fact on the SubjectKey.

**Open question — pricing InvestigateRemoteCommand:**
The original proposal priced by sensitivity tier (50g Public / 200g Restricted /
500g Secret). This is problematic: the player must see the price before committing,
and a tiered price directly reveals the underlying classification. The player should
not be told "this is a Secret fact" by observing the button cost.
Propose a pricing model that:
(a) is visible to the player before they commit,
(b) does not reveal the true sensitivity classification of the fact,
(c) still creates a meaningful gold cost that scales with information value,
(d) is computable purely from data the broker/spy legitimately possesses.
One candidate: base the price on the broker's own Confidence in the fact —
`BrokerConfidence × BaseRate × SensitivityMultiplier` where the SensitivityMultiplier
is hidden from the player and only the final gold figure is displayed. Evaluate this
and propose alternatives if stronger.

**Open question — failure model for InvestigateRemoteCommand:**
A guaranteed 0.90 confidence for any gold payment is too safe. Propose a failure
model: what causes the spy to fail (e.g. target moved, counterintelligence detected),
what the player receives when they fail (nothing, or a low-confidence partial fact),
and whether failure should be distinguishable from "fact simply doesn't exist."

---

## 2b — KnowledgeBroker

Broker inventory = their `IndividualHolder` facts. No separate data structure.
Brokers are static (don't actively propagate their inventory between ports).
BuyKnowledgeCommand is already scaffolded — just needs implementation.
New `SellFactCommand`: player sells a held fact to broker at a fraction of buy price.

**Resolved: bootstrapping.** Brokers are seeded at world creation (in
CaribbeanWorldDefinition) with a snapshot of their FactionHolder's high-confidence
facts. IndividualLifecycleSystem does not instantiate individuals — that's a world
creation concern. After seeding, accumulation is emergent.

**Resolved: Secret fact gap.** Static brokers can never organically acquire Secret
facts (0% propagation rate). Only the bootstrapped FactionHolder snapshot at creation
can contain Secrets. This is intentional: Secret facts must be bought via
InvestigateRemoteCommand (spy network), not from a tavern broker.

**Open question — SellFactCommand broker purchasing logic:**
A player could spam-sell 50 zero-value WeatherClaims to bankrupt a broker.
Design the broker's acceptance criteria for SellFactCommand:
- Minimum confidence threshold to purchase
- Deduplication (broker already holds a higher-confidence version of this fact)
- Sensitivity filter (brokers only buy certain tiers)
- How the broker's gold reserve is modelled (does the broker have a finite wallet,
  and if so what replenishes it?)

---

## 2c — Burning mechanic

When InvestigateCommand exposes a planted lie and the lie's SourceHolder traces back
to a specific Individual (Informant or KnowledgeBroker), set `Individual.IsCompromised = true`.
Emit `IndividualCompromised` WorldEvent. Compromised individuals stop propagating
knowledge and become unavailable as brokers. They remain alive and visible (IsAlive=true).

**Resolved: full inventory confidence penalty.**
When an individual is burned, ALL facts in their IndividualHolder suffer an immediate
confidence penalty (e.g. -0.30). Rationale: from the receiver's perspective,
a burned source's entire output becomes suspect — you cannot distinguish which of
their facts were cover and which were genuine. The epistemic contamination is total.

**Open question — SourceHolder tracing (unsolved, critical):**
The burning mechanic requires tracing a planted lie back to the specific Individual
who injected it. Currently `KnowledgeFact.SourceHolder` is a `KnowledgeHolderId`
(e.g. PortHolder, FactionHolder) — not necessarily an IndividualHolder.
When a faction plants disinformation via a PortHolder, there is no pointer back
to the Individual operative who seeded it.
Propose a concrete solution. Candidates:
(a) Always set SourceHolder to IndividualHolder(agentId) when an Individual
    injects disinformation, rather than the Port or Faction holder.
(b) Add a separate nullable `OriginalSourceIndividualId` field to KnowledgeFact.
(c) Accept that burning is only possible when the player can directly observe
    the individual seeding the lie (physical presence), not via remote investigation.
Evaluate the trade-offs. The chosen solution must be implementable without breaking
existing SourceHolder consumers (ResolveFactionId helper in KnowledgeSystem,
corroboration guard).

**Open question — cascade to other holders:**
When an individual is burned and their IndividualHolder facts take a confidence
penalty, should that penalty also propagate to copies of those facts now held
by other entities (ports, factions, player) that received them from this individual?
If yes: how do you identify which facts in foreign holders originated from this
individual, given SourceHolder may point to a PortHolder intermediary?

**Open question — faction response:**
After detecting IndividualCompromised: does the faction (a) spawn a replacement
Informant after N ticks, (b) escalate against the player via FactionSystem,
(c) both, (d) neither? What is the tick delay before replacement, and what
conditions prevent replacement (e.g. faction treasury too low)?

---

## 2d — Merchant lifecycle + PersonalWealth

**PersonalWealth is required** for this mechanic and for the Individual-first
economic model generally. Without it there is no economic pressure on idle
individuals and merchant routing decisions are arbitrary.

**Open question — PersonalWealth minimal design:**
Define the minimal viable `PersonalWealth` field (or struct) on Individual:
- What data does it contain (current gold, income per tick, expenditure per tick)?
- Where does merchant income come from (completed trade runs, sold cargo margin)?
- Where does expenditure come from (crew wages per tick while docked/at sea)?
- Which system owns the PersonalWealth update — EconomySystem (system 3) or a new
  individual finance pass?
- Is a float sufficient or does it need to be an int (gold pieces)?
Design only the minimal model needed to gate merchant departure decisions.
Do not design a full individual economy.

**Resolved: knowledge-gated departure with HomePort fallback.**
`HomePortId` already exists on `Individual`. Merchant departure decision chain:
1. If IndividualHolder contains a PortPriceClaim for an accessible port with
   higher expected margin than current port → route there.
2. Else if FactionHolder (employer) contains such a PortPriceClaim → route there.
3. Else → route to HomePortId to seek new orders.
The "idle > 4 ticks" mechanical trigger is replaced by PersonalWealth bleeding:
when idle costs exceed income, the HomePort fallback fires naturally.
`TicksDockedAtCurrentPort` on Ship is still needed to compute idle expenditure.

**Open question — what triggers re-evaluation:**
ShipMovementSystem (system 2) calls AssignNpcRoute only when RoutingDestination
is null. Under the knowledge-gated model, a merchant may have routed to a port,
arrived, sold their cargo, and now need to re-evaluate. What sets RoutingDestination
back to null after a successful trade run, and in which system/tick does this happen?
If IndividualLifecycleSystem (system 5) clears it, ShipMovementSystem (system 2)
won't act until the next tick — is that one-tick lag acceptable or does it create
a visible simulation artifact?

---

## Group 3 — FactionIntelligenceCapability

Proposed: add `float IntelligenceCapability` (0.0–1.0) to Faction.
Three effects:
1. **Disinformation quality**: scales BaseConfidence of disinformation facts the
   faction seeds (high capability → more convincing lies, harder to contradict).
2. **Detection gate**: `Random.NextDouble() < faction.IntelligenceCapability`
   determines whether DeceptionExposed event reaches the FactionHolder when a lie
   is exposed. Low-capability factions don't always know their asset was burned.
3. **Investigation yield reduction**: investigating a high-capability faction's
   Secrets yields lower confidence (e.g. normal=0.90, against cap=0.9 faction → 0.65).
   The player never knows whether the low yield reflects a clever faction or just
   poor intelligence — deliberate epistemic fog.

**Resolved: no gold-cost scaling by IntelligenceCapability.** Scaling the player's
gold cost by a hidden faction stat allows the player to reverse-engineer the stat
by price observation. Investigation yield reduction achieves the same gameplay
effect (diminished returns against clever factions) without leaking the stat.

**Open question — capability initialisation and change:**
How is IntelligenceCapability set at world creation, and can it change during play?
Candidates: (a) static value defined in CaribbeanWorldDefinition per faction,
(b) derived from faction TreasuryGold (richer factions have better intelligence),
(c) a separate tracked value that grows/shrinks based on FactionGoal activation
(choosing EspionageGoal raises it, losing assets lowers it).
If it can change: which system owns the update, and does it emit an event?

---

## Evaluation instructions

For each of the five proposals above:
1. Identify any architectural conflicts with the existing system (tick ordering,
   data model assumptions, event flow)
2. Answer each open question with a concrete decision and rationale
3. Flag any missing pieces not mentioned in the proposal
4. Rate the proposal as: **Solid** (implement as described), **Revise**
   (specific changes needed), or **Rethink** (fundamental problem)

Do not propose wholesale rewrites. The architecture is established.
Focus on what is missing or wrong in the specific proposals above.
