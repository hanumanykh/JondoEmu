using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers.Economy;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis-like economy (Managers.Economy): the Orbes formula, the zone gauge's thresholds and
/// 72-hour reset, the tide schedule and the zone counter's buffs -- on the defaults, not on
/// content/economy, so a tuned file does not break them.
/// </summary>
public sealed class FarmEconomyTests : IDisposable
{
    private DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    public FarmEconomyTests()
    {
        Orbes.Current = new Orbes.Settings();
        Farm.Current = new Farm.Settings();
        Farm.Clock = () => _now;
        FarmProfiles.Persist = false;
        FarmProfiles.Forget();
    }

    public void Dispose()
    {
        Farm.Clock = () => DateTime.UtcNow;
        FarmProfiles.Forget();
    }

    private static List<Orbes.Monster> Group(int level, int count, bool boss = false)
        => Enumerable.Repeat(new Orbes.Monster(level, boss), count).ToList();

    private static void Near(long expected, long actual, long tolerance)
        => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    // ─── Orbes ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50, 35_000, 45_000)]
    [InlineData(150, 180_000, 210_000)]
    [InlineData(200, 280_000, 320_000)]
    public void An_hour_of_farm_pays_the_calibrated_kamas(int level, long low, long high)
    {
        long perHour = Orbes.PerHour(level);
        Assert.InRange(perHour, low, high);
    }

    [Fact]
    public void Bosses_archmonsters_and_tides_multiply_the_fight()
    {
        long plain = Orbes.ForFighter(100, Group(100, 4), false, false);
        Near(plain * 5, Orbes.ForFighter(100, Group(100, 4), true, false), 2);
        Near(plain * 2, Orbes.ForFighter(100, Group(100, 4), false, true), 2);
        Near(plain * 10, Orbes.ForFighter(100, Group(100, 4), true, true), 3);
        Near(plain * 5, Orbes.ForFighter(100, Group(100, 4, boss: true), false, false), 2);
        Assert.Equal(0, Orbes.ForFighter(100, new List<Orbes.Monster>(), true, true));
    }

    [Fact]
    public void A_carried_low_level_and_an_overlevelled_character_earn_less()
    {
        long fair = Orbes.ForFighter(200, Group(200, 4), false, false);
        long carried = Orbes.ForFighter(20, Group(200, 4), false, false);
        Assert.Equal(Orbes.ForFighter(40, Group(40, 4), false, false), carried);
        Assert.True(carried < fair / 10);

        long atLevel = Orbes.ForFighter(100, Group(100, 4), false, false);
        long over = Orbes.ForFighter(180, Group(100, 4), false, false);      // 50 past the tolerance: x0.5
        Near(atLevel / 2, over, 2);
        long far = Orbes.ForFighter(300, Group(100, 4), false, false);       // floored at a quarter
        Near(atLevel / 4, far, 2);
    }

    // ─── Gauge ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_threshold_climbs_and_stops_at_twice_the_base()
    {
        Assert.Equal(160, ZoneGauge.BaseThreshold(200));
        Assert.Equal(52, ZoneGauge.BaseThreshold(20));
        Assert.Equal(160, ZoneGauge.Threshold(200, 0));
        Assert.Equal(240, ZoneGauge.Threshold(200, 1));
        Assert.Equal(320, ZoneGauge.Threshold(200, 2));
        Assert.Equal(320, ZoneGauge.Threshold(200, 9));
    }

    [Fact]
    public void A_full_gauge_pays_its_target_and_waits_without_one()
    {
        var list = new[] { 100, 200 };
        var state = new GaugeState();
        Assert.Equal(0, ZoneGauge.Add(state, 100, 99, list, _now));
        Assert.Equal(0, ZoneGauge.Add(state, 100, 50, list, _now));    // full, no target: waits
        Assert.Equal(100, state.Progress);
        state.Target = 200;
        Assert.Equal(200, ZoneGauge.Claim(state, 100, list));
        Assert.Equal(0, state.Progress);
        Assert.Equal(1, state.Completions);
        Assert.Equal(200, state.Target);                                // kept for the next one
        Assert.Equal(0, ZoneGauge.Add(state, 100, 149, list, _now));   // now 150
        Assert.Equal(200, ZoneGauge.Add(state, 100, 10, list, _now));  // surplus lost
        Assert.Equal(0, state.Progress);
    }

    [Fact]
    public void The_thresholds_fall_back_to_the_base_after_72_hours()
    {
        var list = new[] { 100 };
        var state = new GaugeState { Target = 100 };
        ZoneGauge.Add(state, 100, 100, list, _now);
        ZoneGauge.Add(state, 100, 150, list, _now);
        ZoneGauge.Add(state, 100, 180, list, _now);
        Assert.Equal(2, state.Completions);
        Assert.Equal(180, state.Progress);
        _now = _now.AddHours(71);
        ZoneGauge.Roll(state, 100, _now);
        Assert.Equal(2, state.Completions);
        _now = _now.AddHours(2);
        ZoneGauge.Roll(state, 100, _now);
        Assert.Equal(0, state.Completions);
        Assert.Equal(99, state.Progress);                                 // kept, under the base
    }

    [Fact]
    public void Archmonsters_tides_and_the_buff_double_the_gain()
    {
        Assert.Equal(4, ZoneGauge.Gain(4, false, false, false));
        Assert.Equal(8, ZoneGauge.Gain(4, true, false, false));
        Assert.Equal(16, ZoneGauge.Gain(4, true, true, false));
        Assert.Equal(8, ZoneGauge.Gain(4, false, false, true));
        Assert.Equal(8, ZoneGauge.Gain(4, false, true, true));            // the buff does nothing under a tide
    }

    // ─── Tides ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tides_run_fifty_minutes_then_rest_ten()
    {
        var s = new Farm.TideSettings();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var at0 = Tides.PhaseAt(start, s);
        Assert.True(at0.Active);
        Assert.Equal(50, at0.Left.TotalMinutes, 3);
        var at49 = Tides.PhaseAt(start.AddMinutes(49), s);
        Assert.True(at49.Active);
        var at50 = Tides.PhaseAt(start.AddMinutes(50), s);
        Assert.False(at50.Active);
        Assert.Equal(10, at50.Left.TotalMinutes, 3);
        var at60 = Tides.PhaseAt(start.AddMinutes(60), s);
        Assert.True(at60.Active);
        Assert.Equal(at0.Cycle + 1, at60.Cycle);
        s.Enabled = false;
        Assert.False(Tides.PhaseAt(start, s).Active);
    }

    // ─── Counter ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_buff_lasts_thirty_minutes_in_its_zone_and_rebuying_lengthens_it()
    {
        var profile = FarmProfiles.Of(42);
        ZoneCounter.Extend(profile, 7, ZoneCounter.Density, _now);
        Assert.True(ZoneCounter.IsActive(profile, 7, ZoneCounter.Density, _now.AddMinutes(29)));
        Assert.False(ZoneCounter.IsActive(profile, 8, ZoneCounter.Density, _now));
        Assert.False(ZoneCounter.IsActive(profile, 7, ZoneCounter.Study, _now));
        Assert.False(ZoneCounter.IsActive(profile, 7, ZoneCounter.Density, _now.AddMinutes(30)));

        var end = ZoneCounter.Extend(profile, 7, ZoneCounter.Density, _now.AddMinutes(10));
        Assert.Equal(_now.AddMinutes(60), end);
        Assert.True(ZoneCounter.IsActive(profile, 7, ZoneCounter.Density, _now.AddMinutes(59)));
    }

    [Fact]
    public void The_counter_prices_follow_the_zone_and_jauge_costs_twice_densite()
    {
        long density = ZoneCounter.Price(ZoneCounter.Density, 200);
        Assert.InRange(density, 28_000, 28_500);
        Near(density * 2, ZoneCounter.Price(ZoneCounter.Gauge, 200), 1);
        Assert.Equal(500, ZoneCounter.Price(ZoneCounter.Density, 1));
        Assert.Equal(ZoneCounter.Density, ZoneCounter.Parse("Densité"));
        Assert.Equal(ZoneCounter.Study, ZoneCounter.Parse("ÉTUDE"));
        Assert.Null(ZoneCounter.Parse("recrue"));
    }
}
