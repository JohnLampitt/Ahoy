# Ahoy — System Design: Actor Decision System

> **Status:** Living document.
> **Version:** 0.2 — Intervention window decoupled from inference; decision matrix model
> **Depends on:** SDD-WorldState.md, SDD-KnowledgeSystem.md, SDD-FactionSystem.md, SDD-SimulationLOD.md

---

## 1. Overview

Most NPC behaviour in Ahoy is handled by rule-based utility scoring — routine decisions that don't need contextual reasoning. But at **inflection points** — moments where an actor's knowledge, personality, history, and circumstances converge into a genuinely interesting choice — pure utility scoring produces mechanical, characterless decisions.

The Actor Decision System provides a second, richer decision pathway for these moments: a small local LLM that reasons over an actor's full context and returns a choice from a constrained action set.

### Design Principles

- **Two genuinely different contracts** — synchronous rule-based decisions and asynchronous LLM decisions are not two implementations of one interface. They have different mechanics and are used in different situations.
- **Latency is hidden by design** — LLM inference happens on a background thread; the simulation ticks forward; the decision arrives and is applied cleanly. The delay is narratively plausible (decisions take time).
- **Intervention windows are game mechanics, not inference artifacts** — the window during which the player can act is defined by the trigger type in game ticks; it has no relationship to how long inference takes.
- **First inference encodes all anticipated interventions** — the LLM returns a decision matrix covering the base scenario and likely player interventions simultaneously. Player intervention resolves as an instant lookup against this matrix; no re-inference required.
- **Non-determinism is acceptable** — the simulation runs forward only and never replays to rebuild state. Variation in LLM output is world unpredictability, not a bug.
- **Inflection points are LOD-agnostic** — a distant faction reaching a crisis point deserves a considered decision as much as a local NPC. The LLM queue is used regardless of the originating LOD tier.
- **The simulation never depends on the LLM** — every decision point has a rule-based fallback. The LLM makes decisions *richer*, not *possible*.
- **LLM output enters the world** — rationale text is not discarded; it feeds into the knowledge system as narrative flavour.

---

## 2. Decision Providers — Two Distinct Interfaces

These are not polymorphic implementations of a shared interface. They have different call sites, different mechanics, and different use cases.

```csharp
// Ahoy.Simulation/Decisions/ISyncActorDecisionProvider.cs

/// Synchronous. Returns immediately.
/// Used for: routine background decisions, fallback when LLM is unavailable.
public interface ISyncActorDecisionProvider
{
    ActorDecision ResolveDecision(ActorDecisionContext context);
}
```

```csharp
// Ahoy.Simulation/Decisions/IAsyncActorDecisionProvider.cs

/// Asynchronous. Queues the request; result arrives in a future tick.
/// Used for: named individual character moments, faction inflection points, all LOD tiers.
public interface IAsyncActorDecisionProvider
{
    void EnqueueDecision(ActorDecisionRequest request);
}
```

---

## 3. What Is an Inflection Point?

An inflection point is a moment where an actor's accumulated state — knowledge, traits, relationships, history — makes their next decision genuinely interesting. It is not a routine choice.

Inflection points are identified by the system that owns the relevant entity. They are not player-proximity-dependent — a distant faction or a far-away named individual can reach an inflection point at any time.

### Faction Inflection Points

| Trigger | Example |
|---|---|
| Treasury collapses (hits zero) | Does the faction raise taxes, abandon a port, seek an ally, or capitulate? |
| Standing crosses war threshold | Does the faction actually declare war, given all it knows? |
| Alliance opportunity emerges | Does the faction trust this potential partner? |
| Port lost to enemy | Does the faction commit to recapture, negotiate, or redirect resources? |
| Naval strength drops below 30% | Does the faction sue for peace, consolidate, or gamble on a desperate assault? |
| Brotherhood cohesion drops below 25 | Does the leader unify by force, share power, or let the brotherhood fracture? |

### Individual Inflection Points

| Trigger | Example |
|---|---|
| Player arrives at governor's port (first visit after reputation event) | Does Mendez grant harbour, demand a bribe, arrest the player, or look the other way? |
| Rival captain encounters player at sea | Attack, parley, flee, or something else shaped by their shared history? |
| Information broker receives high-value offer | Do they sell, negotiate, deflect, or alert the subject of the fact? |
| Governor authority drops below 20 | Does the governor appeal to the faction, resign, turn corrupt, or dig in? |
| Merchant captain faces a desperate run | Do they take the dangerous route, wait, or approach the pirate brotherhood for safe passage? |

---

## 4. Core Data Structures

### 4.1 DecisionSubject

Who is making this decision.

```csharp
// Ahoy.Simulation/Decisions/DecisionSubject.cs

public abstract record DecisionSubject;
public record IndividualSubject(IndividualId Id)  : DecisionSubject;
public record FactionSubject(FactionId Id)        : DecisionSubject;
```

For `FactionSubject`, the LLM reasons about the faction as a collective actor — shaped by its type, resources, relationships, and goals. The faction's named leader (if one exists as an `Individual`) can be included in context for additional personality influence.

### 4.2 DecisionTrigger

What caused the inflection point. Used both to build context and to categorise decisions for analytics/debugging.

```csharp
// Ahoy.Simulation/Decisions/DecisionTrigger.cs

public abstract record DecisionTrigger;

// Faction triggers
public record TreasuryCollapsedTrigger                          : DecisionTrigger;
public record WarThresholdCrossedTrigger(FactionId Adversary)   : DecisionTrigger;
public record AllianceOpportunityTrigger(FactionId Candidate)   : DecisionTrigger;
public record PortLostTrigger(PortId Port, FactionId To)        : DecisionTrigger;
public record NavalStrengthCriticalTrigger                      : DecisionTrigger;
public record CohesionCriticalTrigger                           : DecisionTrigger;

// Individual triggers
public record PlayerArrivedAtPortTrigger(PortId Port)           : DecisionTrigger;
public record EncounterWithPlayerTrigger(RegionId Region)       : DecisionTrigger;
public record HighValueKnowledgeOfferTrigger(KnowledgeFactId Fact) : DecisionTrigger;
public record AuthorityCriticalTrigger                          : DecisionTrigger;
public record DesperationThresholdTrigger                       : DecisionTrigger;
```

### 4.3 ActorDecisionContext

Everything the LLM needs to make a decision. Constructed by the system that identified the inflection point.

```csharp
// Ahoy.Simulation/Decisions/ActorDecisionContext.cs

public sealed class ActorDecisionContext
{
    public DecisionSubject                  Subject          { get; init; }
    public DecisionTrigger                  Trigger          { get; init; }

    // Structured natural language summary of the situation (generated, not hand-authored)
    public string                           SituationSummary { get; init; } = string.Empty;

    // Relevant facts the actor holds — pre-filtered to those germane to this decision
    public IReadOnlyList<KnowledgeFact>     RelevantFacts    { get; init; } = [];

    // The constrained action set — LLM must choose one
    public IReadOnlyList<ActorAction>       AvailableActions { get; init; } = [];

    // LOD at point of trigger — informs confidence of the resulting knowledge fact
    public SimulationLod                    OriginatingLod   { get; init; }
}
```

### 4.4 ActorAction

A single option in the constrained choice set.

```csharp
// Ahoy.Simulation/Decisions/ActorAction.cs

public sealed class ActorAction
{
    public string   Id          { get; init; } = string.Empty;  // e.g. "GRANT_HARBOUR"
    public string   Description { get; init; } = string.Empty;  // shown in prompt
    // The concrete mutation to apply to WorldState when this action is chosen
    public Action<WorldState, DecisionSubject, IEventEmitter> Apply { get; init; } = null!;
}
```

### 4.5 ActorDecision

The result of a resolved decision.

```csharp
// Ahoy.Simulation/Decisions/ActorDecision.cs

public sealed class ActorDecision
{
    public ActorAction  ChosenAction { get; init; } = null!;
    public string       Rationale    { get; init; } = string.Empty;
    // Whether this came from the LLM or the rule-based fallback
    public DecisionSource Source     { get; init; }
}

public enum DecisionSource { LlmProvider, RuleBasedFallback }
```

### 4.6 ActorDecisionMatrix

The LLM returns a matrix covering the base scenario and all anticipated player interventions simultaneously. Player intervention resolves as an instant lookup — no re-inference required.

```csharp
// Ahoy.Simulation/Decisions/ActorDecisionMatrix.cs

public sealed class ActorDecisionMatrix
{
    // What the actor does if the player does not intervene
    public ActorDecision                               BaseDecision         { get; init; } = null!;

    // What the actor does for each anticipated intervention type
    public Dictionary<InterventionType, ActorDecision> ConditionalDecisions { get; init; } = new();

    /// Resolve the matrix against a player intervention (or null for base case).
    /// Falls back to BaseDecision for unanticipated intervention types.
    public ActorDecision Resolve(PlayerIntervention? intervention) =>
        intervention is null                                                          ? BaseDecision :
        ConditionalDecisions.TryGetValue(intervention.Type, out var conditional) ? conditional :
        BaseDecision;
}

public enum InterventionType
{
    BribeSmall,       // Under a low gold threshold
    BribeLarge,       // Over a high gold threshold
    IntelProvided,    // Player offers a knowledge fact
    Threat,           // Player makes a threatening gesture
    FavourInvoked,    // Player calls in a prior favour or debt
    ReputationPlayed, // Player explicitly invokes their faction standing
}
```

```csharp
// Ahoy.Simulation/Decisions/PlayerIntervention.cs

public sealed class PlayerIntervention
{
    public InterventionType   Type    { get; init; }
    public object?            Payload { get; init; }  // e.g. gold amount, KnowledgeFactId
    public WorldDate          OccurredAt { get; init; }
}
```

### 4.7 ActorDecisionRequest

The queued request. Tracks the game-mechanic intervention window independently of inference state.

```csharp
// Ahoy.Simulation/Decisions/ActorDecisionRequest.cs

public sealed class ActorDecisionRequest
{
    public Guid                   Id                      { get; } = Guid.NewGuid();
    public ActorDecisionContext   Context                 { get; init; } = null!;
    public WorldDate              QueuedAt                { get; init; }

    // Game-mechanic window — defined by trigger type, independent of inference latency
    public int                    InterventionWindowTicks { get; init; }
    public int                    TicksElapsed            { get; internal set; }
    public bool                   InterventionWindowOpen  => TicksElapsed < InterventionWindowTicks;

    // LLM result stored here when inference completes — not applied until window closes
    public ActorDecisionMatrix?   PendingMatrix           { get; internal set; }

    // Player intervention recorded here if it occurs during the window
    public PlayerIntervention?    Intervention            { get; internal set; }
}
```

### Intervention Window Durations

Defined by trigger type. These are game-meaningful durations, not technical parameters.

| Trigger | Window (ticks) | In-World Meaning |
|---|---|---|
| `PlayerArrivedAtPort` | 2 | Governor reviews credentials overnight |
| `EncounterWithPlayer` | 1 | Sea encounter — a quick decision |
| `WarThresholdCrossed` | 5 | Diplomatic deliberation takes days |
| `AllianceOpportunity` | 7 | Negotiations take time |
| `TreasuryCollapsed` | 4 | Faction council deliberates |
| `AuthorityCritical` | 3 | Governor faces a crisis |
| `DesperationThreshold` | 2 | Desperate merchant makes a call |

---

## 5. The Decision Queue

The sole shared boundary between the main simulation thread and the LLM inference background thread.

```csharp
// Ahoy.Simulation/Decisions/DecisionQueue.cs

public sealed class DecisionQueue : IAsyncActorDecisionProvider, IDisposable
{
    // Bounded — prevents unbounded backlog during heavy load
    private readonly Channel<ActorDecisionRequest> _requests =
        Channel.CreateBounded<ActorDecisionRequest>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest  // old pending decisions dropped under load
        });

    // Background thread writes here when inference completes
    private readonly ConcurrentQueue<(ActorDecisionRequest Request, ActorDecision Decision)> _completed = new();

    private readonly ISyncActorDecisionProvider _fallback;
    private readonly int _maxWaitTicks;
    private readonly CancellationTokenSource _cts = new();

    public DecisionQueue(ISyncActorDecisionProvider fallback, int maxWaitTicks = 30)
    {
        _fallback     = fallback;
        _maxWaitTicks = maxWaitTicks;
    }

    // Called by simulation systems — returns immediately
    public void EnqueueDecision(ActorDecisionRequest request) =>
        _requests.Writer.TryWrite(request);

    // Called by SimulationEngine at the start of each tick
    public IReadOnlyList<(ActorDecisionRequest Request, ActorDecision Decision)> DrainCompleted()
    {
        var results = new List<(ActorDecisionRequest, ActorDecision)>();
        while (_completed.TryDequeue(out var item))
            results.Add(item);
        return results;
    }

    // Called by SimulationEngine each tick to age pending requests
    public void TickPending(IReadOnlyList<ActorDecisionRequest> pending, WorldDate currentDate)
    {
        foreach (var request in pending)
        {
            request.TicksWaiting++;
            if (request.TicksWaiting > _maxWaitTicks)
            {
                // Timed out — resolve with fallback and queue as completed
                var fallbackDecision = _fallback.ResolveDecision(request.Context);
                _completed.Enqueue((request, fallbackDecision));
            }
        }
    }

    // Runs on background thread — started by SimulationEngine at initialisation
    public async Task RunInferenceLoopAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(_cts.Token))
        {
            var decision = await InferAsync(request.Context);
            _completed.Enqueue((request, decision));
        }
    }
}
```

### Threading Model

```
Main simulation thread         │  Background inference thread
───────────────────────────────┼──────────────────────────────────
SimulationEngine               │  DecisionQueue.RunInferenceLoopAsync()
WorldState (sole writer)       │  Reads from Channel<ActorDecisionRequest>
                               │  Calls LLM
                               │  Writes to ConcurrentQueue<CompletedDecision>
                               │
     Channel<ActorDecisionRequest>    ← requests flow this way →
     ConcurrentQueue<CompletedDecision> ← decisions flow this way ←
```

`WorldState` is never accessed from the background thread. No locks required on simulation state.

---

## 6. Tick Integration

```csharp
// Ahoy.Simulation/Engine/SimulationEngine.cs  (additions)

public void Tick()
{
    // 1. Store any completed LLM matrices — do not apply yet, window may still be open
    foreach (var (requestId, matrix) in _decisionQueue.DrainCompleted())
        GetPendingRequest(requestId)?.PendingMatrix = matrix;

    // 2. Age all pending requests; resolve those whose window has closed
    foreach (var request in GetAllPendingRequests(_state).ToList())
    {
        request.TicksElapsed++;

        if (request.InterventionWindowOpen) continue;

        // Window closed — resolve now
        var matrix = request.PendingMatrix
                  ?? _fallback.ResolveMatrix(request.Context);  // LLM timed out

        var decision = matrix.Resolve(request.Intervention);

        decision.ChosenAction.Apply(_state, request.Context.Subject, _events);
        ClearPendingDecision(request.Context.Subject, _state);
        EmitDecisionFact(request, decision, _state, _events);  // rationale → knowledge
    }

    // 3. Normal tick pipeline
    foreach (var system in _systems)
        system.Tick(_state, _context, _events);

    _state.Date = _state.Date.Advance();
    DispatchEvents(_events.DrainAll());
}
```

---

## 7. Pending Decision State & Stalling

While a decision is outstanding, the subject is marked as pending. Other systems respect this and do not take conflicting actions on the entity.

```csharp
// Added to Individual
public ActorDecisionRequest? PendingDecision { get; internal set; }

// Added to Faction
public ActorDecisionRequest? PendingDecision { get; internal set; }
```

**Stalling behaviours** are plausible in-world. The delay is narratively honest — important decisions take time:

| Subject | Stalling Behaviour |
|---|---|
| Governor (player interaction) | *"The governor is occupied with other matters — return tomorrow"* |
| Rival captain (encounter) | Shadows the player's ship without engaging; neither attacks nor flees |
| Merchant broker | *"I'll need to think on your offer overnight"* |
| Faction (war threshold) | Diplomatic overtures and troop movements begin, formal declaration deferred |
| Brotherhood leader (cohesion crisis) | Public statements of unity while internal dispute plays out |

At `Distant` LOD, the stall is even more natural — decisions from faraway actors realistically take days or weeks to propagate to anyone who would know about them.

---

## 8. Situation Summary Generation

The `SituationSummary` is generated programmatically from world state and structured facts — not hand-authored. Each trigger type has a summary builder:

```csharp
// Ahoy.Simulation/Decisions/SituationSummaryBuilder.cs

public static string Build(
    DecisionSubject subject,
    DecisionTrigger trigger,
    IReadOnlyList<KnowledgeFact> facts,
    WorldState state)
{
    return (subject, trigger) switch
    {
        (IndividualSubject ind, PlayerArrivedAtPortTrigger t) =>
            BuildGovernorPlayerArrival(state.Individuals[ind.Id], t.Port, facts, state),

        (FactionSubject fac, WarThresholdCrossedTrigger t) =>
            BuildFactionWarDecision(state.Factions[fac.Id], t.Adversary, facts, state),

        (FactionSubject fac, TreasuryCollapsedTrigger) =>
            BuildFactionTreasuryCollapse(state.Factions[fac.Id], facts, state),

        _ => BuildGenericSummary(subject, trigger, facts, state)
    };
}

private static string BuildGovernorPlayerArrival(
    Individual governor, PortId port, IReadOnlyList<KnowledgeFact> facts, WorldState state)
{
    var p        = state.Ports[port];
    var faction  = state.Factions[p.ControllingFaction];
    var playerRep = governor.PersonalReputationWithPlayer;
    var instRep   = p.InstitutionalReputation;

    return $"""
        {governor.Name}, Governor of {p.Name}, must decide how to receive the player captain.
        The port is controlled by {faction.Name}. Port prosperity is {p.Prosperity}/100.
        Governor's personal reputation with the player: {playerRep} (-100 hostile to 100 trusted).
        Institutional port reputation with the player: {instRep}.
        Governor traits — Greed: {governor.Personality.Greed}/100,
                          Ambition: {governor.Personality.Ambition}/100,
                          Loyalty: {governor.Personality.Loyalty}/100,
                          Boldness: {governor.Personality.Boldness}/100.
        Relevant recent events the governor is aware of:
        {FormatFacts(facts)}
        """;
}
```

Summaries are structured but read naturally. They give the LLM factual grounding without leaving anything to hallucination.

---

## 9. Prompt Structure

The prompt requests a **decision matrix** — one response per scenario — in a single inference pass. The LLM reasons over all scenarios in a shared context, producing internally consistent responses.

```
SYSTEM:
You are resolving decisions for an actor in a historical Caribbean naval simulation set in the late 1600s.
You will be given a situation and a set of intervention scenarios.
For EACH scenario, choose exactly one action from the available actions list.
Base every decision on the actor's personality, knowledge, and the specific scenario.
Respond with one line per scenario in exactly this format:
SCENARIO_[NAME]: ACTION: [action_id] | RATIONALE: [one sentence, narrator perspective]

USER:
{situationSummary}

Available actions:
{foreach action: "- {action.Id}: {action.Description}"}

Provide your decision for each of the following scenarios:

SCENARIO BASE:              The player arrives and takes no action.
SCENARIO BRIBE_SMALL:       The player offers a small bribe (under {smallBribeThreshold} gold).
SCENARIO BRIBE_LARGE:       The player offers a substantial bribe (over {largeBribeThreshold} gold).
SCENARIO INTEL_PROVIDED:    The player offers valuable intelligence relevant to {subjectName}'s interests.
SCENARIO THREAT:            The player makes a threatening gesture or veiled threat.
SCENARIO FAVOUR_INVOKED:    The player explicitly references a prior debt or favour owed to them.

What does {subjectName} decide in each scenario?
```

### Output Format

```
SCENARIO BASE:           ACTION: DENY_HARBOUR     | RATIONALE: Spain's warrant is too recent to ignore openly.
SCENARIO BRIBE_SMALL:    ACTION: DENY_HARBOUR     | RATIONALE: The offer is insultingly small for the risk involved.
SCENARIO BRIBE_LARGE:    ACTION: GRANT_QUIETLY    | RATIONALE: Enough gold makes loyalty a flexible concept for a man of Mendez's appetites.
SCENARIO INTEL_PROVIDED: ACTION: GRANT_QUIETLY    | RATIONALE: Intelligence on the English fleet is worth more than Spanish favour tonight.
SCENARIO THREAT:         ACTION: CALL_GUARDS      | RATIONALE: Mendez is not a man to be intimidated in his own port.
SCENARIO FAVOUR_INVOKED: ACTION: GRANT_QUIETLY    | RATIONALE: A debt is a debt — Mendez honours it, if only to preserve his reputation for reliability.
```

### Parsing

```csharp
private static ActorDecisionMatrix ParseLlmResponse(string response, ActorDecisionContext context)
{
    var matrix = new Dictionary<string, ActorDecision>();

    foreach (Match match in Regex.Matches(response,
        @"SCENARIO\s+(\w+):\s+ACTION:\s*(\w+)\s*\|\s*RATIONALE:\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline))
    {
        var scenario  = match.Groups[1].Value.Trim().ToUpperInvariant();
        var actionId  = match.Groups[2].Value.Trim();
        var rationale = match.Groups[3].Value.Trim();

        var action = context.AvailableActions
            .FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));

        if (action is not null)
            matrix[scenario] = new ActorDecision
            {
                ChosenAction = action,
                Rationale    = rationale,
                Source       = DecisionSource.LlmProvider
            };
    }

    // Require at least the BASE scenario; fall back entirely if missing
    if (!matrix.TryGetValue("BASE", out var baseDecision))
        return FallbackMatrix(context);

    return new ActorDecisionMatrix
    {
        BaseDecision = baseDecision,
        ConditionalDecisions = new Dictionary<InterventionType, ActorDecision>
        {
            [InterventionType.BribeSmall]       = matrix.GetValueOrDefault("BRIBE_SMALL",    baseDecision),
            [InterventionType.BribeLarge]       = matrix.GetValueOrDefault("BRIBE_LARGE",    baseDecision),
            [InterventionType.IntelProvided]    = matrix.GetValueOrDefault("INTEL_PROVIDED", baseDecision),
            [InterventionType.Threat]           = matrix.GetValueOrDefault("THREAT",         baseDecision),
            [InterventionType.FavourInvoked]    = matrix.GetValueOrDefault("FAVOUR_INVOKED", baseDecision),
        }
    };
}
```

---

## 10. Re-Inference for Novel Interventions

The matrix covers all **anticipated** intervention types. Occasionally the player does something that doesn't map to any pre-encoded scenario — delivering a rare artifact, committing a violent act in the port, or revealing information of exceptional sensitivity.

In these cases, re-inference is triggered:

1. The existing `PendingMatrix` is discarded
2. The novel intervention is injected into the context as an additional fact
3. A fresh `ActorDecisionRequest` is queued — same window duration reset from now
4. The stalling behaviour continues; the delay is narratively plausible ("the governor has retired to consider this unexpected development")

Re-inference is the **exception**, not the rule. The matrix handles the vast majority of cases. Re-inference is only triggered when `intervention.Type == InterventionType.Novel` — a type set by the player action system when the action doesn't map to a standard intervention type.

```csharp
if (request.Intervention?.Type == InterventionType.Novel
    && request.PendingMatrix is not null)
{
    // Discard existing matrix; re-queue with enriched context
    var enrichedContext = request.Context with
    {
        RelevantFacts = [..request.Context.RelevantFacts, BuildNovelInterventionFact(request.Intervention)]
    };
    var newRequest = new ActorDecisionRequest
    {
        Context                 = enrichedContext,
        QueuedAt                = _state.Date,
        InterventionWindowTicks = request.InterventionWindowTicks,  // reset window
    };
    _decisionQueue.EnqueueDecision(newRequest);
    ReplacePendingDecision(request.Context.Subject, newRequest, _state);
}
```

---

## 11. Rationale as Knowledge Flavour

The LLM's rationale is not discarded. It enters the knowledge system as a narrative fact attached to the decision event, at the confidence level of the originating LOD:

```csharp
// Ahoy.Simulation/Engine/SimulationEngine.cs

private void EmitDecisionFact(
    ActorDecisionRequest request, ActorDecision decision,
    WorldState state, IEventEmitter events)
{
    events.Emit(new ActorDecisionMade(
        OccurredAt:  state.Date,
        Subject:     request.Context.Subject,
        Trigger:     request.Context.Trigger,
        ActionTaken: decision.ChosenAction.Id,
        Rationale:   decision.Rationale        // becomes knowledge flavour text
    ), request.Context.OriginatingLod);
}
```

The `Rationale` field on the resulting `KnowledgeFact` is what information brokers, tavern keepers, and rumour networks use to give the player narrative context:

> *"Word is Mendez let a wanted captain dock quietly — something about an old debt being repaid."*
> *"They say Spain's treasury chiefs chose to cede the northern ports rather than bleed themselves dry defending them."*

The LLM writes the world's gossip layer.

---

## 12. Recommended Local Model

For C# backend, CPU-runnable, low memory footprint:

| Model | Params | Notes |
|---|---|---|
| **Phi-3 Mini** | 3.8B | Microsoft; strong reasoning for size; first choice |
| **Llama 3.2 3B** | 3B | Meta; good instruction following |
| **Gemma 2 2B** | 2B | Google; very efficient; good for constrained output |

**C# integration:** [LLamaSharp](https://github.com/SciSharp/LLamaSharp) — C# bindings for llama.cpp. Supports CPU inference, quantized models (Q4_K_M recommended for balance of quality and speed), and async inference.

**Expected latency:** 1–5 seconds per decision on a modern CPU with a quantized 3B model. Acceptable given the stalling mechanic hides it entirely.

**Model is an implementation detail** of `IAsyncActorDecisionProvider`. Swapping from Phi-3 to a different model (or to a remote API during development) requires no changes to the simulation.

---

## 13. LOD Behaviour

The LLM decision provider is used at **all LOD tiers** for inflection points. LOD affects only the confidence of the resulting knowledge fact — not whether the LLM is invoked.

| LOD | LLM Usage | Resulting Fact Confidence |
|---|---|---|
| **Local** | Full context, high-detail facts | 0.85–1.0 |
| **Regional** | Full context, aggregate facts | 0.45–0.75 |
| **Distant** | Summary context, major-event facts | 0.10–0.40 |

At `Distant` LOD, the situation summary is naturally coarser (fewer precise facts, more aggregate state). The LLM still produces a decision — just from less detailed input. This is appropriate: distant actors are making decisions with less information, and the world's representation of those decisions is less precise.

---

## 14. Revisions to Prior SDDs

### SDD-WorldState
- `Individual` gains `ActorDecisionRequest? PendingDecision`
- `Faction` gains `ActorDecisionRequest? PendingDecision`
- `WorldState` gains `IReadOnlyList<ActorDecisionRequest> GetAllPendingDecisions()`

### SDD-FactionSystem
- Faction inflection point detection added to `FactionSystem.Tick()` — identifies when a faction has crossed a trigger threshold and calls `_decisionQueue.EnqueueDecision()`
- Systems check `faction.PendingDecision != null` before applying conflicting actions (e.g. don't auto-resolve war declaration if a decision is pending)

### SDD-KnowledgeSystem
- `ActorDecisionMade` is a new `WorldEvent` subtype that generates a `KnowledgeFact` with `Rationale` as flavour text
- Brokers can include rationale text when selling facts about major decisions

---

## 15. Open Questions

- [ ] Should multiple simultaneous inflection points for the same faction/individual be coalesced into a single decision with a richer context, or queued sequentially? Coalescing produces better LLM output but requires more complex trigger management.
- [ ] Prompt language and tone — how much historical flavour should the system prompt include? More flavour produces more period-appropriate rationale; less flavour produces cleaner, more reliable output.
- [ ] Should `Rationale` text ever be shown directly to the player, or always filtered through the knowledge system's confidence/hearsay layer? The latter feels more consistent with the information delay mechanic.

---

*Next step: WeatherSystem and ShipMovementSystem — the remaining pipeline systems.*
