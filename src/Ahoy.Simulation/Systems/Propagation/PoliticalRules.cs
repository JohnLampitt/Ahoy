using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems.Propagation;

/// <summary>
/// B1 — Port captured → relationship penalty between attacker and defender factions.
/// </summary>
public sealed class PortCaptureRelationshipRule : PropagationRule
{
    public override string RuleId => "B1_PortCaptureRelationship";

    public override bool Matches(WorldEvent e) => e is PortCaptured;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (PortCaptured)worldEvent;
        if (!ev.OldFaction.HasValue) return;

        var attacker = ev.NewFaction;
        var defender = ev.OldFaction.Value;

        if (!state.Factions.TryGetValue(attacker, out var attackFaction)) return;
        if (!state.Factions.TryGetValue(defender, out var defFaction)) return;

        var oldRel = attackFaction.Relationships.GetValueOrDefault(defender);
        var newRel = Math.Clamp(oldRel - 25f, -100f, 100f);
        attackFaction.Relationships[defender] = newRel;
        defFaction.Relationships[attacker] = Math.Clamp(
            defFaction.Relationships.GetValueOrDefault(attacker) - 25f, -100f, 100f);

        events.Emit(new FactionRelationshipChanged(state.Date, ev.SourceLod,
            attacker, defender, oldRel, newRel), ev.SourceLod);

        // Queue naval loss stimulus for defender
        state.PendingFactionStimuli.Enqueue(new FactionStimulus
        {
            FactionId = defender,
            StimulusType = "PortCaptured",
            Magnitude = 1f,
            Description = $"Port captured by {attackFaction.Name}.",
            PortId = ev.PortId,
        });
    }
}

/// <summary>
/// Group 9 — Port defected → queue stimulus to the original faction for response.
/// </summary>
public sealed class PortDefectionRule : PropagationRule
{
    public override string RuleId => "B1b_PortDefection";

    public override bool Matches(WorldEvent e) => e is PortDefected;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (PortDefected)worldEvent;
        if (!state.Factions.TryGetValue(ev.OldFaction, out _)) return;

        state.PendingFactionStimuli.Enqueue(new FactionStimulus
        {
            FactionId = ev.OldFaction,
            StimulusType = "PortDefected",
            Magnitude = 1f,
            Description = ev.PortId.Value.ToString(),
            PortId = ev.PortId,
        });
    }
}

/// <summary>
/// B2 — Governor authority drop → faction authority penalty.
/// Low-authority ports are harder to defend and generate less income.
/// </summary>
public sealed class GovernorAuthorityRule : PropagationRule
{
    public override string RuleId => "B2_GovernorAuthority";

    public override bool Matches(WorldEvent e) => e is PortProsperityChanged pc && pc.NewValue < 30f;

    public override void Apply(WorldEvent worldEvent, WorldState state,
        SimulationContext context, IEventEmitter events)
    {
        var ev = (PortProsperityChanged)worldEvent;
        if (!state.Ports.TryGetValue(ev.PortId, out var port)) return;
        if (!port.GovernorId.HasValue) return;
        if (!state.Individuals.TryGetValue(port.GovernorId.Value, out var governor)) return;

        // Low prosperity undermines governor's authority
        governor.Authority = Math.Clamp(governor.Authority - 5f, 0f, 100f);
        port.FactionAuthority = Math.Clamp(port.FactionAuthority - 3f, 0f, 100f);
    }
}
