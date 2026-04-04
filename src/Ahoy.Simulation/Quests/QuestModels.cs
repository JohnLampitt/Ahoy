using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Quests;

// ---- Typed IDs ----

public readonly record struct QuestInstanceId(Guid Value)
{
    public static QuestInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => $"Quest:{Value:N}"[..14];
}

// ---- Status ----

public enum ContractQuestStatus { Active, LostTrail, Fulfilled, TargetGone, Expired }

// ---- ContractQuestInstance ----

public sealed class ContractQuestInstance
{
    public QuestInstanceId Id { get; init; } = QuestInstanceId.New();
    public required KnowledgeFactId ContractFactId { get; init; }
    public required ContractClaim Contract { get; init; }
    public required WorldDate ActivatedDate { get; init; }
    public ContractQuestStatus Status { get; set; } = ContractQuestStatus.Active;

    /// <summary>KnowledgeNarrator prompt fragment for LLM dialogue; fallback to mechanical description.</summary>
    public string? NarrativePromptFragment { get; set; }

    public WorldDate? ResolvedDate { get; set; }
}
