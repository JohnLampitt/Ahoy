# Ahoy — Game Design Document

> **Status:** Living document. Updated iteratively as design is validated.
> **Version:** 0.4 — Factions, map scope, and game start resolved

---

## 1. Vision & Pillars

**Ahoy** is a nautical sandbox set in the **Caribbean during the age of sail**. The player is a ship captain navigating a world that does not wait for them. There is no win condition — only the story of who you became and what you left behind.

### Inspirations

| Game | What We Take From It |
|---|---|
| **Sid Meier's Pirates** | Player-driven arc; exploration, trade, combat, and reputation as interlocking systems; the captain as protagonist |
| **Dwarf Fortress** | A living world with genuine agency; named individuals whose stories emerge from systems; cause-and-effect that creates narrative without scripting |

### Core Pillars

1. **The world is alive** — factions pursue goals, economies shift, individuals rise and fall, whether or not the player is involved
2. **Information is power** — the world is too large and too complex to fully know; distance and time degrade the reliability of what you know
3. **Actions have consequences** — nothing happens in isolation; decisions ripple outward through interconnected systems
4. **Stories emerge, not scripts** — no hand-crafted quest chains; the world generates situations that become memorable because they arise from play

### Technical Approach

- **Language:** C#
- **Architecture:** Backend-first; simulation layer is decoupled from any rendering or UI concern
- **Frontend:** Not committed at this stage; designed to be slotted in later without rearchitecting the simulation
- **World:** Hand-crafted Caribbean map; procedural generation is reserved for stories, characters, and events — not geography
- **Map scope:** Five broad regions (see Section 8) sized to make the distance mechanic meaningful — sailing from the Gulf of Mexico to the Lesser Antilles should feel like a genuine journey with information lag

---

## 2. Simulation Model

### Primary Mode: World Simulation

The world runs on a **paused-but-simulating** model (à la Dwarf Fortress). Time advances in **day-ticks** — the coarsest granularity that still feels meaningful. Within each tick, factions act, economies shift, ships move, and events fire.

The player can pause the world to make decisions without time pressure.

### Secondary Mode: Ship Combat

When combat is initiated, the world simulation **freezes** and hands off to a separate **combat simulation context** with finer-grained ticks. The wider world stands still.

On combat resolution, results are **projected back** onto world state: ships damaged, cargo transferred, captains captured, factions notified (with appropriate delay).

### Simulation LOD (Level of Detail by Distance)

The world is too large to simulate at full fidelity everywhere. Simulation accuracy scales down with distance from the player:

| Tier | Distance | Fidelity | Information Model |
|---|---|---|---|
| **Local** | Same region | Full day-ticks; all entities active | Player has real-time awareness |
| **Regional** | Nearby seas | Coarser ticks; key events only | Reports are days to weeks old |
| **Distant** | Far regions | State summaries only; events resolved coarsely | Rumours; old news; potentially inaccurate |

When the player arrives in a new region, the simulation **catches up** — reconciling the coarse distant state into full fidelity. The time-delay model provides natural cover for any discontinuities.

### Time-Delayed Information

Realistic information latency is both a **narrative mechanic** and an **architectural feature**:

- News travels at the speed of ships — a distant battle is reported weeks after it happened
- Orders sent to allies may be overtaken by events before they arrive
- The player cannot verify what is happening far away, making distant sim shortcuts invisible and believable

### SimulationContext Abstraction

The engine maintains a **SimulationContext** that can be swapped between modes (World, Combat, and potentially others in future). Systems operate against the context interface and do not need to know which mode is active.

> **Open Question:** What is the exact tick rate for combat mode — seconds, actions, or something else?

---

## 3. Player Role

The player is the **captain of a single ship**, or a small fleet of two to three vessels at most.

This is a **protagonist lens**, not a god view. The player does not command factions or manage colonies (at least at this stage). Their influence on the wider world is indirect — accumulated through reputation, relationships, and decisions over time.

### Game Start

The player begins as a **blank slate captain** — a ship, a small crew, and no history. No framing story or backstory is provided. Identity is built entirely through play.

### Home Port & Ownership

The player has **no fixed base of operations**. Every port is a potential haven; none is owned. This keeps the early game lean and the player nomadic by nature.

Port ownership and territorial control are **explicitly extensible** — the port loop is designed such that ownership mechanics can be layered in later without rearchitecting the core systems. It's a future design pass, not a closed door.

### Scale and Influence

For larger-scale effects, the game favours **indirect player agency**:

- Raid enough Spanish merchants and their trade presence genuinely weakens
- Cultivate a governor long enough and a port becomes a de facto safe harbour
- The world changes because of what you did, not because you issued top-down commands

---

## 4. The Living World

### 4.1 Factions with Agency

Factions are not static backdrops. Each tick they evaluate their state and act on goals:

- **Territory goals** — expand, consolidate, or defend based on current strength
- **Shifting relationships** — alliances and hostilities emerge from events, not hard-coded scripts. A war starts because of what happened, not because the designer placed it on a timeline
- **Resource pressure** — income, naval strength, and trade health feed back into capability. A faction losing trade income fields fewer patrols; fewer patrols attract pirates; more pirates damage trade further. Self-reinforcing loops generate emergent stories

### Faction Roster

Two categories of faction at launch:

**Colonial Powers** — hold ports, project naval power, control trade, pursue territorial ambitions against each other. Relationships between them shift based on world events. At minimum: Spain, England, France, the Netherlands. Each has a distinct presence and strategic posture in the Caribbean.

**Pirate Brotherhoods** — looser organisations than colonial powers; hold few or no ports outright but control safe havens, exert influence over sea lanes, and operate as an alternative power structure the player can align with or exploit. Relationships within and between brotherhoods can shift — a brotherhood weakened by colonial crackdowns may fracture or consolidate.

The two categories have fundamentally different relationships with territory, law, and trade — and fundamentally different things to offer the player.

### 4.2 Named Individuals with Careers

The most powerful emergent storytelling tool. Individuals are **generated, not hand-crafted**:

- **Governors** — personalities (corrupt, ambitious, principled), tenures, relationships; can be bribed, cultivated, deposed
- **Rival captains** — careers that develop in parallel with the player's. A nobody can become the most feared pirate in the Caribbean through system-driven play, or end up hanged in Nassau after a bad run
- **Merchants** — control specific trade relationships; can be cultivated as contacts or ruined as targets
- **Key crew** — gain experience and names over time; losing a veteran navigator to scurvy is a different loss to losing a generic crewman

Individuals **remember specific acts**, not just a reputation score. The merchant you let go tells people at the next three ports. The captain you defeated carries a grudge.

### 4.3 Reactive Economy

Trade is a **signal about world state**, not a static price table:

- Ports produce goods based on geography and infrastructure (sugar colonies export sugar; mining settlements export ore)
- Prices respond to supply and demand — blockade a port and its imports become scarce and valuable elsewhere
- Trade routes shift when alternatives open or existing routes become dangerous
- Player actions have **downstream consequences** — persistent enough raiding changes what goods are available and where

The economy does not need to be perfectly accurate. It needs to react believably.

### 4.4 Propagating Events

The world generates events that ripple outward with cause and effect:

> *A hurricane destroys a port's warehouse → food prices spike locally → colonial governor loses authority → power vacuum → pirates establish a foothold → regional trade suffers → distant faction takes notice*

No event is scripted. Each system responds to the state change created by the one before it. The player learns about these chains through the time-delayed information system — arriving at a port to be told "you haven't heard? The Spanish took Tortuga three weeks ago" is exactly the kind of moment we're building toward.

### 4.5 Factional Reputation

Reputation is **multi-dimensional and contextual**:

- Per-faction and per-port reputation tracked independently — England officially wants you hanged while the Nassau governor personally tolerates you
- Not a binary alignment — more like a web of specific relationships
- Decays over time; old deeds fade; new actions redefine you
- NPCs respond to the specifics of your history with them, not just your overall score

Port-level reputation has two layers:
- **Institutional memory** — the port remembers you in broad strokes regardless of who governs it; a new governor inherits a dim awareness of your history there
- **Personal relationship** — built with the individual governor; resets on succession, giving the player a genuine fresh start with new leadership without wiping the slate entirely

---

## 5. Core Gameplay Loops

### 5.1 The Sea Loop (Immediate)

The player is sailing. Moment-to-moment decisions:

- **Navigation** — plotting a course with imperfect information. Trade-offs between speed, safety, and opportunity
- **Encounters** — other vessels, weather, distant smoke, survivors in the water. Most offer a choice, not a forced outcome
- **Resource tension** — crew morale, food, water, and ship condition create constant low-level pressure. Long voyages have costs
- **Intel gathering** — passing ships and port rumours provide a drip of world information, always slightly out of date

The sea should feel alive and full of latent consequence, not empty and not overwhelming.

### 5.2 The Port Loop (Medium-Term)

Arriving at port is the primary interface with the living world:

- **Trade** — buy low, sell high; prices are signals about world state, not just profit opportunities
- **Contracts and rumours** — generated from current world conditions, not hand-crafted. A governor needs an escort because the world generated a threat; a merchant wants a rival ruined because the economy created competition
- **Recruit and refit** — hire crew, repair ship, upgrade equipment; availability reflects the port's prosperity and faction
- **Relationship cultivation** — invest in specific individuals; build obligations and favours
- **Information brokering** — buy and sell intelligence; what you know has value to others

Ports are **places with agendas**, not menus.

### 5.3 The Strategic Loop (Long-Term)

The score the player is building across many sea-and-port cycles:

- **Reputation** — your name precedes you. Being feared and being trusted open different doors; pursuing both is costly
- **Wealth** — prize ships, trade profits, and contracts fund ambitions
- **Influence** — shifting regional balance of power through accumulated decisions, not direct command
- **Rivals** — the world generates antagonists with their own careers and grievances
- **Legacy** — what kind of captain are you becoming? The world reflects it back

### 5.4 Loop Interdependencies

```
Sea Loop
  → consumes resources (crew, food, ship condition)
  → generates encounters and intel

Port Loop
  → replenishes resources
  → generates contracts, relationships, and strategic leads
  → feeds the Strategic Loop with reputation events

Strategic Loop
  → changes what's available in the Port Loop (new allies, new enemies)
  → changes what the Sea Loop looks like (more hostile waters, new safe routes)
  → is the long-term score / identity of the run
```

A good session ends with the player having been pulled sideways from their original plan by the world.

---

## 6. Explicit Scope Boundaries

### Out of Scope (for now)

| Feature | Notes |
|---|---|
| **Ship combat (detailed)** | Combat mode deferred entirely for now; resolved as a stat-based outcome until the system earns its own design pass |
| **Land combat** | Abstracted to a stat-based resolution (you stormed the fort — here's the outcome) until it demonstrably earns its own mode |
| **Colony management** | Strong DF resonance but risks scope creep; flagged for a later design pass |
| **Large fleet command** | Player commands a small fleet only; armada-scale operations are out of core loop |
| **Individual sailor simulation** | DF-level granularity on every port inhabitant is too expensive; population is abstracted |
| **Full economic modelling** | Every good at every price point is overkill; economy needs to react believably, not simulate accurately |

---

## 8. The Caribbean — Map Regions

The map is hand-crafted around the real Caribbean geography, divided into **five regions**. Distance between regions is the primary driver of the information latency mechanic — events in a distant region are old news by the time they reach you.

| Region | Character | Colonial Presence |
|---|---|---|
| **The Gulf** | Spanish heartland; rich trade, heavy patrols, dangerous for pirates | Spain-dominant |
| **The Spanish Main** | Northern coast of South America; treasure fleets, mining ports, chokepoints | Spain-dominant |
| **The Greater Antilles** | Cuba, Hispaniola, Jamaica, Puerto Rico; contested major islands; political centre of gravity | Mixed — Spain, England, France |
| **The Lesser Antilles** | Eastern island chain; smaller ports, trade crossroads, pirate havens tucked in the margins | Mixed — England, France, Netherlands |
| **The Bahamas & Florida Straits** | Northern passage; strategic transit zone; shallow waters; wrecks and salvage | Sparse — England, Pirates |

Each region has **4–8 ports** of varying size and importance — major colonial capitals down to small fishing settlements. Port density is high enough that there's always somewhere to put in; sparse enough that reaching the next port is a meaningful decision.

> Regions are the primary unit of the simulation LOD system — local/regional/distant tiers map naturally onto 1/2/3+ regions away from the player.

---

## 7. Open Questions

These are unresolved design decisions to be revisited:

- [ ] How many pirate brotherhoods at launch — one dominant brotherhood, or two to three with distinct regional territories and rivalries?
- [ ] What does the player's starting ship look like — class, condition, crew size? Is it always the same, or varied?
- [ ] Weather and natural events — hurricanes, storms, becalmed seas — in scope for the world sim, or deferred?

---

*Next step: System design specifications, beginning with the simulation engine and world-state model.*
