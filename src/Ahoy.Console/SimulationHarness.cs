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

                case "pois":
                    PrintPois();
                    break;

                case "fleet":
                    PrintFleet();
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
                ShipDestroyed sdes         => $"💥 {ShipName(sdes.ShipId)} destroyed"
                    + (sdes.AttackerId.HasValue ? $" (by {ShipName(sdes.AttackerId.Value)})" : " (lost at sea)"),
                IndividualDied id          => $"✝  {IndividualName(id.IndividualId)} has died ({id.Cause})",
                GovernorChanged gc         => gc.NewGovernor.HasValue
                    ? $"🏛  {PortName(gc.PortId)}: new governor {IndividualName(gc.NewGovernor.Value)}"
                    : $"🏛  {PortName(gc.PortId)}: governor vacancy",
                RumourSpread rs            => $"💬 Rumour at {PortName(rs.PortId)}: {rs.Rumour}",
                BribeAccepted ba           => $"🤑 Bribe accepted by {IndividualName(ba.GovernorId)} at {PortName(ba.PortId)} ({ba.GoldAmount}g)",
                BribeRejected br           => $"😤 Bribe rejected by {IndividualName(br.GovernorId)} at {PortName(br.PortId)}",
                AgentBurned ab             => $"🔥 Agent burned: {IndividualName(ab.AgentId)}",
                QuestActivated qa          => $"📜 Quest available: {qa.Title}",
                QuestResolved qr           => qr.Status == Ahoy.Simulation.Quests.ContractQuestStatus.Fulfilled
                    ? $"📜 Contract fulfilled: {qr.Title}"
                    : $"📜 Contract {qr.Status}: {qr.Title}",
                ContractFulfilled cf       => $"💰 Contract paid: {cf.GoldPaid}g for {cf.TargetSubjectKey}",
                KnowledgeConflictDetected kcd => $"⚠  Knowledge conflict: {kcd.SubjectKey}",
                DeceptionExposed de        => $"🔍 Deception exposed (faction: {FactionName(de.DeceivingFactionId)})",
                PoiDiscovered pd           => $"🗺  POI discovered: {PoiName(pd.PoiId)} (by {ShipName(pd.DiscoveredBy)})",
                PoiEncountered pe          => pe.GoldFound > 0
                    ? $"💎 POI loot: {pe.GoldFound}g from {PoiName(pe.PoiId)}"
                    : pe.HullDamage > 0
                        ? $"💥 POI hazard at {PoiName(pe.PoiId)}: -{pe.HullDamage:P0} hull"
                        : $"⚓ POI visited: {PoiName(pe.PoiId)}",
                FleetDeparted fd           => $"⚓ Fleet departed {PortName(fd.From)} → {PortName(fd.Destination)} ({fd.Ships.Count} ships)",
                FleetArrived fa            => $"⚓ Fleet arrived at {PortName(fa.At)} ({fa.Ships.Count} ships)",
                AllegianceRevealed ar      => $"🕵 Allegiance revealed: {IndividualName(ar.Individual)} was {FactionName(ar.ClaimedFaction)} → actually serves {FactionName(ar.ActualFaction)}",
                _                          => null,   // suppress other noise
            };

            if (desc is null) continue;

            System.Console.WriteLine($"{prefix}{desc}");
        }
    }

    private string PoiName(Ahoy.Core.Ids.OceanPoiId id) =>
        _engine.State.OceanPois.TryGetValue(id, out var poi) ? poi.Name : id.ToString();

    private string PortName(Ahoy.Core.Ids.PortId id) =>
        _engine.State.Ports.TryGetValue(id, out var p) ? p.Name : id.ToString();

    private string ShipName(Ahoy.Core.Ids.ShipId id) =>
        _engine.State.Ships.TryGetValue(id, out var s) ? s.Name : id.ToString();

    private string IndividualName(Ahoy.Core.Ids.IndividualId id) =>
        _engine.State.Individuals.TryGetValue(id, out var ind) ? ind.FullName : id.ToString();

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
        var active = _engine.State.Quests.ActiveContractQuests;
        System.Console.WriteLine();

        if (active.Count == 0)
        {
            System.Console.WriteLine("  No active contract quests.");
            return;
        }

        System.Console.WriteLine($"  Active contract quests: {active.Count}");

        foreach (var (q, i) in active.Select((q, i) => (q, i + 1)))
        {
            var target = q.Contract.TargetSubjectKey;
            var reward = q.Contract.GoldReward;
            var status = q.Status;
            System.Console.WriteLine();
            System.Console.WriteLine($"  [{i}] {target}  ({q.Id})");
            System.Console.WriteLine($"     Condition : {q.Contract.Condition}");
            if (q.Contract.Condition == ContractConditionType.GoodsDelivered)
                System.Console.WriteLine($"     Deliver   : 20t Food → {q.Contract.TargetSubjectKey}");
            System.Console.WriteLine($"     Reward    : {reward}g");
            System.Console.WriteLine($"     Status    : {status}");
            System.Console.WriteLine($"     Activated : {q.ActivatedDate}");
            System.Console.WriteLine($"     Archetype : {q.Contract.Archetype}");
        }
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
            var corr = fact.CorroborationCount > 0 ? $"  corr:{fact.CorroborationCount}" : "";
            var src = FormatSourceHolder(fact.SourceHolder);
            var infiltratorNote = fact.Claim is Ahoy.Simulation.State.IndividualAllegianceClaim iac && iac.ActualFaction.HasValue
                ? $"  [claims:{FactionName(iac.ClaimedFaction)} actual:{FactionName(iac.ActualFaction.Value)}]" : "";
            System.Console.WriteLine($"  {conf,7}  {claim,-22}  {age}  hops:{fact.HopCount}{corr}  src:{src}{infiltratorNote}");
        }

        var disinfoFacts = facts.Where(f => f.IsDisinformation && !f.IsSuperseded).ToList();
        if (disinfoFacts.Count > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"  [DISINFORMATION in player knowledge: {disinfoFacts.Count}]");
            foreach (var df in disinfoFacts.OrderByDescending(f => f.Confidence))
            {
                var dConf  = $"[{df.Confidence:P0}]";
                var dClaim = df.Claim.GetType().Name.Replace("Claim", "");
                var dSrc   = FormatSourceHolder(df.SourceHolder);
                var dSubKey = Ahoy.Simulation.State.KnowledgeFact.GetSubjectKey(df.Claim);
                System.Console.WriteLine($"    {dConf,7}  {dClaim,-22}  key:{dSubKey}  src:{dSrc}");
            }
        }

        var player = new Ahoy.Simulation.State.PlayerHolder();
        var conflicts = s.Knowledge.GetConflicts(player).ToList();
        if (conflicts.Count > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"  Conflicts: {conflicts.Count}");
            foreach (var conflict in conflicts)
            {
                var spread = $"{conflict.ConfidenceSpread:P0}";
                System.Console.WriteLine($"  [CONFLICT] {conflict.SubjectKey} — {conflict.CompetingFacts.Count} competing claims (spread: {spread})");
                foreach (var cf in conflict.CompetingFacts.OrderByDescending(f => f.Confidence))
                {
                    var marker = cf == conflict.DominantFact ? "dominant" : "competing";
                    var src = FormatSourceHolder(cf.SourceHolder);
                    System.Console.WriteLine($"    [{cf.Confidence:P0}] {cf.Claim.GetType().Name.Replace("Claim", "")}  hops:{cf.HopCount}  src:{src}  ({marker})");
                }
            }
        }
    }

    private void PrintPois()
    {
        var s = _engine.State;
        var playerFacts = s.Knowledge.GetFacts(new Ahoy.Simulation.State.PlayerHolder());
        var knownPois = playerFacts
            .Where(f => !f.IsSuperseded && f.Claim is Ahoy.Simulation.State.OceanPoiClaim)
            .Select(f => (Fact: f, Claim: (Ahoy.Simulation.State.OceanPoiClaim)f.Claim))
            .ToList();

        System.Console.WriteLine();
        if (knownPois.Count == 0)
        {
            System.Console.WriteLine("  No POIs in player knowledge.");
            return;
        }
        System.Console.WriteLine($"  Known POIs: {knownPois.Count}");
        System.Console.WriteLine($"  {"Name",-26} {"Type",-14} {"Region",-18} {"Cache",-18} {"Conf"}");
        System.Console.WriteLine($"  {new string('-', 82)}");
        foreach (var (fact, claim) in knownPois)
        {
            var poiName = s.OceanPois.TryGetValue(claim.Poi, out var poi) ? poi.Name : claim.Poi.ToString();
            var regionName = s.Regions.TryGetValue(claim.Region, out var reg) ? reg.Name : "?";
            var cacheStr = claim.Type is Ahoy.Core.Enums.PoiType.Shipwreck
                                      or Ahoy.Core.Enums.PoiType.PirateCache
                ? claim.CacheStatus.ToString()
                : "—";
            System.Console.WriteLine(
                $"  {poiName,-26} {claim.Type,-14} {regionName,-18} {cacheStr,-18} {fact.Confidence:P0}");
        }
    }

    private void PrintFleet()
    {
        var s = _engine.State;
        System.Console.WriteLine();
        System.Console.WriteLine($"  Fleet ({s.Player.FleetIds.Count} ships)  — Gold: {s.Player.PersonalGold:N0}g");
        System.Console.WriteLine($"  {"Ship",-22} {"Class",-14} {"Hull",5} {"Location",-28} {"Cargo"}");
        System.Console.WriteLine($"  {new string('-', 85)}");

        foreach (var shipId in s.Player.FleetIds)
        {
            if (!s.Ships.TryGetValue(shipId, out var ship)) continue;
            var flag = ship.IsPlayerShip ? "★ " : "  ";
            var hull = $"{ship.HullIntegrity:P0}";
            var loc = ship.Location switch
            {
                AtPort ap when s.Ports.TryGetValue(ap.Port, out var p) => $"@ {p.Name}",
                AtSea aas when s.Regions.TryGetValue(aas.Region, out var r) => $"~ {r.Name}",
                EnRoute er => $"→ {s.Regions.GetValueOrDefault(er.To)?.Name ?? "?"}  ({er.Progress:P0})",
                Ahoy.Simulation.State.AtPoi atPoi when s.OceanPois.TryGetValue(atPoi.Poi, out var p2) => $"[POI] {p2.Name}",
                _ => "unknown",
            };
            var cargo = ship.Cargo.Count == 0 ? "empty"
                : string.Join(", ", ship.Cargo.Select(kv => $"{kv.Value}t {kv.Key}"));
            var convoy = ship.ConvoyId.HasValue ? " [convoy]" : "";
            System.Console.WriteLine($"{flag}{ship.Name,-22} {ship.Class,-14} {hull,5} {loc,-28} {cargo}{convoy}");
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
        System.Console.WriteLine("    quests        — active contract quests");
        System.Console.WriteLine("    pois          — known ocean points of interest");
        System.Console.WriteLine("    fleet         — player fleet status");
        System.Console.WriteLine("    help          — this message");
        System.Console.WriteLine("    quit          — exit");
    }

    private string FormatSourceHolder(Ahoy.Simulation.State.KnowledgeHolderId? holder) =>
        holder switch
        {
            null => "witnessed",
            Ahoy.Simulation.State.PortHolder ph =>
                "port:" + (_engine.State.Ports.TryGetValue(ph.Port, out var p) ? p.Name : ph.Port.ToString()),
            Ahoy.Simulation.State.ShipHolder sh =>
                "ship:" + (_engine.State.Ships.TryGetValue(sh.Ship, out var s) ? s.Name : sh.Ship.ToString()),
            Ahoy.Simulation.State.FactionHolder fh =>
                "faction:" + (_engine.State.Factions.TryGetValue(fh.Faction, out var f) ? f.Name : fh.Faction.ToString()),
            Ahoy.Simulation.State.PlayerHolder => "player",
            _ => holder.GetType().Name,
        };

    private static string ProsperityBar(float value)
    {
        var filled = (int)(value / 10f);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }
}
