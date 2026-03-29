using Ahoy.Core.Ids;

namespace Ahoy.Simulation.Engine;

/// <summary>Base class for all commands the player can issue.</summary>
public abstract record PlayerCommand;

// ---- Navigation ----

public record SetCourseCommand(ShipId ShipId, PortId DestinationPort) : PlayerCommand;
public record AnchorCommand(ShipId ShipId) : PlayerCommand;

// ---- Trade ----

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

// ---- Actor decisions ----

public record IntervedeInDecisionCommand(
    string RequestId,
    Decisions.PlayerIntervention Intervention) : PlayerCommand;
