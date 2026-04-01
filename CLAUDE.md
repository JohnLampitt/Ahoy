# Ahoy — Claude Code Guide

## Project
Pirate sandbox simulation inspired by Sid Meier's Pirates + Dwarf Fortress.
C# backend-first, frontend-agnostic. See `docs/` for full design documents.

## Build
```bash
dotnet build
```

## Run (console harness)
```bash
dotnet run --project src/Ahoy.Console
```

### Console commands
| Command | Effect |
|---|---|
| `tick [n]` | Advance n ticks (default 1), print events |
| `run [n]` | Advance n ticks silently, print summary |
| `world` | World summary |
| `ports` | All ports with prosperity bars |
| `factions` | Faction treasury + naval strength |
| `ships` | Ship positions and cargo |
| `weather` | Weather by region |
| `knowledge` | Player's current knowledge facts |
| `quit` | Exit |

## Solution structure
```
src/
  Ahoy.Core/                  # Typed IDs, enums, value objects — no dependencies
  Ahoy.Simulation/            # WorldState, 6 systems, engine, events, decisions
    State/                    # WorldState and all entity models
    Systems/                  # IWorldSystem implementations + PropagationRules
    Engine/                   # SimulationEngine, TickEventEmitter, SimulationContext
    Events/                   # WorldEvent hierarchy
    Decisions/                # ActorDecisionMatrix, DecisionQueue, RuleBasedProvider
  Ahoy.Simulation.LlmDecisions/  # LLamaSharp stub (optional, not wired yet)
  Ahoy.WorldData/             # CaribbeanWorldDefinition — hand-crafted world content
  Ahoy.Console/               # Text REPL observation harness
docs/
  GDD.md                      # Game Design Document
  SDD-*.md                    # System Design Documents (one per system)
  SDD-Architecture.md         # Authoritative architecture reference
```

## Key design decisions
- **Tick = 1 day** in normal play
- **LOD = knowledge fidelity**: Local (player's region) / Regional (adjacent) / Distant
- **WorldState is pure data** — all mutation happens inside `IWorldSystem.Tick()`
- **6 systems run in order**: Weather → ShipMovement → Economy → Faction → EventPropagation → Knowledge
- **Actor decisions**: sync `RuleBasedDecisionProvider` (always available) + async LLM queue (optional)
- **Events carry `SourceLod`** which sets `BaseConfidence` of derived KnowledgeFacts
- **No serialisation yet** — world is recreated from `CaribbeanWorldDefinition` each run

## Target framework
.NET 10 (`net10.0`)

## Context
Full design history in `.claude/memory/`. Architecture decisions in `docs/SDD-Architecture.md`.
