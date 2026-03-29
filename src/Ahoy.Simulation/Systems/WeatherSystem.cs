using Ahoy.Core.Enums;
using Ahoy.Core.Ids;
using Ahoy.Simulation.Engine;
using Ahoy.Simulation.Events;
using Ahoy.Simulation.State;

namespace Ahoy.Simulation.Systems;

/// <summary>
/// System 1 — runs first each tick.
/// Advances Markov-like weather states per region, driven by SeasonalProfiles.
/// Handles storm lifecycle (spawn → propagate → dissipate) and wind shifts.
/// </summary>
public sealed class WeatherSystem : IWorldSystem
{
    private readonly Random _rng;

    public WeatherSystem(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public void Tick(WorldState state, SimulationContext context, IEventEmitter events)
    {
        var profile = SeasonalProfiles.ForDate(state.Date.Month);

        foreach (var (regionId, weather) in state.Weather)
        {
            var lod = context.GetLod(regionId);

            // At Distant LOD we use a coarser update — only tick storm countdown
            if (lod == SimulationLod.Distant)
            {
                TickDistant(weather, profile, regionId, state, events);
                continue;
            }

            TickFull(weather, profile, regionId, state, events, lod);
        }
    }

    private void TickFull(RegionWeather weather, SeasonalProfile profile,
        RegionId regionId, WorldState state, IEventEmitter events, SimulationLod lod)
    {
        // --- Storm lifecycle ---
        switch (weather.StormPresence)
        {
            case StormPresence.None:
                TrySpawnStorm(weather, profile, regionId, state, events, lod);
                break;

            case StormPresence.Approaching:
                weather.StormPresence = StormPresence.Active;
                weather.StormDaysRemaining = _rng.Next(3, 8);
                break;

            case StormPresence.Active:
                weather.StormDaysRemaining--;
                if (weather.StormDaysRemaining <= 0)
                {
                    weather.StormPresence = StormPresence.Dissipating;
                    weather.StormDaysRemaining = _rng.Next(1, 3);
                    TryPropagateStorm(weather, regionId, state, events, lod);
                }
                break;

            case StormPresence.Dissipating:
                weather.StormDaysRemaining--;
                if (weather.StormDaysRemaining <= 0)
                {
                    weather.StormPresence = StormPresence.None;
                    weather.WindStrength = profile.TypicalStrength;
                    events.Emit(new StormDissipated(state.Date, lod, regionId), lod);
                }
                break;
        }

        // --- Wind shift (small random walk around seasonal dominant) ---
        if (_rng.NextDouble() < 0.15f)
        {
            var oldDir = weather.WindDirection;
            var oldStr = weather.WindStrength;

            weather.WindDirection = ShiftDirection(weather.WindDirection, profile.DominantWind);
            weather.WindStrength = ShiftStrength(weather.WindStrength, profile.TypicalStrength,
                weather.StormPresence == StormPresence.Active);

            if (weather.WindDirection != oldDir || weather.WindStrength != oldStr)
                events.Emit(new WindShifted(state.Date, lod, regionId,
                    weather.WindDirection, weather.WindStrength), lod);
        }

        // --- Visibility ---
        weather.Visibility = DeriveVisibility(weather);
    }

    private void TickDistant(RegionWeather weather, SeasonalProfile profile,
        RegionId regionId, WorldState state, IEventEmitter events)
    {
        if (weather.StormPresence is StormPresence.Active or StormPresence.Dissipating)
        {
            weather.StormDaysRemaining--;
            if (weather.StormDaysRemaining <= 0)
            {
                weather.StormPresence = StormPresence.None;
                weather.WindStrength = profile.TypicalStrength;
                events.Emit(new StormDissipated(state.Date, SimulationLod.Distant, regionId), SimulationLod.Distant);
            }
        }
        else if (_rng.NextDouble() < profile.BaseStormChance * 0.5f)
        {
            weather.StormPresence = StormPresence.Active;
            weather.StormDaysRemaining = _rng.Next(3, 8);
            events.Emit(new StormFormed(state.Date, SimulationLod.Distant, regionId), SimulationLod.Distant);
        }
    }

    private void TrySpawnStorm(RegionWeather weather, SeasonalProfile profile,
        RegionId regionId, WorldState state, IEventEmitter events, SimulationLod lod)
    {
        var chance = state.Date.IsHurricaneSeason
            ? profile.HurricaneChance
            : profile.BaseStormChance;

        if (_rng.NextDouble() < chance)
        {
            weather.StormPresence = StormPresence.Approaching;
            weather.WindStrength = WindStrength.Strong;
            events.Emit(new StormFormed(state.Date, lod, regionId), lod);
        }
    }

    private void TryPropagateStorm(RegionWeather weather, RegionId regionId,
        WorldState state, IEventEmitter events, SimulationLod lod)
    {
        if (!state.Regions.TryGetValue(regionId, out var region)) return;

        foreach (var adjacent in region.AdjacentRegions)
        {
            if (!state.Weather.TryGetValue(adjacent, out var adjWeather)) continue;
            if (adjWeather.StormPresence != StormPresence.None) continue;
            if (_rng.NextDouble() < 0.40f)
            {
                adjWeather.StormPresence = StormPresence.Approaching;
                events.Emit(new StormPropagated(state.Date, lod, regionId, adjacent), lod);
                break; // propagate to at most one neighbour
            }
        }
    }

    private WindDirection ShiftDirection(WindDirection current, WindDirection dominant)
    {
        // 60% chance drift back toward dominant; 40% chance random step
        if (_rng.NextDouble() < 0.60f) return dominant;
        var directions = Enum.GetValues<WindDirection>();
        var idx = ((int)current + (_rng.NextDouble() < 0.5 ? 1 : -1) + directions.Length) % directions.Length;
        return directions[idx];
    }

    private WindStrength ShiftStrength(WindStrength current, WindStrength typical, bool inStorm)
    {
        if (inStorm) return WindStrength.Gale;

        var values = Enum.GetValues<WindStrength>();
        var currentIdx = Array.IndexOf(values, current);
        var typicalIdx = Array.IndexOf(values, typical);

        // Drift toward typical strength
        if (currentIdx < typicalIdx && _rng.NextDouble() < 0.4f)
            return values[Math.Min(currentIdx + 1, values.Length - 1)];
        if (currentIdx > typicalIdx && _rng.NextDouble() < 0.4f)
            return values[Math.Max(currentIdx - 1, 0)];

        return current;
    }

    private static Visibility DeriveVisibility(RegionWeather weather) =>
        weather.StormPresence switch
        {
            StormPresence.Active    => weather.WindStrength >= WindStrength.Hurricane
                ? Visibility.None : Visibility.Poor,
            StormPresence.Approaching  => Visibility.Hazy,
            StormPresence.Dissipating  => Visibility.Hazy,
            _ => weather.WindStrength >= WindStrength.Gale ? Visibility.Hazy : Visibility.Clear,
        };
}
