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
            }
        }
    }

    private void TryDepartPort(Ship ship, AtPort atPort, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        // Player ship — only departs via PlayerCommand
        if (ship.IsPlayerShip) return;

        // Track how long this ship has been docked
        if (!ship.IsPlayerShip)
            ship.TicksDockedAtCurrentPort++;

        // NPC ship at port with no destination — pick one if agent has knowledge or has waited long enough
        if (!ship.RoutingDestination.HasValue &&
            state.Ports.TryGetValue(atPort.Port, out var currentPort2))
        {
            var shouldDepart = ShouldDepartPort(ship, state);
            if (shouldDepart)
                AssignNpcRoute(ship, currentPort2.RegionId, state);
        }

        // NPC with a routing destination set — depart
        if (ship.RoutingDestination.HasValue &&
            state.Ports.TryGetValue(ship.RoutingDestination.Value, out var dest) &&
            state.Ports.TryGetValue(atPort.Port, out var currentPort))
        {
            var destRegion = dest.RegionId;
            var fromRegion = currentPort.RegionId;

            if (fromRegion != destRegion)
            {
                // Move toward dest region
                var nextRegion = FindNextRegion(fromRegion, destRegion, state);
                if (nextRegion.HasValue)
                {
                    var travelDays = GetTravelDays(fromRegion, nextRegion.Value, state);
                    ship.Location = new EnRoute(fromRegion, nextRegion.Value, 0f, travelDays);
                    events.Emit(new ShipDeparted(state.Date, lod, ship.Id, atPort.Port), lod);
                }
            }
        }
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
        // Check if ship's routing destination is a port in this region
        if (ship.RoutingDestination.HasValue &&
            state.Ports.TryGetValue(ship.RoutingDestination.Value, out var destPort) &&
            destPort.RegionId == regionId)
        {
            // Dock at port
            var portId = ship.RoutingDestination.Value;
            ship.Location = new AtPort(portId);
            ship.ArrivedThisTick = true;
            ship.RouteProgressAccumulator = 0;
            ship.RoutingDestination = null;
            ship.TicksDockedAtCurrentPort = 0;

            // Update port's docked list
            if (state.Ports.TryGetValue(portId, out var port))
                port.DockedShips.Add(ship.Id);

            events.Emit(new ShipArrived(state.Date, lod, ship.Id, portId), lod);
        }
        else
        {
            // Still at sea in this region
            ship.Location = new AtSea(regionId);
            ship.RouteProgressAccumulator = 0;
            events.Emit(new ShipEnteredRegion(state.Date, lod, ship.Id, regionId), lod);
        }
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

        // Depart if captain has market intelligence for accessible ports
        if (ship.CaptainId.HasValue)
        {
            var captainHolder = new IndividualHolder(ship.CaptainId.Value);
            var hasActionableKnowledge = state.Knowledge.GetFacts(captainHolder)
                .Any(f => !f.IsSuperseded
                    && f.Claim is PortPriceClaim
                    && f.Confidence > 0.40f);
            if (hasActionableKnowledge) return true;
        }

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
                ship.RoutingDestination = bountyDest;
                var nextReg = FindNextRegion(currentRegion, state.Ports[bountyDest.Value].RegionId, state);
                if (nextReg.HasValue)
                {
                    var travelDays = GetTravelDays(currentRegion, nextReg.Value, state);
                    ship.Location = new EnRoute(currentRegion, nextReg.Value, 0f, travelDays);
                }
                return;
            }
        }

        // Find the highest-confidence known port that is accessible from current region.
        // Agent routes toward what they know, not what is objectively best.
        var accessiblePortIds = GetAccessiblePortIds(currentRegion, state);
        var best = state.Knowledge.GetFacts(agentHolder)
            .Where(f => !f.IsSuperseded && f.Claim is PortPriceClaim pc
                        && accessiblePortIds.Contains(pc.Port))
            .OrderByDescending(f => f.Confidence)
            .Select(f => (PortId?)((PortPriceClaim)f.Claim).Port)
            .FirstOrDefault();

        // Fallback chain: HomePort (if stranded) → random accessible port
        PortId? homePort = null;
        if (ship.CaptainId.HasValue
            && state.Individuals.TryGetValue(ship.CaptainId.Value, out var captain)
            && captain.HomePortId.HasValue)
        {
            homePort = captain.HomePortId;
        }

        var candidate = best ?? homePort ?? GetRandomAccessiblePort(currentRegion, state);
        if (!candidate.HasValue) return;

        ship.RoutingDestination = candidate;
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

    private PortId? GetRandomAccessiblePort(RegionId from, WorldState state)
    {
        var candidates = new List<PortId>();
        if (state.Regions.TryGetValue(from, out var region))
            candidates.AddRange(region.Ports);

        foreach (var adj in region?.AdjacentRegions ?? [])
            if (state.Regions.TryGetValue(adj, out var adjRegion))
                candidates.AddRange(adjRegion.Ports);

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
}
