namespace Ahoy.Simulation.Quests;

public sealed class QuestStore
{
    private readonly List<QuestInstance> _active  = new();
    private readonly List<QuestInstance> _history = new();

    public IReadOnlyList<QuestInstance> ActiveQuests => _active;
    public IReadOnlyList<QuestInstance> History      => _history;

    public void AddActive(QuestInstance instance) => _active.Add(instance);

    public void Resolve(QuestInstance instance)
    {
        _active.Remove(instance);
        _history.Add(instance);
    }

    /// <summary>True if an instance of this template is currently active.</summary>
    public bool HasActiveInstance(QuestTemplateId templateId)
        => _active.Any(q => q.Template.Id == templateId);

    /// <summary>True if this template has ever been completed.</summary>
    public bool HasCompleted(QuestTemplateId templateId)
        => _history.Any(q => q.Template.Id == templateId && q.Status == QuestStatus.Completed);
}
