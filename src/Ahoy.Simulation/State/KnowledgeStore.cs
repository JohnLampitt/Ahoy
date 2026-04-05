using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Simulation.State;

// ---- Contract enums ----

public enum NarrativeArchetype
{
    PoliticalBounty,
    UnderworldHit,
    DesperatePlea,
    ColonialCommission,
    PirateRival,
}

public enum ContractConditionType
{
    TargetDestroyed,
    TargetDead,
    GoodsDelivered,
}

// ---- Deed ledger enums (6A) ----

public enum ActionSeverity { Nuisance = 15, Significant = 35, Severe = 60, Heroic = 100 }
public enum ActionPolarity { Hostile = -1, Friendly = 1 }

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
public record IndividualStatusClaim(IndividualId Individual, IndividualRole Role, FactionId? FactionId, bool IsAlive) : KnowledgeClaim;
public record ShipStatusClaim(ShipId Ship, bool IsDestroyed, RegionId? LastKnownRegion) : KnowledgeClaim;
public record PlayerActionClaim(string QuestTemplateId, string BranchId, PortId? Location) : KnowledgeClaim;
public record CustomClaim(string Subject, string Detail) : KnowledgeClaim;

public record ContractClaim(
    IndividualId IssuerId,
    FactionId IssuerFactionId,
    string TargetSubjectKey,
    ContractConditionType Condition,
    int GoldReward,
    NarrativeArchetype Archetype = NarrativeArchetype.PoliticalBounty) : KnowledgeClaim;

/// <summary>
/// What an actor believes about an ocean POI.
/// Uses PoiCacheStatus (not raw gold) so NPC gossip doesn't leak exact treasury values —
/// a sailor who saw Vane's crew celebrating might say "the cache is empty," not "432g remains."
/// </summary>
public record OceanPoiClaim(
    OceanPoiId Poi,
    RegionId Region,
    PoiType Type,
    bool IsDiscovered,
    PoiCacheStatus CacheStatus) : KnowledgeClaim;

/// <summary>
/// Records the player's belief about an individual's loyalty.
/// ClaimedFaction is the cover story (what the individual publicly presents).
/// ActualFaction is the ground truth (what they really serve) — null until exposed.
/// </summary>
public record IndividualAllegianceClaim(
    IndividualId Individual,
    FactionId ClaimedFaction,
    FactionId? ActualFaction) : KnowledgeClaim;

/// <summary>
/// Universal deed record: "Actor X did Y to Target Z."
/// Propagates as gossip — distant deeds become rumours.
/// When ingested by an IndividualHolder, triggers relationship mutation (6C).
/// BeneficiaryId tracks who ordered/paid for the act (e.g., the governor who posted the bounty).
/// </summary>
/// <summary>
/// A pardon issued by a governor/official, clearing hostilities for a specific actor
/// within a faction. When propagated, accelerates forgetting of hostile deeds and
/// shifts relationships toward neutral.
/// </summary>
public record PardonClaim(
    IndividualId GrantedBy,
    FactionId Faction,
    IndividualId PardonedActor) : KnowledgeClaim;

public record IndividualActionClaim(
    IndividualId ActorId,
    IndividualId TargetId,
    IndividualId? BeneficiaryId,
    ActionPolarity Polarity,
    ActionSeverity Severity,
    string Context) : KnowledgeClaim;

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

    /// <summary>True when a newer fact about the same subject has superseded this one.</summary>
    public bool IsSuperseded { get; set; }

    /// <summary>
    /// Set to the tick number on which this fact was superseded.
    /// Superseded facts are kept for one full tick so other systems (e.g. LLM context)
    /// can read "what the player used to believe" before it is pruned.
    /// </summary>
    public int? SupersededOnTick { get; set; }

    // ---- Provenance ----

    /// <summary>
    /// The immediate entity that passed this copy of the fact to the current holder.
    /// Null means the holder witnessed the underlying event directly (HopCount == 0).
    /// Uses the same KnowledgeHolderId union as all other actors — the player is not
    /// special-cased; a PlayerHolder source is valid when the player shares knowledge.
    /// </summary>
    public KnowledgeHolderId? SourceHolder { get; init; }

    /// <summary>
    /// Confidence at the moment this copy was created, before any per-tick decay.
    /// Lets downstream consumers ask "how credible was this when I received it?"
    /// </summary>
    public float BaseConfidence { get; init; }

    /// <summary>
    /// Number of times the same claim has subsequently arrived from a different source.
    /// Corroborating arrivals increment this instead of creating a duplicate fact.
    /// </summary>
    public int CorroborationCount { get; set; }

    /// <summary>
    /// FactionIds that have already contributed a corroboration boost for this fact.
    /// A faction's echo chamber (multiple ports controlled by the same faction all
    /// repeating the same claim) can only boost confidence once per faction, not once
    /// per port. Null origin (player / direct witness) bypasses this guard.
    /// </summary>
    public HashSet<FactionId> CorroboratingFactionIds { get; } = new();

    /// <summary>
    /// When true, the per-tick decay pass skips this fact.
    /// Used for the PlayerHolder copy of PlayerActionClaim: the player's own record
    /// of their actions is a ledger of agency, not an epistemic guess that should fade.
    /// Third-party copies (PortHolder, FactionHolder) are NOT exempt — factions do forget.
    /// </summary>
    public bool IsDecayExempt { get; init; }

    /// <summary>
    /// The Individual who originally injected this fact as disinformation.
    /// Immutable; set only at injection time. Null for all organically-observed facts.
    /// Preserved on propagation so burning can trace planted lies regardless of how
    /// many PortHolders the fact passed through.
    /// </summary>
    public IndividualId? OriginatingAgentId { get; init; }

    /// <summary>
    /// Returns a canonical key identifying what this fact is about.
    /// Two facts with the same subject key held by the same holder supersede each other.
    /// </summary>
    public static string GetSubjectKey(KnowledgeClaim claim) => claim switch
    {
        PortPriceClaim c           => $"PortPrice:{c.Port.Value}:{c.Good}",
        PortProsperityClaim c      => $"PortProsperity:{c.Port.Value}",
        PortControlClaim c         => $"PortControl:{c.Port.Value}",
        // Ship: unified prefix so ShipStatusClaim(destroyed) supersedes ShipLocationClaim(at-sea)
        ShipLocationClaim c        => $"Ship:{c.Ship.Value}",
        ShipStatusClaim c          => $"Ship:{c.Ship.Value}",
        ShipCargoClaim c           => $"ShipCargo:{c.Ship.Value}",
        FactionStrengthClaim c     => $"FactionStrength:{c.Faction.Value}",
        FactionIntentionClaim c    => $"FactionIntention:{c.Faction.Value}",
        WeatherClaim c             => $"Weather:{c.Region.Value}",
        RouteHazardClaim c         => $"RouteHazard:{c.From.Value}:{c.To.Value}",
        // Individual: unified prefix so IndividualStatusClaim(dead) supersedes IndividualWhereaboutsClaim(at port)
        IndividualWhereaboutsClaim c  => $"Individual:{c.Individual.Value}",
        IndividualStatusClaim c       => $"Individual:{c.Individual.Value}",
        // Each distinct quest resolution is its own subject — two A1 completions on different ships don't supersede each other.
        PlayerActionClaim c        => $"PlayerAction:{c.QuestTemplateId}:{c.BranchId}",
        CustomClaim c              => $"Custom:{c.Subject}",
        ContractClaim c                 => $"Contract:{c.IssuerId.Value}:{c.TargetSubjectKey}",
        OceanPoiClaim c                 => $"Poi:{c.Poi.Value}",
        IndividualAllegianceClaim c     => $"Allegiance:{c.Individual.Value}",
        PardonClaim c                  => $"Pardon:{c.Faction.Value}:{c.PardonedActor.Value}",
        IndividualActionClaim c        => $"Action:{c.ActorId.Value}:{c.TargetId.Value}:{c.Context.GetHashCode():X8}",
        _                               => claim.GetType().Name,
    };
}

// ---- KnowledgeConflict ----

/// <summary>
/// A first-class record of two or more contradicting facts held by the same actor
/// about the same subject. Created when <see cref="KnowledgeStore.AddFact"/> detects
/// that a new fact's claim differs from an existing non-superseded fact with the same
/// subject key. Conflicts are resolved when one side is superseded (by an authoritative
/// direct observation or by natural confidence decay below the pruning floor).
/// </summary>
public sealed record KnowledgeConflict
{
    public required string SubjectKey { get; init; }
    /// <summary>All competing non-superseded facts for this subject key.</summary>
    public required IReadOnlyList<KnowledgeFact> CompetingFacts { get; init; }

    /// <summary>The fact with the highest current confidence — the "winning" belief.</summary>
    public KnowledgeFact? DominantFact =>
        CompetingFacts.OrderByDescending(f => f.Confidence).FirstOrDefault();

    /// <summary>Confidence difference between highest and lowest competing fact.</summary>
    public float ConfidenceSpread =>
        CompetingFacts.Count < 2 ? 0f
        : CompetingFacts.Max(f => f.Confidence) - CompetingFacts.Min(f => f.Confidence);

    /// <summary>True when only one (or zero) active facts remain for this subject.</summary>
    public bool IsResolved => CompetingFacts.Count(f => !f.IsSuperseded) <= 1;
}

// ---- KnowledgeStore ----

public sealed class KnowledgeStore
{
    private readonly Dictionary<KnowledgeHolderId, List<KnowledgeFact>> _store = new();

    // Per-holder conflict index. Key: (holder, subjectKey).
    // Updated in AddFact whenever a contradiction is detected.
    private readonly Dictionary<(KnowledgeHolderId, string), KnowledgeConflict> _conflicts = new();

    // Source reliability weights. Default 1.0; modified by RecordSourceOutcome.
    // Applied as a multiplier to incoming confidence during propagation.
    private readonly Dictionary<KnowledgeHolderId, float> _sourceReliability = new();

    /// <returns>True if this addition created a brand-new conflict for this (holder, subjectKey) pair.</returns>
    public bool AddFact(KnowledgeHolderId holder, KnowledgeFact fact)
    {
        if (!_store.TryGetValue(holder, out var list))
        {
            list = new List<KnowledgeFact>();
            _store[holder] = list;
        }
        list.Add(fact);

        // Update conflict index for this (holder, subject)
        var subjectKey = KnowledgeFact.GetSubjectKey(fact.Claim);
        return UpdateConflicts(holder, subjectKey);
    }

    public IReadOnlyList<KnowledgeFact> GetFacts(KnowledgeHolderId holder)
        => _store.TryGetValue(holder, out var list) ? list : Array.Empty<KnowledgeFact>();

    public IReadOnlyList<KnowledgeFact> GetAllFacts()
        => _store.Values.SelectMany(x => x).ToList();

    /// <summary>All active KnowledgeConflicts held by the specified actor.</summary>
    public IEnumerable<KnowledgeConflict> GetConflicts(KnowledgeHolderId holder) =>
        _conflicts
            .Where(kv => kv.Key.Item1.Equals(holder))
            .Select(kv => kv.Value);

    /// <summary>All active conflicts across all holders. Returns (holder, conflict) pairs.</summary>
    public IEnumerable<(KnowledgeHolderId Holder, KnowledgeConflict Conflict)> GetAllConflicts() =>
        _conflicts.Select(kv => (kv.Key.Item1, kv.Value));

    /// <summary>
    /// Reliability weight for a given source. Default 1.0.
    /// Null source (direct witness) always returns 1.0.
    /// </summary>
    public float GetSourceReliability(KnowledgeHolderId? source) =>
        source is null ? 1.0f
        : _sourceReliability.GetValueOrDefault(source, 1.0f);

    /// <summary>
    /// Record whether a source proved accurate when their claim was checked by direct
    /// observation. Accurate sources gain +0.05; inaccurate lose -0.15. Clamped 0.1..1.5.
    /// </summary>
    public void RecordSourceOutcome(KnowledgeHolderId source, bool wasAccurate)
    {
        var current = _sourceReliability.GetValueOrDefault(source, 1.0f);
        _sourceReliability[source] = Math.Clamp(current + (wasAccurate ? 0.05f : -0.15f), 0.1f, 1.5f);
    }

    /// <summary>
    /// Marks all existing facts for this holder that share the same subject key
    /// as the incoming fact as superseded on the given tick. Call before AddFact.
    /// Superseded facts are retained until the following tick's prune pass.
    /// </summary>
    public void MarkSuperseded(KnowledgeHolderId holder, KnowledgeFact incoming, int currentTick)
    {
        if (!_store.TryGetValue(holder, out var list)) return;
        var subjectKey = KnowledgeFact.GetSubjectKey(incoming.Claim);
        foreach (var existing in list)
        {
            if (existing.Id == incoming.Id) continue;
            if (!existing.IsSuperseded && KnowledgeFact.GetSubjectKey(existing.Claim) == subjectKey)
            {
                existing.IsSuperseded = true;
                existing.SupersededOnTick = currentTick;
            }
        }
    }

    /// <summary>
    /// Remove facts with confidence below threshold, or superseded facts from a previous tick.
    /// Superseded facts are kept for one full tick so other systems can read prior beliefs.
    /// After pruning, re-evaluates conflicts for any affected subjects.
    /// </summary>
    public void PruneExpired(int currentTick, float minimumConfidence = 0.05f)
    {
        foreach (var (holder, list) in _store)
        {
            var keysAffected = list
                .Where(f => f.Confidence < minimumConfidence ||
                            (f.IsSuperseded && f.SupersededOnTick < currentTick))
                .Select(f => KnowledgeFact.GetSubjectKey(f.Claim))
                .Distinct()
                .ToList();

            list.RemoveAll(f =>
                f.Confidence < minimumConfidence ||
                (f.IsSuperseded && f.SupersededOnTick < currentTick));

            foreach (var subjectKey in keysAffected)
                UpdateConflicts(holder, subjectKey);
        }
    }

    /// <summary>
    /// Returns whether any holder has an anchoring fact for the given entity
    /// with confidence >= threshold. Used by ContinuitySystem for entity anchoring.
    /// </summary>
    public bool IsAnchored(KnowledgeHolderId holder, float confidenceThreshold = 0.7f)
        => GetFacts(holder).Any(f => f.Confidence >= confidenceThreshold);

    /// <summary>
    /// Returns every fact across all holders, paired with the holder that owns it.
    /// Used by KnowledgeSystem for cross-holder disinformation contradiction scanning.
    /// </summary>
    public IReadOnlyList<(KnowledgeHolderId Holder, KnowledgeFact Fact)> GetAllFactsWithHolders()
        => _store.SelectMany(kv => kv.Value.Select(f => (kv.Key, f))).ToList();

    // ---- Conflict maintenance ----

    /// <returns>True if this call created a brand-new conflict (was not already tracked).</returns>
    private bool UpdateConflicts(KnowledgeHolderId holder, string subjectKey)
    {
        if (!_store.TryGetValue(holder, out var list)) return false;

        var active = list
            .Where(f => !f.IsSuperseded && KnowledgeFact.GetSubjectKey(f.Claim) == subjectKey)
            .ToList();

        var hasContradiction = active.Count > 1
            && active.Select(f => f.Claim).Distinct().Count() > 1;

        var key = (holder, subjectKey);
        if (hasContradiction)
        {
            var isNew = !_conflicts.ContainsKey(key);
            _conflicts[key] = new KnowledgeConflict { SubjectKey = subjectKey, CompetingFacts = active };
            return isNew;
        }
        else
        {
            _conflicts.Remove(key);
            return false;
        }
    }
}
