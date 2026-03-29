using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 4 — runs after EconomySystem.
/// Handles faction income/expenditure, naval allocation convergence,
/// goal adoption/abandonment, relationship drift, and self-reinforcing decline loops.
/// Drains PendingFactionStimuli at the start of its run.
/// </summary>
public sealed class FactionSystem : IWorldSystem
{
    private readonly Random _rng;

    // Utility scoring thresholds
    private const float GoalAdoptThreshold  = 0.65f;
    private const float GoalAbandonThreshold = 0.25f;
    private const int MaxActiveGoals = 3;

    public FactionSystem(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Drain stimuli queued by EventPropagationSystem last tick
        var stimuli = new List<FactionStimulus>();
        while (state.PendingFactionStimuli.TryDequeue(out var s))
            stimuli.Add(s);

        foreach (var (factionId, faction) in state.Factions)
        {
            ApplyStimuli(faction, stimuli.Where(s => s.FactionId == factionId));

            switch (faction.Type)
            {
                case FactionType.Colonial:
                    TickColonial(faction, factionId, state, events, context);
                    break;
                case FactionType.PirateBrotherhood:
                    TickPirate(faction, factionId, state, events, context);
                    break;
            }

            UpdateGoals(faction, factionId, state, context);
            TickRelationshipDecay(faction);
        }
    }

    // ---- Colonial ----

    private void TickColonial(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events, SimulationContext context)
    {
        // Income: port taxes + trade duties (derived from port prosperity)
        var portIncome = state.Ports.Values
            .Where(p => p.ControllingFactionId == factionId)
            .Sum(p => (int)(p.Prosperity * 2.5f)); // rough formula

        faction.IncomePerTick = portIncome;

        // Expenditure: naval maintenance + patrol costs
        var navalCost = (int)(faction.NavalStrength * 15f * faction.CurrentNavalAllocationFraction);
        faction.ExpenditurePerTick = navalCost;

        faction.TreasuryGold += faction.IncomePerTick - faction.ExpenditurePerTick;
        faction.TreasuryGold = Math.Max(0, faction.TreasuryGold);

        // Naval allocation convergence (20%/tick toward desired)
        var allocationDelta = (faction.DesiredNavalAllocationFraction - faction.CurrentNavalAllocationFraction) * 0.20f;
        faction.CurrentNavalAllocationFraction = Math.Clamp(
            faction.CurrentNavalAllocationFraction + allocationDelta, 0f, 1f);

        // Self-reinforcing decline: if treasury is low, can't maintain patrols
        if (faction.TreasuryGold < navalCost * 3)
        {
            faction.NavalStrength = Math.Max(0, faction.NavalStrength - 1);
        }
        else if (faction.TreasuryGold > navalCost * 10 && _rng.NextDouble() < 0.1f)
        {
            faction.NavalStrength++;
        }
    }

    // ---- Pirate brotherhood ----

    private void TickPirate(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events, SimulationContext context)
    {
        // Income: raiding proceeds + haven fees
        var raidIncome = (int)(faction.RaidingMomentum * 5f);
        var havenFees = faction.HavenPresence.Values.Sum(h => (int)(h * 0.8f));
        faction.IncomePerTick = raidIncome + havenFees;

        faction.TreasuryGold += faction.IncomePerTick;

        // Cohesion drift
        if (faction.RaidingMomentum > 60f)
            faction.Cohesion = Math.Min(100f, faction.Cohesion + 0.5f);
        else if (faction.RaidingMomentum < 30f)
            faction.Cohesion = Math.Max(0f, faction.Cohesion - 1.0f);

        // Fracture risk below 20
        if (faction.Cohesion < 20f && _rng.NextDouble() < 0.05f)
            TriggerFracture(faction, factionId, state, events);

        // Raiding momentum natural decay
        faction.RaidingMomentum = Math.Max(0f, faction.RaidingMomentum - 1f);
    }

    private void TriggerFracture(Faction faction, FactionId factionId, WorldState state,
        IEventEmitter events)
    {
        // Split off a portion of haven presence — simplified model
        foreach (var region in faction.HavenPresence.Keys.ToList())
            faction.HavenPresence[region] *= 0.6f;

        faction.Cohesion = 30f;
        faction.RaidingMomentum = Math.Max(0f, faction.RaidingMomentum - 20f);
    }

    // ---- Goal management ----

    private void UpdateGoals(Faction faction, FactionId factionId, WorldState state,
        SimulationContext context)
    {
        // Increment active goal ticks
        foreach (var goal in faction.ActiveGoals)
            goal.TicksActive++;

        // Abandon low-utility goals
        var toAbandon = faction.ActiveGoals
            .Where(g => ScoreGoal(g, faction, state) < GoalAbandonThreshold)
            .ToList();
        foreach (var g in toAbandon)
            faction.ActiveGoals.Remove(g);

        // Adopt high-utility new goals if room
        if (faction.ActiveGoals.Count < MaxActiveGoals)
        {
            var candidates = GenerateCandidateGoals(faction, factionId, state);
            foreach (var candidate in candidates)
            {
                if (faction.ActiveGoals.Count >= MaxActiveGoals) break;
                candidate.Utility = ScoreGoal(candidate, faction, state);
                if (candidate.Utility >= GoalAdoptThreshold)
                    faction.ActiveGoals.Add(candidate);
            }
        }
    }

    private static float ScoreGoal(FactionGoal goal, Faction faction, WorldState state) =>
        goal switch
        {
            ExpandTerritory et => faction.TreasuryGold > 5000 && faction.NavalStrength > 5 ? 0.80f : 0.30f,
            SuppressPiracy sp  => faction.Type == FactionType.Colonial ? 0.70f : 0.10f,
            BuildNavy          => faction.TreasuryGold > 3000 && faction.NavalStrength < 10 ? 0.75f : 0.25f,
            AccumulateTreasury => faction.TreasuryGold < 2000 ? 0.80f : 0.20f,
            RaidShippingLane r => faction.Type == FactionType.PirateBrotherhood ? 0.75f : 0.05f,
            EstablishHaven eh  => faction.Type == FactionType.PirateBrotherhood ? 0.65f : 0.00f,
            _                  => 0.50f,
        };

    private static List<FactionGoal> GenerateCandidateGoals(Faction faction, FactionId factionId,
        WorldState state)
    {
        var goals = new List<FactionGoal>();

        if (faction.Type == FactionType.Colonial)
        {
            // Expand into uncontrolled regions
            foreach (var (regionId, region) in state.Regions)
                if (region.DominantFactionId != factionId)
                    goals.Add(new ExpandTerritory(regionId));

            goals.Add(new BuildNavy());
            goals.Add(new AccumulateTreasury());

            foreach (var (regionId, _) in state.Regions)
                goals.Add(new SuppressPiracy(regionId));
        }
        else
        {
            foreach (var (regionId, _) in state.Regions)
                goals.Add(new RaidShippingLane(regionId));

            foreach (var (portId, port) in state.Ports.Where(p => p.Value.IsPirateHaven))
                goals.Add(new EstablishHaven(portId));
        }

        return goals;
    }

    private static void TickRelationshipDecay(Faction faction)
    {
        // Relationships drift very slowly toward 0 over time
        foreach (var key in faction.Relationships.Keys.ToList())
        {
            var val = faction.Relationships[key];
            faction.Relationships[key] = val > 0
                ? Math.Max(0f, val - 0.05f)
                : Math.Min(0f, val + 0.05f);
        }
    }

    private static void ApplyStimuli(Faction faction, IEnumerable<FactionStimulus> stimuli)
    {
        foreach (var s in stimuli)
        {
            switch (s.StimulusType)
            {
                case "TradeDisrupted":
                    faction.TreasuryGold = Math.Max(0, (int)(faction.TreasuryGold - s.Magnitude));
                    break;
                case "PortCaptured":
                    // Remove from controlled ports — handled by the event that queued this stimulus
                    break;
                case "RaidingGain":
                    faction.TreasuryGold += (int)s.Magnitude;
                    faction.RaidingMomentum = Math.Min(100f, faction.RaidingMomentum + s.Magnitude * 0.1f);
                    break;
                case "NavalLoss":
                    faction.NavalStrength = Math.Max(0, faction.NavalStrength - (int)s.Magnitude);
                    break;
            }
        }
    }
}
