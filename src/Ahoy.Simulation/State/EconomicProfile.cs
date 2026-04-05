using Ahoy.Core.Enums;

namespace Ahoy.Simulation.State;

/// <summary>Describes what a port produces, consumes, and how prices shift over time.</summary>
public sealed class EconomicProfile
{
    /// <summary>Units produced per tick at baseline (per 1000 population at 100% prosperity).</summary>
    public Dictionary<TradeGood, int> BaseProduction { get; } = new();

    /// <summary>Non-essential goods consumed per tick at baseline (per 1000 population).</summary>
    public Dictionary<TradeGood, int> BaseConsumption { get; } = new();

    /// <summary>Current supply on-hand.</summary>
    public Dictionary<TradeGood, int> Supply { get; } = new();

    /// <summary>
    /// How much of each good this port needs per tick to sustain its population.
    /// Recalculated each tick by EconomySystem from population size.
    /// Food: 1 unit per 100 pop. Medicine: 1 unit per 500 pop. Others: BaseConsumption scaled.
    /// </summary>
    public Dictionary<TradeGood, int> TargetSupply { get; } = new();

    /// <summary>Current outstanding demand (legacy — being replaced by TargetSupply for essentials).</summary>
    public Dictionary<TradeGood, int> Demand { get; } = new();

    /// <summary>Base prices in gold per unit before supply/demand adjustment.</summary>
    public Dictionary<TradeGood, int> BasePrice { get; } = new();

    /// <summary>Active price modifiers applied on top of the supply/demand formula.</summary>
    public List<PriceModifier> ActiveModifiers { get; } = new();

    /// <summary>True for faction capitals and major hubs that can export to Europe.
    /// These ports convert surplus goods into fresh gold (the economy's only faucet).</summary>
    public bool CanExportToEurope { get; set; }

    /// <summary>Essential goods scale non-linearly when scarce — people pay everything they have.</summary>
    public static bool IsEssential(TradeGood good) =>
        good is TradeGood.Food or TradeGood.Medicine or TradeGood.Water;

    /// <summary>
    /// Fixed European prices — the Caribbean doesn't set world prices.
    /// Scaled to match the simulation's economic size (~41K starting gold).
    /// These determine the faucet rate: total gold injection ≈ hubs × goods × price × cap/tick.
    /// </summary>
    public static int EuropeanPrice(TradeGood good) => good switch
    {
        TradeGood.Gold => 50,
        TradeGood.Silver => 25,
        TradeGood.Sugar => 8,
        TradeGood.Tobacco => 10,
        TradeGood.Indigo => 12,
        TradeGood.Rum => 7,
        TradeGood.Coffee => 10,
        TradeGood.Cocoa => 8,
        TradeGood.Cotton => 6,
        TradeGood.Spices => 15,
        TradeGood.Silk => 20,
        _ => 0, // non-exportable
    };

    /// <summary>
    /// Calculates the current effective price for a good.
    /// Essentials: BasePrice × (TargetSupply / Supply)^2 — exponential scarcity.
    /// Luxuries:   BasePrice × (TargetSupply / Supply)^1 — linear scarcity.
    /// </summary>
    public int EffectivePrice(TradeGood good)
    {
        if (!BasePrice.TryGetValue(good, out var basePrice)) return 0;

        var supply = Supply.GetValueOrDefault(good, 0);
        var target = TargetSupply.GetValueOrDefault(good, Math.Max(1, Demand.GetValueOrDefault(good, 1)));
        var ratio = (float)target / Math.Max(supply, 1);

        // Essentials scale exponentially when scarce
        var exponent = IsEssential(good) && ratio > 1.0f ? 2.0f : 1.0f;
        var multiplier = MathF.Pow(ratio, exponent);

        // Apply modifiers
        foreach (var mod in ActiveModifiers)
            if (mod.Good == good) multiplier *= mod.Multiplier;

        // Wider band for essentials (10%–1000%) vs luxuries (20%–500%)
        // Cap at 10× not 50× — higher creates gold hyperinflation since ports
        // don't have explicit treasuries (gold is created from trade volume).
        var minMult = IsEssential(good) ? 0.10f : 0.20f;
        var maxMult = IsEssential(good) ? 10.0f : 5.0f;
        return (int)Math.Clamp(basePrice * multiplier, basePrice * minMult, basePrice * maxMult);
    }
}

public sealed class PriceModifier
{
    public TradeGood Good { get; init; }
    public float Multiplier { get; init; }
    public required string Reason { get; init; }
    public int TicksRemaining { get; set; }
}
