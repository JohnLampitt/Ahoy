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

/// <summary>Emitted when SetFleetCourseCommand causes docked fleet ships to depart together.</summary>
public record FleetDeparted(
    WorldDate Date, SimulationLod SourceLod,
    IReadOnlyList<ShipId> Ships,
    PortId From,
    PortId Destination) : WorldEvent(Date, SourceLod);

/// <summary>Emitted when all ships in a convoy dock at the destination port.</summary>
public record FleetArrived(
    WorldDate Date, SimulationLod SourceLod,
    IReadOnlyList<ShipId> Ships,
    PortId At) : WorldEvent(Date, SourceLod);

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

public record BribeAccepted(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId GovernorId, PortId PortId,
    int GoldAmount) : WorldEvent(Date, SourceLod);

public record BribeRejected(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId GovernorId, PortId PortId) : WorldEvent(Date, SourceLod);

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

// ---- Investigation & intelligence events ----

/// <summary>Emitted when an investigation resolves (local or remote).</summary>
public record InvestigationResolved(
    WorldDate Date, SimulationLod SourceLod,
    string SubjectKey,
    KnowledgeFactId? ResultFactId,
    bool WasSuccessful) : WorldEvent(Date, SourceLod);

/// <summary>Emitted when an individual is burned (IsCompromised set to true).</summary>
public record AgentBurned(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId AgentId,
    FactionId OwningFactionId) : WorldEvent(Date, SourceLod);

/// <summary>Emitted when a faction replaces a burned agent after the 30-tick timer.</summary>
public record AgentReplaced(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId OldAgentId,
    IndividualId NewAgentId,
    FactionId OwningFactionId) : WorldEvent(Date, SourceLod);

// ---- POI events ----

/// <summary>Emitted the first time a ship enters an undiscovered POI's location.</summary>
public record PoiDiscovered(
    WorldDate Date, SimulationLod SourceLod,
    OceanPoiId PoiId, ShipId DiscoveredBy) : WorldEvent(Date, SourceLod);

/// <summary>Emitted each time a ship interacts with a POI (loot found, damage taken, or nothing).</summary>
public record PoiEncountered(
    WorldDate Date, SimulationLod SourceLod,
    OceanPoiId PoiId, ShipId ShipId,
    int GoldFound, float HullDamage) : WorldEvent(Date, SourceLod);

// ---- Intelligence / allegiance events ----

/// <summary>
/// Emitted when an infiltrator's actual allegiance is revealed to the player —
/// via InvestigateLocalCommand, BurnAgentCommand, or death at local LOD.
/// ActualFaction = Individual.FactionId (the real master).
/// ClaimedFaction = Individual.ClaimedFactionId (the cover story now blown).
/// </summary>
public record AllegianceRevealed(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId Individual,
    FactionId ActualFaction,
    FactionId ClaimedFaction) : WorldEvent(Date, SourceLod);

// ---- Quest events ----

public record QuestActivated(
    WorldDate Date, SimulationLod SourceLod,
    string QuestTemplateId, string QuestInstanceId,
    string Title) : WorldEvent(Date, SourceLod);

public record QuestResolved(
    WorldDate Date, SimulationLod SourceLod,
    string QuestTemplateId, string QuestInstanceId,
    string Title, Quests.ContractQuestStatus Status) : WorldEvent(Date, SourceLod);

public record ContractFulfilled(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId IssuerId, string TargetSubjectKey, int GoldPaid) : WorldEvent(Date, SourceLod);

// ---- Knowledge conflict events (5C) ----

/// <summary>Emitted when a knowledge conflict auto-resolves (spread > 0.40) or is resolved by investigation.</summary>
public record KnowledgeConflictResolved(
    WorldDate Date, SimulationLod SourceLod,
    string SubjectKey,
    KnowledgeFactId WinningFactId,
    KnowledgeHolderId HolderId) : WorldEvent(Date, SourceLod);

// ---- NPC goal pursuit events (5B) ----

/// <summary>Emitted when an NPC abandons a goal after stalling too long. Triggers Stall & Leak.</summary>
public record NpcPursuitAbandoned(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId NpcId, string GoalDescription) : WorldEvent(Date, SourceLod);

/// <summary>Emitted when an NPC fulfils a contract before the player. Player's quest expires as ClaimedByNpc.</summary>
public record NpcClaimedContract(
    WorldDate Date, SimulationLod SourceLod,
    IndividualId NpcId, string TargetSubjectKey, int GoldPaid) : WorldEvent(Date, SourceLod);
