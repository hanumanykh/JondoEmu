using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis daily events, their tokens and the Fissure (Managers/Events): the rotation, the
/// rankings against the bots' seeded field, the purse and the rune's cap, and the Fissure's wave
/// scaling and reward arithmetic -- on hand-made settings, plus one read of each content file.
/// </summary>
public sealed class StasisEventsTests
{
    private static StasisEvents.EventDef Def(string kind, int botMin = 100, int botMax = 1000) => new(
        kind, kind, 60, 18, 21,
        new Dictionary<string, int[]> { ["I"] = new[] { 25, 15, 10 }, ["II"] = new[] { 50, 30, 20 }, ["III"] = new[] { 100, 75, 50 } },
        12, botMin, botMax, new Dictionary<string, string>(), 0, 3, new Dictionary<int, double>(), 5, 5, 1);

    private static StasisEvents.Settings Rotation(DateOnly anchor) => StasisEvents.Settings.Off with { Anchor = anchor };

    private static Fissure.Band Band(int limit = 0, int validate = 5) => new(
        "abime", "Abîme", 7950, 250, 275, 3, 400, 4, 3, validate, limit, 1600, 3, 20,
        new[] { 4879, 4878 }, Array.Empty<Fissure.Resource>());

    // ─── Rotation ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_rotation_runs_time_attack_poutch_traque_from_the_anchor_and_backwards_too()
    {
        var anchor = new DateOnly(2026, 11, 24);
        var s = Rotation(anchor);
        Assert.Equal(StasisEvents.TimeAttack, StasisEvents.KindOn(anchor, s));
        Assert.Equal(StasisEvents.Poutch, StasisEvents.KindOn(anchor.AddDays(1), s));
        Assert.Equal(StasisEvents.Traque, StasisEvents.KindOn(anchor.AddDays(2), s));
        Assert.Equal(StasisEvents.TimeAttack, StasisEvents.KindOn(anchor.AddDays(3), s));
        Assert.Equal(StasisEvents.Traque, StasisEvents.KindOn(anchor.AddDays(-1), s));
        Assert.Equal(StasisEvents.Poutch, StasisEvents.KindOn(anchor.AddDays(-302), s));
    }

    [Fact]
    public void The_weekend_hour_applies_on_saturday_and_sunday()
    {
        var def = Def(StasisEvents.TimeAttack);
        Assert.Equal(18, StasisEvents.StartHourOf(def, new DateOnly(2026, 11, 24)));   // a Tuesday
        Assert.Equal(21, StasisEvents.StartHourOf(def, new DateOnly(2026, 11, 28)));   // a Saturday
        Assert.Equal(21, StasisEvents.StartHourOf(def, new DateOnly(2026, 11, 29)));
    }

    [Theory]
    [InlineData(150, false, "I")]
    [InlineData(200, false, "I")]
    [InlineData(201, false, "II")]
    [InlineData(300, false, "II")]
    [InlineData(120, true, "III")]
    public void Categories_follow_level_and_evolution(int level, bool evolved, string category)
        => Assert.Equal(category, StasisEvents.Category(level, evolved));

    // ─── Ranking ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_time_ranks_lowest_first_and_damage_highest_first_ties_to_the_player()
    {
        var entries = new[]
        {
            new StasisEvents.Entry("Jondobot-Aa", 0, "I", 600),
            new StasisEvents.Entry("Moi", 42, "I", 600),
            new StasisEvents.Entry("Jondobot-Ab", 0, "I", 300),
        };
        var times = StasisEvents.Rank(entries, lowerIsBetter: true);
        Assert.Equal(new[] { "Jondobot-Ab", "Moi", "Jondobot-Aa" }, times.Select(e => e.Name));
        var damage = StasisEvents.Rank(entries, lowerIsBetter: false);
        Assert.Equal("Moi", damage[0].Name);
    }

    [Fact]
    public void The_podium_pays_its_category_and_nothing_past_third()
    {
        var def = Def(StasisEvents.TimeAttack);
        Assert.Equal(100, StasisEvents.TokensFor(def, "III", 1));
        Assert.Equal(30, StasisEvents.TokensFor(def, "II", 2));
        Assert.Equal(10, StasisEvents.TokensFor(def, "I", 3));
        Assert.Equal(0, StasisEvents.TokensFor(def, "I", 4));
        Assert.Equal(0, StasisEvents.TokensFor(def, "IV", 1));
    }

    [Fact]
    public void The_bots_field_is_seeded_by_day_within_bounds_and_over_three_categories()
    {
        var def = Def(StasisEvents.Poutch, 2500, 14000);
        var day = new DateOnly(2026, 11, 25);
        var a = StasisEvents.BotEntries(def, day);
        var b = StasisEvents.BotEntries(def, day);
        Assert.Equal(a, b);
        Assert.Equal(12, a.Count);
        Assert.All(a, e => Assert.InRange(e.Score, 2500, 14000));
        Assert.All(a, e => Assert.Equal(0, e.CharacterId));
        Assert.Equal(new[] { "I", "II", "III" }, a.Select(e => e.Category).Distinct().OrderBy(c => c));
        Assert.Equal(a.Count, a.Select(e => e.Name).Distinct().Count());
        Assert.NotEqual(a, StasisEvents.BotEntries(def, day.AddDays(3)));
    }

    [Fact]
    public void A_better_result_replaces_the_kept_one_only_when_it_is_better()
    {
        var old = new StasisEvents.Entry("Moi", 1, "I", 500);
        Assert.Equal(400, StasisEvents.Better(old, old with { Score = 400 }, true).Score);
        Assert.Equal(500, StasisEvents.Better(old, old with { Score = 600 }, true).Score);
        Assert.Equal(600, StasisEvents.Better(old, old with { Score = 600 }, false).Score);
        Assert.Equal(7, StasisEvents.Better(null, old with { Score = 7 }, false).Score);
    }

    [Theory]
    [InlineData(3000, 2, 1.0, 3000)]
    [InlineData(3000, 3, 1.25, 3750)]
    [InlineData(6000, 6, 1.0, 3000)]
    [InlineData(4000, 1, 0.8, 3200)]
    [InlineData(0, 1, 1.0, 0)]
    public void The_poutch_counts_three_turns_and_the_class_coefficient(long damage, int turns, double coef, long score)
        => Assert.Equal(score, StasisEvents.PoutchScore(damage, turns, 3, coef));

    [Fact]
    public void The_traque_clues_narrow_down()
        => Assert.Equal(new[] { 6, 4, 2, 1, 1 }, Enumerable.Range(1, 5).Select(StasisEvents.ClueSpread));

    // ─── Tokens ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_purse_pays_only_what_it_holds()
    {
        Assert.Equal((true, 50L), EventTokens.Spend(300, 250));
        Assert.Equal((false, 200L), EventTokens.Spend(200, 250));
        Assert.Equal((false, 200L), EventTokens.Spend(200, -1));
    }

    [Fact]
    public void A_rune_adds_one_percent_up_to_four_per_piece()
    {
        Assert.Equal(1, EventTokens.AfterRune(0));
        Assert.Equal(4, EventTokens.AfterRune(3));
        Assert.Null(EventTokens.AfterRune(4));
    }

    [Fact]
    public void Tokens_and_runes_kept_in_memory()
    {
        EventTokens.InMemoryOnly = true;
        EventTokens.Forget();
        const long who = 987654321;
        Assert.Equal(0, EventTokens.Balance(who));
        Assert.Equal(300, EventTokens.Add(who, 300));
        Assert.True(EventTokens.TrySpend(who, 250));
        Assert.False(EventTokens.TrySpend(who, 51));
        Assert.Equal(50, EventTokens.Balance(who));
        const long uid = 555000111;
        for (int i = 0; i < 4; i++) Assert.NotNull(EventTokens.ApplyRune(uid));
        Assert.Null(EventTokens.ApplyRune(uid));
        Assert.Equal(4, EventTokens.RuneOf(uid));
        Assert.Equal(4, EventTokens.RuneBonusOf(new[] { uid, uid, 1L }));
        EventTokens.Forget();
    }

    // ─── Fissure ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Waves_grow_in_level_and_number_within_their_caps()
    {
        var band = Band();
        Assert.Equal(275, Fissure.MonsterLevel(band, 1));
        Assert.Equal(278, Fissure.MonsterLevel(band, 2));
        Assert.Equal(400, Fissure.MonsterLevel(band, 1000));
        Assert.Equal(4, Fissure.MonsterCount(band, 1, 1));
        Assert.Equal(5, Fissure.MonsterCount(band, 4, 1));
        Assert.Equal(8, Fissure.MonsterCount(band, 100, 1));
        Assert.Equal(4, Fissure.MonsterCount(band with { BaseMonsters = 1 }, 1, 4));
        var members = Fissure.WaveMembers(band, 7, 2, new Random(3), (m, level) => level / 100);
        Assert.Equal(Fissure.MonsterCount(band, 7, 2), members.Count);
        Assert.All(members, m => Assert.Contains(m.Monster, band.Monsters));
        Assert.All(members, m => Assert.Equal(2, m.Grade));
    }

    [Fact]
    public void Rewards_per_wave_and_the_validation_bonus()
    {
        var band = Band(validate: 5);
        Assert.Equal(1600, Fissure.KamasFor(band, 1));
        Assert.Equal(16000, Fissure.KamasFor(band, 10));
        Assert.Equal(3, Fissure.TokensFor(band, 4));
        Assert.Equal(23, Fissure.TokensFor(band, 5));
        Assert.True(Fissure.MoreWaves(band, 500));
        Assert.True(Fissure.MoreWaves(Band(limit: 5), 4));
        Assert.False(Fissure.MoreWaves(Band(limit: 5), 5));
    }

    [Fact]
    public void The_formation_bonus_follows_the_spec_table()
    {
        var table = new[]
        {
            new Fissure.Formation(1, 4, 3, 25, 10), new Fissure.Formation(2, 3, 2, 15, 8),
            new Fissure.Formation(3, 2, 1, 8, 5), new Fissure.Formation(4, 0, 0, 0, 0),
        };
        Assert.Equal(4, Fissure.FormationFor(1, table).Ap);
        Assert.Equal(8, Fissure.FormationFor(2, table).DamagePercent);
        Assert.Equal(0, Fissure.FormationFor(4, table).LifePercent);
        Assert.Same(Fissure.Formation.None, Fissure.FormationFor(6, table));
    }

    [Fact]
    public void A_dofus_is_owed_every_thousand_eligible_waves()
    {
        var rules = new Fissure.DofusRules(1000, new[] { "abime" }, 5, Array.Empty<(string, int)>());
        Assert.False(Fissure.Eligible(Band(), 4, rules));
        Assert.True(Fissure.Eligible(Band(), 5, rules));
        Assert.False(Fissure.Eligible(Band() with { Id = "surface" }, 9, rules));
        Assert.True(Fissure.Eligible(Band() with { Id = "surface" }, 1, rules with { EligibleBands = Array.Empty<string>(), EligibleFromWave = 1 }));
        Assert.Equal(0, Fissure.DofusOwed(999, 0, 1000));
        Assert.Equal(1, Fissure.DofusOwed(1000, 0, 1000));
        Assert.Equal(0, Fissure.DofusOwed(1999, 1, 1000));
        Assert.Equal(1, Fissure.DofusOwed(2000, 1, 1000));
    }

    [Fact]
    public void The_record_counts_waves_and_claims()
    {
        Fissure.InMemoryOnly = true;
        Fissure.Forget();
        const long who = 123456789;
        for (int i = 1; i <= 3; i++) Fissure.Record(who, i, eligible: i >= 2);
        var p = Fissure.ProgressOf(who);
        Assert.Equal((3L, 2L, 3), (p.Waves, p.Eligible, p.Best));
        Assert.False(Fissure.Claim(who));
        Fissure.Forget();
    }

    // ─── Content ────────────────────────────────────────────────────────────────────────

    private static string Content(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "content", "events"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "content", relative);
    }

    [Fact]
    public void The_events_file_reads_with_its_three_events_and_the_rune()
    {
        var s = StasisEvents.Load(Content("events/events.json"));
        Assert.Equal(new[] { StasisEvents.TimeAttack, StasisEvents.Poutch, StasisEvents.Traque }, s.Rotation);
        Assert.Equal(3, s.Events.Count);
        Assert.Equal(new[] { 100, 75, 50 }, s.Events[StasisEvents.TimeAttack].Rewards["III"]);
        Assert.Equal(99090957, s.Events[StasisEvents.Poutch].Map);
        Assert.Equal(1.25, s.Events[StasisEvents.Poutch].ClassCoefficients[7]);
        Assert.Contains(s.Shop, i => i.Rune && i.Price == 250);
    }

    [Fact]
    public void The_fissure_file_reads_its_three_keys_and_six_dofus()
    {
        var s = Fissure.Load(Content("events/fissure.json"));
        Assert.Equal(new[] { 1800, 4650, 7950 }, s.Bands.Select(b => b.Key));
        Assert.Equal(new[] { 5, 15, 0 }, s.Bands.Select(b => b.Limit));
        Assert.Equal(6, s.Dofus.Items.Count);
        Assert.Equal(1000, s.Dofus.WavesNeeded);
        Assert.Equal(4, s.Formations.Count);
    }
}
