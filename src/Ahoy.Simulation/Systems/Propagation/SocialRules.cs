using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// E1 — Individual dies → governor replacement at their port.
/// </summary>
public sealed class GovernorDeathReplacementRule : PropagationRule
{
    public override string RuleId => "E1_GovernorDeathReplacement";

    public override bool Matches(WorldEvent e) => e is IndividualDied;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (IndividualDied)worldEvent;
        if (!state.Individuals.TryGetValue(ev.IndividualId, out var individual)) return;
        if (!individual.LocationPortId.HasValue) return;
        if (!state.Ports.TryGetValue(individual.LocationPortId.Value, out var port)) return;
        if (port.GovernorId != ev.IndividualId) return;

        // Clear governor — FactionSystem will appoint a new one on next tick (future work)
        var oldGov = port.GovernorId;
        port.GovernorId = null;
        port.FactionAuthority = Math.Clamp(port.FactionAuthority - 15f, 0f, 100f);

        events.Emit(new GovernorChanged(state.Date, ev.SourceLod,
            port.Id, oldGov, null), ev.SourceLod);
    }
}
