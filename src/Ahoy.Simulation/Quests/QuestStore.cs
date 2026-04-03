using Ahoy.Core.ValueObjects;

namespace Ahoy.Simulation.Quests;

public sealed class QuestStore
{
    private readonly List<QuestInstance> _active  = new();
    private readonly List<QuestInstance> _history = new();

    /// <summary>
    /// Prevents the same quest/subject pair from re-triggering immediately after
    /// expiry or completion. Key: (templateId, subjectKey). Value: date until which
    /// re-triggering is suppressed.
    /// </summary>
    private readonly Dictionary<(QuestTemplateId, string), WorldDate> _cooldowns = new();

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

    /// <summary>Record a cooldown that suppresses re-triggering for this template+subject pair.</summary>
    public void RecordCooldown(QuestTemplateId templateId, string subjectKey, WorldDate expiresAfter)
        => _cooldowns[(templateId, subjectKey)] = expiresAfter;

    /// <summary>True if the cooldown for this template+subject pair is still active.</summary>
    public bool IsOnCooldown(QuestTemplateId templateId, string subjectKey, WorldDate current)
        => _cooldowns.TryGetValue((templateId, subjectKey), out var until)
           && current.CompareTo(until) <= 0;
}
