using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 5 — runs after FactionSystem, before EventPropagationSystem.
/// Drives named individual movement: governors leave their home port on short
/// inspection/diplomatic tours and return after a few ticks.
///
/// Only individuals with HomePortId set (currently: Governors) travel.
/// Pirates and brokers roam freely without this system's oversight.
///
/// When an individual moves, an <see cref="IndividualMoved"/> event is emitted.
/// KnowledgeSystem converts this into an IndividualWhereaboutsClaim seeded only
/// at the departure and arrival ports — creating information asymmetry.
/// </summary>
public sealed class IndividualLifecycleSystem : IWorldSystem
{
    private const double TourStartChance = 0.02;   // 2% per tick while at home
    private const int    TourMinTicks    = 3;
    private const int    TourMaxTicks    = 10;

    private readonly Random _rng;

    public IndividualLifecycleSystem(Random rng)
    {
        _rng = rng;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.HomePortId is not { } homePort) continue;

            if (individual.TourTicksRemaining is null)
            {
                // At home — chance to start a tour, scaled by faction prosperity.
                // Wealthy factions have more diplomatic activity; impoverished ones stay home.
                // Floor at 0.5× ensures even broke factions occasionally travel.
                var tourChance = TourStartChance;
                if (individual.FactionId.HasValue
                    && state.Factions.TryGetValue(individual.FactionId.Value, out var faction))
                {
                    var prosperityValues = faction.ControlledPorts
                        .Where(pid => state.Ports.ContainsKey(pid))
                        .Select(pid => (double)state.Ports[pid].Prosperity)
                        .ToList();
                    if (prosperityValues.Count > 0)
                        tourChance *= Math.Max(0.5, prosperityValues.Average() / 100.0);
                }
                if (_rng.NextDouble() >= tourChance) continue;

                if (PickTourDestination(homePort, individual.FactionId, state) is not { } dest)
                    continue;

                individual.LocationPortId     = dest;
                individual.TourTicksRemaining = _rng.Next(TourMinTicks, TourMaxTicks + 1);
                events.Emit(
                    new IndividualMoved(state.Date, SimulationLod.Local,
                        individual.Id, homePort, dest),
                    SimulationLod.Local);
            }
            else
            {
                // On tour — count down
                individual.TourTicksRemaining--;
                if (individual.TourTicksRemaining > 0) continue;

                // Tour complete — return home
                var fromPort = individual.LocationPortId ?? homePort;
                individual.LocationPortId     = homePort;
                individual.TourTicksRemaining = null;
                events.Emit(
                    new IndividualMoved(state.Date, SimulationLod.Local,
                        individual.Id, fromPort, homePort),
                    SimulationLod.Local);
            }
        }
    }

    /// <summary>
    /// Pick a tour destination: a friendly faction-controlled port that isn't the individual's home.
    /// Excludes ports where the individual's faction has a hostile relationship (relationship &lt; -25).
    /// Falls back to any non-hostile port, then any port, if no faction match found.
    /// </summary>
    private PortId? PickTourDestination(PortId homePort, FactionId? factionId, WorldState state)
    {
        var candidates = state.Ports.Values
            .Where(p => p.Id != homePort)
            .Where(p => factionId == null || p.ControllingFactionId == factionId)
            .Where(p => !IsHostile(factionId, p.ControllingFactionId, state))
            .Select(p => p.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            // Fallback: any non-hostile port that isn't home
            candidates = state.Ports.Values
                .Where(p => p.Id != homePort)
                .Where(p => !IsHostile(factionId, p.ControllingFactionId, state))
                .Select(p => p.Id)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            // Last resort: any port (shouldn't normally be reached)
            candidates = state.Ports.Values
                .Where(p => p.Id != homePort)
                .Select(p => p.Id)
                .ToList();
        }

        if (candidates.Count == 0) return null;
        return candidates[_rng.Next(candidates.Count)];
    }

    /// <summary>Relationship below this threshold is considered hostile — governors won't travel there.</summary>
    private const float HostileThreshold = -25f;

    private static bool IsHostile(FactionId? traveller, FactionId? destination, WorldState state)
    {
        if (traveller is null || destination is null || traveller == destination) return false;
        if (!state.Factions.TryGetValue(traveller.Value, out var faction)) return false;
        return faction.Relationships.TryGetValue(destination.Value, out var rel) && rel < HostileThreshold;
    }
}
