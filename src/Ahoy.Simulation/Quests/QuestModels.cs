using Ahoy.Core.Enums;
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

// ---- Outcome actions ----

/// <summary>
/// A declarative action executed when the player chooses a quest branch.
/// Processed by SimulationEngine after OutcomeEvents are emitted.
/// </summary>
public abstract record QuestOutcomeAction;

/// <summary>Mark all facts that triggered this quest as superseded.</summary>
public record SupersedeTriggerFacts() : QuestOutcomeAction;

/// <summary>Add a new knowledge fact to the specified holders.</summary>
public record AddKnowledgeFact(
    KnowledgeClaim Claim,
    float Confidence,
    KnowledgeSensitivity Sensitivity,
    KnowledgeHolderId[] Holders) : QuestOutcomeAction;

/// <summary>Emit a RumourSpread event at a port determined by the selector.</summary>
public record EmitRumourAction(
    string Text,
    Func<WorldState, PortId?> PortSelector) : QuestOutcomeAction;

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

    /// <summary>
    /// Declarative world-state mutations applied after OutcomeEvents.
    /// Runs against KnowledgeStore to supersede facts, add new facts, emit rumours, etc.
    /// </summary>
    public IReadOnlyList<QuestOutcomeAction> OutcomeActions { get; init; } = [];

    /// <summary>
    /// If non-null, this branch is hidden when the condition evaluates to false against
    /// the player's current knowledge. Evaluated at display time and at choose-time.
    /// </summary>
    public Func<IReadOnlyList<KnowledgeFact>, bool>? AvailabilityCondition { get; init; }

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

    /// <summary>
    /// Optional factory that derives a display title from the triggering facts.
    /// Falls back to <see cref="Title"/> when null.
    /// Use this for variety: "A galleon matching <em>San Cristóbal</em> was spotted…"
    /// rather than a hardcoded generic line.
    /// </summary>
    public Func<IReadOnlyList<KnowledgeFact>, string>? TitleFactory { get; init; }

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

    /// <summary>
    /// Display title for this specific instance.
    /// Set at activation from TitleFactory(triggerFacts) if defined, otherwise Template.Title.
    /// </summary>
    public required string Title { get; init; }

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
