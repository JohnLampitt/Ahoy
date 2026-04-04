using Ahoy.Core.Ids;
using Ahoy.Simulation.Quests;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Engine;

/// <summary>Base class for all commands the player can issue.</summary>
public abstract record PlayerCommand;

// ---- Navigation ----

public record SetCourseCommand(ShipId ShipId, PortId DestinationPort) : PlayerCommand;
public record AnchorCommand(ShipId ShipId) : PlayerCommand;

/// <summary>Order the ship to sail toward an OceanPoi.</summary>
public record SetCourseToPoi(ShipId ShipId, OceanPoiId PoiId) : PlayerCommand;

/// <summary>Set a destination for all ships in the player's fleet simultaneously.</summary>
public record SetFleetCourseCommand(PortId DestinationPort) : PlayerCommand;

// ---- Trade ----

/// <summary>Transfer cargo between two player fleet ships at the same port.</summary>
public record TransferCargoCommand(
    ShipId FromShip, ShipId ToShip,
    Core.Enums.TradeGood Good, int Quantity) : PlayerCommand;

public record BuyGoodCommand(
    ShipId ShipId, PortId PortId,
    Core.Enums.TradeGood Good, int Quantity) : PlayerCommand;

public record SellGoodCommand(
    ShipId ShipId, PortId PortId,
    Core.Enums.TradeGood Good, int Quantity) : PlayerCommand;

// ---- Diplomacy ----

public record BribeGovernorCommand(
    IndividualId GovernorId, int GoldAmount) : PlayerCommand;

public record ProvideintelCommand(
    IndividualId TargetId, Core.Ids.KnowledgeFactId FactId) : PlayerCommand;

// ---- Knowledge ----

public record BuyKnowledgeCommand(
    IndividualId BrokerId, Core.Ids.KnowledgeFactId FactId, int Price) : PlayerCommand;

public record InvestigateLocalCommand(string SubjectKey) : PlayerCommand;

public record InvestigateRemoteCommand(string SubjectKey, int GoldCost) : PlayerCommand;

public record SellFactCommand(IndividualId BrokerId, Core.Ids.KnowledgeFactId FactId) : PlayerCommand;

public record BurnAgentCommand(IndividualId AgentId) : PlayerCommand;

// ---- Quests ----

/// <summary>
/// Plant a fabricated KnowledgeClaim into the world's knowledge network,
/// attributed to the specified faction so suspicion falls on them.
/// </summary>
public record FabricateFactCommand(
    KnowledgeClaim FabricatedClaim,
    Core.Ids.FactionId AppearsToBeFromFaction) : PlayerCommand;

public record ClaimContractRewardCommand(QuestInstanceId QuestInstanceId) : PlayerCommand;

// ---- Actor decisions ----

public record IntervedeInDecisionCommand(
    string RequestId,
    Decisions.PlayerIntervention Intervention) : PlayerCommand;
