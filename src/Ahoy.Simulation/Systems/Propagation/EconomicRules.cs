using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// A1 — Storm → trade disruption → prosperity loss.
/// </summary>
public sealed class StormTradeDisruptionRule : PropagationRule
{
    public override string RuleId => "A1_StormTradeDisruption";

    public override bool Matches(WorldEvent e) => e is StormFormed;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (StormFormed)worldEvent;
        if (!state.Regions.TryGetValue(ev.RegionId, out var region)) return;

        foreach (var portId in region.Ports)
        {
            if (!state.Ports.TryGetValue(portId, out var port)) continue;
            var oldProsperity = port.Prosperity;
            port.Prosperity = Math.Clamp(port.Prosperity - 8f, 0f, 100f);

            events.Emit(new PortProsperityChanged(state.Date, ev.SourceLod,
                portId, oldProsperity, port.Prosperity), ev.SourceLod);
        }

        // Queue faction stimulus for trade disruption
        if (region.DominantFactionId.HasValue)
        {
            state.PendingFactionStimuli.Enqueue(new FactionStimulus
            {
                FactionId = region.DominantFactionId.Value,
                StimulusType = "TradeDisrupted",
                Magnitude = 500f,
                Description = $"Storm in {region.Name} disrupted trade.",
                RegionId = ev.RegionId,
            });
        }
    }
}

/// <summary>
/// A2 — Port prosperity change → faction income adjustment stimulus.
/// </summary>
public sealed class ProsperityFactionIncomeRule : PropagationRule
{
    public override string RuleId => "A2_ProsperityFactionIncome";

    public override bool Matches(WorldEvent e) => e is PortProsperityChanged;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (PortProsperityChanged)worldEvent;
        if (!state.Ports.TryGetValue(ev.PortId, out var port)) return;
        if (!port.ControllingFactionId.HasValue) return;

        var delta = ev.NewValue - ev.OldValue;
        if (Math.Abs(delta) < 5f) return; // only react to significant changes

        state.PendingFactionStimuli.Enqueue(new FactionStimulus
        {
            FactionId = port.ControllingFactionId.Value,
            StimulusType = delta < 0 ? "TradeDisrupted" : "RaidingGain",
            Magnitude = Math.Abs(delta) * 20f,
            Description = $"Prosperity at {port.Name} changed by {delta:+0.0;-0.0}.",
            PortId = ev.PortId,
        });
    }
}

/// <summary>
/// A3 — Raiding event → prosperity loss at nearby ports.
/// </summary>
public sealed class RaidingProsperityRule : PropagationRule
{
    public override string RuleId => "A3_RaidingProsperity";

    public override bool Matches(WorldEvent e) => e is ShipRaided;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (ShipRaided)worldEvent;
        var regionId = state.GetShipRegion(ev.TargetShipId);
        if (!regionId.HasValue) return;
        if (!state.Regions.TryGetValue(regionId.Value, out var region)) return;

        foreach (var portId in region.Ports)
        {
            if (!state.Ports.TryGetValue(portId, out var port)) continue;
            var old = port.Prosperity;
            port.Prosperity = Math.Clamp(port.Prosperity - 3f, 0f, 100f);
            events.Emit(new PortProsperityChanged(state.Date, ev.SourceLod,
                portId, old, port.Prosperity), ev.SourceLod);
        }
    }
}
