using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis tiers (Managers/Stasis/Tiers.cs): the experience gates from 200, the tier lists of
/// content/progression/tiers.json, the key brackets and the weekly free entry's Tuesday reset.
/// </summary>
/// <remarks>
/// The gate is tested through its pure core, <see cref="Tiers.Allowed"/>, with a made-up floor
/// table (level x 1000), so it does not depend on the experience curve nor on a database.
/// </remarks>
public sealed class TierProgressionTests
{
    private static readonly TierFile File = LoadFile();

    private static TierFile LoadFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (System.IO.File.Exists(Path.Combine(candidate, "tiers.json"))) return Tiers.Load(candidate);
        }
        throw new InvalidOperationException("content/progression/tiers.json not found above the test binaries.");
    }

    private static readonly int[] Levels = { 200, 225, 250, 275, 300 };
    private static long Floor(int level) => level * 1000L;

    [Fact]
    public void Tier_lists_load_with_six_six_seven_seven_and_nine_distinct_dungeons()
    {
        Assert.Equal(Levels, File.Tiers.Select(t => t.Level).ToArray());
        Assert.Equal(new[] { 6, 6, 7, 7, 9 }, File.Tiers.Select(t => t.Dungeons.Count).ToArray());
        var all = File.Tiers.SelectMany(t => t.Dungeons).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(200, File.GateFromLevel);
        Assert.Equal(3, File.Hardcore.Life);
        Assert.Equal(2.25, File.Hardcore.Damage);
        Assert.Equal(2, File.Hardcore.Drop);
        Assert.True(File.KeysNeverDrop);
    }

    [Fact]
    public void Below_the_next_floor_nothing_is_cut()
        => Assert.Equal(500, Tiers.Allowed(210_000, 500, Levels, 200, _ => false, Floor));

    [Fact]
    public void An_undone_tier_stops_the_experience_at_the_next_floor_and_the_rest_is_lost()
    {
        Assert.Equal(1_000, Tiers.Allowed(224_000, 50_000, Levels, 200, _ => false, Floor));
        // Already at the floor: nothing more.
        Assert.Equal(0, Tiers.Allowed(225_000, 50_000, Levels, 200, _ => false, Floor));
        // Already past it (no gate before): nothing taken back, nothing added.
        Assert.Equal(0, Tiers.Allowed(260_000, 50_000, Levels, 200, _ => false, Floor));
    }

    [Fact]
    public void The_lowest_undone_tier_gates_whatever_the_level()
    {
        // 200 done, 225 not: stops at 250.
        Assert.Equal(250_000 - 230_000, Tiers.Allowed(230_000, 100_000, Levels, 200, t => t == 200, Floor));
        // A level 190 character with a huge gain stops at 225.
        Assert.Equal(225_000 - 190_000, Tiers.Allowed(190_000, 1_000_000, Levels, 200, _ => false, Floor));
        Assert.Equal(200, Tiers.GatingTier(1_190_000, Levels, 200, _ => false, Floor));
    }

    [Fact]
    public void All_tiers_done_or_the_last_tier_never_gate()
    {
        Assert.Equal(90_000, Tiers.Allowed(220_000, 90_000, Levels, 200, _ => true, Floor));
        // 275 done: the 300 tier (the cap) does not gate.
        Assert.Equal(10_000_000, Tiers.Allowed(280_000, 10_000_000, Levels, 200, t => t != 300, Floor));
        Assert.Equal(0, Tiers.Allowed(280_000, 0, Levels, 200, _ => false, Floor));
    }

    [Fact]
    public void A_state_without_character_is_never_gated()
    {
        var state = new SessionState { CharacterLevel = 250, Experience = 0 };
        Assert.Equal(123, Tiers.LimitExperience(state, 123));
    }

    [Fact]
    public void Completions_count_only_since_the_last_ascension_and_in_hardcore_once_evolved()
    {
        var previous = Tiers.Backend;
        Tiers.Backend = new MemoryTierStore();
        var previousContent = Tiers.Current;
        Tiers.Current = File;
        if (ProgressionStore.Backend is not MemoryProgressionStore) ProgressionStore.Backend = new MemoryProgressionStore();
        try
        {
            long id = 9_100_000_001L;
            var profile = ProgressionStore.Of(id);
            foreach (int d in File.Tier(200)!.Dungeons) Tiers.RecordCompletion(id, d, hardcore: false);
            Assert.True(Tiers.TierDone(id, 200));
            Assert.False(Tiers.TierDone(id, 225));

            profile.Ascensions++;
            Assert.False(Tiers.TierDone(id, 200));

            foreach (int d in File.Tier(200)!.Dungeons) Tiers.RecordCompletion(id, d, hardcore: false);
            Assert.True(Tiers.TierDone(id, 200));
            profile.Evolved = true;
            Assert.False(Tiers.TierDone(id, 200));
            foreach (int d in File.Tier(200)!.Dungeons) Tiers.RecordCompletion(id, d, hardcore: true);
            Assert.True(Tiers.TierDone(id, 200));

            // A dungeon that is no tier's is not recorded.
            Tiers.RecordCompletion(id, 1, hardcore: false);
            Assert.DoesNotContain(1, Tiers.DoneDungeons(id));
        }
        finally
        {
            Tiers.Backend = previous;
            Tiers.Current = previousContent;
        }
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(40, 500)]
    [InlineData(41, 1500)]
    [InlineData(190, 10000)]
    [InlineData(200, 15000)]
    [InlineData(201, 20000)]
    [InlineData(250, 25000)]
    [InlineData(275, 30000)]
    [InlineData(300, 40000)]
    public void Key_prices_follow_the_ten_brackets(int level, long price)
    {
        Assert.Equal(10, File.KeyBrackets.Count);
        Assert.Equal(price, File.BracketFor(level)!.Price);
    }

    [Fact]
    public void Brackets_cover_one_to_three_hundred_without_holes()
    {
        for (int level = 1; level <= 300; level++) Assert.NotNull(File.BracketFor(level));
        Assert.Null(File.BracketFor(0));
    }

    [Theory]
    [InlineData("2026-11-23 23:59", "2026-11-17")] // Monday: still last week's
    [InlineData("2026-11-24 00:00", "2026-11-24")] // Tuesday 00:00: the new week
    [InlineData("2026-11-29 12:00", "2026-11-24")]
    public void The_free_entry_week_turns_over_on_tuesday_midnight(string now, string week)
    {
        var at = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(week, DungeonKeyring.WeekKey(at));
        Assert.Equal(DayOfWeek.Tuesday, DungeonKeyring.NextReset(at).DayOfWeek);
        Assert.True(DungeonKeyring.NextReset(at) > at);
    }

    [Fact]
    public void The_gate_message_is_french_and_names_what_remains()
    {
        string text = Tiers.GateText(225, new[] { "A", "B" });
        Assert.Contains("Palier 225", text);
        Assert.Contains("niveau 250", text);
        Assert.Contains("A, B", text);
    }
}
