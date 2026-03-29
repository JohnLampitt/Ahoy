using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

// ---- Knowledge holder union ----

public abstract record KnowledgeHolderId;
public record PortHolder(PortId Port) : KnowledgeHolderId;
public record ShipHolder(ShipId Ship) : KnowledgeHolderId;
public record IndividualHolder(IndividualId Individual) : KnowledgeHolderId;
public record FactionHolder(FactionId Faction) : KnowledgeHolderId;
public record PlayerHolder : KnowledgeHolderId;

// ---- Claim hierarchy ----

public abstract record KnowledgeClaim;
public record PortPriceClaim(PortId Port, TradeGood Good, int Price) : KnowledgeClaim;
public record PortProsperityClaim(PortId Port, float Prosperity) : KnowledgeClaim;
public record PortControlClaim(PortId Port, FactionId? FactionId) : KnowledgeClaim;
public record ShipLocationClaim(ShipId Ship, ShipLocation LastKnownLocation) : KnowledgeClaim;
public record ShipCargoClaim(ShipId Ship, Dictionary<TradeGood, int> Cargo) : KnowledgeClaim;
public record FactionStrengthClaim(FactionId Faction, int NavalStrength, int TreasuryGold) : KnowledgeClaim;
public record FactionIntentionClaim(FactionId Faction, string IntentionSummary) : KnowledgeClaim;
public record WeatherClaim(RegionId Region, WindStrength Wind, StormPresence Storm) : KnowledgeClaim;
public record RouteHazardClaim(RegionId From, RegionId To, string Description) : KnowledgeClaim;
public record IndividualWhereaboutsClaim(IndividualId Individual, PortId? Port) : KnowledgeClaim;
public record CustomClaim(string Subject, string Detail) : KnowledgeClaim;

// ---- KnowledgeFact ----

public sealed class KnowledgeFact
{
    public KnowledgeFactId Id { get; init; } = KnowledgeFactId.New();
    public required KnowledgeClaim Claim { get; init; }
    public KnowledgeSensitivity Sensitivity { get; init; }

    /// <summary>Confidence 0..1. Degrades each hop and each tick without confirmation.</summary>
    public float Confidence { get; set; }

    /// <summary>The world date this fact was observed / last confirmed.</summary>
    public required Core.ValueObjects.WorldDate ObservedDate { get; init; }

    /// <summary>
    /// True if this fact was deliberately seeded as false information.
    /// The holder believes it is true; only the seeder knows otherwise.
    /// </summary>
    public bool IsDisinformation { get; init; }

    /// <summary>Number of hops this fact has propagated from its origin.</summary>
    public int HopCount { get; set; }
}

// ---- KnowledgeStore ----

public sealed class KnowledgeStore
{
    private readonly Dictionary<KnowledgeHolderId, List<KnowledgeFact>> _store = new();

    public void AddFact(KnowledgeHolderId holder, KnowledgeFact fact)
    {
        if (!_store.TryGetValue(holder, out var list))
        {
            list = new List<KnowledgeFact>();
            _store[holder] = list;
        }
        list.Add(fact);
    }

    public IReadOnlyList<KnowledgeFact> GetFacts(KnowledgeHolderId holder)
        => _store.TryGetValue(holder, out var list) ? list : Array.Empty<KnowledgeFact>();

    public IReadOnlyList<KnowledgeFact> GetAllFacts()
        => _store.Values.SelectMany(x => x).ToList();

    /// <summary>Remove facts with confidence below the threshold.</summary>
    public void PruneExpired(float minimumConfidence = 0.05f)
    {
        foreach (var list in _store.Values)
            list.RemoveAll(f => f.Confidence < minimumConfidence);
    }

    /// <summary>
    /// Returns whether any holder has an anchoring fact for the given entity
    /// with confidence >= threshold. Used by ContinuitySystem for entity anchoring.
    /// </summary>
    public bool IsAnchored(KnowledgeHolderId holder, float confidenceThreshold = 0.7f)
        => GetFacts(holder).Any(f => f.Confidence >= confidenceThreshold);
}
