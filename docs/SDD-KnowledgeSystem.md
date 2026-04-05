# Ahoy — System Design: Knowledge System

> **Status:** Living document.
> **Version:** 0.2 — added fact provenance model (SourceHolder, BaseConfidence, CorroborationCount)
> **Depends on:** SDD-WorldState.md, SDD-EconomySystem.md

---

## 1. Overview

Knowledge is a **first-class resource** in Ahoy — as real and as valuable as gold or cannon. It spreads, ages, gets guarded, gets sold, and gets faked. Every actor in the world — player, merchant, governor, faction — operates on what they *know*, not what is objectively true.

This system replaces the simple `KnownEvent` and `MerchantKnowledge` models from the World State SDD with a unified, world-spanning knowledge layer.

### Design Goals

- **Consistency** — the same rules govern player knowledge and NPC knowledge. No omniscient NPCs.
- **Emergent value** — knowledge of a treasure route or a faction's weakness has real, tradeable worth.
- **Believable spread** — information travels with ships and people; distance and time degrade it.
- **Meaningful secrecy** — actors can guard what they know; some facts never reach open circulation.
- **Exploitable uncertainty** — the player can act on false intelligence, and factions can plant it.

---

## 2. Core Data Model

### 2.1 KnowledgeFact

A `KnowledgeFact` is a claim about the state of the world at a specific point in time, held by one or more actors.

```csharp
// Ahoy.Simulation/State/KnowledgeStore.cs (actual implementation)

public sealed class KnowledgeFact
{
    public KnowledgeFactId      Id              { get; init; } = KnowledgeFactId.New();
    public required KnowledgeClaim Claim        { get; init; }
    public KnowledgeSensitivity Sensitivity     { get; init; }

    // How confident the holder is in this fact (0..1). Degrades each tick and each hop.
    public float                Confidence      { get; set; }

    // The world date this fact was observed / last confirmed.
    public required WorldDate   ObservedDate    { get; init; }

    // True if this fact was deliberately seeded as false information.
    // The holder believes it is true; only the seeder knows otherwise.
    public bool                 IsDisinformation { get; init; }

    // Number of hops this fact has propagated from its origin.
    // 0 = directly witnessed by the holder; 1 = heard from a witness; etc.
    public int                  HopCount        { get; set; }

    // True when a newer fact about the same subject has superseded this one.
    public bool                 IsSuperseded    { get; set; }

    // Tick on which this fact was superseded. Superseded facts are kept for one full
    // tick so other systems (e.g. LLM context) can read prior beliefs before pruning.
    public int?                 SupersededOnTick { get; set; }

    // ---- Provenance ----

    // Who passed this copy of the fact to the current holder.
    // Null means the holder witnessed the underlying event directly.
    // Uses the same KnowledgeHolderId union as every other actor — the player is
    // not special-cased; a PlayerHolder source is possible (player told the port).
    public KnowledgeHolderId?   SourceHolder    { get; init; }

    // The confidence value at the moment this copy was created, before any
    // per-tick decay. Stored so "how reliable was this when I got it?" can be
    // answered without reconstructing history.
    public float                BaseConfidence  { get; init; }

    // How many times the same claim has arrived from a *different* source.
    // Corroborating arrivals increment this counter rather than creating a
    // duplicate fact. Higher counts increase effective credibility.
    public int                  CorroborationCount { get; set; }
}
```

`KnowledgeFact` is largely immutable after creation. Mutable fields are limited to runtime state (`Confidence`, `HopCount`, `IsSuperseded`, `SupersededOnTick`, `CorroborationCount`). When the world changes, the old fact is marked superseded and a new fact is created — the history of what people believed at each point is preserved.

---

### 2.2 KnowledgeClaim

The actual content of a fact. A discriminated union typed by category. All
claim types are `record` subtypes of `KnowledgeClaim` in `KnowledgeStore.cs`.

```csharp
public abstract record KnowledgeClaim;

// ---- Economic ----
public record PortPriceClaim(PortId Port, TradeGood Good, int Price) : KnowledgeClaim;
public record PortProsperityClaim(PortId Port, float Prosperity) : KnowledgeClaim;
public record PortControlClaim(PortId Port, FactionId? FactionId) : KnowledgeClaim;
public record PortConditionClaim(PortId Port, PortConditionFlags Condition) : KnowledgeClaim;

// ---- Naval / Ship ----
public record ShipLocationClaim(ShipId Ship, ShipLocation LastKnownLocation) : KnowledgeClaim;
public record ShipStatusClaim(ShipId Ship, bool IsDestroyed, RegionId? LastKnownRegion) : KnowledgeClaim;
public record ShipCargoClaim(ShipId Ship, Dictionary<TradeGood, int> Cargo) : KnowledgeClaim;

// ---- Faction / Political ----
public record FactionStrengthClaim(FactionId Faction, int NavalStrength, int TreasuryGold) : KnowledgeClaim;
public record FactionIntentionClaim(FactionId Faction, string IntentionSummary) : KnowledgeClaim;

// ---- Environment ----
public record WeatherClaim(RegionId Region, WindStrength Wind, StormPresence Storm) : KnowledgeClaim;
public record RouteHazardClaim(RegionId From, RegionId To, string Description) : KnowledgeClaim;

// ---- Individual ----
public record IndividualWhereaboutsClaim(IndividualId Individual, PortId? Port) : KnowledgeClaim;
public record IndividualStatusClaim(IndividualId Individual, IndividualRole Role, FactionId? FactionId, bool IsAlive) : KnowledgeClaim;
public record IndividualAllegianceClaim(IndividualId Individual, FactionId ClaimedFaction, FactionId? ActualFaction) : KnowledgeClaim;

// ---- Deed Ledger (Group 6A) ----
public record IndividualActionClaim(IndividualId ActorId, IndividualId TargetId, IndividualId? BeneficiaryId,
    ActionPolarity Polarity, ActionSeverity Severity, string Context) : KnowledgeClaim;

// ---- Reconciliation (Group 6E) ----
public record PardonClaim(IndividualId GrantedBy, FactionId Faction, IndividualId PardonedActor) : KnowledgeClaim;

// ---- Contracts ----
public record ContractClaim(IndividualId IssuerId, FactionId IssuerFactionId, string TargetSubjectKey,
    ContractConditionType Condition, int GoldReward, NarrativeArchetype Archetype) : KnowledgeClaim;

// ---- POI / Geographic ----
public record OceanPoiClaim(OceanPoiId Poi, RegionId Region, PoiType Type,
    bool IsDiscovered, PoiCacheStatus CacheStatus) : KnowledgeClaim;

// ---- Other ----
public record PlayerActionClaim(string QuestTemplateId, string BranchId, PortId? Location) : KnowledgeClaim;
public record CustomClaim(string Subject, string Detail) : KnowledgeClaim;
```

---

### 2.3 KnowledgeSensitivity

Governs passive propagation rate between holders.

```csharp
public enum KnowledgeSensitivity
{
    Public,         // 30% propagation per tick — tavern gossip, port news
    Restricted,     // 10% propagation — factional intelligence, broker inventory
    Secret,         //  0% propagation — never spreads passively, requires investigation
    Disinformation  // 30% propagation — planted lies spread freely by design
}
```

---

### 2.4 KnowledgeStore

The world-level registry. Owned by `WorldState`. Keyed by holder — each
holder has a list of `KnowledgeFact`s they believe to be true.

```csharp
public sealed class KnowledgeStore
{
    public IReadOnlyList<KnowledgeFact> GetFacts(KnowledgeHolderId holder);
    public bool AddFact(KnowledgeHolderId holder, KnowledgeFact fact);
    public void MarkSuperseded(KnowledgeHolderId holder, KnowledgeFact fact, int tick);

    // Conflict tracking
    public IEnumerable<KnowledgeConflict> GetConflicts(KnowledgeHolderId holder);
    public IEnumerable<(KnowledgeHolderId, KnowledgeConflict)> GetAllConflicts();

    // Source reliability — per-holder float (default 1.0)
    public float GetSourceReliability(KnowledgeHolderId? source);
    public void RecordSourceOutcome(KnowledgeHolderId source, bool wasAccurate);

    public void PruneExpired(int currentTick);
}
```

---

### 2.5 KnowledgeHolderId

A union type identifying who holds knowledge.

```csharp
public abstract record KnowledgeHolderId;
public record PlayerHolder : KnowledgeHolderId;
public record IndividualHolder(IndividualId Individual) : KnowledgeHolderId;
public record FactionHolder(FactionId Faction) : KnowledgeHolderId;
public record PortHolder(PortId Port) : KnowledgeHolderId;
public record ShipHolder(ShipId Ship) : KnowledgeHolderId;
```

- **ShipHolder** — crew collective knowledge (navigator's log, tavern chatter)
- **IndividualHolder** — captain's personal knowledge (drives NPC routing and decisions)
- **PortHolder** — population knowledge pool (collective awareness of residents)

Ports hold a **population knowledge pool** — the collective awareness of residents and frequent visitors. This is distinct from the knowledge of individual named NPCs at that port.

---

## 3. Knowledge Categories

| Category | Examples | Typical Sensitivity | Primary Value |
|---|---|---|---|
| **Economic** | Port prices, supply shortages, trade route disruption | Common | Profitable trading |
| **Naval** | Fleet movements, patrol routes, ship sightings | Guarded–Secret | Safe routing, ambush |
| **Political** | Alliance shifts, governor personalities, faction plans | Restricted–Guarded | Diplomacy, contracts |
| **Personal** | Individual locations, reputations, secrets | Restricted–Secret | Contracts, leverage |
| **Geographic** | Safe passages, hidden coves, wreck sites | Common–Secret | Navigation, treasure |

---

## 4. Knowledge Origination

Every `WorldEvent` is a potential `KnowledgeFact`. When `EventPropagationSystem` fires an event, `KnowledgeSystem` intercepts it and:

1. Creates a `KnowledgeFact` from the event
2. Assigns initial holders (witnesses — those present at the event's location)
3. Sets sensitivity based on the event type and the interests of affected parties
4. Checks if any faction wants to guard it and registers an `ActiveGuard` if so

```csharp
// Ahoy.Simulation/Knowledge/KnowledgeSystem.cs (origination)

private void OnWorldEvent(WorldEvent worldEvent, WorldState state)
{
    var fact = CreateFact(worldEvent);
    if (fact is null) return;   // not all events produce knowledge facts

    state.Knowledge.Facts[fact.Id] = fact;

    // Immediate holders: witnesses at the location
    var witnesses = GetWitnesses(worldEvent, state);
    foreach (var witness in witnesses)
        GrantKnowledge(witness, fact.Id, state.Knowledge);

    // Faction guard check: does any faction want to suppress this?
    var guard = EvaluateGuardInterest(fact, state);
    if (guard is not null)
        state.Knowledge.GuardedFacts[fact.Id] = guard;
}
```

---

## 5. Knowledge Spreading

### 5.1 Ships as Knowledge Carriers

Ships carry knowledge the same way they carry cargo — picking it up at ports of departure and depositing it at ports of arrival.

When a ship **departs** a port:
- The captain acquires a **knowledge packet** from the port's population pool
- Packet contents are filtered by sensitivity (the port doesn't broadcast guarded facts openly)
- The captain's existing knowledge is merged with the packet

When a ship **arrives** at a port:
- The captain deposits their knowledge packet into the port's population pool
- Sensitivity filtering applies: captains don't freely share guarded or secret knowledge in open company
- The captain updates their own knowledge from the port's current pool

```csharp
private void ProcessKnowledgeArrival(Ship ship, Port port, WorldState state)
{
    var captain = GetCaptain(ship, state);
    var portHolder = new PortHolder(port.Id);
    var captainHolder = captain is not null ? new IndividualHolder(captain.Id) : null;

    // Port → Captain: captain learns from port gossip
    if (captainHolder is not null)
    {
        var portFacts = state.Knowledge.FactsKnownBy(portHolder)
            .Where(f => f.Sensitivity <= KnowledgeSensitivity.Common
                     || HasRelationship(captainHolder, port, state));

        foreach (var fact in portFacts)
            GrantKnowledge(captainHolder, fact.Id, state.Knowledge);
    }

    // Captain → Port: captain contributes their knowledge to port gossip
    if (captainHolder is not null)
    {
        var captainFacts = state.Knowledge.FactsKnownBy(captainHolder)
            .Where(f => f.Sensitivity <= KnowledgeSensitivity.Common);

        foreach (var fact in captainFacts)
            GrantKnowledge(portHolder, fact.Id, state.Knowledge);
    }
}
```

### 5.2 Spread Rate by Sensitivity

| Sensitivity | Spreads to port pool? | Spreads ship → ship? | Requires? |
|---|---|---|---|
| **Public** | Instantly | Yes | Nothing |
| **Common** | On arrival | Yes | Nothing |
| **Restricted** | On arrival | Only with relationship | Acquaintance |
| **Guarded** | Never passively | Rarely | Trust + context |
| **Secret** | Never | Never passively | Active trade only |

### 5.3 Corroboration Instead of Duplication

When a holder receives a fact via propagation, `ShareFacts` checks whether they already hold a non-superseded fact with the same subject key (`KnowledgeFact.GetSubjectKey`):

- **Same claim, same source** — identical retelling; skip entirely (no duplicate).
- **Same claim, different source** — corroboration. Increment `CorroborationCount` on the existing fact; do *not* create a new fact. This avoids knowledge stores ballooning with duplicate entries.
- **Different claim, same subject** — potential contradiction. Both facts coexist; both are flagged `IsContradicted = true` (future field). The holder must resolve the ambiguity through investigation or time.

```
incoming fact arrives for holder H:

  existing? ──No──► create new fact (SourceHolder = propagating entity)
      │
     Yes
      │
  same claim? ──No──► contradiction: flag both; keep both active
      │
     Yes
      │
  same source? ──Yes──► skip (duplicate retelling)
      │
      No
      │
  CorroborationCount++   (no new fact object)
```

The net effect: each holder has **at most one active fact per subject key** (unless contradicted), and `CorroborationCount` summarises independent confirmation.

### 5.3 Confidence Degradation

When a fact passes through an intermediary, confidence is reduced. A firsthand account loses fidelity as it becomes secondhand, then thirdhand:

```csharp
private float DegradeConfidence(float baseConfidence, int hops, int ageInDays)
{
    var hopPenalty = MathF.Pow(0.85f, hops);       // -15% per retelling
    var agePenalty = KnowledgeConfidence(ageInDays); // time decay (same as merchant model)
    return baseConfidence * hopPenalty * agePenalty;
}
```

A player who hears a rumour three times removed about an event from six weeks ago is working with heavily degraded information. They see `Confidence = 0.18` — barely a rumour.

### 5.4 Contradictions

When a newer fact about the same subject arrives, the older fact is **superseded**, not deleted:

```csharp
private void SupersedeOlderFacts(KnowledgeFact newFact, WorldState state)
{
    var related = state.Knowledge.Facts.Values
        .Where(f => !f.IsSuperseded && AreContradictory(f, newFact));

    foreach (var old in related)
        old.IsSuperseded = true;
}
```

Superseded facts remain in the store — they represent what people *used to* know. An actor who hasn't received the newer fact still acts on the superseded version. This is intentional: stale knowledge drives bad decisions, which drives emergent stories.

---

## 6. Knowledge Guarding

### 6.1 Faction Intelligence

Factions with strong intelligence capability actively suppress sensitive facts about themselves and gather facts about others.

```csharp
// Added to Faction

public int IntelligenceCapability { get; internal set; }  // 0–100
// High = better at keeping secrets and intercepting others' intel
```

Each tick, `KnowledgeSystem` runs suppression for guarded facts:

```csharp
private void RunSuppression(WorldState state)
{
    foreach (var (factId, guard) in state.Knowledge.GuardedFacts)
    {
        var faction = state.Factions[(FactionId)((FactionHolder)guard.Guardian).Id];
        var suppressionChance = faction.IntelligenceCapability / 100f * guard.Intensity / 10f;

        // For each holder that shouldn't have this: attempt to "burn" it
        // (in practice: mark it as known-to-be-false for specific holders)
        // Full suppression is expensive and rarely 100% effective
    }
}
```

Active guarding is imperfect. A well-resourced faction can slow a fact's spread significantly but rarely stop it entirely. Determined players or information brokers can still surface guarded facts — it just costs more.

### 6.2 Secret Facts

`Secret`-sensitivity facts are never in open circulation. They are only transferred through **deliberate, direct knowledge trades** (Section 7). They don't enter port population pools at all.

---

## 7. Knowledge Trading

This is the player-facing mechanic. Information is bought and sold openly in ports — at taverns, through information brokers, via faction contacts.

### 7.1 Information Brokers

Each port with a `Tavern` facility has at least one information broker NPC. Brokers hold a curated subset of facts known at that port, and deal in Restricted through Secret sensitivity.

```csharp
// Added to Individual
public bool IsInformationBroker { get; init; }
public List<KnowledgeFactId> BrokerInventory { get; } = new();  // facts they'll sell
public List<KnowledgeFactId> BrokerWantList  { get; } = new();  // facts they'll buy
```

A broker's inventory reflects their connections — a broker in a pirate haven knows different things than one in a Spanish colonial capital.

### 7.2 Buying Facts

The player requests a category of knowledge from a broker:

```
"What do you know about fleet movements near Hispaniola?"
```

The broker reveals:
- **What they have** — which facts they hold in that category
- **What it costs** — price scales with sensitivity, freshness, and the player's relationship with the broker

Pricing formula:
```
Price = BaseFee × SensitivityMultiplier × FreshnessMultiplier × (1 - RelationshipDiscount)

SensitivityMultiplier: Common=1, Restricted=3, Guarded=8, Secret=20
FreshnessMultiplier:  < 7 days=2.0, < 30 days=1.0, < 90 days=0.5, older=0.2
RelationshipDiscount: 0.0 (stranger) to 0.4 (trusted contact)
```

### 7.3 Selling Facts

The player can sell facts they hold to brokers or faction contacts:

- Brokers pay for facts on their want list; price uses the same formula
- **Faction contacts** pay a premium for strategically valuable facts (enemy fleet positions, governor secrets)
- Selling to a faction that is hostile to the fact's subject generates reputation gain with the buying faction
- Selling sensitive facts about allies damages those relationships

```csharp
public record KnowledgeSaleResult(
    int GoldReceived,
    FactionId? BuyingFaction,
    int ReputationDelta  // with buying faction
);
```

### 7.4 Trading Facts

The player can propose a direct exchange with an NPC — "I know where the Dutch patrol tonight; tell me where Castillo keeps his reserves."

The NPC evaluates:
- Is the offered fact on their want list or otherwise valuable?
- Do they trust the player enough to deal in this sensitivity level?
- Is the fact they're being asked for something they're willing to part with?

Trust gates secret trades. An NPC will not trade `Secret` facts with a stranger regardless of what's on offer.

### 7.5 Fact Verification

The player can't always know if a purchased fact is:
- **Stale** — superseded by newer events
- **Disinformation** — deliberately falsified

Indicators of reliability visible to the player:
- `Confidence` score (decays with time and hops)
- `HopCount` — 0 = firsthand; higher = more retelling noise
- `CorroborationCount` — how many independent sources have confirmed the same claim
- `SourceHolder` identity — a named individual source (`IndividualHolder`) may have a known reputation; a `PortHolder` source means general port gossip

The player cannot see `IsDisinformation` directly. Discovery is through contradiction (they hold a conflicting claim from a different source) or failed action (the claimed ship was not there).

**Investigation mechanic**: if the player sails to a location relevant to a fact (e.g. the region named in a `ShipLocationClaim`), the system produces a new direct observation: `HopCount = 0`, `SourceHolder = null` (self-witnessed). This either:
- corroborates the existing fact (`CorroborationCount++`, confidence reinforced), or
- contradicts it (new fact with conflicting claim, both flagged).

The player then holds first-hand evidence and can sell it, act on it, or confront the original source.

---

## 8. Fact Provenance

### 8.1 Fields Summary

| Field | Type | Mutability | Purpose |
|---|---|---|---|
| `SourceHolder` | `KnowledgeHolderId?` | immutable | Who passed this copy. Null = directly witnessed. |
| `BaseConfidence` | `float` | immutable | Confidence at creation; reference for how credible the original report was. |
| `CorroborationCount` | `int` | mutable | Number of additional independent confirmations received after the original. |
| `HopCount` | `int` | mutable | How many intermediaries the fact traversed before reaching this holder. |

### 8.2 Why SourceHolder, Not SourcePortId

An earlier design considered recording only the port where a fact was acquired. This was rejected because:

1. **Players are not special.** If a `PlayerHolder` tells a port about something they witnessed, the port should record `SourceHolder = new PlayerHolder()` — the same mechanism a `ShipHolder` or `IndividualHolder` source uses.
2. **Port is not the source.** The port population pool (`PortHolder`) is a relay, not the origin. `SourceHolder` names the immediate entity that passed the fact on, not the intermediary pool.
3. **Uniformity.** The same `KnowledgeHolderId` union represents all actors. No special casing, no parallel system for player vs NPC provenance.

### 8.3 Propagation: Setting SourceHolder

When `ShareFacts(from, to, ...)` creates a propagated copy:

```csharp
var propagated = new KnowledgeFact
{
    Claim           = fact.Claim,
    Sensitivity     = fact.Sensitivity,
    Confidence      = Math.Max(0f, fact.Confidence - HopConfidencePenalty),
    BaseConfidence  = fact.Confidence,   // confidence at the moment of handoff
    ObservedDate    = fact.ObservedDate,
    IsDisinformation = fact.IsDisinformation,
    HopCount        = fact.HopCount + 1,
    SourceHolder    = from,              // who is handing this to `to`
};
```

When `KnowledgeSystem.IngestEvent` creates an original fact (the event just happened):

```csharp
var fact = new KnowledgeFact
{
    Claim          = ...,
    Confidence     = baseConfidence,
    BaseConfidence = baseConfidence,
    HopCount       = 0,
    SourceHolder   = null,   // directly witnessed — no intermediary
};
```

### 8.4 Corroboration Logic in ShareFacts

Before creating a new propagated fact for holder `to`, check the holder's existing facts:

```
subjectKey = GetSubjectKey(fact.Claim)
existing = to's facts where SubjectKey == subjectKey && !IsSuperseded

if existing is null:
    create new propagated fact with SourceHolder = from

elif existing.Claim == fact.Claim:
    if existing.SourceHolder != from:
        existing.CorroborationCount++      // independent confirmation
    // same source → skip (retelling of the same chain)

else:
    // contradiction: different claim, same subject
    create new propagated fact; flag both (IsContradicted — future)
```

This keeps the store clean: one active fact per subject per holder, with `CorroborationCount` summarising how many independent voices agree.

### 8.5 Display (Console Harness)

The `knowledge` command will show:

```
  [68%]  ShipLocation           (observed 1683-04-02)  hops:2  corr:1  src:PortHolder(nassau)
  [35%]  IndividualWhereabouts  (observed 1683-03-15)  hops:0  corr:0  src:null (witnessed)
```

The `quests` command shows trigger facts with the same provenance summary so the player can gauge how credible the quest hook is.

---

## 9. Misinformation (Disinformation Injection)

Factions with high `IntelligenceCapability` can **inject false facts** into the knowledge network.

```csharp
// KnowledgeSystem — disinformation injection

public KnowledgeFact InjectDisinformation(
    FactionId sourceFaction,
    KnowledgeClaim falseClaim,
    RegionId injectedAt,
    WorldDate date,
    WorldState state)
{
    var fact = new KnowledgeFact
    {
        Id              = new KnowledgeFactId(Guid.NewGuid()),
        Category        = DeriveCategory(falseClaim),
        Sensitivity     = KnowledgeSensitivity.Common,  // false facts spread easily by design
        Claim           = falseClaim,
        OccurredAt      = date,
        OccurredIn      = injectedAt,
        BaseConfidence  = 0.7f,   // plausible but not certain
        IsDisinformation = true   // only the injector knows this
    };

    state.Knowledge.Facts[fact.Id] = fact;
    // Seed it into the port pool at injection location
    GrantKnowledge(new PortHolder(GetNearestPort(injectedAt, state)), fact.Id, state.Knowledge);
    return fact;
}
```

Disinformation looks identical to legitimate facts from the receiver's perspective. `IsDisinformation` is a server-side flag — the player and NPCs never see it directly. They discover it's false through contradiction or failed action.

---

## 10. KnowledgeSystem Tick

`KnowledgeSystem` replaces `InformationPropagationSystem` in the tick pipeline. It runs last, after all other systems have mutated state and emitted events.

```
Tick phases:
1. Originate     — convert this tick's WorldEvents into KnowledgeFacts; assign witnesses
2. Spread        — process ship arrivals/departures; update port pools
3. Degrade       — age all held facts; mark heavily degraded ones as near-useless
4. Suppress      — run faction intelligence suppression on guarded facts
5. Player update — rebuild player's effective knowledge view from their held facts
```

### Pipeline Position Update

| Order | System |
|---|---|
| 1 | WeatherSystem |
| 2 | ShipMovementSystem |
| 3 | EconomySystem |
| 4 | FactionSystem |
| 5 | EventPropagationSystem |
| **6** | **KnowledgeSystem** ← replaces InformationPropagationSystem |

---

## 11. Revisions to Prior SDDs

### WorldState
- Remove `KnownEvent` from `PlayerState.KnowledgeLog`
- Add `KnowledgeStore Knowledge { get; } = new();` to `WorldState`
- Add `KnowledgeHolderId PlayerId = new PlayerHolder();` as a well-known constant

### PlayerState
- Replace `List<KnownEvent> KnowledgeLog` with:
  ```csharp
  // Convenience view — player's facts from KnowledgeStore, pre-filtered and sorted
  // Rebuilt by KnowledgeSystem each tick; not persisted separately
  public List<KnowledgeFactId> KnownFactIds { get; } = new();
  ```

### Individual (MerchantKnowledge)
- Remove `MerchantKnowledge? MerchantKnowledge` from `Individual`
- Merchant captains now use `KnowledgeStore.FactsKnownBy(new IndividualHolder(id))` directly
- `DaysSinceLastProfitableTrade` and `CurrentGold` (desperation state) remain on a new `MerchantState` property on `Individual`

### EconomySystem
- Replace all `MerchantKnowledge` reads with `KnowledgeStore` queries
- `ScoreDestination()` queries `FactsKnownBy(captainHolder)` for price and safety facts relevant to the destination, using the fact's `OccurredAt` as the age for confidence decay

---

## 12. Open Questions

- [ ] Should the player see raw `Confidence` scores, or should the UI translate them into qualitative labels ("reliable tip", "old rumour", "unverified claim")?
- [ ] Can the player run their own intelligence operation — e.g. hiring a spy network at a port to passively feed them facts?
- [ ] Faction counter-intelligence: if the player sells a sensitive fact about a faction, can that faction trace the leak back and retaliate?
- [ ] Should port population knowledge pools decay over time (people forget), or persist indefinitely?

---

*Next step: FactionSystem — goals, relationships, and how factions act on the world each tick.*
