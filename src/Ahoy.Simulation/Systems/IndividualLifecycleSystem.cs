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
    private const double TourStartChance        = 0.02;  // 2% per tick while at home
    private const int    TourMinTicks           = 3;
    private const int    TourMaxTicks           = 10;
    private const double BaseMortality          = 0.002; // ~1% per year (360-tick year)
    private const double CompromisedMultiplier  = 5.0;   // burned agents: ~5% per year

    private readonly Random _rng;

    public IndividualLifecycleSystem(Random rng)
    {
        _rng = rng;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // --- Mortality pass ---
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            var chance = individual.IsCompromised
                ? BaseMortality * CompromisedMultiplier
                : BaseMortality;
            if (_rng.NextDouble() < chance)
            {
                individual.IsAlive = false;
                var lod = individual.LocationPortId.HasValue
                    && state.Ports.TryGetValue(individual.LocationPortId.Value, out var p)
                    && state.Regions.TryGetValue(p.RegionId, out _)
                        ? context.GetLod(p.RegionId)
                        : SimulationLod.Distant;
                var cause = individual.IsCompromised ? "Burned agent — accident" : "Natural causes";
                events.Emit(new IndividualDied(state.Date, lod, individual.Id, cause), lod);
            }
        }

        // --- Tour movement pass ---
        foreach (var individual in state.Individuals.Values)
        {
            if (!individual.IsAlive) continue;
            if (individual.HomePortId is not { } homePort) continue;

            if (individual.TourTicksRemaining is null)
            {
                // 5A-2: Tour likelihood based on FactionHolder knowledge, not ground truth.
                // Governor consults what their faction knows about port prosperity.
                // Stable/prosperous factions travel more; factions under pressure stay home.
                var tourChance = TourStartChance;
                if (individual.FactionId.HasValue)
                {
                    var factionHolder = new FactionHolder(individual.FactionId.Value);
                    var knownProsperity = state.Knowledge.GetFacts(factionHolder)
                        .Where(f => !f.IsSuperseded && f.Claim is PortProsperityClaim)
                        .Select(f => (double)((PortProsperityClaim)f.Claim).Prosperity * f.Confidence)
                        .ToList();
                    if (knownProsperity.Count > 0)
                        tourChance *= Math.Max(0.5, knownProsperity.Average() / 100.0);
                }
                if (_rng.NextDouble() >= tourChance) continue;

                if (PickTourDestination(homePort, individual, state) is not { } dest)
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
    /// 5A-2: Pick tour destination using governor's knowledge, not ground truth.
    /// Uses IndividualWhereaboutsClaim (visit known allied governors),
    /// PortProsperityClaim (visit struggling ports), and PortControlClaim
    /// (suppress tours to enemy-controlled ports).
    /// </summary>
    private PortId? PickTourDestination(PortId homePort, Individual individual, WorldState state)
    {
        var factionId = individual.FactionId;
        var governorHolder = new IndividualHolder(individual.Id);
        var factionHolder = factionId.HasValue ? new FactionHolder(factionId.Value) : null;

        // Gather what the governor knows about port control
        var knownControl = new Dictionary<PortId, FactionId?>();
        var holderFacts = state.Knowledge.GetFacts(governorHolder)
            .Where(f => !f.IsSuperseded)
            .ToList();
        if (factionHolder is not null)
            holderFacts.AddRange(state.Knowledge.GetFacts(factionHolder).Where(f => !f.IsSuperseded));

        foreach (var fact in holderFacts)
        {
            if (fact.Claim is PortControlClaim pcc && !knownControl.ContainsKey(pcc.Port))
                knownControl[pcc.Port] = pcc.FactionId;
        }

        // Score candidate ports
        var portScores = new Dictionary<PortId, float>();
        foreach (var port in state.Ports.Values)
        {
            if (port.Id == homePort) continue;

            // Use known control if available; if unknown, treat as neutral (allowed)
            if (knownControl.TryGetValue(port.Id, out var controller))
            {
                if (IsHostile(factionId, controller, state)) continue; // skip enemy ports
            }

            var score = 1f; // base

            // Prefer allied (same faction) ports the governor knows about
            if (knownControl.TryGetValue(port.Id, out var ctrl) && ctrl == factionId)
                score += 2f;

            // Prefer ports with known low prosperity (governor visits struggling ports)
            var prosperityFact = holderFacts
                .FirstOrDefault(f => f.Claim is PortProsperityClaim ppc && ppc.Port == port.Id);
            if (prosperityFact is not null)
            {
                var prosperity = ((PortProsperityClaim)prosperityFact.Claim).Prosperity;
                score += (100f - prosperity) / 50f * prosperityFact.Confidence; // lower prosperity = higher priority
            }

            // Prefer ports where known allied governors are located
            var allyPresent = holderFacts
                .Any(f => f.Claim is IndividualWhereaboutsClaim iwc && iwc.Port == port.Id);
            if (allyPresent) score += 1.5f;

            portScores[port.Id] = score;
        }

        if (portScores.Count == 0) return null;

        // Weighted random selection from top candidates (prevents always picking the same port)
        var sorted = portScores.OrderByDescending(kv => kv.Value).Take(5).ToList();
        var totalScore = sorted.Sum(kv => kv.Value);
        var roll = (float)(_rng.NextDouble() * totalScore);
        float acc = 0;
        foreach (var kv in sorted)
        {
            acc += kv.Value;
            if (roll <= acc) return kv.Key;
        }
        return sorted[0].Key;
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
