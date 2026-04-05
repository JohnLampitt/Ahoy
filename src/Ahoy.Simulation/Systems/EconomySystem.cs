using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 3 — runs after ShipMovementSystem.
/// Handles production/consumption, price updates, merchant trade execution,
/// and prosperity drift per port.
/// Queries KnowledgeStore (read-only) for merchant routing decisions at non-Local LOD.
/// </summary>
public sealed class EconomySystem : IWorldSystem
{
    private const float CaptainIncomeFraction = 0.10f;

    private readonly Random _rng;

    public EconomySystem(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        foreach (var (portId, port) in state.Ports)
        {
            var lod = context.GetLod(port.RegionId);

            // Tick modifiers — decrement durations and remove expired ones
            var expired = port.Economy.ActiveModifiers
                .Where(m => --m.TicksRemaining <= 0)
                .ToList();
            foreach (var m in expired)
                port.Economy.ActiveModifiers.Remove(m);

            // Reputation decay — extreme reputations require active maintenance
            port.PersonalReputation *= 0.99f;

            // PortConditionFlags modifier pass
            ApplyConditionFlags(port);

            // Production and consumption
            ProduceAndConsume(port, lod);

            // Price signal events for visible ports
            if (lod is SimulationLod.Local or SimulationLod.Regional)
                EmitPriceShifts(port, portId, state, events, lod);
        }

        // Notoriety decay — fame fades without continued notorious acts
        state.Player.Notoriety = Math.Clamp(state.Player.Notoriety * 0.999f, 0f, 100f);

        // Merchant trade — only ships that are docked
        foreach (var ship in state.Ships.Values.Where(s => !s.IsPlayerShip))
        {
            if (ship.Location is not AtPort atPort) continue;
            if (!state.Ports.TryGetValue(atPort.Port, out var port)) continue;

            var lod = context.GetLod(port.RegionId);
            ExecuteMerchantTrade(ship, port, state, events, lod);

            // If arrived this tick — process knowledge gossip (KnowledgeSystem handles this;
            // EconomySystem just ensures cargo is exchanged before KnowledgeSystem runs)
        }

        // Crisis 2: Epidemic propagation
        TickEpidemics(state, events);
    }

    // ---- Crisis 2: Epidemic Outbreak ----

    private void TickEpidemics(WorldState state, IEventEmitter events)
    {
        // Random epidemic spawning on tropical ports (2% per tick)
        foreach (var port in state.Ports.Values)
        {
            if (port.Conditions.HasFlag(PortConditionFlags.Plague)) continue;
            if (_rng.NextDouble() < 0.002) // 0.2% — rare but devastating
            {
                port.Conditions |= PortConditionFlags.Plague;
                port.EpidemicTicksRemaining = 30;
            }
        }

        // Tick active epidemics
        foreach (var port in state.Ports.Values)
        {
            if (!port.Conditions.HasFlag(PortConditionFlags.Plague)) continue;

            // Natural decay
            if (port.EpidemicTicksRemaining.HasValue)
            {
                port.EpidemicTicksRemaining--;
                if (port.EpidemicTicksRemaining <= 0)
                {
                    port.Conditions &= ~PortConditionFlags.Plague;
                    port.EpidemicTicksRemaining = null;
                    continue;
                }
            }

            // Infect docked ships (10% per tick)
            foreach (var shipId in port.DockedShips)
            {
                if (!state.Ships.TryGetValue(shipId, out var ship)) continue;
                if (ship.HasInfectedCrew) continue;
                if (_rng.NextDouble() < 0.10)
                    ship.HasInfectedCrew = true;
            }
        }

        // Infected ships spread to clean ports (5% per tick on dock)
        foreach (var ship in state.Ships.Values)
        {
            if (!ship.HasInfectedCrew) continue;
            if (!ship.ArrivedThisTick) continue;
            if (ship.Location is not AtPort atPort) continue;
            if (!state.Ports.TryGetValue(atPort.Port, out var port)) continue;
            if (port.Conditions.HasFlag(PortConditionFlags.Plague)) continue;

            if (_rng.NextDouble() < 0.05)
            {
                port.Conditions |= PortConditionFlags.Plague;
                port.EpidemicTicksRemaining = 30;
            }
        }
    }

    private static void ApplyConditionFlags(Port port)
    {
        if (port.Conditions == PortConditionFlags.None) return;

        var eco = port.Economy;

        if (port.Conditions.HasFlag(PortConditionFlags.Famine))
        {
            // Food and Grain are scarce — multiply effective price by 2.5
            // We adjust the BasePrice temporarily via a multiplier on Supply (reduce supply to drive price up)
            // Better approach: adjust BasePrice directly for the tick
            foreach (var good in new[] { TradeGood.Food })
            {
                if (eco.BasePrice.ContainsKey(good))
                    eco.BasePrice[good] = (int)(eco.BasePrice[good] * 2.5f);
            }
        }

        if (port.Conditions.HasFlag(PortConditionFlags.Plague))
        {
            port.Prosperity = Math.Clamp(port.Prosperity - 2f, 0f, 100f);
        }

        if (port.Conditions.HasFlag(PortConditionFlags.GoodHarvest))
        {
            foreach (var good in new[] { TradeGood.Sugar, TradeGood.Tobacco })
            {
                if (eco.BasePrice.ContainsKey(good))
                    eco.BasePrice[good] = (int)(eco.BasePrice[good] * 0.6f);
            }
        }
    }

    private static void ProduceAndConsume(Port port, SimulationLod lod)
    {
        var economy = port.Economy;

        // Apply scaled production/consumption at coarser LODs
        var scale = lod switch
        {
            SimulationLod.Local    => 1.0f,
            SimulationLod.Regional => 1.0f,
            SimulationLod.Distant  => 1.0f, // still tick — just emits no events
            _ => 1.0f,
        };

        foreach (var (good, amount) in economy.BaseProduction)
        {
            var produced = (int)(amount * scale);
            economy.Supply[good] = economy.Supply.GetValueOrDefault(good) + produced;
        }

        foreach (var (good, amount) in economy.BaseConsumption)
        {
            var consumed = (int)(amount * scale);
            var demand = economy.Demand.GetValueOrDefault(good) + consumed;
            economy.Demand[good] = demand;

            // Consume from supply if available
            var supply = economy.Supply.GetValueOrDefault(good);
            var actualConsumed = Math.Min(supply, consumed);
            economy.Supply[good] = supply - actualConsumed;
            economy.Demand[good] = Math.Max(0, demand - actualConsumed);
        }

        // Prosperity drift based on supply/demand balance
        var surplusGoods = economy.BaseProduction.Keys
            .Count(g => economy.Supply.GetValueOrDefault(g) > economy.Demand.GetValueOrDefault(g));
        var shortageGoods = economy.BaseConsumption.Keys
            .Count(g => economy.Demand.GetValueOrDefault(g) > economy.Supply.GetValueOrDefault(g));

        var prosperityDelta = (surplusGoods - shortageGoods) * 0.5f;
        port.Prosperity = Math.Clamp(port.Prosperity + prosperityDelta, 0f, 100f);
    }

    private static void EmitPriceShifts(Port port, PortId portId, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        foreach (var good in port.Economy.BasePrice.Keys)
        {
            // Emit event if effective price moved significantly (>10%)
            var basePrice = port.Economy.BasePrice[good];
            var effective = port.Economy.EffectivePrice(good);
            var delta = Math.Abs(effective - basePrice) / (float)Math.Max(basePrice, 1);
            if (delta > 0.10f)
                events.Emit(new PriceShifted(state.Date, lod, portId, good, basePrice, effective), lod);
        }
    }

    private void ExecuteMerchantTrade(Ship ship, Port port, WorldState state,
        IEventEmitter events, SimulationLod lod)
    {
        var economy = port.Economy;

        // SELL cargo the ship is carrying that the port demands
        foreach (var (good, qty) in ship.Cargo.ToList())
        {
            var demand = economy.Demand.GetValueOrDefault(good);
            if (demand <= 0) continue;

            var sellQty = Math.Min(qty, demand);
            var price = economy.EffectivePrice(good);

            ship.Cargo[good] -= sellQty;
            if (ship.Cargo[good] <= 0) ship.Cargo.Remove(good);

            economy.Supply[good] = economy.Supply.GetValueOrDefault(good) + sellQty;
            economy.Demand[good] -= sellQty;

            ship.GoldOnBoard += price * sellQty;

            // Credit captain's personal wealth: 10% of trade revenue
            if (ship.CaptainId.HasValue
                && state.Individuals.TryGetValue(ship.CaptainId.Value, out var captain))
            {
                captain.CurrentGold += (int)((price * sellQty) * CaptainIncomeFraction);
            }

            events.Emit(new TradeCompleted(state.Date, lod, ship.Id, port.Id, good, sellQty, price, false), lod);
        }

        // BUY goods the port produces that the ship has space for
        var cargoUsed = ship.Cargo.Values.Sum();
        var cargoFree = ship.MaxCargoTons - cargoUsed;
        if (cargoFree <= 0) return;

        foreach (var (good, supply) in economy.Supply.ToList())
        {
            if (supply <= 0) continue;
            if (cargoFree <= 0) break;

            // Find a destination that demands this good (simple: just buy if profitable at any adjacent port)
            var buyQty = Math.Min(Math.Min(supply, cargoFree), 50); // cap 50 tons
            var price = economy.EffectivePrice(good);

            if (ship.GoldOnBoard < price * buyQty) continue;

            economy.Supply[good] -= buyQty;
            ship.Cargo[good] = ship.Cargo.GetValueOrDefault(good) + buyQty;
            ship.GoldOnBoard -= price * buyQty;
            cargoFree -= buyQty;

            events.Emit(new TradeCompleted(state.Date, lod, ship.Id, port.Id, good, buyQty, price, true), lod);
        }
    }
}
