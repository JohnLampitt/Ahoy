using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Tests.Infrastructure;

/// <summary>
/// Captures macro-level simulation statistics at a point in time.
/// Used for statistical observability — graph these over time to see if the world
/// behaves "sensibly" (organic waves, not flat lines or wild oscillation).
/// </summary>
public sealed record TelemetrySnapshot
{
    public int Tick { get; init; }
    public float AvgPortProsperity { get; init; }
    public int TotalGoldInEconomy { get; init; }
    public int ActiveShips { get; init; }
    public int ShipsAtSea { get; init; }
    public int AliveIndividuals { get; init; }
    public int ActiveNpcPursuits { get; init; }
    public int StalledNpcPursuits { get; init; }
    public int TotalKnowledgeFacts { get; init; }
    public int ActiveConflicts { get; init; }
    public int ActiveEpidemics { get; init; }
    public int ActiveWars { get; init; }
    public int ActiveBounties { get; init; }

    public static TelemetrySnapshot Capture(WorldState state, int tick)
    {
        var allFacts = state.Knowledge.GetAllFacts();

        return new TelemetrySnapshot
        {
            Tick = tick,
            AvgPortProsperity = state.Ports.Values.Count > 0
                ? state.Ports.Values.Average(p => p.Prosperity) : 0,
            TotalGoldInEconomy = state.Factions.Values.Sum(f => f.TreasuryGold)
                + state.Individuals.Values.Sum(i => i.CurrentGold)
                + state.Ships.Values.Sum(s => s.GoldOnBoard),
            ActiveShips = state.Ships.Count,
            ShipsAtSea = state.Ships.Values.Count(s => s.Location is not AtPort),
            AliveIndividuals = state.Individuals.Values.Count(i => i.IsAlive),
            ActiveNpcPursuits = state.NpcPursuits.Values.Count(p => p.State == PursuitState.Active),
            StalledNpcPursuits = state.NpcPursuits.Values.Count(p => p.State == PursuitState.Stalled),
            TotalKnowledgeFacts = allFacts.Count(f => !f.IsSuperseded),
            ActiveConflicts = state.Knowledge.GetAllConflicts().Count(c => !c.Conflict.IsResolved),
            ActiveEpidemics = state.Ports.Values.Count(p => p.Conditions.HasFlag(PortConditionFlags.Plague)),
            ActiveWars = state.Factions.Values.Sum(f => f.AtWarWith.Count) / 2, // each war counted twice
            ActiveBounties = state.Knowledge.GetAllFacts()
                .Count(f => !f.IsSuperseded && f.Claim is ContractClaim),
        };
    }

    public string ToCsvLine() =>
        $"{Tick},{AvgPortProsperity:F1},{TotalGoldInEconomy},{ActiveShips},{ShipsAtSea}," +
        $"{AliveIndividuals},{ActiveNpcPursuits},{StalledNpcPursuits},{TotalKnowledgeFacts}," +
        $"{ActiveConflicts},{ActiveEpidemics},{ActiveWars},{ActiveBounties}";

    public static string CsvHeader =>
        "Tick,AvgProsperity,TotalGold,Ships,ShipsAtSea," +
        "AliveNPCs,ActivePursuits,StalledPursuits,KnowledgeFacts," +
        "Conflicts,Epidemics,Wars,Bounties";
}
