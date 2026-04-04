using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Quests;

public sealed class QuestStore
{
    private readonly List<ContractQuestInstance> _active  = new();
    private readonly List<ContractQuestInstance> _history = new();
    private readonly Dictionary<string, WorldDate> _cooldowns = new();

    public IReadOnlyList<ContractQuestInstance> ActiveContractQuests => _active;
    public IReadOnlyList<ContractQuestInstance> History => _history;

    public void AddActive(ContractQuestInstance instance) => _active.Add(instance);

    public void Resolve(ContractQuestInstance instance)
    {
        _active.Remove(instance);
        _history.Add(instance);
    }

    public bool HasActiveContractQuest(string targetSubjectKey)
        => _active.Any(q => q.Contract.TargetSubjectKey == targetSubjectKey);

    public void RecordCooldown(string subjectKey, WorldDate expiresAfter)
        => _cooldowns[subjectKey] = expiresAfter;

    public bool IsOnCooldown(string subjectKey, WorldDate current)
        => _cooldowns.TryGetValue(subjectKey, out var until) && current.CompareTo(until) <= 0;
}
