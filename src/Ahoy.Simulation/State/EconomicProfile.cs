using Ahoy.Core.Enums;

namespace Ahoy.Simulation.State;

/// <summary>Describes what a port produces, consumes, and how prices shift over time.</summary>
public sealed class EconomicProfile
{
    /// <summary>Units produced per tick at baseline.</summary>
    public Dictionary<TradeGood, int> BaseProduction { get; } = new();

    /// <summary>Units consumed per tick at baseline.</summary>
    public Dictionary<TradeGood, int> BaseConsumption { get; } = new();

    /// <summary>Current supply on-hand.</summary>
    public Dictionary<TradeGood, int> Supply { get; } = new();

    /// <summary>Current outstanding demand.</summary>
    public Dictionary<TradeGood, int> Demand { get; } = new();

    /// <summary>Base prices in gold per unit before supply/demand adjustment.</summary>
    public Dictionary<TradeGood, int> BasePrice { get; } = new();

    /// <summary>Active price modifiers applied on top of the supply/demand formula.</summary>
    public List<PriceModifier> ActiveModifiers { get; } = new();

    /// <summary>
    /// Calculates the current effective price for a good.
    /// Formula: BasePrice × (Demand / max(Supply, 1)), clamped 20%–500%.
    /// </summary>
    public int EffectivePrice(TradeGood good)
    {
        if (!BasePrice.TryGetValue(good, out var basePrice)) return 0;

        var supply = Supply.GetValueOrDefault(good, 0);
        var demand = Demand.GetValueOrDefault(good, 0);
        var ratio = (float)demand / Math.Max(supply, 1);
        var price = basePrice * ratio;

        // Apply modifiers multiplicatively
        foreach (var mod in ActiveModifiers)
            if (mod.Good == good)
                price *= mod.Multiplier;

        var min = basePrice * 0.20f;
        var max = basePrice * 5.00f;
        return (int)Math.Clamp(price, min, max);
    }
}

public sealed class PriceModifier
{
    public TradeGood Good { get; init; }
    public float Multiplier { get; init; }
    public required string Reason { get; init; }
    public int TicksRemaining { get; set; }
}
