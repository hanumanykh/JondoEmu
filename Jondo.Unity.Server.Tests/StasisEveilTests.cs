using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Éveil des Dofus, Partie I (Managers/Stasis/Eveil.cs): content/progression/eveil.json, the
/// stages, which dungeon wins count for a route, the sacrifice plan and the reward's effects.
/// </summary>
/// <remarks>
/// Only the pure rules and the in-memory store: no database, no session, no fight.
/// </remarks>
public sealed class StasisEveilTests : IDisposable
{
    private static readonly EveilFile File = LoadFile();

    private readonly IEveilStore _previousStore = Eveil.Backend;

    public void Dispose()
    {
        // The repository's own file is what the server reads too.
        Eveil.Current = File;
        Eveil.Backend = _previousStore;
    }

    private static EveilFile LoadFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (System.IO.File.Exists(Path.Combine(candidate, "eveil.json"))) return Eveil.Load(candidate);
        }
        throw new InvalidOperationException("content/progression/eveil.json not found above the test binaries.");
    }

    private static EveilDofus Dofus(string id) => File.Find(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void The_file_has_the_six_dofus_of_the_spec_with_their_levels_and_copies()
    {
        Assert.True(File.Enabled);
        Assert.True(File.RequireHardcore);
        var table = File.Dofus.Select(d => (d.Id, d.Level, d.Copies)).ToArray();
        Assert.Equal(new[]
        {
            ("cawotte", 225, 25), ("glaces", 250, 7), ("emeraude", 250, 7),
            ("pourpre", 275, 8), ("dolmanax", 275, 7), ("vulbis", 300, 6),
        }, table);
        Assert.Equal(new[] { 972, 7043, 737, 694, 13344, 6980 }, File.Dofus.Select(d => d.Item).ToArray());
    }

    [Fact]
    public void Every_dofus_has_a_route_a_boss_and_effects()
    {
        foreach (var d in File.Dofus)
        {
            Assert.NotEmpty(d.Dungeons);
            Assert.All(d.Dungeons, g => Assert.True(g.Id > 0 && g.Name.Length > 0, $"{d.Id}: {g.Id}"));
            Assert.Equal(d.Dungeons.Count, d.Dungeons.Select(g => g.Id).Distinct().Count());
            Assert.True(d.Boss.Monster > 0, d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Boss.Name));
            Assert.NotEmpty(d.Effects);
            Assert.All(d.Approximated, a => Assert.False(string.IsNullOrWhiteSpace(a.Replaces)));
        }
        // The spec's route lengths; the Émeraude's Koulosse dungeon is not in this client.
        Assert.Equal(new[] { 5, 7, 4, 6, 8, 5 }, File.Dofus.Select(d => d.Dungeons.Count).ToArray());
    }

    [Fact]
    public void The_spec_flat_effects_are_in_the_file()
    {
        var cawotte = Dofus("cawotte").Effects.ToDictionary(e => e.Effect, e => e.Value);
        Assert.Equal(200, cawotte[124]);
        Assert.Equal(60, cawotte[160]);
        Assert.Equal(60, cawotte[161]);
        Assert.Equal(800, Dofus("emeraude").Effects.Single(e => e.Effect == 125).Value);
        Assert.Equal(275, Dofus("pourpre").Effects.Single(e => e.Effect == 138).Value);
        Assert.Equal(5, Dofus("glaces").Effects.Count(e => e.Value == 100));
        var vulbis = Dofus("vulbis").Effects.ToDictionary(e => e.Effect, e => e.Value);
        Assert.Equal(1, vulbis[128]);
        Assert.Equal(1, vulbis[117]);
    }

    [Theory]
    [InlineData("Cawotte", "cawotte")]
    [InlineData("émeraude", "emeraude")]
    [InlineData("EMERAUDE", "emeraude")]
    [InlineData("dolma", "dolmanax")]
    [InlineData("Glaces", "glaces")]
    public void A_dofus_is_found_by_its_name_accents_and_case_aside(string word, string id)
        => Assert.Equal(id, File.Find(word)?.Id);

    [Fact]
    public void An_unknown_word_finds_nothing()
    {
        Assert.Null(File.Find("ocre"));
        Assert.Null(File.Find(""));
    }

    [Fact]
    public void The_stages_follow_start_route_and_completion()
    {
        var d = Dofus("vulbis");
        Assert.Equal(EveilStage.NotStarted, Eveil.StageOf(d, null));
        var p = new EveilProgress();
        Assert.Equal(EveilStage.Route, Eveil.StageOf(d, p));
        foreach (var g in d.Dungeons.Take(d.Dungeons.Count - 1)) p.Dungeons.Add(g.Id);
        Assert.Equal(EveilStage.Route, Eveil.StageOf(d, p));
        Assert.Single(Eveil.Remaining(d, p));
        p.Dungeons.Add(d.Dungeons[^1].Id);
        Assert.Equal(EveilStage.Challenge, Eveil.StageOf(d, p));
        Assert.Empty(Eveil.Remaining(d, p));
        p.Completed = true;
        Assert.Equal(EveilStage.Done, Eveil.StageOf(d, p));
    }

    [Fact]
    public void Only_hardcore_wins_of_route_dungeons_after_the_start_count()
    {
        var d = Dofus("glaces");
        int onRoute = d.Dungeons[0].Id;
        // Not started: nothing counts.
        Assert.False(Eveil.Counts(d, null, onRoute, hardcore: true, requireHardcore: true));
        var p = new EveilProgress();
        Assert.True(Eveil.Counts(d, p, onRoute, hardcore: true, requireHardcore: true));
        Assert.False(Eveil.Counts(d, p, onRoute, hardcore: false, requireHardcore: true));
        Assert.True(Eveil.Counts(d, p, onRoute, hardcore: false, requireHardcore: false));
        // Off the route.
        Assert.False(Eveil.Counts(d, p, 1, hardcore: true, requireHardcore: true));
        // Already counted, or the Éveil done.
        p.Dungeons.Add(onRoute);
        Assert.False(Eveil.Counts(d, p, onRoute, hardcore: true, requireHardcore: true));
        p.Completed = true;
        Assert.False(Eveil.Counts(d, p, d.Dungeons[1].Id, hardcore: true, requireHardcore: true));
    }

    [Fact]
    public void A_won_dungeon_is_recorded_for_every_started_dofus_whose_route_has_it_and_only_in_hardcore()
    {
        var store = new MemoryEveilStore();
        Eveil.Backend = store;
        Eveil.Current = File;
        const long me = 4242;
        // The Torre de Solar (109) is on the Pourpre route only.
        Eveil.OnDungeonWon(me, 109, hardcore: true);
        Assert.Empty(store.Of(me));

        store.Start(me, "pourpre", DateTime.Now);
        Eveil.OnDungeonWon(me, 109, hardcore: false);
        Assert.Empty(store.Of(me)["pourpre"].Dungeons);
        Eveil.OnDungeonWon(me, 109, hardcore: true);
        Assert.Equal(new[] { 109 }, store.Of(me)["pourpre"].Dungeons.ToArray());
        // A dungeon of another route records nothing here.
        Eveil.OnDungeonWon(me, 71, hardcore: true);
        Assert.Single(store.Of(me)["pourpre"].Dungeons);
        Assert.False(store.Of(me).ContainsKey("glaces"));
    }

    [Fact]
    public void Starting_again_clears_the_route_and_the_completion()
    {
        var store = new MemoryEveilStore();
        store.Start(7, "cawotte", DateTime.Now);
        store.AddDungeon(7, "cawotte", 52, DateTime.Now);
        store.Complete(7, "cawotte");
        Assert.True(store.Of(7)["cawotte"].Completed);
        store.Start(7, "cawotte", DateTime.Now);
        Assert.False(store.Of(7)["cawotte"].Completed);
        Assert.Empty(store.Of(7)["cawotte"].Dungeons);
    }

    [Fact]
    public void One_may_start_once_at_the_dofus_level_and_again_only_when_repeatable()
    {
        var d = Dofus("cawotte");
        Assert.False(Eveil.MayStart(d, null, 224, repeatable: false));
        Assert.True(Eveil.MayStart(d, null, 225, repeatable: false));
        var begun = new EveilProgress();
        Assert.False(Eveil.MayStart(d, begun, 300, repeatable: true));
        var done = new EveilProgress { Completed = true };
        Assert.False(Eveil.MayStart(d, done, 300, repeatable: false));
        Assert.True(Eveil.MayStart(d, done, 300, repeatable: true));
    }

    [Fact]
    public void The_sacrifice_takes_from_the_stacks_in_order_and_nothing_when_short()
    {
        var plan = Eveil.PlanSacrifice(new (long, int)[] { (1, 3), (2, 0), (3, 10), (4, 5) }, 7);
        Assert.NotNull(plan);
        Assert.Equal(new (long, int)[] { (1, 3), (3, 4) }, plan!.ToArray());
        Assert.Null(Eveil.PlanSacrifice(new (long, int)[] { (1, 3), (2, 2) }, 6));
        Assert.Equal(new (long, int)[] { (1, 1), (2, 1) }, Eveil.PlanSacrifice(new (long, int)[] { (1, 1), (2, 1) }, 2)!.ToArray());
    }

    [Fact]
    public void The_reward_adds_the_file_lines_to_the_template_and_carries_the_label()
    {
        var d = new EveilDofus
        {
            Id = "test",
            Effects = { new EveilEffect { Effect = 124, Value = 200 }, new EveilEffect { Effect = 160, Value = 60 } },
            Approximated = { new EveilEffect { Effect = 160, Value = 30, Replaces = "x" } },
        };
        var template = new[]
        {
            new Equipment.ItemEffect(124, 50, 0, 0),
            new Equipment.ItemEffect(125, 100, 0, 0),
            new Equipment.ItemEffect(Stasis.LabelEffect, 0, 0, 0, "old"),
        };
        var reward = Eveil.RewardEffects(template, d, "Stasis Éveil");
        Assert.Equal(250, reward.Single(e => e.Effect == 124).Value);
        Assert.Equal(100, reward.Single(e => e.Effect == 125).Value);
        Assert.Equal(90, reward.Single(e => e.Effect == 160).Value);
        var label = reward.Single(e => e.Effect == Stasis.LabelEffect);
        Assert.Equal("Stasis Éveil", label.Text);
        Assert.True(Eveil.IsAwakened(reward, "Stasis Éveil"));
        Assert.False(Eveil.IsAwakened(template, "Stasis Éveil"));
        // Never read as an S1-S4 tier, so never fused nor refined.
        Assert.Equal(0, Stasis.TierInEffects(reward));
    }

    [Fact]
    public void The_boss_is_fought_at_the_dofus_level_unless_the_file_says_otherwise()
    {
        Assert.Equal(300, Eveil.BossLevel(Dofus("vulbis")));
        var d = new EveilDofus { Level = 250, Boss = new EveilBoss { Level = 280 } };
        Assert.Equal(280, Eveil.BossLevel(d));
    }

    [Fact]
    public void The_effects_read_in_french_with_their_stand_in_said()
    {
        Assert.Equal("+200 sagesse", Eveil.EffectText(new EveilEffect { Effect = 124, Value = 200 }));
        Assert.Contains("à la place de", Eveil.EffectText(new EveilEffect { Effect = 2812, Value = 4, Replaces = "x" }));
    }

    [Fact]
    public void A_challenge_group_is_remembered_until_forgotten()
    {
        Eveil.RegisterChallenge(-987654, 1, "vulbis");
        Assert.True(Eveil.IsChallenge(-987654));
        Eveil.ForgetChallenge(-987654);
        Assert.False(Eveil.IsChallenge(-987654));
    }
}
