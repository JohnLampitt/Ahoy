using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// D1 — Storm dissipation → minor prosperity recovery for affected ports.
/// </summary>
public sealed class StormRecoveryRule : PropagationRule
{
    public override string RuleId => "D1_StormRecovery";

    public override bool Matches(WorldEvent e) => e is StormDissipated;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (StormDissipated)worldEvent;
        if (!state.Regions.TryGetValue(ev.RegionId, out var region)) return;

        foreach (var portId in region.Ports)
        {
            if (!state.Ports.TryGetValue(portId, out var port)) continue;
            var old = port.Prosperity;
            port.Prosperity = Math.Clamp(port.Prosperity + 2f, 0f, 100f);
            if (port.Prosperity > old + 0.5f)
                events.Emit(new PortProsperityChanged(state.Date, ev.SourceLod,
                    portId, old, port.Prosperity), ev.SourceLod);
        }
    }
}
