using Ahoy.Core.Enums;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Console;

/// <summary>
/// Text-mode observation harness for the simulation.
/// Provides a simple REPL: tick the world, observe output, issue commands.
///
/// Commands:
///   tick [n]       — advance n ticks (default 1)
///   run [n]        — advance n ticks silently then print summary
///   world          — print full world state summary
///   ports          — list all ports with prosperity
///   factions       — list faction treasury + naval strength
///   ships          — list all ships and their locations
///   help           — show this list
///   quit           — exit
/// </summary>
public sealed class SimulationHarness
{
    private readonly SimulationEngine _engine;
    private readonly List<WorldEvent> _recentEvents = new();

    public SimulationHarness(SimulationEngine engine)
    {
        _engine = engine;
        _engine.EventOccurred += ev => _recentEvents.Add(ev);
    }

    public void Run()
    {
        PrintHeader();
        PrintWorldSummary();

        while (true)
        {
            System.Console.Write("\n> ");
            var line = System.Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "tick":
                    var tickCount = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
                    TickN(tickCount, verbose: true);
                    break;

                case "run":
                    var runCount = parts.Length > 1 && int.TryParse(parts[1], out var rn) ? rn : 30;
                    TickN(runCount, verbose: false);
                    PrintWorldSummary();
                    break;

                case "world":
                    PrintWorldSummary();
                    break;

                case "ports":
                    PrintPorts();
                    break;

                case "factions":
                    PrintFactions();
                    break;

                case "ships":
                    PrintShips();
                    break;

                case "weather":
                    PrintWeather();
                    break;

                case "knowledge":
                    PrintPlayerKnowledge();
                    break;

                case "quests":
                    PrintQuests();
                    break;

                case "choose" when parts.Length == 3:
                    // choose <n|questPrefix> <branchId>
                    ChooseBranch(parts[1], parts[2]);
                    break;

                case "choose" when parts.Length == 2:
                    // choose <branchId>  — shorthand when only one quest is active
                    var onlyQuest = _engine.State.Quests.ActiveQuests;
                    if (onlyQuest.Count == 1)
                        ChooseBranch(onlyQuest[0].Id.ToString(), parts[1]);
                    else if (onlyQuest.Count == 0)
                        System.Console.WriteLine("  No active quests.");
                    else
                        System.Console.WriteLine("  Multiple active quests — use: choose <n> <branch>  (n = 1, 2, ...)");
                    break;

                case "help":
                    PrintHelp();
                    break;

                case "quit" or "exit" or "q":
                    System.Console.WriteLine("Fair winds, Captain.");
                    return;

                default:
                    System.Console.WriteLine($"Unknown command '{cmd}'. Type 'help' for a list.");
                    break;
            }
        }
    }

    private void TickN(int count, bool verbose)
    {
        for (var i = 0; i < count; i++)
        {
            _recentEvents.Clear();
            _engine.Tick();

            var state = _engine.State;
            var snap = $"[{state.Date}]";

            if (verbose)
            {
                System.Console.WriteLine();
                System.Console.WriteLine(snap);
                PrintTickEvents(_recentEvents);
            }
            else if (i % 10 == 9)
            {
                System.Console.Write($"{snap} ");
            }
        }

        if (!verbose) System.Console.WriteLine();
    }

    private void PrintTickEvents(IReadOnlyList<WorldEvent> events)
    {
        if (events.Count == 0)
        {
            System.Console.WriteLine("  (no notable events)");
            return;
        }

        foreach (var ev in events)
        {
            var lod = ev.SourceLod;
            var prefix = lod switch
            {
                SimulationLod.Local    => "  [LOCAL]    ",
                SimulationLod.Regional => "  [REGIONAL] ",
                SimulationLod.Distant  => "  [DISTANT]  ",
                _ => "  [?]        ",
            };

            // Suppress noisy price-shift spam unless Local
            if (ev is PriceShifted && ev.SourceLod != SimulationLod.Local) continue;

            var desc = ev switch
            {
                ShipArrived sa             => $"🚢 {PortName(sa.PortId)}: {ShipName(sa.ShipId)} arrived",
                ShipDeparted sdep          => $"🚢 {PortName(sdep.FromPort)}: {ShipName(sdep.ShipId)} departed",
                StormFormed sf             => $"⛈  Storm formed in {RegionName(sf.RegionId)}",
                StormDissipated sdis       => $"☀  Storm dissipated in {RegionName(sdis.RegionId)}",
                StormPropagated sp         => $"⛈  Storm spread to {RegionName(sp.ToRegion)}",
                WindShifted ws             => $"💨 Wind shifted → {ws.NewDirection} {ws.NewStrength}",
                TradeCompleted tc          => tc.IsBuying
                    ? $"📦 Merchant bought {tc.Quantity}t {tc.Good} @ {tc.PricePerUnit}g"
                    : $"📦 Merchant sold  {tc.Quantity}t {tc.Good} @ {tc.PricePerUnit}g",
                PriceShifted ps            => $"💰 {PortName(ps.PortId)}: {ps.Good} {ps.OldPrice}→{ps.NewPrice}g",
                PortProsperityChanged pc   => $"{FormatProsperityChange(pc.OldValue, pc.NewValue)} {PortName(pc.PortId)} prosperity {pc.OldValue:F0}→{pc.NewValue:F0}",
                FactionRelationshipChanged frc => $"🤝 {FactionName(frc.FactionA)} ↔ {FactionName(frc.FactionB)} relationship changed",
                PortCaptured cap           => $"⚔  {PortName(cap.PortId)} captured by {FactionName(cap.NewFaction)}!",
                ShipDestroyed sdes         => $"💥 {ShipName(sdes.ShipId)} destroyed",
                QuestActivated qa          => $"📜 Quest available: {qa.Title}",
                QuestResolved qr           => qr.Status == Ahoy.Simulation.Quests.QuestStatus.Completed
                    ? $"📜 Quest resolved ({qr.ChosenBranchId}): {qr.Title}"
                    : $"📜 Quest expired: {qr.Title}",
                _                          => null,   // suppress other noise
            };

            if (desc is null) continue;

            System.Console.WriteLine($"{prefix}{desc}");
        }
    }

    private string PortName(Ahoy.Core.Ids.PortId id) =>
        _engine.State.Ports.TryGetValue(id, out var p) ? p.Name : id.ToString();

    private string ShipName(Ahoy.Core.Ids.ShipId id) =>
        _engine.State.Ships.TryGetValue(id, out var s) ? s.Name : id.ToString();

    private string FactionName(Ahoy.Core.Ids.FactionId id) =>
        _engine.State.Factions.TryGetValue(id, out var f) ? f.Name : id.ToString();

    private string RegionName(Ahoy.Core.Ids.RegionId id) =>
        _engine.State.Regions.TryGetValue(id, out var r) ? r.Name : id.ToString();

    private static string FormatProsperityChange(float old, float next) =>
        next > old ? "▲" : "▼";

    // ---- Summary printers ----

    private void PrintHeader()
    {
        try { System.Console.Clear(); } catch { /* piped stdin — skip clear */ }
        var sep = new string('═', 60);
        System.Console.WriteLine(sep);
        System.Console.WriteLine("  A H O Y  —  Simulation Harness");
        System.Console.WriteLine(sep);
    }

    private void PrintWorldSummary()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  Date      : {s.Date}");
        System.Console.WriteLine($"  Captain   : {s.Player.CaptainName}");
        System.Console.WriteLine($"  Gold      : {s.Player.PersonalGold:N0}g");
        System.Console.WriteLine($"  Notoriety : {s.Player.Notoriety:F0}");
        System.Console.WriteLine($"  Ships     : {s.Ships.Count} total, {s.Ships.Values.Count(sh => sh.IsPlayerShip)} player");
        System.Console.WriteLine($"  Ports     : {s.Ports.Count}");
        System.Console.WriteLine($"  Regions   : {s.Regions.Count}");
        System.Console.WriteLine($"  Factions  : {s.Factions.Count}");
    }

    private void PrintPorts()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  {"Port",-22} {"Region",-20} {"Faction",-14} {"Prosperity",10}");
        System.Console.WriteLine($"  {new string('-', 68)}");

        foreach (var (_, region) in s.Regions.OrderBy(kv => kv.Value.Name))
        {
            foreach (var portId in region.Ports)
            {
                var port = s.Ports[portId];
                var faction = port.ControllingFactionId.HasValue
                    ? s.Factions[port.ControllingFactionId.Value].Name : "Neutral";
                var bar = ProsperityBar(port.Prosperity);
                System.Console.WriteLine($"  {port.Name,-22} {region.Name,-20} {faction,-14} {bar} {port.Prosperity,4:F0}");
            }
        }
    }

    private void PrintFactions()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  {"Faction",-28} {"Type",-12} {"Treasury",10} {"Naval",6} {"Cohesion",9}");
        System.Console.WriteLine($"  {new string('-', 68)}");

        foreach (var (_, f) in s.Factions.OrderBy(kv => kv.Value.Name))
        {
            var cohesion = f.Type == FactionType.PirateBrotherhood ? $"{f.Cohesion,4:F0}" : "  —  ";
            System.Console.WriteLine($"  {f.Name,-28} {f.Type,-12} {f.TreasuryGold,9:N0}g {f.NavalStrength,5}  {cohesion}");
        }
    }

    private void PrintShips()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  {"Ship",-22} {"Class",-14} {"Location",-30} {"Cargo"}");
        System.Console.WriteLine($"  {new string('-', 75)}");

        foreach (var (_, ship) in s.Ships.OrderBy(kv => kv.Value.IsPlayerShip ? 0 : 1))
        {
            var player = ship.IsPlayerShip ? "★ " : "  ";
            var loc = ship.Location switch
            {
                AtPort ap when s.Ports.TryGetValue(ap.Port, out var p) => $"@ {p.Name}",
                AtSea aas when s.Regions.TryGetValue(aas.Region, out var r) => $"~ {r.Name}",
                EnRoute er => $"→ {s.Regions.GetValueOrDefault(er.To)?.Name ?? "?"}  ({er.Progress:P0})",
                _ => "unknown",
            };
            var cargo = ship.Cargo.Count == 0 ? "empty"
                : string.Join(", ", ship.Cargo.Select(kv => $"{kv.Value}t {kv.Key}"));
            System.Console.WriteLine($"{player}{ship.Name,-22} {ship.Class,-14} {loc,-30} {cargo}");
        }
    }

    private void PrintWeather()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  {"Region",-22} {"Wind",-10} {"Direction",-12} {"Storm",-14} {"Visibility"}");
        System.Console.WriteLine($"  {new string('-', 65)}");

        foreach (var (regionId, w) in s.Weather)
        {
            var regionName = s.Regions.TryGetValue(regionId, out var reg) ? reg.Name : "?";
            System.Console.WriteLine($"  {regionName,-22} {w.WindStrength,-10} {w.WindDirection,-12} {w.StormPresence,-14} {w.Visibility}");
        }
    }

    private void PrintQuests()
    {
        var active = _engine.State.Quests.ActiveQuests;
        System.Console.WriteLine();

        if (active.Count == 0)
        {
            System.Console.WriteLine("  No active quests.");
            return;
        }

        System.Console.WriteLine($"  Active quests: {active.Count}");

        foreach (var (q, i) in active.Select((q, i) => (q, i + 1)))
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"  [{i}] {q.Template.Title}  ({q.Id})");
            System.Console.WriteLine($"     {q.DisplayDialogue}");
            System.Console.WriteLine($"     NPC: {q.DisplayNpcName}   Activated: {q.ActivatedDate}");

            if (q.TriggerFacts.Count > 0)
            {
                System.Console.WriteLine("     Trigger facts:");
                foreach (var f in q.TriggerFacts)
                    System.Console.WriteLine($"       [{f.Confidence:P0}] {f.Claim.GetType().Name.Replace("Claim", "")}  hops:{f.HopCount}");
            }

            System.Console.WriteLine("     Branches:");
            foreach (var b in q.Template.Branches)
                System.Console.WriteLine($"       choose {q.Id} {b.BranchId,-18}  {b.Label}");
        }
    }

    private void ChooseBranch(string questPrefix, string branchId)
    {
        var active = _engine.State.Quests.ActiveQuests;

        // Numeric index: "1" = first active quest, "2" = second, etc.
        var quest = int.TryParse(questPrefix, out var idx) && idx >= 1 && idx <= active.Count
            ? active[idx - 1]
            : active.FirstOrDefault(q => q.Id.ToString().Contains(questPrefix, StringComparison.OrdinalIgnoreCase));

        if (quest is null)
        {
            System.Console.WriteLine($"  No active quest matching '{questPrefix}'.");
            return;
        }

        var branch = quest.Template.Branches.FirstOrDefault(b =>
            b.BranchId.Equals(branchId, StringComparison.OrdinalIgnoreCase));

        if (branch is null)
        {
            System.Console.WriteLine($"  Unknown branch '{branchId}' for quest '{quest.Template.Title}'.");
            return;
        }

        _engine.EnqueueCommand(new Ahoy.Simulation.Engine.ChooseQuestBranchCommand(quest.Id, branch.BranchId));
        System.Console.WriteLine($"  Choice queued — '{branch.Label}' will resolve next tick.");
    }

    private void PrintPlayerKnowledge()
    {
        var s = _engine.State;
        var facts = s.Knowledge.GetFacts(new Ahoy.Simulation.State.PlayerHolder());
        System.Console.WriteLine();
        System.Console.WriteLine($"  Player knowledge: {facts.Count} facts");
        System.Console.WriteLine();

        foreach (var fact in facts.OrderByDescending(f => f.Confidence))
        {
            var age = $"(observed {fact.ObservedDate})";
            var conf = $"[{fact.Confidence:P0}]";
            var claim = fact.Claim.GetType().Name.Replace("Claim", "");
            System.Console.WriteLine($"  {conf,7}  {claim,-22}  {age}  hops:{fact.HopCount}");
        }
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine();
        System.Console.WriteLine("  Commands:");
        System.Console.WriteLine("    tick [n]      — advance n ticks (default 1), print events");
        System.Console.WriteLine("    run [n]       — advance n ticks silently, print summary");
        System.Console.WriteLine("    world         — world summary");
        System.Console.WriteLine("    ports         — port list with prosperity");
        System.Console.WriteLine("    factions      — faction treasury + naval");
        System.Console.WriteLine("    ships         — all ship positions");
        System.Console.WriteLine("    weather       — weather by region");
        System.Console.WriteLine("    knowledge     — player's current knowledge facts");
        System.Console.WriteLine("    quests        — active quests and branches");
        System.Console.WriteLine("    choose <branch>      — choose branch (when one quest active)");
        System.Console.WriteLine("    choose <n> <branch>  — choose branch of quest n (1, 2, ...)");
        System.Console.WriteLine("    help          — this message");
        System.Console.WriteLine("    quit          — exit");
    }

    private static string ProsperityBar(float value)
    {
        var filled = (int)(value / 10f);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }
}
