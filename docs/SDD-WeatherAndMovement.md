# Ahoy — System Design: WeatherSystem & ShipMovementSystem

> **Status:** Living document.
> **Version:** 0.1
> **Depends on:** SDD-WorldState.md, SDD-SimulationLOD.md, SDD-KnowledgeSystem.md, SDD-EconomySystem.md

---

## 1. Overview

`WeatherSystem` runs first in the tick pipeline — it updates regional weather state before any ship or economic processing occurs. `ShipMovementSystem` runs second — it advances ships using the weather state `WeatherSystem` just produced.

Their coupling is deliberate and one-directional: weather affects movement, movement does not affect weather. Weather is a world-level pressure system; ships respond to it.

Together these two systems are responsible for the feel of the sea: the danger of a hurricane season, the delay of being becalmed, the decision of whether to risk a crossing in deteriorating conditions.

---

## 2. WeatherSystem

### 2.1 RegionWeather — State Model

Weather is tracked per region, not per tile. The Caribbean has five regions; each has independent but correlated weather state.

```csharp
// Ahoy.Simulation/State/RegionWeather.cs

public sealed class RegionWeather
{
    public RegionId       Region            { get; init; }
    public WindStrength   WindStrength      { get; internal set; }
    public WindDirection  WindDirection     { get; internal set; }
    public StormPresence  Storm             { get; internal set; }
    public float          Visibility        { get; internal set; }  // 0.0–1.0
    public int            StormDaysRemaining { get; internal set; } // ticks until storm passes
    public bool           IsHurricaneSeason => IsInHurricaneSeason(CurrentDate);
}

public enum WindStrength   { Calm, Light, Moderate, Strong, Gale }
public enum WindDirection  { N, NE, E, SE, S, SW, W, NW }
public enum StormPresence  { Clear, Squall, Storm, Hurricane }
```

`WorldState` gains:
```csharp
public Dictionary<RegionId, RegionWeather> Weather { get; } = new();
```

Weather is initialised at world creation with historically plausible Caribbean defaults: predominantly easterly trade winds at `Moderate` strength, `Clear` conditions.

---

### 2.2 Seasonality

The Caribbean has a well-defined hurricane season (June–November) and a calmer dry season (December–May). `WeatherSystem` uses the world calendar to shift probability distributions each tick.

```csharp
// Ahoy.Simulation/Systems/WeatherSystem.cs

private SeasonalProfile GetSeasonalProfile(WorldDate date)
{
    var month = date.ToCalendarDate().Month;
    return month switch
    {
        6 or 7 or 8 or 9 or 10 or 11 => SeasonalProfile.HurricaneSeason,
        12 or 1 or 2                  => SeasonalProfile.WinterPassat,
        _                             => SeasonalProfile.DryTrade
    };
}
```

```csharp
public sealed record SeasonalProfile(
    float HurricaneChancePerTick,   // probability a hurricane forms in any region this tick
    float StormChancePerTick,       // probability of a non-hurricane storm forming
    float CalmChancePerTick,        // probability of becalmed conditions
    WindDirection DominantWind,     // most likely wind direction this season
    float DominantWindBias          // 0–1, how strongly the dominant wind is favoured
);

// Approximate historical Caribbean profiles
public static readonly SeasonalProfile HurricaneSeason = new(
    HurricaneChancePerTick:  0.002f,   // roughly 1 hurricane per region every 500 days in season
    StormChancePerTick:      0.015f,
    CalmChancePerTick:       0.005f,
    DominantWind:            WindDirection.E,
    DominantWindBias:        0.6f
);

public static readonly SeasonalProfile DryTrade = new(
    HurricaneChancePerTick:  0.0001f,
    StormChancePerTick:      0.004f,
    CalmChancePerTick:       0.008f,
    DominantWind:            WindDirection.NE,
    DominantWindBias:        0.75f
);

public static readonly SeasonalProfile WinterPassat = new(
    HurricaneChancePerTick:  0.0001f,
    StormChancePerTick:      0.008f,
    CalmChancePerTick:       0.003f,
    DominantWind:            WindDirection.NE,
    DominantWindBias:        0.7f
);
```

---

### 2.3 Weather Transitions

Weather follows a **Markov-like** model — current state biases next state. Storms don't appear from clear skies in a single tick; they build over days and dissipate similarly.

```csharp
public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
{
    var profile = GetSeasonalProfile(state.Date);

    foreach (var (regionId, weather) in state.Weather)
    {
        var lod = context.GetLod(regionId);
        UpdateWeather(weather, profile, lod, state, events);
    }
}

private void UpdateWeather(
    RegionWeather weather, SeasonalProfile profile,
    SimulationLod lod, WorldState state, IEventEmitter events)
{
    // Active storms tick down
    if (weather.Storm != StormPresence.Clear)
    {
        weather.StormDaysRemaining--;
        if (weather.StormDaysRemaining <= 0)
            ClearStorm(weather, state, events, lod);
        else
            IntensifyOrWeaken(weather, profile, lod, events);
        return;
    }

    // Chance of new storm forming
    var roll = _rng.NextSingle();
    if (roll < profile.HurricaneChancePerTick)
        SpawnHurricane(weather, state, events, lod);
    else if (roll < profile.HurricaneChancePerTick + profile.StormChancePerTick)
        SpawnStorm(weather, state, events, lod);
    else
        UpdateFairWeather(weather, profile);
}
```

### 2.4 Storm Lifecycle

Storms have a defined duration with gradual intensification and dissipation:

```csharp
private void SpawnStorm(RegionWeather weather, WorldState state, IEventEmitter events, SimulationLod lod)
{
    weather.Storm             = StormPresence.Squall;
    weather.StormDaysRemaining = _rng.Next(3, 10);   // squalls last 3–9 days
    weather.WindStrength      = WindStrength.Strong;
    weather.Visibility        = 0.3f;

    events.Emit(new StormEntered(state.Date, weather.Region, StormPresence.Squall), lod);
}

private void SpawnHurricane(RegionWeather weather, WorldState state, IEventEmitter events, SimulationLod lod)
{
    weather.Storm              = StormPresence.Hurricane;
    weather.StormDaysRemaining = _rng.Next(5, 14);   // hurricanes last 5–13 days
    weather.WindStrength       = WindStrength.Gale;
    weather.Visibility         = 0.1f;

    events.Emit(new HurricaneEvent(state.Date, weather.Region, severity: 0.8f + _rng.NextSingle() * 0.2f), lod);
    // HurricaneEvent is consumed by EventPropagationSystem → ProductionModifier on ports
}

private void ClearStorm(RegionWeather weather, WorldState state, IEventEmitter events, SimulationLod lod)
{
    var previous       = weather.Storm;
    weather.Storm      = StormPresence.Clear;
    weather.WindStrength = WindStrength.Moderate;
    weather.Visibility = 1.0f;

    events.Emit(new WeatherCleared(state.Date, weather.Region, previous), lod);
}
```

**Storm propagation between regions:** A hurricane has a chance to move into an adjacent region when it intensifies, simulating realistic hurricane tracks. At `Distant` LOD this is resolved as a single-tick probability; at `Local`/`Regional` it plays out day by day.

---

### 2.5 Wind Generation

Outside of storms, wind is generated from the seasonal profile with some random variation:

```csharp
private void UpdateFairWeather(RegionWeather weather, SeasonalProfile profile)
{
    // Wind direction: biased toward seasonal dominant, with variation
    weather.WindDirection = _rng.NextSingle() < profile.DominantWindBias
        ? profile.DominantWind
        : (WindDirection)_rng.Next(8);

    // Wind strength: normally distributed around Moderate, season-adjusted
    var strengthRoll = _rng.NextSingle();
    weather.WindStrength = strengthRoll switch
    {
        < 0.05f => WindStrength.Calm,
        < 0.25f => WindStrength.Light,
        < 0.70f => WindStrength.Moderate,
        < 0.92f => WindStrength.Strong,
        _       => WindStrength.Gale
    };

    weather.Visibility = weather.WindStrength == WindStrength.Gale ? 0.5f : 1.0f;
}
```

---

### 2.6 WeatherSystem LOD Behaviour

| LOD | Behaviour |
|---|---|
| **Local** | Full daily weather transitions; all events emitted; player experiences specific conditions |
| **Regional** | Full transitions; aggregate events only (`StormPresenceChanged` level changes, not daily shifts) |
| **Distant** | Major events only (hurricane formation, hurricane clearance); daily wind variation not tracked |

At `Distant` LOD, `RegionWeather` still holds a valid state but is updated less precisely. When a region transitions to `Local` LOD, weather state is as-is — no materialisation needed, since even coarse state is plausible.

---

### 2.7 WeatherSystem Events Emitted

| Event | LOD | Trigger |
|---|---|---|
| `StormEntered(region, StormPresence)` | All | Storm forms in a region |
| `HurricaneEvent(region, severity)` | All | Hurricane forms |
| `WeatherCleared(region, previous)` | All | Storm dissipates |
| `StormMovedToRegion(from, to)` | Local/Regional | Hurricane propagates to adjacent region |
| `BecalmedConditions(region)` | Local | Wind drops to Calm |

---

## 3. ShipMovementSystem

### 3.1 Movement Mechanics

Each tick, `ShipMovementSystem` advances all ships with an `EnRoute` location.

```csharp
public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
{
    var weather = state.Weather;

    foreach (var ship in state.Ships.Values)
    {
        if (ship.Location is not EnRoute route) continue;

        var regionLod    = context.GetLod(route.To);  // LOD of destination region
        var regionWeather = weather.GetValueOrDefault(route.From)
                         ?? weather.GetValueOrDefault(route.To);

        AdvanceShip(ship, route, regionWeather, regionLod, state, context, events);
    }

    // Detect encounters at Local LOD after all movement has resolved
    foreach (var region in state.Regions.Values)
        if (context.GetLod(region.Id) == SimulationLod.Local)
            DetectEncounters(region, state, events);
}
```

### 3.2 Weather Speed Modifier

Wind direction relative to ship heading, combined with wind strength, determines a movement speed modifier applied to `DaysRemaining` each tick:

```csharp
private float GetSpeedModifier(RegionWeather? weather, EnRoute route)
{
    if (weather is null) return 1.0f;

    // Storm conditions override everything
    var stormModifier = weather.Storm switch
    {
        StormPresence.Hurricane => 0.0f,   // ships do not sail in hurricanes
        StormPresence.Storm     => 0.35f,
        StormPresence.Squall    => 0.60f,
        _                       => 1.0f
    };
    if (stormModifier == 0.0f) return 0.0f;  // ship is held in place

    // Wind relationship to route heading
    var routeHeading = ComputeHeading(route.From, route.To);
    var windAngle    = AngleBetween(routeHeading, weather.WindDirection);

    var windModifier = (weather.WindStrength, windAngle) switch
    {
        (WindStrength.Calm, _)                   => 0.45f,   // barely moving
        (WindStrength.Light, < 45)               => 0.85f,   // light tailwind
        (WindStrength.Moderate, < 45)            => 1.20f,   // good following wind
        (WindStrength.Strong, < 45)              => 1.45f,   // excellent conditions
        (WindStrength.Moderate, > 135)           => 0.65f,   // beating into wind
        (WindStrength.Strong, > 135)             => 0.50f,   // hard beating
        (WindStrength.Gale, > 90)                => 0.30f,   // dangerous beat
        _                                        => 1.0f
    };

    return stormModifier * windModifier;
}
```

### 3.3 Advancing Ships

```csharp
private void AdvanceShip(
    Ship ship, EnRoute route, RegionWeather? weather,
    SimulationLod lod, WorldState state, SimulationContext context, IEventEmitter events)
{
    var speedMod = GetSpeedModifier(weather, route);

    if (speedMod == 0.0f)
    {
        // Held by hurricane — ship cannot move, may take damage
        ApplyStormHazard(ship, weather!, state, events, lod);
        return;
    }

    // Speed modifier < 1.0 means the ship takes longer; we reduce progress accordingly.
    // A modifier of 1.0 = advance by 1 tick. 0.5 = half speed = advance by 0.5 ticks.
    // We track fractional progress to avoid rounding loss over many ticks.
    ship.RouteProgressAccumulator += speedMod;

    while (ship.RouteProgressAccumulator >= 1.0f)
    {
        ship.RouteProgressAccumulator -= 1.0f;
        ship.Location = route with { DaysRemaining = route.DaysRemaining - 1 };
        route = (EnRoute)ship.Location;

        if (route.DaysRemaining <= 0)
        {
            ArriveAtDestination(ship, route, state, context, events, lod);
            return;
        }
    }
}
```

`Ship` gains `float RouteProgressAccumulator` to track sub-tick progress without losing fractional movement.

---

### 3.4 Arrival Processing

When `DaysRemaining` reaches 0, the ship arrives at its destination port or region:

```csharp
private void ArriveAtDestination(
    Ship ship, EnRoute route, WorldState state,
    SimulationContext context, IEventEmitter events, SimulationLod lod)
{
    // Find the destination port in the target region
    // For NPC ships: their assigned destination port
    // For ships without a specific port: they enter the region as AtSea
    var destPort = GetDestinationPort(ship, route.To, state);

    if (destPort is not null)
    {
        ship.Location = new AtPort(destPort.Value);
        events.Emit(new ShipArrivedAtPort(state.Date, ship.Id, destPort.Value), lod);
        // EconomySystem will process cargo unloading next tick (Phase 3)
    }
    else
    {
        ship.Location = new AtSea(route.To);
        events.Emit(new ShipEnteredRegion(state.Date, ship.Id, route.To), lod);
    }

    // Update captain's regional safety knowledge on arrival
    UpdateSafetyKnowledge(ship, route.To, state);
}
```

`ShipArrivedAtPort` is the event `EconomySystem` Phase 3 watches for — it triggers cargo unloading.

---

### 3.5 Storm Damage

Ships at sea during storms risk damage. Ships in port during hurricanes risk lesser damage (the port provides partial shelter).

```csharp
private void ApplyStormHazard(
    Ship ship, RegionWeather weather, WorldState state, IEventEmitter events, SimulationLod lod)
{
    var damageChance = weather.Storm switch
    {
        StormPresence.Hurricane => 0.25f,  // 25% chance of significant damage per tick
        StormPresence.Storm     => 0.08f,
        StormPresence.Squall    => 0.02f,
        _                       => 0.0f
    };

    if (_rng.NextSingle() < damageChance)
    {
        var damage = weather.Storm == StormPresence.Hurricane
            ? _rng.Next(20, 50)    // severe
            : _rng.Next(5, 20);    // moderate

        ship.HullIntegrity = Math.Max(0, ship.HullIntegrity - damage);

        events.Emit(new ShipDamagedByWeather(state.Date, ship.Id, damage, weather.Storm), lod);

        if (ship.HullIntegrity <= 0)
        {
            ship.Location = new AtSea(GetCurrentRegion(ship, state));
            events.Emit(new ShipDestroyed(state.Date, ship.Id, DestroyedBy: null), lod);
        }
    }
}
```

**Named/important ships** (anchored per `SDD-SimulationLOD.md`) have their damage applied explicitly even at `Distant` LOD, generating knowledge facts. Unnamed ships at `Distant` LOD have damage resolved statistically in aggregate (X% of ships in a hurricane-affected region are lost).

---

### 3.6 Encounter Detection

At `Local` LOD only. Encounters are detected after all movement has resolved for the tick, so ships are in their final positions.

```csharp
private void DetectEncounters(Region region, WorldState state, IEventEmitter events)
{
    var shipsPresent = state.Ships.Values
        .Where(s => ShipIsInRegion(s, region.Id))
        .ToList();

    var playerShips = shipsPresent.Where(s => IsPlayerShip(s, state)).ToList();

    // Player encounter detection — prioritised and always checked
    foreach (var playerShip in playerShips)
    {
        foreach (var otherShip in shipsPresent.Where(s => !IsPlayerShip(s, state)))
        {
            if (!EncounterAlreadyPending(playerShip, otherShip, state)
                && ShouldEncounter(playerShip, otherShip, region, state))
            {
                events.Emit(new ShipEncounterDetected(
                    state.Date, playerShip.Id, otherShip.Id, region.Id),
                    SimulationLod.Local);
            }
        }
    }

    // NPC-NPC encounter detection — pirates vs merchants, patrols vs pirates
    // Resolved directly (no ActorDecisionSystem involvement for unnamed ships)
    DetectNpcEncounters(shipsPresent.Where(s => !IsPlayerShip(s, state)).ToList(),
                        region, state, events);
}
```

**Encounter probability** is not 100% for all ships in the same region — the Caribbean is large. Probability is modulated by:
- Ship speed (faster ships are harder to intercept)
- Visibility (weather-derived — fog and storms reduce encounter chance)
- Whether a ship is actively hunting vs. simply transiting
- Relative size of the region (smaller regions have higher encounter density)

```csharp
private bool ShouldEncounter(Ship hunter, Ship prey, Region region, WorldState state)
{
    var visibility      = state.Weather.GetValueOrDefault(region.Id)?.Visibility ?? 1.0f;
    var regionDensity   = 1.0f / region.PortIds.Count;  // fewer ports = less-trafficked = lower chance
    var huntingModifier = IsActivelyHunting(hunter, prey, state) ? 3.0f : 1.0f;

    var chance = BaseEncounterChance * visibility * regionDensity * huntingModifier;
    return _rng.NextSingle() < chance;
}
```

`ShipEncounterDetected` is consumed by the player-facing interaction layer (not yet designed) and by the `ActorDecisionSystem` for named NPC ships — triggering an `EncounterWithPlayerTrigger` inflection point if appropriate.

---

### 3.7 NPC-NPC Encounters

When a pirate ship and a merchant ship are in the same region, a rule-based encounter resolution fires without ActorDecisionSystem involvement:

```csharp
private void DetectNpcEncounters(
    IReadOnlyList<Ship> ships, Region region, WorldState state, IEventEmitter events)
{
    var pirates   = ships.Where(s => IsPirateShip(s, state)).ToList();
    var merchants = ships.Where(s => IsMerchantShip(s, state) && s.Cargo.Any()).ToList();

    foreach (var pirate in pirates)
    foreach (var merchant in merchants)
    {
        if (_rng.NextSingle() < NpcEncounterChance)
            ResolveNpcEncounter(pirate, merchant, region, state, events);
    }
}

private void ResolveNpcEncounter(
    Ship pirate, Ship merchant, Region region, WorldState state, IEventEmitter events)
{
    // Simple rule: pirate strength vs merchant escort strength
    var pirateStr   = ShipStrength(pirate);
    var merchantStr = ShipStrength(merchant) + GetEscortStrength(merchant, ships);

    if (pirateStr > merchantStr * 0.8f)
    {
        // Successful raid: transfer cargo
        foreach (var (good, qty) in merchant.Cargo.ToList())
        {
            pirate.Cargo[good] = pirate.Cargo.GetValueOrDefault(good) + qty;
            merchant.Cargo.Remove(good);
        }
        events.Emit(new ShipRaided(state.Date, merchant.Id, pirate.Id, region.Id),
                    SimulationLod.Local);
    }
    else
    {
        events.Emit(new RaidRepelled(state.Date, merchant.Id, pirate.Id, region.Id),
                    SimulationLod.Local);
    }
}
```

NPC-NPC outcomes are approximate. They don't model ship damage in detail — just cargo transfer and event emission. The knowledge system picks these up and they propagate as safety facts.

---

### 3.8 ShipMovementSystem LOD Behaviour

| LOD | Behaviour |
|---|---|
| **Local** | Individual ships advance daily; full speed calculation; encounter detection active; specific arrival/departure events |
| **Regional** | Ships advance daily; speed calculation runs; no encounter detection; aggregate events (ship arrived at port, ship entered region) |
| **Distant** | Ships bulk-advanced (skip intermediate ticks); only major transitions fire (arrived at major port, route abandoned); no per-ship events |

At `Distant` LOD, bulk-advancing means a ship with `DaysRemaining = 15` that has been distant for 20 ticks is simply set to arrived — no tick-by-tick progression. This is fast and produces the right end state.

---

## 4. System Interactions

| System | Interaction |
|---|---|
| `EventPropagationSystem` | Consumes `HurricaneEvent` → applies `ProductionModifier` to ports in region; consumes `ShipDamagedByWeather` → faction naval strength updates |
| `EconomySystem` | Consumes `ShipArrivedAtPort` → cargo unloading (Phase 3); consumes `StormEntered` → `TradeRouteDisrupted` stimulus |
| `FactionSystem` | Consumes `ShipDestroyed` (named faction vessel) → `NavalStrengthDecline` stimulus; patrol ships lost in hurricanes reduce `PatrolAllocations` |
| `KnowledgeSystem` | `ShipEncounterDetected`, `ShipRaided`, `StormEntered` → knowledge facts; merchant captains update `RegionSafetySnapshot` on arrival |
| `ActorDecisionSystem` | `ShipEncounterDetected` involving named NPCs → potential `EncounterWithPlayerTrigger` inflection point |

---

## 5. New Data Additions

### WorldState
```csharp
public Dictionary<RegionId, RegionWeather> Weather { get; } = new();
```

### Ship
```csharp
public float RouteProgressAccumulator { get; internal set; }  // fractional tick progress
```

---

## 6. Revisions to Prior SDDs

### SDD-WorldState
- `Ship` gains `float RouteProgressAccumulator`
- `WorldState` gains `Dictionary<RegionId, RegionWeather> Weather`

### SDD-EconomySystem
- Phase 3 (Arrivals) watches for `ShipArrivedAtPort` events from `ShipMovementSystem` rather than directly checking `ShipLocation` transitions — cleaner decoupling
- `TradeRouteDisrupted` stimuli from storms received by `EconomySystem` next tick reduce merchant routing scores for affected regions

### SDD-EventPropagationSystem
- `ShipDamagedByWeather` added as an event triggering `NavalStrengthDecline` stimulus via `PendingFactionStimuli`
- `StormEntered` (regional/distant) triggers `TradeRouteDisrupted` propagation rule

---

## 7. Open Questions

- [ ] Should the player have advance warning of weather — e.g. a barometer mechanic, or knowledge facts from ships that came from a storm region? Fits naturally into the knowledge system; a merchant arriving from a stormy region carries a `StormEntered` knowledge fact.
- [ ] Should ships be able to deliberately seek shelter (divert to nearest port when a storm threatens)? This would be an emergency override on `ShipMovementSystem` routing — ships abandon their route and dock. Adds realism but increases system complexity.
- [ ] Hurricane tracks — should hurricanes follow historically plausible Caribbean paths (west-to-east, curving northward) or be fully random? A path model would make hurricane season more predictable and navigable for skilled players.
- [ ] Fog as a separate weather condition — reduces encounter detection range significantly, creates opportunities for ambush. Currently folded into `Visibility` but could be its own `StormPresence` value.
- [ ] Should weather patterns be visible on the world map, or only discoverable through knowledge facts (ships reporting conditions)?

---

*All six tick pipeline systems are now designed. Next step: architecture review — how the systems wire together in the engine, project structure, and readiness assessment for implementation.*
