using Ahoy.Core.Ids;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Tests.Infrastructure;

/// <summary>
/// Validates world-state invariants that must hold after any number of ticks.
/// Used by deep-time tests to prove the simulation is stable and coherent.
/// Returns a list of violations — empty means all invariants pass.
/// </summary>
public static class InvariantValidator
{
    public static List<string> Validate(WorldState state)
    {
        var violations = new List<string>();

        ValidateEconomics(state, violations);
        ValidateEpistemics(state, violations);
        ValidateRelationships(state, violations);
        ValidateEntityCoherence(state, violations);
        ValidateCrisisCoherence(state, violations);
        ValidateWorldAlive(state, violations);

        return violations;
    }

    private static void ValidateEconomics(WorldState state, List<string> v)
    {
        // No negative gold
        foreach (var faction in state.Factions.Values)
            if (faction.TreasuryGold < 0)
                v.Add($"Faction {faction.Name} has negative treasury: {faction.TreasuryGold}");

        foreach (var individual in state.Individuals.Values)
            if (individual.CurrentGold < 0)
                v.Add($"Individual {individual.FullName} has negative gold: {individual.CurrentGold}");

        // Port prosperity in bounds
        foreach (var port in state.Ports.Values)
            if (port.Prosperity < 0 || port.Prosperity > 100)
                v.Add($"Port {port.Name} prosperity out of bounds: {port.Prosperity}");

        // Economic velocity: gold should be circulating, not hoarded in one place.
        // If any single faction holds >80% of total gold, the economy has deadlocked.
        var totalGold = state.Factions.Values.Sum(f => (long)f.TreasuryGold)
            + state.Individuals.Values.Sum(i => (long)i.CurrentGold)
            + state.Ships.Values.Sum(s => (long)s.GoldOnBoard);
        if (totalGold > 0)
        {
            foreach (var faction in state.Factions.Values)
            {
                var share = (float)faction.TreasuryGold / totalGold;
                if (share > 0.80f)
                    v.Add($"Economic deadlock: {faction.Name} holds {share:P0} of total gold ({faction.TreasuryGold}/{totalGold})");
            }
        }
    }

    private static void ValidateEpistemics(WorldState state, List<string> v)
    {
        // No fact below pruning floor should exist (proves GC works)
        var allFacts = state.Knowledge.GetAllFacts();
        var zombieFacts = allFacts.Count(f => !f.IsSuperseded && f.Confidence < 0.05f);
        if (zombieFacts > 0)
            v.Add($"{zombieFacts} non-superseded facts with confidence < 0.05 (decay/GC failure)");

        // No holder should have duplicate non-superseded facts for the same subject key
        foreach (var holder in GetAllHolders(state))
        {
            var facts = state.Knowledge.GetFacts(holder).Where(f => !f.IsSuperseded).ToList();
            var subjectGroups = facts.GroupBy(f => KnowledgeFact.GetSubjectKey(f.Claim));
            foreach (var group in subjectGroups)
            {
                if (group.Count() > 3) // >3 indicates accumulation bug; 2-3 can be legitimate conflicts
                    v.Add($"Holder {holder} has {group.Count()} non-superseded facts for subject {group.Key}");
            }
        }

        // Echo chamber check: no fact should have CorroborationCount > 10 without
        // at least 3 distinct CorroboratingFactionIds. If a rumour bounced between
        // allied ships 50 times, the faction-origin guard should have capped it.
        foreach (var fact in allFacts)
        {
            if (fact.IsSuperseded) continue;
            if (fact.CorroborationCount > 10 && fact.CorroboratingFactionIds.Count < 3)
                v.Add($"Echo chamber: fact {fact.Id} has {fact.CorroborationCount} corroborations " +
                      $"from only {fact.CorroboratingFactionIds.Count} distinct factions " +
                      $"(subject: {KnowledgeFact.GetSubjectKey(fact.Claim)})");
        }
    }

    private static void ValidateRelationships(WorldState state, List<string> v)
    {
        // All relationship values in bounds
        foreach (var ((observer, subject), rel) in state.RelationshipMatrix)
            if (rel < -100 || rel > 100)
                v.Add($"Relationship ({observer}, {subject}) out of bounds: {rel}");

        // War symmetry: if A is at war with B, B must be at war with A
        foreach (var (factionId, faction) in state.Factions)
            foreach (var enemy in faction.AtWarWith)
                if (state.Factions.TryGetValue(enemy, out var enemyFaction) && !enemyFaction.AtWarWith.Contains(factionId))
                    v.Add($"War asymmetry: {faction.Name} at war with {enemy} but not reciprocal");
    }

    private static void ValidateEntityCoherence(WorldState state, List<string> v)
    {
        // No NpcPursuit references a dead individual
        foreach (var (npcId, pursuit) in state.NpcPursuits)
            if (!state.Individuals.TryGetValue(npcId, out var npc) || !npc.IsAlive)
                if (pursuit.State is PursuitState.Active or PursuitState.Stalled)
                    v.Add($"Active NpcPursuit for dead/missing individual {npcId}");

        // No CaptorId references a dead individual
        foreach (var individual in state.Individuals.Values)
            if (individual.CaptorId.HasValue
                && state.Individuals.TryGetValue(individual.CaptorId.Value, out var captor)
                && !captor.IsAlive)
                v.Add($"{individual.FullName} held captive by dead captor {individual.CaptorId}");

        // Ships with captains that don't exist
        foreach (var ship in state.Ships.Values)
            if (ship.CaptainId.HasValue && !state.Individuals.ContainsKey(ship.CaptainId.Value))
                v.Add($"Ship {ship.Name} references non-existent captain {ship.CaptainId}");
    }

    private static void ValidateCrisisCoherence(WorldState state, List<string> v)
    {
        // Epidemic coherence: Plague flag requires timer
        foreach (var port in state.Ports.Values)
            if (port.Conditions.HasFlag(PortConditionFlags.Plague) && !port.EpidemicTicksRemaining.HasValue)
                v.Add($"Port {port.Name} has Plague flag without EpidemicTicksRemaining");
    }

    private static void ValidateWorldAlive(WorldState state, List<string> v)
    {
        // At least one faction should have positive treasury
        if (state.Factions.Values.All(f => f.TreasuryGold <= 0))
            v.Add("All factions are bankrupt — economic deadlock");

        // At least one captained ship should be moving (not all permanently docked)
        var totalShips = state.Ships.Count;
        var shipsWithCaptain = state.Ships.Values.Count(s => s.CaptainId.HasValue);
        var shipsWithLivingCaptain = state.Ships.Values
            .Count(s => s.CaptainId.HasValue
                && state.Individuals.TryGetValue(s.CaptainId.Value, out var cap) && cap.IsAlive);
        var captainedAtSea = state.Ships.Values
            .Count(s => s.CaptainId.HasValue
                && state.Individuals.TryGetValue(s.CaptainId.Value, out var cap) && cap.IsAlive
                && s.Location is not AtPort);
        var shipsWithRoutes = state.Ships.Values.Count(s => s.Route is not null);

        if (shipsWithLivingCaptain > 3 && captainedAtSea == 0)
            v.Add($"World movement stopped: {totalShips} ships total, " +
                  $"{shipsWithCaptain} with captain, {shipsWithLivingCaptain} with living captain, " +
                  $"{captainedAtSea} at sea, {shipsWithRoutes} with active routes");

        // Average port prosperity should be above survival floor
        if (state.Ports.Count > 0)
        {
            var avgProsperity = state.Ports.Values.Average(p => p.Prosperity);
            if (avgProsperity < 5f)
            {
                var worstPorts = state.Ports.Values
                    .OrderBy(p => p.Prosperity)
                    .Take(3)
                    .Select(p => $"{p.Name}={p.Prosperity:F0}")
                    .ToList();
                var activeEpidemics = state.Ports.Values.Count(p => p.Conditions.HasFlag(PortConditionFlags.Plague));
                var activeBlockades = state.Ports.Values.Count(p => p.Conditions.HasFlag(PortConditionFlags.Blockaded));
                v.Add($"Average port prosperity critically low: {avgProsperity:F1}. " +
                      $"Worst: {string.Join(", ", worstPorts)}. " +
                      $"Active epidemics: {activeEpidemics}, blockades: {activeBlockades}");
            }
        }
    }

    private static IEnumerable<KnowledgeHolderId> GetAllHolders(WorldState state)
    {
        yield return new PlayerHolder();
        foreach (var id in state.Individuals.Keys) yield return new IndividualHolder(id);
        foreach (var id in state.Factions.Keys) yield return new FactionHolder(id);
        foreach (var id in state.Ports.Keys) yield return new PortHolder(id);
        foreach (var id in state.Ships.Keys) yield return new ShipHolder(id);
    }
}
