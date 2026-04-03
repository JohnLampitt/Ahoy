using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Events;

/// <summary>
/// Base record for all simulation events. Derived positional records pass
/// Date and SourceLod through to this constructor — they are not redundant fields.
/// </summary>
public abstract record WorldEvent(WorldDate Date, SimulationLod SourceLod);

// ---- Economic events ----

public record TradeCompleted(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, PortId PortId,
    TradeGood Good, int Quantity, int PricePerUnit,
    bool IsBuying) : WorldEvent(Date, SourceLod);

public record PriceShifted(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, TradeGood Good,
    int OldPrice, int NewPrice) : WorldEvent(Date, SourceLod);

public record MerchantArrived(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, PortId PortId) : WorldEvent(Date, SourceLod);

public record MerchantDeparted(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, PortId FromPort, PortId? DestPort) : WorldEvent(Date, SourceLod);

public record PortProsperityChanged(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, float OldValue, float NewValue) : WorldEvent(Date, SourceLod);

// ---- Navigation / movement events ----

public record ShipArrived(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, PortId PortId) : WorldEvent(Date, SourceLod);

public record ShipDeparted(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, PortId FromPort) : WorldEvent(Date, SourceLod);

public record ShipEnteredRegion(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, RegionId RegionId) : WorldEvent(Date, SourceLod);

// ---- Weather events ----

public record StormFormed(
    WorldDate Date, SimulationLod SourceLod,
    RegionId RegionId) : WorldEvent(Date, SourceLod);

public record StormDissipated(
    WorldDate Date, SimulationLod SourceLod,
    RegionId RegionId) : WorldEvent(Date, SourceLod);

public record StormPropagated(
    WorldDate Date, SimulationLod SourceLod,
    RegionId FromRegion, RegionId ToRegion) : WorldEvent(Date, SourceLod);

public record WindShifted(
    WorldDate Date, SimulationLod SourceLod,
    RegionId RegionId,
    WindDirection NewDirection,
    WindStrength NewStrength) : WorldEvent(Date, SourceLod);

// ---- Political events ----

public record PortCaptured(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, FactionId? OldFaction, FactionId NewFaction) : WorldEvent(Date, SourceLod);

public record GovernorChanged(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, IndividualId? OldGovernor, IndividualId? NewGovernor) : WorldEvent(Date, SourceLod);

public record FactionRelationshipChanged(
    WorldDate Date, SimulationLod SourceLod,
    FactionId FactionA, FactionId FactionB,
    float OldValue, float NewValue) : WorldEvent(Date, SourceLod);

public record TreatyFormed(
    WorldDate Date, SimulationLod SourceLod,
    FactionId FactionA, FactionId FactionB) : WorldEvent(Date, SourceLod);

// ---- Military events ----

public record PatrolEngaged(
    WorldDate Date, SimulationLod SourceLod,
    ShipId PatrolShip, ShipId TargetShip, RegionId RegionId) : WorldEvent(Date, SourceLod);

public record ShipDestroyed(
    WorldDate Date, SimulationLod SourceLod,
    ShipId ShipId, ShipId? AttackerId) : WorldEvent(Date, SourceLod);

public record ShipRaided(
    WorldDate Date, SimulationLod SourceLod,
    ShipId AttackerShipId, ShipId TargetShipId,
    int GoldTaken) : WorldEvent(Date, SourceLod);

// ---- Social events ----

public record RumourSpread(
    WorldDate Date, SimulationLod SourceLod,
    PortId PortId, string Rumour) : WorldEvent(Date, SourceLod);

public record IndividualDied(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId IndividualId, string Cause) : WorldEvent(Date, SourceLod);

public record IndividualMoved(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId IndividualId,
    PortId FromPort,
    PortId ToPort) : WorldEvent(Date, SourceLod);

// ---- Intelligence events ----

/// <summary>
/// Emitted when InjectDisinformationCorrection fires — the player acted on planted
/// false intel and the deception was exposed. The deceiving faction's FactionHolder
/// is seeded with a corresponding CustomClaim so their future decision-making can react.
/// </summary>
public record DeceptionExposed(
    WorldDate Date, SimulationLod SourceLod,
    FactionId DeceivingFactionId,
    PortId? ExposedAtPort) : WorldEvent(Date, SourceLod);

/// <summary>
/// Emitted by KnowledgeSystem when propagation creates a brand-new conflict between two
/// facts held by the same actor about the same subject. Both facts coexist — neither is
/// superseded. The QuestSystem (and future systems) can listen to this as a trigger.
/// </summary>
public record KnowledgeConflictDetected(
    WorldDate Date, SimulationLod SourceLod,
    string SubjectKey,
    KnowledgeFactId FactAId,
    KnowledgeFactId FactBId,
    KnowledgeHolderId HolderId) : WorldEvent(Date, SourceLod);

// ---- Quest events ----

public record QuestActivated(
    WorldDate Date, SimulationLod SourceLod,
    string QuestTemplateId, string QuestInstanceId,
    string Title) : WorldEvent(Date, SourceLod);

public record QuestResolved(
    WorldDate Date, SimulationLod SourceLod,
    string QuestTemplateId, string QuestInstanceId,
    string Title, Quests.QuestStatus Status,
    string? ChosenBranchId) : WorldEvent(Date, SourceLod);
