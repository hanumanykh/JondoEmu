using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis progression (Managers/Stasis): which offer each level earns, offers queueing and
/// being picked, rerolled and re-picked, the bonuses they add, the Ascension's Éclats and reset,
/// the catch-up multiplier through CharacterProgression.AddExperience, the research tree and the
/// Evolution.
/// </summary>
/// <remarks>
/// The rules read the real content/progression files of the checkout, so a balancing edit that
/// breaks a rule shows here. Profiles live in memory (<see cref="MemoryProgressionStore"/>), and
/// each test builds its own profile with a character id nobody else uses: the store's cache is
/// static, shared with the rest of the suite.
/// </remarks>
public sealed class StasisProgressionTests
{
    private static readonly ProgressionContent Content = LoadContent();
    private static long _nextId = 9_000_000_000L;

    private static ProgressionContent LoadContent()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (File.Exists(Path.Combine(candidate, "dons.json")))
            {
                var content = ProgressionContent.Load(candidate);
                ProgressionContent.Current = content;
                if (ProgressionStore.Backend is not MemoryProgressionStore) ProgressionStore.Backend = new MemoryProgressionStore();
                return content;
            }
        }
        throw new InvalidOperationException("content/progression not found above the test binaries.");
    }

    /// <summary>The content and the memory store installed before any test of the class runs.</summary>
    public StasisProgressionTests() => Assert.NotNull(Content);

    private static ProgressionProfile NewProfile() => new() { CharacterId = Interlocked.Increment(ref _nextId) };

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, Dons.KindLevel)]
    [InlineData(5, Dons.KindAdventure)]
    [InlineData(10, Dons.KindSpell)]
    [InlineData(30, Dons.KindSpell)]
    [InlineData(50, Dons.KindPassive)]
    [InlineData(55, Dons.KindAdventure)]
    [InlineData(199, Dons.KindLevel)]
    [InlineData(200, Dons.KindAdventure)]
    [InlineData(201, null)]
    [InlineData(225, Dons.KindAccomplishment)]
    [InlineData(300, Dons.KindAccomplishment)]
    public void Each_level_earns_the_offer_of_its_kind(int level, string? kind)
        => Assert.Equal(kind, Dons.KindOf(level, Content));

    [Fact]
    public void Several_levels_at_once_queue_one_offer_each_oldest_first_and_never_twice()
    {
        var p = NewProfile();
        Assert.Equal(11, Dons.QueueOffers(p, Content, 8, 1, 12));
        Assert.Equal(Enumerable.Range(2, 11), p.Pending.Select(o => o.Level));
        Assert.All(p.Pending, o => Assert.Equal(3, o.Cards.Count));
        // A spell level offers the class's spell dons (SpellDonsTests has the rules).
        Assert.All(p.Pending.Single(o => o.Level == 10).Cards, c => Assert.StartsWith("iop-", c));
        Assert.Equal(0, Dons.QueueOffers(p, Content, 8, 1, 12));

        // The class passives: all of the breed's, for the Iop his own.
        Dons.QueueOffers(p, Content, 8, 49, 50);
        Assert.Equal(new[] { "iop_terre", "iop_puissance" }, p.Pending.Last().Cards);
    }

    [Fact]
    public void An_accomplishment_also_brings_a_spell_offer_shown_at_its_own_level()
    {
        var p = NewProfile();
        Assert.Equal(2, Dons.QueueOffers(p, Content, 8, 224, 225));
        Assert.Contains(p.Pending, o => o.Level == 225 && o.Kind == Dons.KindAccomplishment);
        var spell = Assert.Single(p.Pending, o => o.Kind == Dons.KindSpell);
        Assert.Equal(Dons.SpellPointLevelOffset + 225, spell.Level);
        Assert.Equal(225, Dons.RealLevel(spell.Level));
        Assert.All(spell.Cards, c => Assert.StartsWith("iop-", c));
        Assert.Equal(0, Dons.QueueOffers(p, Content, 8, 224, 225));
    }

    [Fact]
    public void The_fourth_card_research_adds_a_card()
    {
        var p = NewProfile();
        p.Research["quatrieme_carte"] = 1;
        Dons.QueueOffers(p, Content, 1, 1, 3);
        Assert.All(p.Pending, o => Assert.Equal(4, o.Cards.Count));
    }

    [Fact]
    public void The_characteristic_card_wants_a_characteristic_and_adds_it_on_top()
    {
        var p = NewProfile();
        p.Pending.Add(new DonOffer { Level = 2, Kind = Dons.KindLevel, Cards = { "carac", "vitalite", "pods" } });
        Assert.Equal("carac-requise", Dons.Pick(p, Content, 1, null, out _));
        Assert.Equal("carte-inconnue", Dons.Pick(p, Content, 4, null, out _));
        Assert.Equal("ok", Dons.Pick(p, Content, 1, "Agilité", out var kept));
        Assert.Equal(Stat.Agility, kept!.Stat);
        Assert.Empty(p.Pending);

        p.Pending.Add(new DonOffer { Level = 3, Kind = Dons.KindLevel, Cards = { "carac", "vitalite", "pods" } });
        Assert.Equal("ok", Dons.Pick(p, Content, 2, null, out _));

        var bonuses = Dons.BonusesOf(p, Content);
        Assert.Equal(3, bonuses[Stat.Agility]);
        Assert.Equal(20, bonuses[Stat.Vitality]);
        Assert.Equal("aucune-offre", Dons.Pick(p, Content, 1, null, out _));
    }

    [Fact]
    public void Adventure_ranks_stack_and_discovery_is_experience_not_a_characteristic()
    {
        var p = NewProfile();
        for (int level = 5; level <= 15; level += 5)
        {
            p.Pending.Add(new DonOffer { Level = level, Kind = Dons.KindAdventure, Cards = { "aventure_decouverte", "aventure_pods" } });
            Assert.Equal("ok", Dons.Pick(p, Content, level == 15 ? 2 : 1, null, out _));
        }
        Assert.Equal(2, Dons.RankOf(p, "aventure_decouverte"));
        Assert.Equal(4, Dons.DiscoveryPercent(p, Content));
        var bonuses = Dons.BonusesOf(p, Content);
        Assert.Equal(50, bonuses[Stat.Pods]);
        Assert.False(bonuses.ContainsKey(Dons.XpPercent));
        Assert.Equal(1.04, Dons.ExperienceFactor(p, 10, Content), 6);
    }

    [Fact]
    public void A_reroll_is_once_per_offer_paid_in_kamas_unless_a_free_one_is_left()
    {
        var p = NewProfile();
        Dons.QueueOffers(p, Content, 8, 1, 2);
        long kamas = 1_500;
        Assert.Equal("kamas", Dons.Reroll(p, Content, 8, ref kamas, out _));
        kamas = 10_000;
        Assert.Equal("ok", Dons.Reroll(p, Content, 8, ref kamas, out long paid));
        Assert.Equal(2_000, paid);
        Assert.Equal(8_000, kamas);
        Assert.True(p.Pending[0].Rerolled);
        Assert.Equal("deja-relancee", Dons.Reroll(p, Content, 8, ref kamas, out _));

        p.Research["relance"] = 1;
        Dons.QueueOffers(p, Content, 8, 2, 3);
        p.Pending.RemoveAt(0);
        Assert.Equal("ok", Dons.Reroll(p, Content, 8, ref kamas, out paid));
        Assert.Equal(0, paid);
        Assert.Equal(1, p.RerollsUsed);
    }

    [Fact]
    public void A_refonte_moves_the_characteristic_points_once_per_cycle()
    {
        var p = NewProfile();
        p.Dons.Add(new ChosenDon { Level = 2, Card = "carac", Stat = Stat.Strength, Amount = 3 });
        p.Dons.Add(new ChosenDon { Level = 3, Card = "carac", Stat = Stat.Strength, Amount = 3 });
        Assert.Equal("ok", Dons.Repick(p, Content, "force", "chance", out int moved));
        Assert.Equal(6, moved);
        Assert.Equal(6, Dons.BonusesOf(p, Content)[Stat.Chance]);
        Assert.Equal("plus-de-refonte", Dons.Repick(p, Content, "chance", "force", out _));
        p.Research["seconde_refonte"] = 1;
        Assert.Equal("ok", Dons.Repick(p, Content, "chance", "force", out _));
    }

    [Theory]
    [InlineData(49, 0)]
    [InlineData(50, 10)]
    [InlineData(100, 40)]
    [InlineData(200, 160)]
    [InlineData(300, 360)]
    public void Eclats_follow_the_content_formula(int level, long eclats)
        => Assert.Equal(eclats, Ascension.EclatsFor(level, NewProfile(), Content));

    [Fact]
    public void Moisson_adds_its_percent_of_eclats()
    {
        var p = NewProfile();
        p.Research["moisson"] = 2;
        Assert.Equal(432, Ascension.EclatsFor(300, p, Content));
    }

    [Fact]
    public void An_ascension_resets_to_level_one_pays_eclats_and_clears_the_dons()
    {
        var p = NewProfile();
        var state = new SessionState { CharacterId = p.CharacterId, CharacterLevel = 120, StatStrength = 300, ScrolledStrength = 101, Kamas = 5 };
        p.Dons.Add(new ChosenDon { Level = 2, Card = "carac", Stat = Stat.Strength, Amount = 3 });
        p.Pending.Add(new DonOffer { Level = 3, Kind = Dons.KindLevel, Cards = { "pods" } });
        p.Research["fortune"] = 1;

        Assert.Equal("niveau", Ascension.CannotAscend(p, 49, Content));
        Assert.Null(Ascension.CannotAscend(p, 120, Content));
        var outcome = Ascension.Ascend(state, p, Content);

        Assert.Equal((120, 1, 57L), (outcome.FromLevel, outcome.ToLevel, outcome.Eclats));
        Assert.Equal(57, p.Eclats);
        Assert.Equal(1, p.Ascensions);
        Assert.Equal(120, p.BestLevel);
        Assert.Empty(p.Dons);
        Assert.Empty(p.Pending);
        Assert.Equal(0, state.StatStrength);
        Assert.Equal(101, state.ScrolledStrength);
        Assert.Equal(5, state.Kamas);
        Assert.Equal(CharacteristicRules.TotalCapitalForLevel(1), state.CharacterRemainingPoints);
        // The research stays: Fortune still adds its prospecting.
        Assert.Equal(10, Dons.BonusesOf(p, Content)[Stat.Prospecting]);
    }

    [Fact]
    public void Catch_up_doubles_experience_until_the_best_level_and_rattrapage_raises_it_to_nine()
    {
        var p = NewProfile();
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 10, Content));
        p.Ascensions = 1;
        p.BestLevel = 100;
        Assert.Equal(2.0, Dons.ExperienceFactor(p, 10, Content));
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 100, Content));
        p.Research["rattrapage"] = 7;
        Assert.Equal(9.0, Dons.ExperienceFactor(p, 99, Content));
    }

    [Fact]
    public void AddExperience_goes_through_the_catch_up_hook_and_queues_the_offers()
    {
        long id = Interlocked.Increment(ref _nextId);
        var p = ProgressionStore.Of(id);
        p.Ascensions = 1;
        p.BestLevel = 100;
        var state = new SessionState { CharacterId = id, CharacterLevel = 1, Experience = 0 };
        var gain = CharacterProgression.AddExperience(state, 100);
        Assert.Equal(200, gain.Added);
        Assert.Equal(200, state.Experience);
        if (gain.LeveledUp) Assert.Equal(gain.NewLevel - 1, p.Pending.Count);

        // No character, no hook: the bots' and the tests' sessions are untouched.
        var nobody = new SessionState { CharacterLevel = 1 };
        Assert.Equal(100, CharacterProgression.AddExperience(nobody, 100).Added);
    }

    [Fact]
    public void Research_costs_eclats_rank_by_rank_up_to_its_maximum()
    {
        var p = NewProfile();
        Assert.Equal("eclats", Ascension.BuyResearch(p, Content, "quatrieme_carte"));
        p.Eclats = 600;
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "quatrieme_carte"));
        Assert.Equal(100, p.Eclats);
        Assert.Equal("rang-max", Ascension.BuyResearch(p, Content, "quatrieme_carte"));
        Assert.Equal("recherche-inconnue", Ascension.BuyResearch(p, Content, "nope"));
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "rattrapage"));
        Assert.Equal(80, Ascension.NextCost(p, Content.ResearchOf("rattrapage")!));
        Assert.Equal(3, Ascension.CatchUpMultiplier(p, Content));
    }

    [Fact]
    public void An_evolution_needs_level_eclats_and_dungeons_then_keeps_the_dons_up_to_200()
    {
        var p = NewProfile();
        var state = new SessionState { CharacterId = p.CharacterId, CharacterLevel = 300, StatVitality = 500 };
        Assert.Equal("niveau", Ascension.CannotEvolve(p, 299, Content));
        Assert.Equal("eclats", Ascension.CannotEvolve(p, 300, Content));
        p.Eclats = 1_200;
        var before = Ascension.TierDungeonsDone;
        try
        {
            Ascension.TierDungeonsDone = id => id != p.CharacterId;
            Assert.Equal("donjons", Ascension.CannotEvolve(p, 300, Content));
        }
        finally
        {
            Ascension.TierDungeonsDone = before;
        }
        Assert.Null(Ascension.CannotEvolve(p, 300, Content));

        p.Dons.Add(new ChosenDon { Level = 150, Card = "pods" });
        p.Dons.Add(new ChosenDon { Level = 200, Card = "aventure_pods" });
        p.Dons.Add(new ChosenDon { Level = 225, Card = "acc_pa" });
        var outcome = Ascension.Evolve(state, p, Content);

        Assert.Equal((300, 200, 2), (outcome.FromLevel, outcome.ToLevel, outcome.DonsKept));
        Assert.True(p.Evolved);
        Assert.Equal(200, p.Eclats);
        Assert.DoesNotContain(p.Dons, d => d.Level > 200);
        Assert.Equal(0, state.StatVitality);
        Assert.Equal("evolue", Ascension.CannotAscend(p, 250, Content));
        Assert.Equal("deja-evolue", Ascension.CannotEvolve(p, 300, Content));

        // Climbing back, only the accomplishments past 200 are offered again, each with its spell
        // offer (an accomplishment's spell point, kept under SpellPointLevelOffset + its level).
        Assert.Equal(8, Dons.QueueOffers(p, Content, 8, 200, 300));
        Assert.Equal(new[] { 225, 250, 275, 300 }, p.Pending.Where(o => o.Kind == Dons.KindAccomplishment).Select(o => o.Level));
        Assert.Equal(new[] { 225, 250, 275, 300 }, p.Pending.Where(o => o.Kind == Dons.KindSpell).Select(o => Dons.RealLevel(o.Level)));
    }

    [Fact]
    public void IsEvolved_reads_the_stored_profile()
    {
        long id = Interlocked.Increment(ref _nextId);
        Assert.False(Ascension.IsEvolved(id));
        ProgressionStore.Of(id).Evolved = true;
        Assert.True(Ascension.IsEvolved(id));
        Assert.False(Ascension.IsEvolved(0));
    }
}
