# Ahoy — System Design: Knowledge System

> **Status:** Living document.
> **Version:** 0.1
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
// Ahoy.Simulation/Knowledge/KnowledgeFact.cs

public sealed class KnowledgeFact
{
    public KnowledgeFactId   Id          { get; init; }
    public KnowledgeCategory Category    { get; init; }
    public KnowledgeSensitivity Sensitivity { get; init; }

    // What the fact describes
    public KnowledgeClaim    Claim       { get; init; }

    // When and where the underlying event occurred
    public WorldDate         OccurredAt  { get; init; }
    public RegionId          OccurredIn  { get; init; }

    // How confident the original source was (1.0 = directly witnessed)
    public float             BaseConfidence { get; init; }

    // Is this fact still current, or has it been superseded by newer information?
    public bool              IsSuperseded   { get; internal set; }

    // Has this fact been deliberately falsified by an actor?
    // Only the injecting faction knows this is true — holders do not.
    public bool              IsDisinformation { get; internal set; }
}
```

`KnowledgeFact` is **immutable after creation** (except `IsSuperseded` and `IsDisinformation`). When the world changes, the old fact is marked superseded and a new fact is created — the history of what people believed at each point is preserved.

---

### 2.2 KnowledgeClaim

The actual content of a fact. A discriminated union typed by category.

```csharp
// Ahoy.Simulation/Knowledge/KnowledgeClaim.cs

public abstract record KnowledgeClaim;

// Economic
public record PortPricesClaim(PortId Port, Dictionary<TradeGood, int> Prices)
    : KnowledgeClaim;
public record TradeRouteSafetyChangedClaim(RegionId Region, float SafetyScore)
    : KnowledgeClaim;

// Naval / Military
public record ShipSpottedClaim(ShipId Ship, RegionId Region, FactionId? Owner)
    : KnowledgeClaim;
public record FleetMovementClaim(FactionId Faction, RegionId Destination, int EstimatedSize)
    : KnowledgeClaim;
public record PatrolRouteClaim(FactionId Faction, List<RegionId> Regions)
    : KnowledgeClaim;

// Political
public record FactionRelationshipClaim(FactionId A, FactionId B, int Standing)
    : KnowledgeClaim;
public record GovernorTraitClaim(IndividualId Governor, string Trait, object Value)
    : KnowledgeClaim;   // e.g. Trait="AcceptsBribes", Value=true

// Personal
public record IndividualLocationClaim(IndividualId Individual, PortId Port)
    : KnowledgeClaim;
public record IndividualReputationClaim(IndividualId Individual, int Reputation)
    : KnowledgeClaim;

// Geographic / Secret
public record TreasureRouteClaim(List<RegionId> Route, int EstimatedValue)
    : KnowledgeClaim;
public record HiddenLocationClaim(string Description, RegionId Region)
    : KnowledgeClaim;
```

---

### 2.3 KnowledgeSensitivity

How much the subject of a fact wants it suppressed. Governs spread rate and access restrictions.

```csharp
public enum KnowledgeSensitivity
{
    Public,      // Openly known; no restrictions (port ownership, faction allegiance)
    Common,      // Spreads freely through normal social channels (approximate prices, sea conditions)
    Restricted,  // Subject prefers privacy; needs a relationship to access
    Guarded,     // Subject actively works to suppress; requires trust, gold, or coercion
    Secret       // Faction/individual will act to prevent spread; high risk to trade openly
}
```

---

### 2.4 KnowledgeStore

The world-level registry. Owned by `WorldState`.

```csharp
// Ahoy.Simulation/Knowledge/KnowledgeStore.cs

public sealed class KnowledgeStore
{
    // All facts that exist in the world
    public Dictionary<KnowledgeFactId, KnowledgeFact> Facts { get; } = new();

    // Who holds which facts — the spreading state of each piece of knowledge
    // KnowledgeHolderId can represent a player, individual, faction, or port
    public Dictionary<KnowledgeHolderId, HashSet<KnowledgeFactId>> HolderFacts { get; } = new();

    // Facts being actively guarded by an entity
    public Dictionary<KnowledgeFactId, ActiveGuard> GuardedFacts { get; } = new();

    // Convenience: facts held by a specific actor
    public IEnumerable<KnowledgeFact> FactsKnownBy(KnowledgeHolderId holder) =>
        HolderFacts.TryGetValue(holder, out var ids)
            ? ids.Select(id => Facts[id])
            : Enumerable.Empty<KnowledgeFact>();
}

public sealed class ActiveGuard
{
    public KnowledgeFactId  Fact        { get; init; }
    public KnowledgeHolderId Guardian   { get; init; }   // faction or individual
    public int              Intensity   { get; init; }   // 1–10; affects spread suppression
}
```

`WorldState` gains: `KnowledgeStore Knowledge { get; } = new();`

---

### 2.5 KnowledgeHolderId

A union type identifying who holds knowledge — player, named individual, faction, or port population.

```csharp
public abstract record KnowledgeHolderId;
public record PlayerHolder                              : KnowledgeHolderId;
public record IndividualHolder(IndividualId Id)         : KnowledgeHolderId;
public record FactionHolder(FactionId Id)               : KnowledgeHolderId;
public record PortHolder(PortId Id)                     : KnowledgeHolderId;
```

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

Indicators of reliability:
- `Confidence` score (shown to the player)
- Source reputation (a known reliable broker vs. a dock rat)
- Corroboration — the same claim from multiple independent sources raises effective confidence

If the player acts on a fact and it proves false (they sail to an "unguarded" port and find a full patrol), they can mark the source as unreliable — feeding back into their relationship with that broker.

---

## 8. Misinformation

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

## 9. KnowledgeSystem Tick

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

## 10. Revisions to Prior SDDs

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

## 11. Open Questions

- [ ] Should the player see raw `Confidence` scores, or should the UI translate them into qualitative labels ("reliable tip", "old rumour", "unverified claim")?
- [ ] Can the player run their own intelligence operation — e.g. hiring a spy network at a port to passively feed them facts?
- [ ] Faction counter-intelligence: if the player sells a sensitive fact about a faction, can that faction trace the leak back and retaliate?
- [ ] Should port population knowledge pools decay over time (people forget), or persist indefinitely?

---

*Next step: FactionSystem — goals, relationships, and how factions act on the world each tick.*
