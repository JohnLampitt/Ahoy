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
                if (group.Count() > 2) // >2 is definitely wrong; 2 is a conflict
                    v.Add($"Holder {holder} has {group.Count()} non-superseded facts for subject {group.Key}");
            }
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

        // At least one ship should be moving (not all permanently docked)
        var shipsAtSea = state.Ships.Values.Count(s => s.Location is not AtPort);
        if (state.Ships.Count > 3 && shipsAtSea == 0)
            v.Add("No ships at sea — world movement has stopped");

        // Average port prosperity should be above survival floor
        var avgProsperity = state.Ports.Values.Average(p => p.Prosperity);
        if (avgProsperity < 5f)
            v.Add($"Average port prosperity critically low: {avgProsperity:F1} — economic collapse");
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
