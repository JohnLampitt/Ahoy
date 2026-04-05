using Ahoy.Simulation.Tests.Infrastructure;
using Ahoy.WorldData;
using Xunit;
using Xunit.Abstractions;

namespace Ahoy.Simulation.Tests;

/// <summary>
/// Tier 2: Deep-Time Invariant Testing — boots the full Caribbean,
/// runs for extended periods, and asserts core invariants hold.
/// </summary>
public class DeepTimeTests
{
    private readonly ITestOutputHelper _output;

    public DeepTimeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Run the full Caribbean simulation for 1 in-game year (365 ticks).
    /// Assert all invariants hold at the end.
    /// </summary>
    [Fact]
    public void FullCaribbean_365Ticks_InvariantsHold()
    {
        var state = CaribbeanWorldDefinition.Build();
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        var snapshots = new List<TelemetrySnapshot>();

        for (int tick = 1; tick <= 365; tick++)
        {
            engine.Tick();

            // Capture telemetry every 10 ticks
            if (tick % 10 == 0)
                snapshots.Add(TelemetrySnapshot.Capture(state, tick));
        }

        // Validate invariants
        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations)
            _output.WriteLine($"VIOLATION: {v}");

        // Output telemetry summary
        _output.WriteLine(TelemetrySnapshot.CsvHeader);
        foreach (var snap in snapshots)
            _output.WriteLine(snap.ToCsvLine());

        Assert.Empty(violations);
    }

    /// <summary>
    /// Run for 5 in-game years (1825 ticks) — longer stability test.
    /// </summary>
    [Fact]
    public void FullCaribbean_5Years_InvariantsHold()
    {
        var state = CaribbeanWorldDefinition.Build();
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        for (int tick = 1; tick <= 1825; tick++)
            engine.Tick();

        var violations = InvariantValidator.Validate(state);
        foreach (var v in violations)
            _output.WriteLine($"VIOLATION: {v}");

        Assert.Empty(violations);
    }

    /// <summary>
    /// Run with telemetry export — write CSV to test output for manual review.
    /// Useful for graphing prosperity curves, gold circulation, knowledge growth.
    /// </summary>
    [Fact]
    public void FullCaribbean_TelemetryExport_500Ticks()
    {
        var state = CaribbeanWorldDefinition.Build();
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        _output.WriteLine(TelemetrySnapshot.CsvHeader);

        for (int tick = 1; tick <= 500; tick++)
        {
            engine.Tick();
            if (tick % 5 == 0)
                _output.WriteLine(TelemetrySnapshot.Capture(state, tick).ToCsvLine());
        }

        // Basic sanity: world should still be alive
        var finalSnap = TelemetrySnapshot.Capture(state, 500);
        Assert.True(finalSnap.AvgPortProsperity > 5f, "Economy should not have collapsed");
        Assert.True(finalSnap.ActiveShips > 0, "Ships should still exist");
        Assert.True(finalSnap.AliveIndividuals > 0, "NPCs should still be alive");
    }
}
