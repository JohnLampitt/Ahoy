using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// E1 — Individual dies → if they were a governor, clear the vacancy and immediately
/// spawn a replacement with a cold start (Authority 40, no PlayerRelationship history).
/// The 1-tick window where port.GovernorId is null (between IndividualLifecycleSystem
/// emitting IndividualDied and this rule firing in EventPropagationSystem) is intentional —
/// one day of lawlessness.
/// </summary>
public sealed class GovernorDeathReplacementRule : PropagationRule
{
    public override string RuleId => "E1_GovernorDeathReplacement";

    public override bool Matches(WorldEvent e) => e is IndividualDied;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (IndividualDied)worldEvent;
        if (!state.Individuals.TryGetValue(ev.IndividualId, out var dead)) return;
        if (dead.Role != IndividualRole.Governor) return;
        if (!dead.LocationPortId.HasValue) return;
        if (!state.Ports.TryGetValue(dead.LocationPortId.Value, out var port)) return;
        if (port.GovernorId != ev.IndividualId) return;

        // Vacancy: clear governor, authority hit
        port.GovernorId = null;
        port.FactionAuthority = Math.Clamp(port.FactionAuthority - 15f, 0f, 100f);

        // Spawn replacement — cold start, no inherited relationship
        var newId = IndividualId.New();
        var replacement = new Individual
        {
            Id             = newId,
            FirstName      = NamePool.RandomFirst(context.Rng),
            LastName       = NamePool.RandomLast(context.Rng),
            Role           = IndividualRole.Governor,
            FactionId      = dead.FactionId,
            LocationPortId = port.Id,
            HomePortId     = port.Id,
            Authority      = 40f,
        };
        state.Individuals[newId] = replacement;
        port.GovernorId = newId;

        events.Emit(new GovernorChanged(state.Date, ev.SourceLod,
            port.Id, ev.IndividualId, newId), ev.SourceLod);
    }
}
