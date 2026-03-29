using Ahoy.Core.Enums;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// C1 — Naval patrol in region → raiding momentum suppression for pirate factions.
/// </summary>
public sealed class PatrolSuppressRaidingRule : PropagationRule
{
    public override string RuleId => "C1_PatrolSuppressRaiding";

    public override bool Matches(WorldEvent e) => e is PatrolEngaged;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (PatrolEngaged)worldEvent;

        foreach (var (factionId, faction) in state.Factions)
        {
            if (faction.Type != FactionType.PirateBrotherhood) continue;
            faction.RaidingMomentum = Math.Max(0f, faction.RaidingMomentum - 5f);

            state.PendingFactionStimuli.Enqueue(new FactionStimulus
            {
                FactionId = factionId,
                StimulusType = "NavalLoss",
                Magnitude = 0.5f,
                Description = $"Colonial patrol active in region.",
                RegionId = ev.RegionId,
            });
        }
    }
}

/// <summary>
/// C2 — Ship destroyed → naval strength loss for owning faction.
/// </summary>
public sealed class ShipDestroyedNavalLossRule : PropagationRule
{
    public override string RuleId => "C2_ShipDestroyedNavalLoss";

    public override bool Matches(WorldEvent e) => e is ShipDestroyed;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (ShipDestroyed)worldEvent;
        if (!state.Ships.TryGetValue(ev.ShipId, out var ship)) return;
        if (!ship.OwnerFactionId.HasValue) return;

        state.PendingFactionStimuli.Enqueue(new FactionStimulus
        {
            FactionId = ship.OwnerFactionId.Value,
            StimulusType = "NavalLoss",
            Magnitude = 1f,
            Description = $"Ship {ship.Name} destroyed.",
        });
    }
}
