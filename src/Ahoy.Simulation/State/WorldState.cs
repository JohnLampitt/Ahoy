using Ahoy.Core.Ids;
using Ahoy.Core.ValueObjects;

namespace Ahoy.Simulation.State;

/// <summary>
/// The single authoritative data store for the entire simulation.
/// Pure data — no behaviour. All mutation happens inside IWorldSystem implementations.
/// </summary>
public sealed class WorldState
{
    // ---- Simulation clock ----
    public WorldDate Date { get; set; } = WorldDate.Start;

    // ---- Core entity dictionaries ----
    public Dictionary<RegionId, Region> Regions { get; } = new();
    public Dictionary<PortId, Port> Ports { get; } = new();
    public Dictionary<FactionId, Faction> Factions { get; } = new();
    public Dictionary<ShipId, Ship> Ships { get; } = new();
    public Dictionary<IndividualId, Individual> Individuals { get; } = new();
    public Dictionary<OceanPoiId, OceanPoi> OceanPois { get; } = new();

    // ---- Player ----
    public PlayerState Player { get; } = new() { CaptainName = "Unknown" };

    // ---- Cross-cutting systems ----
    public KnowledgeStore Knowledge { get; } = new();
    public Quests.QuestStore Quests  { get; } = new();
    public Dictionary<RegionId, RegionWeather> Weather { get; } = new();

    // ---- Relationship matrix (6B) ----
    /// <summary>
    /// Sparse NPC-to-NPC (and NPC-to-Player) opinion matrix. Range: -100..+100.
    /// Only populated for pairs that have interacted or received gossip about each other.
    /// Key: (ObserverId, SubjectId). Asymmetric — A's opinion of B != B's opinion of A.
    /// </summary>
    public Dictionary<(IndividualId, IndividualId), float> RelationshipMatrix { get; } = new();

    public float GetRelationship(IndividualId observer, IndividualId subject)
        => RelationshipMatrix.TryGetValue((observer, subject), out var r) ? r : 0f;

    public void AdjustRelationship(IndividualId observer, IndividualId subject, float delta)
    {
        var key = (observer, subject);
        RelationshipMatrix.TryGetValue(key, out var current);
        RelationshipMatrix[key] = Math.Clamp(current + delta, -100f, 100f);
    }

    // ---- NPC goal pursuit (5B) ----
    /// <summary>
    /// Active NPC goal pursuits. Lives on WorldState so ShipMovementSystem (system 2)
    /// can read pursuits for routing, even though QuestSystem (system 8) creates them.
    /// </summary>
    public Dictionary<IndividualId, GoalPursuit> NpcPursuits { get; } = new();

    // ---- Cross-tick communication ----
    /// <summary>
    /// Stimuli queued by EventPropagationSystem for FactionSystem to
    /// consume on the NEXT tick. FactionSystem drains this at the start of its tick.
    /// </summary>
    public Queue<FactionStimulus> PendingFactionStimuli { get; } = new();

    /// <summary>
    /// Remote investigation requests queued by InvestigateRemoteCommand.
    /// EventPropagationSystem resolves each when CurrentTick >= ResolvesOnTick.
    /// </summary>
    public List<PendingInvestigation> PendingInvestigations { get; } = new();

    // ---- Helpers ----

    public Region GetRegion(RegionId id) => Regions[id];
    public Port GetPort(PortId id) => Ports[id];
    public Faction GetFaction(FactionId id) => Factions[id];
    public Ship GetShip(ShipId id) => Ships[id];
    public Individual GetIndividual(IndividualId id) => Individuals[id];

    public bool TryGetRegion(RegionId id, out Region? region) => Regions.TryGetValue(id, out region);
    public bool TryGetPort(PortId id, out Port? port) => Ports.TryGetValue(id, out port);
    public bool TryGetShip(ShipId id, out Ship? ship) => Ships.TryGetValue(id, out ship);

    /// <summary>Returns the region a ship is currently in, or null if ambiguous.</summary>
    public RegionId? GetShipRegion(ShipId shipId)
    {
        if (!Ships.TryGetValue(shipId, out var ship)) return null;
        return ship.Location switch
        {
            AtPort ap when Ports.TryGetValue(ap.Port, out var p) => p.RegionId,
            AtSea @as => @as.Region,
            AtPoi atPoi => atPoi.Region,
            EnRoute er => er.From,
            _ => null,
        };
    }
}

/// <summary>
/// An in-flight remote investigation request queued by InvestigateRemoteCommand.
/// Resolved by EventPropagationSystem after a fixed delay.
/// </summary>
public sealed class PendingInvestigation
{
    public required string SubjectKey { get; init; }
    public required int GoldCost { get; init; }
    public required int SubmittedOnTick { get; init; }
    /// <summary>Resolution tick = SubmittedOnTick + 5.</summary>
    public int ResolvesOnTick => SubmittedOnTick + 5;
}
