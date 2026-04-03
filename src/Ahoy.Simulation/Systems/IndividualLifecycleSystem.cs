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
                // At home — small chance to start a tour
                if (_rng.NextDouble() >= TourStartChance) continue;

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
    /// Pick a tour destination: a faction-controlled port that isn't the individual's home.
    /// Falls back to any port if no faction match found.
    /// </summary>
    private PortId? PickTourDestination(PortId homePort, FactionId? factionId, WorldState state)
    {
        var candidates = state.Ports.Values
            .Where(p => p.Id != homePort)
            .Where(p => factionId == null || p.ControllingFactionId == factionId)
            .Select(p => p.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            // Fallback: any port that isn't home
            candidates = state.Ports.Values
                .Where(p => p.Id != homePort)
                .Select(p => p.Id)
                .ToList();
        }

        if (candidates.Count == 0) return null;
        return candidates[_rng.Next(candidates.Count)];
    }
}
