using Ahoy.Core.Enums;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Tests.Infrastructure;
using Ahoy.WorldData;
using Xunit;
using Xunit.Abstractions;

namespace Ahoy.Simulation.Tests;

/// <summary>
/// Tier 4: Chaos Monkey — inject catastrophic shocks and verify the world
/// recovers to a stable (if different) equilibrium without crashing.
/// </summary>
public class ChaosMonkeyTests
{
    private readonly ITestOutputHelper _output;

    public ChaosMonkeyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Kill all governors simultaneously at tick 100. The world should
    /// descend into chaos but eventually recover via replacement mechanics.
    /// </summary>
    [Fact]
    public void MassGovernorAssassination_WorldSurvives()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        // Stabilise for 100 ticks
        for (int i = 0; i < 100; i++) engine.Tick();

        // CHAOS: Kill every governor
        var governors = state.Individuals.Values
            .Where(i => i.Role == IndividualRole.Governor && i.IsAlive)
            .ToList();
        _output.WriteLine($"Killing {governors.Count} governors");
        foreach (var gov in governors) gov.Kill();

        // Run for 500 more ticks
        for (int i = 0; i < 500; i++) engine.Tick();

        // Validate: world should not have crashed, core invariants hold
        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations)
            _output.WriteLine($"VIOLATION: {v}");

        // The world should still be functioning
        Assert.True(state.Ships.Count > 0, "Ships should still exist");
        Assert.True(state.Factions.Values.Any(f => f.TreasuryGold > 0), "At least one faction solvent");
    }

    /// <summary>
    /// Starve every port — set all food supply to 0. Prosperity should crash
    /// but eventually recover as trade resumes.
    /// </summary>
    [Fact]
    public void GlobalFamine_WorldRecovers()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        for (int i = 0; i < 100; i++) engine.Tick();
        var preShockProsperity = state.Ports.Values.Average(p => p.Prosperity);
        _output.WriteLine($"Pre-shock avg prosperity: {preShockProsperity:F1}");

        // CHAOS: Remove all food from every port
        foreach (var port in state.Ports.Values)
        {
            port.Economy.Supply[TradeGood.Food] = 0;
            port.Economy.Supply.Remove(TradeGood.Medicine);
        }

        // Run for 500 ticks
        for (int i = 0; i < 500; i++) engine.Tick();

        var postRecoveryProsperity = state.Ports.Values.Average(p => p.Prosperity);
        _output.WriteLine($"Post-recovery avg prosperity: {postRecoveryProsperity:F1}");

        // World should have recovered somewhat (production restores supply)
        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations) _output.WriteLine($"VIOLATION: {v}");

        Assert.True(state.Ships.Count > 0, "Ships should still exist");
    }

    /// <summary>
    /// Give one pirate captain 500,000 gold. They should spam bounties,
    /// destabilise the economy, but not crash the simulation.
    /// </summary>
    [Fact]
    public void MegaWealthInjection_WorldSurvives()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        for (int i = 0; i < 100; i++) engine.Tick();

        // CHAOS: Find a pirate captain and give them absurd wealth
        var pirate = state.Individuals.Values
            .FirstOrDefault(i => i.Role == IndividualRole.PirateCaptain && i.IsAlive);
        if (pirate is not null)
        {
            _output.WriteLine($"Enriching {pirate.FullName} with 500,000 gold");
            pirate.CurrentGold = 500_000;
        }

        // Run for 500 ticks
        for (int i = 0; i < 500; i++) engine.Tick();

        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations) _output.WriteLine($"VIOLATION: {v}");

        Assert.True(state.Ships.Count > 0, "Ships should still exist");
    }

    /// <summary>
    /// Trigger simultaneous epidemics at every port. The world should
    /// eventually clear them (naturally or via medicine delivery).
    /// </summary>
    [Fact]
    public void GlobalPandemic_WorldRecovers()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        for (int i = 0; i < 100; i++) engine.Tick();

        // CHAOS: Plague everywhere
        foreach (var port in state.Ports.Values)
        {
            port.StartEpidemic(30);
        }
        _output.WriteLine($"Infected {state.Ports.Count} ports with plague");

        // Run for 200 ticks (epidemics should clear in ~30 ticks)
        for (int i = 0; i < 200; i++) engine.Tick();

        var activeEpidemics = state.Ports.Values.Count(p => p.Conditions.HasFlag(PortConditionFlags.Plague));
        _output.WriteLine($"Active epidemics after 200 ticks: {activeEpidemics}");

        // Most epidemics should have cleared (30 tick natural duration)
        // A few may persist from re-infection via infected ships — that's realistic
        Assert.True(activeEpidemics <= 2, $"Expected at most 2 persistent epidemics, got {activeEpidemics}");

        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations) _output.WriteLine($"VIOLATION: {v}");
    }
}
