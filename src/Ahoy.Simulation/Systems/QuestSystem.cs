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

        // 1. Expire active quests whose predicate is now true
        foreach (var instance in state.Quests.ActiveQuests.ToList())
        {
            if (instance.Template.ExpiryPredicate(instance, state))
            {
                instance.Status = QuestStatus.Expired;
                instance.ResolvedDate = state.Date;
                state.Quests.Resolve(instance);
                events.Emit(new QuestResolved(
                    state.Date, SimulationLod.Local,
                    instance.Template.Id.Value, instance.Id.ToString(),
                    instance.Template.Title, QuestStatus.Expired, null),
                    SimulationLod.Local);
            }
        }

        // 2. Check all templates for new triggers
        foreach (var template in _templates)
        {
            if (!template.AllowDuplicateInstances && state.Quests.HasActiveInstance(template.Id))
                continue;

            if (!EvaluateCondition(template.TriggerCondition, playerFacts))
                continue;

            var triggerFacts = template.TriggerFactSelector(playerFacts);
            var instance = new QuestInstance
            {
                Template      = template,
                ActivatedDate = state.Date,
                TriggerFacts  = triggerFacts,
            };
            state.Quests.AddActive(instance);
            events.Emit(new QuestActivated(
                state.Date, SimulationLod.Local,
                template.Id.Value, instance.Id.ToString(),
                template.Title),
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
