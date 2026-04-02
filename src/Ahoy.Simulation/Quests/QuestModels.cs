using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Quests;

// ---- Typed IDs ----

public readonly record struct QuestTemplateId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct QuestInstanceId(Guid Value)
{
    public static QuestInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => $"Quest:{Value:N}"[..14];
}

// ---- Trigger condition tree ----

public abstract record QuestCondition;

/// <summary>Matches if any non-superseded player fact satisfies the predicate.</summary>
public record FactCondition(
    Func<KnowledgeFact, bool> Predicate,
    string Description) : QuestCondition;

/// <summary>All child conditions must be satisfied.</summary>
public record AndCondition(IReadOnlyList<QuestCondition> Children) : QuestCondition;

/// <summary>At least one child condition must be satisfied.</summary>
public record OrCondition(IReadOnlyList<QuestCondition> Children) : QuestCondition;

// ---- Branch ----

public sealed class QuestBranch
{
    public required string BranchId { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// Events injected when the player chooses this branch.
    /// Evaluated at resolution time so they can capture current WorldState.
    /// </summary>
    public required Func<WorldState, IReadOnlyList<WorldEvent>> OutcomeEvents { get; init; }

    /// <summary>If non-null, this template is triggered after this branch resolves.</summary>
    public QuestTemplateId? NextQuestTemplateId { get; init; }
}

// ---- Template ----

public sealed class QuestTemplate
{
    public required QuestTemplateId Id { get; init; }
    public required string Title { get; init; }
    public required string Synopsis { get; init; }

    /// <summary>Evaluated against the player's non-superseded facts each tick.</summary>
    public required QuestCondition TriggerCondition { get; init; }

    /// <summary>Returns the facts that caused activation, for display.</summary>
    public required Func<IReadOnlyList<KnowledgeFact>, IReadOnlyList<KnowledgeFact>> TriggerFactSelector { get; init; }

    public required IReadOnlyList<QuestBranch> Branches { get; init; }

    /// <summary>Returns true if the quest should expire given current instance state and world.</summary>
    public required Func<QuestInstance, WorldState, bool> ExpiryPredicate { get; init; }

    // ---- Flavour / metadata ----
    public string? NpcName { get; init; }
    public string? DefaultNpcDialogue { get; init; }
    public FactionId? AssociatedFactionId { get; init; }

    /// <summary>If false (default), only one instance of this template can be active at a time.</summary>
    public bool AllowDuplicateInstances { get; init; }
}

// ---- Instance ----

public enum QuestStatus { Active, Completed, Expired, Failed }

public sealed class QuestInstance
{
    public QuestInstanceId Id { get; init; } = QuestInstanceId.New();
    public required QuestTemplate Template { get; init; }
    public QuestStatus Status { get; set; } = QuestStatus.Active;
    public required WorldDate ActivatedDate { get; init; }
    public required IReadOnlyList<KnowledgeFact> TriggerFacts { get; init; }

    // ---- LLM-filled flavour (null until wired) ----
    public string? LlmNpcName { get; set; }
    public string? LlmDialogue { get; set; }

    // ---- Resolution ----
    public QuestBranch? ChosenBranch { get; set; }
    public WorldDate? ResolvedDate { get; set; }

    // ---- Display helpers ----
    public string DisplayNpcName   => LlmNpcName  ?? Template.NpcName          ?? "Unknown Contact";
    public string DisplayDialogue  => LlmDialogue ?? Template.DefaultNpcDialogue ?? Template.Synopsis;
}
