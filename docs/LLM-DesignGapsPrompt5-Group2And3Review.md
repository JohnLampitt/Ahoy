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
- Active investigation, broker mechanics, and burning are NOT YET IMPLEMENTED
- 4 named merchant captains now exist as Individual entities (PortMerchant role),
  each captaining a faction-owned Brig. Their knowledge accumulates via IndividualHolder.

**Proposed designs for evaluation:**

---

## 2a — InvestigateCommand

`InvestigateCommand(string SubjectKey, int GoldCost)` — player spends gold to get
a direct observation (HopCount=0, Confidence=0.90) of any game state subject.
SubjectKey matches KnowledgeFact.GetSubjectKey() (e.g. `"ShipLocation:abc123"`).
If the revealed fact contradicts a held IsDisinformation fact, InjectDisinformationCorrection
fires — this is how investigation triggers burning.
Cost: 50g Public / 200g Restricted / 500g Secret.

**Open question:** Should investigating a Secret subject require the player to already
hold any fact about that subject (must know it exists), or can they guess subject keys?
If guessing is allowed, what prevents brute-force investigation of all ship IDs?

---

## 2b — KnowledgeBroker

Broker inventory = their `IndividualHolder` facts. No separate data structure.
Pricing: `Confidence × SensitivityMultiplier × 100g`.
BuyKnowledgeCommand is already scaffolded — just needs implementation.
New SellFactCommand: player sells a held fact to broker at 40% of buy price.
Brokers are static (don't actively propagate their inventory between ports).

**Open question:** Should brokers' knowledge pools be seeded at world creation,
or should they accumulate entirely through the normal propagation system (starting
with near-zero facts and building up over time)?
If seeded at creation: what facts and at what confidence?
If emergent: a new broker has nothing to sell for the first 20–30 ticks.

---

## 2c — Burning mechanic

When InvestigateCommand exposes a planted lie and the lie's SourceHolder is an
Individual (Informant or KnowledgeBroker), set `Individual.IsCompromised = true`.
Emit `IndividualCompromised` WorldEvent. Compromised individuals stop propagating
knowledge and become unavailable to the broker system.

**Open question:** Should burned individuals remain alive (visible but untrusted),
or be removed from play (IsAlive=false)? What faction reaction follows discovery
of a burned asset — does the faction replace them (spawn a new Informant Individual),
escalate against the player, or simply accept the loss?

---

## 2d — Merchant lifecycle

Merchant captains (PortMerchant role) currently depart only via ShipMovement's
arrival-based trigger. Proposed: add a second pass in IndividualLifecycleSystem
for PortMerchant Individuals — if their captained ship has been idle > 4 ticks
with cargo, force a re-routing by clearing RoutingDestination.
Requires adding `TicksDockedAtCurrentPort` counter to Ship.

**Open question:** Is the "idle > 4 ticks" trigger too mechanical?
Should merchants only depart when their IndividualHolder contains a PortPriceClaim
for an accessible port with better prices than their current port (knowledge-gated
departure)? The risk: a new merchant captain with no facts is permanently idle.
Propose a resolution that is architecturally consistent with the Individual-first
knowledge model without leaving new captains permanently stranded.

---

## Group 3 — FactionIntelligenceCapability

Proposed: add `float IntelligenceCapability` (0.0–1.0) to Faction.
Scales the BaseConfidence of Secret facts that faction seeds
(high capability = harder to contradict via investigation).
Also scales investigation cost against that faction's secrets.

**Open question:** Is IntelligenceCapability the right field name and scope, or
should this be `CounterIntelligenceRating` limited strictly to Secret fact protection?
Should it also affect how reliably the faction detects when their own agents are burned
(i.e. gates the DeceptionExposed feedback to FactionHolder)?

---

## Evaluation instructions

For each of the five proposals above:
1. Identify any architectural conflicts with the existing system (tick ordering,
   data model assumptions, event flow)
2. Answer the open question with a concrete decision and rationale
3. Flag any missing pieces not mentioned in the proposal
4. Rate the proposal as: **Solid** (implement as described), **Revise**
   (specific changes needed), or **Rethink** (fundamental problem)

Do not propose wholesale rewrites. The architecture is established.
Focus on what is missing or wrong in the specific proposals above.
