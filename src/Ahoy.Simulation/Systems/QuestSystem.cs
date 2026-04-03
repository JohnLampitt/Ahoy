using Ahoy.Core.Enums;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 7 — runs after KnowledgeSystem.
/// Each tick:
///   1. Checks expiry on all active quests.
///   2. Evaluates trigger conditions for registered templates against the player's knowledge.
///   3. Instantiates new QuestInstances for triggered templates.
/// Branch resolution (player choice) is handled in SimulationEngine.ApplyCommand.
/// </summary>
public sealed class QuestSystem : IWorldSystem
{
    private readonly IReadOnlyList<QuestTemplate> _templates;

    public QuestSystem(IReadOnlyList<QuestTemplate> templates)
    {
        _templates = templates;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        var playerFacts = state.Knowledge.GetFacts(new PlayerHolder())
            .Where(f => !f.IsSuperseded)
            .ToList();

        // 1. Expire active quests
        foreach (var instance in state.Quests.ActiveQuests.ToList())
        {
            // Primary: natural decay — the trigger condition no longer holds in the player's knowledge.
            // This honours the design goal: "knowledge-gated, not timer-gated."
            var naturallyExpired = !EvaluateCondition(instance.Template.TriggerCondition, playerFacts);
            // Backstop: each template's own hard timer. Keeps per-template calibration;
            // prevents phantom quests if a false fact gets stuck in an echo chamber.
            var timedOut = instance.Template.ExpiryPredicate(instance, state);

            if (!naturallyExpired && !timedOut) continue;

            // Record a 20-tick cooldown so the same rumour doesn't immediately re-trigger.
            var subjectKey = instance.TriggerFacts.Count > 0
                ? KnowledgeFact.GetSubjectKey(instance.TriggerFacts[0].Claim)
                : instance.Template.Id.Value;
            state.Quests.RecordCooldown(instance.Template.Id, subjectKey, state.Date.Advance(20));

            instance.Status       = QuestStatus.Expired;
            instance.ResolvedDate = state.Date;
            state.Quests.Resolve(instance);
            events.Emit(new QuestResolved(
                state.Date, SimulationLod.Local,
                instance.Template.Id.Value, instance.Id.ToString(),
                instance.Title, QuestStatus.Expired, null),
                SimulationLod.Local);
        }

        // 2. Check all templates for new triggers
        foreach (var template in _templates)
        {
            if (!template.AllowDuplicateInstances && state.Quests.HasActiveInstance(template.Id))
                continue;

            if (!EvaluateCondition(template.TriggerCondition, playerFacts))
                continue;

            var triggerFacts = template.TriggerFactSelector(playerFacts);

            // Cooldown guard: don't re-trigger the same quest on the same subject too soon.
            var subjectKey = triggerFacts.Count > 0
                ? KnowledgeFact.GetSubjectKey(triggerFacts[0].Claim)
                : template.Id.Value;
            if (state.Quests.IsOnCooldown(template.Id, subjectKey, state.Date))
                continue;

            var title = template.TitleFactory?.Invoke(triggerFacts) ?? template.Title;
            var instance = new QuestInstance
            {
                Template      = template,
                ActivatedDate = state.Date,
                TriggerFacts  = triggerFacts,
                Title         = title,
            };
            state.Quests.AddActive(instance);
            // Prevent the same fact from triggering another instance for 20 ticks,
            // even when AllowDuplicateInstances = true. Without this, every tick while
            // the fact exists creates a new instance before the first one can expire.
            state.Quests.RecordCooldown(template.Id, subjectKey, state.Date.Advance(20));
            events.Emit(new QuestActivated(
                state.Date, SimulationLod.Local,
                template.Id.Value, instance.Id.ToString(),
                title),
                SimulationLod.Local);
        }
    }

    private static bool EvaluateCondition(QuestCondition condition, IReadOnlyList<KnowledgeFact> facts)
        => condition switch
        {
            FactCondition fc => facts.Any(fc.Predicate),
            AndCondition ac  => ac.Children.All(c => EvaluateCondition(c, facts)),
            OrCondition oc   => oc.Children.Any(c => EvaluateCondition(c, facts)),
            _                => false,
        };
}
