using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 2 — runs after WeatherSystem.
/// Advances ship positions along routes, applies wind speed modifiers,
/// sets Ship.ArrivedThisTick for ships completing transit this tick.
/// NPC merchant routing decisions at Regional/Distant LOD are also resolved here.
/// </summary>
public sealed class ShipMovementSystem : IWorldSystem
{
    private readonly Random _rng;

    // Wind speed modifier matrix: how much wind direction helps/hurts
    // relative to the route heading. 1.0 = neutral; <1 = slower; >1 = faster.
    private static readonly Dictionary<int, float> _windModifiers = new()
    {
        [0]   = 1.30f,  // Directly behind — fast
        [45]  = 1.15f,
        [90]  = 1.00f,  // Beam reach — neutral
        [135] = 0.85f,
        [180] = 0.60f,  // Into the wind — slow
        [225] = 0.85f,
        [270] = 1.00f,
        [315] = 1.15f,
    };

    public ShipMovementSystem(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        // Clear last tick's arrival flags
        foreach (var ship in state.Ships.Values)
            ship.ArrivedThisTick = false;

        foreach (var ship in state.Ships.Values)
        {
            var lod = GetShipLod(ship, state, context);

            // Crew upkeep applies every tick regardless of movement state
            if (!ship.IsPlayerShip)
                DeductCrewUpkeep(ship, state);

            switch (ship.Location)
            {
                case AtPort atPort:
                    TryDepartPort(ship, atPort, state, events, lod);
                    break;

                case EnRoute enRoute:
                    AdvanceShip(ship, enRoute, state, events, lod);
                    break;

                case AtSea atSea:
                    // Ships at sea without a route pick one (NPC logic)
                    if (!ship.IsPlayerShip)
                        AssignNpcRoute(ship, atSea.Region, state);
                    break;

                case AtPoi atPoi:
                    ExplorePoiTick(ship, atPoi, state, events, lod);
                    break;
            }
        }
    }

    private void TryDepartPort(Ship ship, AtPort atPort, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        // Track how long this ship has been docked
        ship.TicksDockedAtCurrentPort++;

        if (!state.Ports.TryGetValue(atPort.Port, out var currentPort)) return;
        var fromRegion = currentPort.RegionId;

        // Handle route based on ShipRoute union type
        switch (ship.Route)
        {
            case PoiRoute poiRoute
                when state.OceanPois.TryGetValue(poiRoute.Poi, out var poiDest):
            {
                if (fromRegion == poiDest.RegionId)
                {
                    events.Emit(new ShipDeparted(state.Date, lod, ship.Id, atPort.Port), lod);
                    ship.Location = new AtPoi(poiRoute.Poi, poiDest.RegionId);
                }
                else
                {
                    DepartTowardRegion(ship, fromRegion, poiDest.RegionId, atPort.Port, state, events, lod);
                }
                return;
            }

            case PursuitRoute pursuitRoute:
            {
                // Navigate toward the target's last known region
                DepartTowardRegion(ship, fromRegion, pursuitRoute.LastKnownRegion, atPort.Port, state, events, lod);
                return;
            }

            case PortRoute portRoute
                when state.Ports.TryGetValue(portRoute.Destination, out var destPort):
            {
                if (fromRegion != destPort.RegionId)
                    DepartTowardRegion(ship, fromRegion, destPort.RegionId, atPort.Port, state, events, lod);
                return;
            }

            case null:
            {
                // Player ship — only departs via PlayerCommand
                if (ship.IsPlayerShip) return;

                // NPC ship with no route — assign one if knowledge or docked too long
                if (ShouldDepartPort(ship, state))
                    AssignNpcRoute(ship, fromRegion, state);

                // If route was just assigned, try to depart on this same tick
                if (ship.Route is PortRoute newPortRoute
                    && state.Ports.TryGetValue(newPortRoute.Destination, out var newDest)
                    && fromRegion != newDest.RegionId)
                {
                    DepartTowardRegion(ship, fromRegion, newDest.RegionId, atPort.Port, state, events, lod);
                }
                return;
            }

            default:
                return;
        }
    }

    /// <summary>Depart from port toward a target region (first hop of BFS path).</summary>
    private void DepartTowardRegion(Ship ship, RegionId from, RegionId to, PortId departPort,
        WorldState state, IEventEmitter events, SimulationLod lod)
    {
        if (from == to) return;
        var nextRegion = FindNextRegion(from, to, state);
        if (!nextRegion.HasValue) return;
        var travelDays = ship.ConvoyId.HasValue
            ? CalculateConvoyTravelDays(ship.ConvoyId.Value, from, nextRegion.Value, state)
            : GetTravelDays(from, nextRegion.Value, state);
        ship.Location = new EnRoute(from, nextRegion.Value, 0f, travelDays);
        events.Emit(new ShipDeparted(state.Date, lod, ship.Id, departPort), lod);
    }

    private void AdvanceShip(Ship ship, EnRoute route, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        var weather = state.Weather.TryGetValue(route.From, out var w) ? w : null;
        var windMod = weather is not null
            ? GetWindModifier(weather.WindDirection, route.From, route.To, state)
            : 1.0f;

        // Storm penalty
        var stormPenalty = weather?.StormPresence switch
        {
            StormPresence.Active       => 0.40f,
            StormPresence.Approaching  => 0.75f,
            StormPresence.Dissipating  => 0.85f,
            _                          => 1.0f,
        };

        var speedMultiplier = windMod * stormPenalty;
        ship.RouteProgressAccumulator += speedMultiplier;

        var newProgress = route.ProgressDays + speedMultiplier;
        ship.Location = route with { ProgressDays = newProgress };

        if (newProgress >= route.TotalDays)
        {
            // Arrived at destination region
            ArriveInRegion(ship, route.To, state, events, lod);
        }
    }

    private void ArriveInRegion(Ship ship, RegionId regionId, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        switch (ship.Route)
        {
            // POI destination in this region — divert to AtPoi
            case PoiRoute poiRoute
                when state.OceanPois.TryGetValue(poiRoute.Poi, out var arrivingPoi)
                     && arrivingPoi.RegionId == regionId:
                ship.Location = new AtPoi(poiRoute.Poi, regionId);
                ship.RouteProgressAccumulator = 0;
                events.Emit(new ShipEnteredRegion(state.Date, lod, ship.Id, regionId), lod);
                return;

            // Port destination in this region — dock
            case PortRoute portRoute
                when state.Ports.TryGetValue(portRoute.Destination, out var destPort2)
                     && destPort2.RegionId == regionId:
            {
                var portId = portRoute.Destination;
                ship.Location = new AtPort(portId);
                ship.ArrivedThisTick = true;
                ship.RouteProgressAccumulator = 0;
                ship.Route = null;
                ship.TicksDockedAtCurrentPort = 0;

                if (state.Ports.TryGetValue(portId, out var port))
                    port.DockedShips.Add(ship.Id);

                events.Emit(new ShipArrived(state.Date, lod, ship.Id, portId), lod);

                // Check convoy completion
                if (ship.ConvoyId.HasValue)
                {
                    var convoyId = ship.ConvoyId.Value;
                    var convoyMembers = state.Ships.Values
                        .Where(s => s.ConvoyId == convoyId)
                        .ToList();
                    var allDocked = convoyMembers.All(s => s.Location is AtPort ap2 && ap2.Port == portId);
                    if (allDocked)
                    {
                        var memberIds = convoyMembers.Select(s => s.Id).ToList();
                        events.Emit(new FleetArrived(state.Date, lod, memberIds, portId), lod);
                        foreach (var m in convoyMembers)
                            m.ConvoyId = null;
                    }
                }
                return;
            }

            // PursuitRoute arrived in target's last known region — dock at nearest port for intercept check
            case PursuitRoute pursuitRoute when pursuitRoute.LastKnownRegion == regionId:
            {
                var nearestPort = state.Ports.Values.FirstOrDefault(p => p.RegionId == regionId);
                if (nearestPort is not null)
                {
                    ship.Location = new AtPort(nearestPort.Id);
                    ship.ArrivedThisTick = true;
                    ship.RouteProgressAccumulator = 0;
                    ship.TicksDockedAtCurrentPort = 0;
                    nearestPort.DockedShips.Add(ship.Id);
                    events.Emit(new ShipArrived(state.Date, lod, ship.Id, nearestPort.Id), lod);
                    // Route stays set — QuestSystem will check for intercept and clear
                }
                else
                {
                    ship.Location = new AtSea(regionId);
                    ship.RouteProgressAccumulator = 0;
                    events.Emit(new ShipEnteredRegion(state.Date, lod, ship.Id, regionId), lod);
                }
                return;
            }
        }

        // Default: still in transit — at sea in this region
        ship.Location = new AtSea(regionId);
        ship.RouteProgressAccumulator = 0;
        events.Emit(new ShipEnteredRegion(state.Date, lod, ship.Id, regionId), lod);
    }

    private void ExplorePoiTick(Ship ship, AtPoi atPoi, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        if (!state.OceanPois.TryGetValue(atPoi.Poi, out var poi))
        {
            ship.Location = new AtSea(atPoi.Region);
            ship.Route = null;
            return;
        }

        // Discovery
        if (!poi.IsDiscovered)
        {
            poi.IsDiscovered = true;
            events.Emit(new PoiDiscovered(state.Date, lod, poi.Id, ship.Id), lod);
        }

        // Outcome resolution
        int goldFound = 0;
        float hullDamage = 0f;

        switch (poi.Type)
        {
            case PoiType.Shipwreck:
            case PoiType.PirateCache:
                if (poi.LootGold > 0 && _rng.NextSingle() < 0.70f)
                {
                    goldFound = Math.Min(poi.LootGold, 100 + _rng.Next(poi.LootGold / 2 + 1));
                    poi.LootGold = Math.Max(0, poi.LootGold - goldFound);
                    if (ship.IsPlayerShip)
                        state.Player.PersonalGold += goldFound;
                    else if (ship.CaptainId.HasValue
                        && state.Individuals.TryGetValue(ship.CaptainId.Value, out var cap))
                        cap.CurrentGold += goldFound;
                }
                break;

            case PoiType.ReefHazard:
            case PoiType.StormEye:
                hullDamage = poi.HazardSeverity * (_rng.NextSingle() * 0.05f + 0.15f);
                ship.HullIntegrity = Math.Max(0f, ship.HullIntegrity - hullDamage);

                if (ship.HullIntegrity <= 0f)
                {
                    events.Emit(new PoiEncountered(state.Date, lod, poi.Id, ship.Id, 0, hullDamage), lod);
                    events.Emit(new ShipDestroyed(state.Date, lod, ship.Id, null), lod);
                    state.Player.FleetIds.Remove(ship.Id);
                    state.Ships.Remove(ship.Id);
                    return;   // ship is gone — do NOT transition to AtSea
                }
                break;

            case PoiType.RendezvousPoint:
                // No mechanical outcome; narrative only
                break;
        }

        events.Emit(new PoiEncountered(state.Date, lod, poi.Id, ship.Id, goldFound, hullDamage), lod);

        // Leave POI
        ship.Location = new AtSea(atPoi.Region);
        ship.Route = null;
    }

    /// <summary>
    /// Finds the slowest effective travel time across all ships in a convoy for the given leg.
    /// Ships in a convoy travel at the speed of the slowest member so they arrive together.
    /// </summary>
    private float CalculateConvoyTravelDays(
        Guid convoyId, RegionId fromRegion, RegionId toRegion, WorldState state)
    {
        var baseDays = GetTravelDays(fromRegion, toRegion, state);
        var slowestDays = baseDays;

        foreach (var ship in state.Ships.Values)
        {
            if (ship.ConvoyId != convoyId) continue;
            if (ship.Location is not AtPort) continue;

            var weather = state.Weather.TryGetValue(fromRegion, out var w) ? w : null;
            var windMod = weather is not null
                ? GetWindModifier(weather.WindDirection, fromRegion, toRegion, state)
                : 1.0f;
            var stormMod = weather?.StormPresence switch
            {
                StormPresence.Active       => 0.40f,
                StormPresence.Approaching  => 0.75f,
                StormPresence.Dissipating  => 0.85f,
                _                          => 1.0f,
            };
            var effectiveSpeed = windMod * stormMod;
            if (effectiveSpeed > 0f)
            {
                var shipDays = baseDays / effectiveSpeed;
                slowestDays = Math.Max(slowestDays, shipDays);
            }
        }
        return slowestDays;
    }

    private static void DeductCrewUpkeep(Ship ship, WorldState state)
    {
        const int UpkeepPerCrewPerTick = 1;
        var totalCost = ship.CurrentCrew * UpkeepPerCrewPerTick;
        if (totalCost <= 0) return;

        if (ship.CaptainId.HasValue
            && state.Individuals.TryGetValue(ship.CaptainId.Value, out var captain))
        {
            var deduct = Math.Min(captain.CurrentGold, totalCost);
            captain.CurrentGold -= deduct;
            var remainder = totalCost - deduct;
            if (remainder > 0)
                ship.GoldOnBoard = Math.Max(0, ship.GoldOnBoard - remainder);
        }
        else
        {
            ship.GoldOnBoard = Math.Max(0, ship.GoldOnBoard - totalCost);
        }
    }

    private static bool ShouldDepartPort(Ship ship, WorldState state)
    {
        // Always depart if docked too long (prevents permanent stranding)
        if (ship.TicksDockedAtCurrentPort >= 5) return true;

        // Depart if captain or crew has market intelligence for accessible ports
        // 5A-1: Check both IndividualHolder (captain) and ShipHolder (crew gossip)
        bool HasActionablePriceKnowledge(KnowledgeHolderId holder) =>
            state.Knowledge.GetFacts(holder)
                .Any(f => !f.IsSuperseded && f.Claim is PortPriceClaim && f.Confidence > 0.40f);

        if (ship.CaptainId.HasValue && HasActionablePriceKnowledge(new IndividualHolder(ship.CaptainId.Value)))
            return true;

        if (HasActionablePriceKnowledge(new ShipHolder(ship.Id)))
            return true;

        return false;
    }

    private void AssignNpcRoute(Ship ship, RegionId currentRegion, WorldState state)
    {
        // Determine epistemic agent: named captain uses personal knowledge (IndividualHolder);
        // uncaptained ships use crew collective knowledge (ShipHolder).
        // This is the Option B placeholder: Individual-first routing without full merchant lifecycle.
        KnowledgeHolderId agentHolder = ship.CaptainId.HasValue
            ? new IndividualHolder(ship.CaptainId.Value)
            : new ShipHolder(ship.Id);

        // Bounty route evaluation for Privateers and Pirate Captains
        if (ship.CaptainId.HasValue
            && state.Individuals.TryGetValue(ship.CaptainId.Value, out var npcCaptain)
            && (npcCaptain.Role == IndividualRole.Privateer
                || npcCaptain.Role == IndividualRole.PirateCaptain))
        {
            var bountyDest = EvaluateBountyRoute(ship, npcCaptain, currentRegion, state);
            if (bountyDest.HasValue)
            {
                ship.Route = new PortRoute(bountyDest.Value);
                var nextReg = FindNextRegion(currentRegion, state.Ports[bountyDest.Value].RegionId, state);
                if (nextReg.HasValue)
                {
                    var travelDays = GetTravelDays(currentRegion, nextReg.Value, state);
                    ship.Location = new EnRoute(currentRegion, nextReg.Value, 0f, travelDays);
                }
                return;
            }
        }

        // 5A-1: Union IndividualHolder + ShipHolder facts, score by expected margin.
        // Crew gossip (ShipHolder) is the navigator's log — captains consult this.
        var accessiblePortIds = GetAccessiblePortIds(currentRegion, state);
        var knownPrices = GetKnownPrices(agentHolder, ship, state, accessiblePortIds);
        var dangerousPorts = GetKnownDangerousPorts(agentHolder, ship, state);
        var best = ScoreMerchantDestination(knownPrices, ship, accessiblePortIds, dangerousPorts);

        // Fallback chain: HomePort (if stranded) → random accessible port
        PortId? homePort = null;
        if (ship.CaptainId.HasValue
            && state.Individuals.TryGetValue(ship.CaptainId.Value, out var captain)
            && captain.HomePortId.HasValue)
        {
            homePort = captain.HomePortId;
        }

        // Exclude current port to prevent routing to where we already are
        var currentPort = ship.Location is AtPort ap ? (PortId?)ap.Port : null;
        if (best == currentPort) best = null;
        if (homePort == currentPort) homePort = null;

        var candidate = best ?? homePort ?? GetRandomAccessiblePort(currentRegion, state, excludePort: currentPort);
        if (!candidate.HasValue) return;

        ship.Route = new PortRoute(candidate.Value);
        var nextRegion = FindNextRegion(currentRegion, state.Ports[candidate.Value].RegionId, state);
        if (nextRegion.HasValue)
        {
            var travelDays = GetTravelDays(currentRegion, nextRegion.Value, state);
            ship.Location = new EnRoute(currentRegion, nextRegion.Value, 0f, travelDays);
        }
    }

    private PortId? EvaluateBountyRoute(Ship ship, Individual captain, RegionId currentRegion,
        WorldState state)
    {
        var captainHolder = new IndividualHolder(captain.Id);
        var captainFacts = state.Knowledge.GetFacts(captainHolder)
            .Where(f => !f.IsSuperseded)
            .ToList();

        PortId? bestPort = null;
        float bestUtility = 100f; // minimum threshold

        foreach (var contractFact in captainFacts.Where(f => f.Claim is ContractClaim cc
            && cc.Condition == ContractConditionType.TargetDestroyed))
        {
            var contract = (ContractClaim)contractFact.Claim;

            // Find a ShipLocationClaim for the target
            var intelFact = captainFacts.FirstOrDefault(f =>
                f.Claim is ShipLocationClaim slc
                && KnowledgeFact.GetSubjectKey(f.Claim) == contract.TargetSubjectKey);

            if (intelFact is null) continue;

            // Determine last known region of target
            var targetLocation = ((ShipLocationClaim)intelFact.Claim).LastKnownLocation;
            RegionId targetRegion;
            if (targetLocation is AtSea ats)
                targetRegion = ats.Region;
            else if (targetLocation is AtPort atp && state.Ports.TryGetValue(atp.Port, out var tp))
                targetRegion = tp.RegionId;
            else if (targetLocation is EnRoute er)
                targetRegion = er.To;
            else
                continue;

            // Find accessible ports in that region
            var portsInRegion = state.Ports.Values
                .Where(p => p.RegionId == targetRegion)
                .ToList();
            if (portsInRegion.Count == 0) continue;

            var distanceInTicks = currentRegion == targetRegion
                ? 1f
                : GetTravelDays(currentRegion, targetRegion, state);

            var utility = (contract.GoldReward * intelFact.Confidence) / distanceInTicks;

            if (utility > bestUtility)
            {
                bestUtility = utility;
                bestPort = portsInRegion[0].Id; // pick any port in target region
            }
        }

        return bestPort;
    }

    private HashSet<PortId> GetAccessiblePortIds(RegionId from, WorldState state)
    {
        var ids = new HashSet<PortId>();
        if (!state.Regions.TryGetValue(from, out var region)) return ids;
        foreach (var pid in region.Ports) ids.Add(pid);
        foreach (var adj in region.AdjacentRegions)
            if (state.Regions.TryGetValue(adj, out var adjRegion))
                foreach (var pid in adjRegion.Ports) ids.Add(pid);
        return ids;
    }

    private PortId? GetRandomAccessiblePort(RegionId from, WorldState state, PortId? excludePort = null)
    {
        var candidates = new List<PortId>();
        if (state.Regions.TryGetValue(from, out var region))
            candidates.AddRange(region.Ports);

        foreach (var adj in region?.AdjacentRegions ?? [])
            if (state.Regions.TryGetValue(adj, out var adjRegion))
                candidates.AddRange(adjRegion.Ports);

        if (excludePort.HasValue)
            candidates.Remove(excludePort.Value);

        if (candidates.Count == 0) return null;
        return candidates[_rng.Next(candidates.Count)];
    }

    private static RegionId? FindNextRegion(RegionId from, RegionId to, WorldState state)
    {
        if (from == to) return null;
        if (!state.Regions.TryGetValue(from, out var region)) return null;
        if (region.AdjacentRegions.Contains(to)) return to;

        // BFS for next hop
        var visited = new HashSet<RegionId> { from };
        var queue = new Queue<(RegionId Id, RegionId FirstHop)>();
        foreach (var adj in region.AdjacentRegions)
            queue.Enqueue((adj, adj));

        while (queue.Count > 0)
        {
            var (current, firstHop) = queue.Dequeue();
            if (visited.Contains(current)) continue;
            visited.Add(current);
            if (current == to) return firstHop;
            if (state.Regions.TryGetValue(current, out var r))
                foreach (var adj in r.AdjacentRegions)
                    if (!visited.Contains(adj))
                        queue.Enqueue((adj, firstHop));
        }
        return null;
    }

    private static float GetTravelDays(RegionId from, RegionId to, WorldState state)
    {
        if (state.Regions.TryGetValue(from, out var region) &&
            region.BaseTravelDays.TryGetValue(to, out var days))
            return days;
        return 5f; // default fallback
    }

    private float GetWindModifier(WindDirection wind, RegionId from, RegionId to, WorldState state)
    {
        // Derive a cardinal heading for the route and compare to wind direction
        // Simplified: use enum ordinal difference as angular proxy
        var routeHeading = (int)wind; // placeholder — real impl would use region coordinates
        var diff = (int)wind - routeHeading;
        var normalised = ((diff % 8) + 8) % 8;
        var degrees = normalised * 45;

        return _windModifiers.TryGetValue(degrees, out var mod) ? mod : 1.0f;
    }

    private static SimulationLod GetShipLod(Ship ship, WorldState state, SimulationContext context)
    {
        var regionId = state.GetShipRegion(ship.Id);
        return regionId.HasValue ? context.GetLod(regionId.Value) : SimulationLod.Distant;
    }

    // ---- 5A-1: Knowledge-driven merchant routing ----

    /// <summary>
    /// Union IndividualHolder + ShipHolder price facts for accessible ports.
    /// Returns the highest-confidence PortPriceClaim per (Port, Good) pair.
    /// </summary>
    private static List<(KnowledgeFact Fact, PortPriceClaim Claim)> GetKnownPrices(
        KnowledgeHolderId agentHolder, Ship ship, WorldState state, HashSet<PortId> accessiblePortIds)
    {
        var holders = new List<KnowledgeHolderId> { agentHolder };

        // Add ShipHolder as secondary source (crew gossip = navigator's log)
        var shipHolder = new ShipHolder(ship.Id);
        if (agentHolder is not ShipHolder) // avoid duplicate if agent IS the ship
            holders.Add(shipHolder);

        // Collect all non-superseded PortPriceClaims from both holders
        var allPriceFacts = new Dictionary<string, (KnowledgeFact Fact, PortPriceClaim Claim)>();

        foreach (var holder in holders)
        {
            foreach (var fact in state.Knowledge.GetFacts(holder))
            {
                if (fact.IsSuperseded) continue;
                if (fact.Claim is not PortPriceClaim pc) continue;
                if (!accessiblePortIds.Contains(pc.Port)) continue;

                // Key by Port+Good — keep highest confidence
                var key = $"{pc.Port.Value}:{pc.Good}";
                if (!allPriceFacts.TryGetValue(key, out var existing) || fact.Confidence > existing.Fact.Confidence)
                    allPriceFacts[key] = (fact, pc);
            }
        }

        return allPriceFacts.Values.ToList();
    }

    /// <summary>
    /// Collect ports the NPC knows are dangerous (epidemic, blockaded, at war).
    /// Merchants avoid these ports — organic quarantine and embargo behaviour.
    /// </summary>
    private static HashSet<PortId> GetKnownDangerousPorts(
        KnowledgeHolderId agentHolder, Ship ship, WorldState state)
    {
        var dangerous = new HashSet<PortId>();
        var holders = new List<KnowledgeHolderId> { agentHolder };
        if (agentHolder is not ShipHolder)
            holders.Add(new ShipHolder(ship.Id));

        foreach (var holder in holders)
        {
            foreach (var fact in state.Knowledge.GetFacts(holder))
            {
                if (fact.IsSuperseded || fact.Confidence < 0.30f) continue;

                if (fact.Claim is PortConditionClaim pcc
                    && (pcc.Condition.HasFlag(PortConditionFlags.Plague)
                        || pcc.Condition.HasFlag(PortConditionFlags.Blockaded)))
                {
                    dangerous.Add(pcc.Port);
                }

                // Also check RouteHazardClaim for blockade-like hazards
                // (merchants already avoid these via price scoring, but this adds direct avoidance)
            }
        }

        // Additionally: avoid ports controlled by factions at war with the ship's owner
        if (ship.OwnerFactionId.HasValue
            && state.Factions.TryGetValue(ship.OwnerFactionId.Value, out var shipFaction))
        {
            foreach (var port in state.Ports.Values)
            {
                if (port.ControllingFactionId.HasValue
                    && shipFaction.AtWarWith.Contains(port.ControllingFactionId.Value))
                {
                    dangerous.Add(port.Id);
                }
            }
        }

        return dangerous;
    }

    /// <summary>
    /// Score candidate ports by expected trade margin.
    /// If ship has cargo: find ports with highest known sell prices for carried goods.
    /// If ship is empty: find ports with lowest known buy prices (buying opportunity).
    /// Falls back to highest-confidence known port if no margin data available.
    /// </summary>
    private static PortId? ScoreMerchantDestination(
        List<(KnowledgeFact Fact, PortPriceClaim Claim)> knownPrices,
        Ship ship,
        HashSet<PortId> accessiblePortIds,
        HashSet<PortId>? dangerousPorts = null)
    {
        if (knownPrices.Count == 0) return null;

        // Filter out known dangerous ports (epidemic, blockade) — merchants avoid them
        if (dangerousPorts is { Count: > 0 })
            knownPrices = knownPrices.Where(x => !dangerousPorts.Contains(x.Claim.Port)).ToList();
        if (knownPrices.Count == 0) return null;

        var hasCargo = ship.Cargo.Any(kv => kv.Value > 0);

        if (hasCargo)
        {
            // Score ports by: sum of (known sell price × confidence) for goods we carry
            var portScores = new Dictionary<PortId, float>();
            foreach (var (fact, claim) in knownPrices)
            {
                if (!ship.Cargo.ContainsKey(claim.Good) || ship.Cargo[claim.Good] <= 0) continue;
                var score = claim.Price * fact.Confidence * ship.Cargo[claim.Good];
                portScores.TryGetValue(claim.Port, out var existing);
                portScores[claim.Port] = existing + score;
            }

            if (portScores.Count > 0)
                return portScores.MaxBy(kv => kv.Value).Key;
        }
        else
        {
            // No cargo — route toward port with lowest known prices (buying opportunity).
            // Score = inverse price weighted by confidence (lower price = better opportunity).
            var portScores = new Dictionary<PortId, float>();
            foreach (var (fact, claim) in knownPrices)
            {
                if (claim.Price <= 0) continue;
                var score = fact.Confidence / claim.Price;
                portScores.TryGetValue(claim.Port, out var existing);
                portScores[claim.Port] = existing + score;
            }

            if (portScores.Count > 0)
                return portScores.MaxBy(kv => kv.Value).Key;
        }

        // Fallback: highest-confidence known port (original behaviour)
        return knownPrices
            .OrderByDescending(x => x.Fact.Confidence)
            .Select(x => (PortId?)x.Claim.Port)
            .FirstOrDefault();
    }
}
