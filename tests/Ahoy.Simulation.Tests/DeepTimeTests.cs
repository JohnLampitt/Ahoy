using Ahoy.Core.Enums;
using Ahoy.Simulation.State;
using Ahoy.Simulation.Tests.Infrastructure;
using Ahoy.WorldData;
using Xunit;
using Xunit.Abstractions;

namespace Ahoy.Simulation.Tests;

/// <summary>
/// Tier 2: Deep-Time Invariant Testing — boots the full Caribbean,
/// runs for extended periods, and asserts core invariants hold.
/// Telemetry is written to files in the test output directory for graphing.
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
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        var snapshots = new List<TelemetrySnapshot>();

        for (int tick = 1; tick <= 365; tick++)
        {
            engine.Tick();
            if (tick % 10 == 0)
                snapshots.Add(TelemetrySnapshot.Capture(state, tick));
        }

        // Validate invariants
        var violations = InvariantValidator.Validate(state);

        WriteSummary("365-tick", state, snapshots, violations);
        WriteCsvFile("telemetry-365.csv", snapshots);
        WriteReportFile("report-365.txt", "365-tick", state, snapshots, violations);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Run for 5 in-game years (1825 ticks) — longer stability test.
    /// </summary>
    [Fact]
    public void FullCaribbean_5Years_InvariantsHold()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        var snapshots = new List<TelemetrySnapshot>();

        for (int tick = 1; tick <= 1825; tick++)
        {
            engine.Tick();
            if (tick % 50 == 0)
                snapshots.Add(TelemetrySnapshot.Capture(state, tick));
        }

        var violations = InvariantValidator.Validate(state);

        WriteSummary("5-year", state, snapshots, violations);
        WriteCsvFile("telemetry-5year.csv", snapshots);
        WriteReportFile("report-5year.txt", "5-year", state, snapshots, violations);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Run with telemetry export — write CSV for graphing.
    /// Also writes to a file at tests/Ahoy.Simulation.Tests/telemetry-500.csv
    /// </summary>
    [Fact]
    public void FullCaribbean_TelemetryExport_500Ticks()
    {
        var state = WorldFactory.Create(new CaribbeanWorldDefinition());
        var engine = Simulation.Engine.SimulationEngine.BuildEngine(state, rng: new Random(42));

        var snapshots = new List<TelemetrySnapshot>();

        for (int tick = 1; tick <= 500; tick++)
        {
            engine.Tick();
            if (tick % 5 == 0)
                snapshots.Add(TelemetrySnapshot.Capture(state, tick));
        }

        // Write CSV to test output
        _output.WriteLine(TelemetrySnapshot.CsvHeader);
        foreach (var snap in snapshots)
            _output.WriteLine(snap.ToCsvLine());

        // Also write to file for external tooling
        WriteCsvFile("telemetry-500.csv", snapshots);

        var violations = InvariantValidator.Validate(state);
        WriteSummary("500-tick", state, snapshots, violations);
        WriteReportFile("report-500.txt", "500-tick", state, snapshots, violations);

        // Basic sanity: world should still be alive
        var finalSnap = TelemetrySnapshot.Capture(state, 500);
        // TODO: Tighten once Group 9 Batch B relief pipeline is complete
        Assert.True(finalSnap.AvgPortProsperity > 3f,
            $"Economy collapsed: avg prosperity {finalSnap.AvgPortProsperity:F1}");
        Assert.True(finalSnap.ActiveShips > 0, "Ships should still exist");
        Assert.True(finalSnap.AliveIndividuals > 0, "NPCs should still be alive");
    }

    // ---- Diagnostics ----

    private void WriteSummary(string label, WorldState state,
        List<TelemetrySnapshot> snapshots, List<string> violations)
    {
        // Write to both xUnit output (visible in test results) and Console.Error
        // (visible in terminal regardless of test runner verbosity)
        void Log(string msg) { _output.WriteLine(msg); Console.Error.WriteLine(msg); }

        Log($"\n=== {label} Summary ===");

        if (snapshots.Count >= 2)
        {
            var first = snapshots[0];
            var last = snapshots[^1];

            Log($"Ticks: {first.Tick} → {last.Tick}");
            Log($"Avg Prosperity: {first.AvgPortProsperity:F1} → {last.AvgPortProsperity:F1}");
            Log($"Total Gold: {first.TotalGoldInEconomy:N0} → {last.TotalGoldInEconomy:N0}");
            Log($"Ships At Sea: {first.ShipsAtSea} → {last.ShipsAtSea}");
            Log($"Alive NPCs: {first.AliveIndividuals} → {last.AliveIndividuals}");
            Log($"Knowledge Facts: {first.TotalKnowledgeFacts} → {last.TotalKnowledgeFacts}");
            Log($"Active Pursuits: {first.ActiveNpcPursuits} → {last.ActiveNpcPursuits}");
            Log($"Active Bounties: {first.ActiveBounties} → {last.ActiveBounties}");
            Log($"Conflicts: {first.ActiveConflicts} → {last.ActiveConflicts}");
            Log($"Epidemics: {first.ActiveEpidemics} → {last.ActiveEpidemics}");
            Log($"Wars: {first.ActiveWars} → {last.ActiveWars}");
        }

        // Port details
        Log("\nPort Status:");
        foreach (var port in state.Ports.Values.OrderByDescending(p => p.Population))
        {
            var food = port.Economy.Supply.GetValueOrDefault(TradeGood.Food);
            var foodTarget = port.Economy.TargetSupply.GetValueOrDefault(TradeGood.Food);
            var conditions = port.Conditions != PortConditionFlags.None ? $" [{port.Conditions}]" : "";
            Log($"  {port.Name,-20} pop={port.Population,6} prosperity={port.Prosperity,5:F1} " +
                $"food={food,4}/{foodTarget,3}{conditions}");
        }

        // Faction details
        Log("\nFaction Status:");
        foreach (var faction in state.Factions.Values)
        {
            var wars = faction.AtWarWith.Count > 0 ? $" AT WAR×{faction.AtWarWith.Count}" : "";
            Log($"  {faction.Name,-15} treasury={faction.TreasuryGold,8:N0} " +
                $"naval={faction.NavalStrength,3} ports={faction.ControlledPorts.Count}{wars}");
        }

        // Violations
        if (violations.Count > 0)
        {
            Log($"\n{violations.Count} VIOLATIONS:");
            foreach (var v in violations)
                Log($"  !! {v}");
        }
        else
        {
            Log("\nAll invariants passed.");
        }
    }

    private void WriteCsvFile(string filename, List<TelemetrySnapshot> snapshots)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var path = Path.Combine(dir, filename);
            using var writer = new StreamWriter(path);
            writer.WriteLine(TelemetrySnapshot.CsvHeader);
            foreach (var snap in snapshots)
                writer.WriteLine(snap.ToCsvLine());
            _output.WriteLine($"[Telemetry CSV written to {path}]");
        }
        catch
        {
            // File write failure is not a test failure
        }
    }

    private void WriteReportFile(string filename, string label, WorldState state,
        List<TelemetrySnapshot> snapshots, List<string> violations)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var path = Path.Combine(dir, filename);
            using var writer = new StreamWriter(path);
            void W(string s) => writer.WriteLine(s);

            W($"=== {label} Summary ===");

            if (snapshots.Count >= 2)
            {
                var first = snapshots[0];
                var last = snapshots[^1];
                W($"Ticks: {first.Tick} -> {last.Tick}");
                W($"Avg Prosperity: {first.AvgPortProsperity:F1} -> {last.AvgPortProsperity:F1}");
                W($"Total Gold: {first.TotalGoldInEconomy:N0} -> {last.TotalGoldInEconomy:N0}");
                W($"Ships At Sea: {first.ShipsAtSea} -> {last.ShipsAtSea}");
                W($"Alive NPCs: {first.AliveIndividuals} -> {last.AliveIndividuals}");
                W($"Knowledge Facts: {first.TotalKnowledgeFacts} -> {last.TotalKnowledgeFacts}");
                W($"Bounties: {first.ActiveBounties} -> {last.ActiveBounties}");
                W($"Epidemics: {first.ActiveEpidemics} -> {last.ActiveEpidemics}");
                W($"Wars: {first.ActiveWars} -> {last.ActiveWars}");
            }

            W("\nPort Status:");
            foreach (var port in state.Ports.Values.OrderByDescending(p => p.Population))
            {
                var food = port.Economy.Supply.GetValueOrDefault(TradeGood.Food);
                var target = port.Economy.TargetSupply.GetValueOrDefault(TradeGood.Food);
                var cond = port.Conditions != PortConditionFlags.None ? $" [{port.Conditions}]" : "";
                W($"  {port.Name,-20} pop={port.Population,6} prosperity={port.Prosperity,5:F1} food={food,4}/{target,3}{cond}");
            }

            W("\nFaction Status:");
            foreach (var f in state.Factions.Values)
            {
                var wars = f.AtWarWith.Count > 0 ? $" AT WAR x{f.AtWarWith.Count}" : "";
                W($"  {f.Name,-15} treasury={f.TreasuryGold,8:N0} naval={f.NavalStrength,3}{wars}");
            }

            if (violations.Count > 0)
            {
                W($"\n{violations.Count} VIOLATIONS:");
                foreach (var v in violations) W($"  !! {v}");
            }
            else W("\nAll invariants passed.");

            _output.WriteLine($"[Report written to {path}]");
        }
        catch { }
    }
}
