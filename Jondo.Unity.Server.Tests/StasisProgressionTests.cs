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
    [InlineData(0, null)]
    [InlineData(1, Dons.KindStart)]
    [InlineData(2, Dons.KindLevel)]
    [InlineData(5, Dons.KindAdventure)]
    [InlineData(10, Dons.KindSpell)]
    [InlineData(30, Dons.KindSpell)]
    [InlineData(50, Dons.KindSpell)]
    [InlineData(60, Dons.KindAdventure)]
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

        // Level 50: its spell point, then two extra offers of the level -- the class passives (all
        // of the breed's, for the Iop his own) and the don «Pension» (a mount egg, one species of three).
        Assert.Equal(3, Dons.QueueOffers(p, Content, 8, 49, 50));
        Assert.Equal(new[] { 50, Dons.PassiveLevelOffset + 50, Dons.PensionLevelOffset + 50 }, p.Pending.Where(o => Dons.RealLevel(o.Level) == 50).Select(o => o.Level));
        // Three passives drawn among the nineteen classes (StasisPassivesTests has the rules).
        var passives = p.Pending.Single(o => o.Kind == Dons.KindPassive).Cards;
        Assert.Equal(3, passives.Count);
        Assert.All(passives, c => Assert.True(Dons.IsPassiveCard(c, Content)));
        Assert.Equal(new[] { "pension_monture_1", "pension_monture_2", "pension_monture_3" }, p.Pending.Single(o => o.Kind == Dons.KindPension).Cards);
    }

    [Fact]
    public void Every_standard_card_has_a_rarity_and_a_rare_card_gives_more()
    {
        var p = NewProfile();
        Dons.QueueOffers(p, Content, 8, 1, 10, new Random(3));
        foreach (var offer in p.Pending.Where(o => o.Kind is Dons.KindLevel or Dons.KindAdventure))
            Assert.Equal(offer.Cards.Count, offer.Rarities.Count);
        Assert.Empty(p.Pending.Single(o => o.Kind == Dons.KindSpell).Rarities);

        p.Pending.Clear();
        p.Pending.Add(new DonOffer { Level = 2, Kind = Dons.KindLevel, Cards = { "carac", "vitalite" }, Rarities = { Dons.Legendaire, Dons.Rare } });
        Assert.Equal("ok", Dons.Pick(p, Content, 1, "force", out var kept));
        Assert.Equal((Dons.Legendaire, 6), (kept!.Rarity, kept.Amount));
        p.Pending.Add(new DonOffer { Level = 3, Kind = Dons.KindLevel, Cards = { "carac", "vitalite" }, Rarities = { Dons.Commun, Dons.Rare } });
        Assert.Equal("ok", Dons.Pick(p, Content, 2, null, out _));
        var bonuses = Dons.BonusesOf(p, Content);
        Assert.Equal(6, bonuses[Stat.Strength]);
        Assert.Equal(30, bonuses[Stat.Vitality]);
        Assert.Equal("+30 Vitalité", Dons.Describe("vitalite", p, Content, Dons.Rare).Description);

        // Stored as "id@rarity", read back the same; an old row without rarity is Commun.
        var (cards, rarities) = DonOffer.Parse(new DonOffer { Cards = { "carac", "pods" }, Rarities = { 2, 0 } }.Stored());
        Assert.Equal(new[] { "carac", "pods" }, cards);
        Assert.Equal(new[] { 2, 0 }, rarities);
        Assert.Equal(new[] { 0 }, DonOffer.Parse("pods").Rarities);
    }

    [Fact]
    public void After_five_offers_without_a_legendary_the_next_has_one()
    {
        var p = NewProfile();
        p.NoLegendaryStreak = 5;
        var offer = Dons.Draw(p, Content, 8, 2, Dons.KindLevel, new Random(1));
        Assert.Contains(Dons.Legendaire, offer.Rarities);
        Assert.Equal(0, p.NoLegendaryStreak);
    }

    [Fact]
    public void Twenty_thousand_cards_draw_the_rarities_at_75_20_5()
    {
        // Pure per-card draw: the streak is reset before each offer so the pity never forces one.
        var p = NewProfile();
        var dice = new Random(12345);
        int[] seen = new int[3];
        int total = 0;
        while (total < 20_000)
        {
            p.NoLegendaryStreak = 0;
            var offer = Dons.Draw(p, Content, 8, 2, Dons.KindLevel, dice);
            foreach (int r in offer.Rarities) { seen[r]++; total++; }
        }
        double commun = seen[0] / (double)total, rare = seen[1] / (double)total, legendary = seen[2] / (double)total;
        Console.WriteLine($"[rarity] {total} cards: Commun {commun:P2}, Rare {rare:P2}, Légendaire {legendary:P2}");
        Assert.InRange(commun, 0.74, 0.76);
        Assert.InRange(rare, 0.19, 0.21);
        Assert.InRange(legendary, 0.045, 0.055);

        // With the pity active, a Légendaire offer comes at least every sixth offer.
        var q = NewProfile();
        int streak = 0, longest = 0;
        for (int i = 0; i < 5_000; i++)
        {
            var offer = Dons.Draw(q, Content, 8, 2, Dons.KindLevel, dice);
            streak = offer.Rarities.Contains(Dons.Legendaire) ? 0 : streak + 1;
            longest = Math.Max(longest, streak);
        }
        Assert.True(longest <= 5, $"longest run without a Légendaire: {longest}");

        // Fortune rank 3: 25 % Rare, 12 % Légendaire.
        var f = NewProfile();
        f.Research["fortune"] = 3;
        seen = new int[3]; total = 0;
        while (total < 20_000)
        {
            f.NoLegendaryStreak = 0;
            foreach (int r in Dons.Draw(f, Content, 8, 2, Dons.KindLevel, dice).Rarities) { seen[r]++; total++; }
        }
        Assert.InRange(seen[1] / (double)total, 0.24, 0.26);
        Assert.InRange(seen[2] / (double)total, 0.11, 0.13);
    }

    [Theory]
    [InlineData(Dons.Commun, 20)]
    [InlineData(Dons.Rare, 30)]
    [InlineData(Dons.Legendaire, 40)]
    public void A_picked_card_gives_its_rarity_value_in_the_bonuses(int rarity, long vitality)
    {
        var p = NewProfile();
        p.Pending.Add(new DonOffer { Level = 2, Kind = Dons.KindLevel, Cards = { "vitalite", "carac" }, Rarities = { rarity, Dons.Commun } });
        Assert.Equal("ok", Dons.Pick(p, Content, 1, null, out var kept));
        Assert.Equal(rarity, kept!.Rarity);
        Assert.Equal(vitality, Dons.BonusesOf(p, Content)[Stat.Vitality]);
    }

    [Fact]
    public void Fortune_raises_the_rarity_chances()
    {
        var p = NewProfile();
        Assert.Equal((0.20, 0.05), Dons.RarityChances(p, Content));
        p.Research["fortune"] = 3;
        Assert.Equal((0.25, 0.12), Dons.RarityChances(p, Content));
    }

    [Fact]
    public void The_first_card_is_mostly_the_characteristic_and_the_last_one_not_taken_recently()
    {
        var p = NewProfile();
        // Ten combat cards taken in the last levels: the last card is never one of them.
        int level = 100;
        foreach (var card in Content.Dons.Level.Where(c => c.Id != "pods").Take(Content.Dons.RecentWindow))
            p.Dons.Add(new ChosenDon { Level = level--, Card = card.Id });
        var recent = Dons.RecentCards(p, Content.Dons.RecentWindow);
        Assert.Equal(Content.Dons.RecentWindow, recent.Count);
        int caracFirst = 0;
        for (int i = 0; i < 50; i++)
        {
            var offer = Dons.Draw(p, Content, 8, 150, Dons.KindLevel, new Random(i));
            Assert.Equal(3, offer.Cards.Count);
            Assert.Equal(offer.Cards.Count, offer.Cards.Distinct().Count());
            if (offer.Cards[0] == "carac") caracFirst++;
            Assert.DoesNotContain(offer.Cards[^1], recent);
        }
        Assert.True(caracFirst >= 35, $"carac first {caracFirst}/50");
    }

    [Fact]
    public void Starting_offer_passive_and_pension_are_given_to_older_characters_and_not_rerolled()
    {
        var p = NewProfile();
        // An older level-70 character: the passive kept at level 50 by the previous rules.
        p.Dons.Add(new ChosenDon { Level = 50, Card = "passif_iop" });
        int changed = Dons.EnsureOffers(p, Content, 8, 70, level => level == 60);
        Assert.Equal(Dons.PassiveLevelOffset + 50, p.Dons.Single(d => d.Card == "passif_iop").Level);
        Assert.Contains(p.Pending, o => o.Level == 1 && o.Kind == Dons.KindStart);
        Assert.Contains(p.Pending, o => o.Level == Dons.PensionLevelOffset + 20 && o.Kind == Dons.KindPension);
        Assert.Contains(p.Pending, o => o.Level == Dons.PensionLevelOffset + 50);
        Assert.DoesNotContain(p.Pending, o => o.Level == Dons.PensionLevelOffset + 60);   // already given by the pension
        Assert.DoesNotContain(p.Pending, o => o.Kind == Dons.KindPassive);
        Assert.Equal(4, changed);
        Assert.Equal(0, Dons.EnsureOffers(p, Content, 8, 70, level => level == 60));

        // The starting offer comes first and is not rerolled; nor is a Pension offer.
        Assert.Equal(Dons.KindStart, p.Pending[0].Kind);
        long kamas = 1_000_000;
        Assert.Equal("non-relancable", Dons.Reroll(p, Content, 8, ref kamas, out _));
        Assert.Equal("ok", Dons.Pick(p, Content, 1, "chance", out _));
        Assert.Equal(Dons.KindPension, p.Pending[0].Kind);
        Assert.Equal("non-relancable", Dons.Reroll(p, Content, 8, ref kamas, out _));
        Assert.Equal("ok", Dons.Pick(p, Content, 2, null, out var egg));
        var choice = Dons.PensionChoiceOf(egg!.Card)!.Value;
        Assert.Equal((Pension.KindPet, 2075), (choice.Kind, choice.Choice));
        Assert.Equal("Don Pension", Dons.CardKindLabel(egg.Card, Content));
    }

    [Fact]
    public void Speed_dons_are_a_walk_percent_and_each_card_has_an_icon()
    {
        var p = NewProfile();
        p.Dons.Add(new ChosenDon { Level = 5, Card = "aventure_vitesse", Rarity = Dons.Legendaire });
        p.Dons.Add(new ChosenDon { Level = 10, Card = "aventure_elan", Rarity = Dons.Commun });
        Assert.Equal(7, Dons.PercentOf(p, Content, Dons.SpeedPercentStat));
        Assert.Equal(2, Dons.PercentOf(p, Content, Dons.AnimationPercentStat));
        Assert.Empty(Dons.BonusesOf(p, Content).Keys.Where(k => k < 0));

        Assert.Equal(("stat", Stat.Vitality), (Dons.IconOf("vitalite", Content).Kind, Dons.IconOf("vitalite", Content).Id));
        Assert.Equal("class", Dons.IconOf("passif_iop", Content).Kind);
        Assert.Equal(("item", 2074), (Dons.IconOf("pension_familier_2074", Content).Kind, Dons.IconOf("pension_familier_2074", Content).Id));
        Assert.Equal("glyph", Dons.IconOf("aventure_vitesse", Content).Kind);
        foreach (var card in Content.Dons.Level.Concat(Content.Dons.Adventure).Concat(Content.Dons.Accomplishments))
            Assert.NotEqual("", Dons.IconOf(card.Id, Content).Glyph);
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
    public void Adventure_ranks_stack_with_their_rarity_values_and_retired_cards_still_count()
    {
        var p = NewProfile();
        for (int level = 5; level <= 15; level += 5)
        {
            p.Pending.Add(new DonOffer
            {
                Level = level, Kind = Dons.KindAdventure, Cards = { "aventure_decouverte", "aventure_pods" },
                Rarities = { level == 10 ? Dons.Legendaire : Dons.Commun, Dons.Commun },
            });
            Assert.Equal("ok", Dons.Pick(p, Content, level == 15 ? 2 : 1, null, out _));
        }
        Assert.Equal(2, Dons.RankOf(p, "aventure_decouverte"));
        var bonuses = Dons.BonusesOf(p, Content);
        // Découverte: +1 Sag +2 PP (Commun), +3 Sag +4 PP (Légendaire).
        Assert.Equal(4, bonuses[Stat.Wisdom]);
        Assert.Equal(6, bonuses[Stat.Prospecting]);
        // aventure_pods is retired: no longer offered, still counted.
        Assert.Equal(50, bonuses[Stat.Pods]);
        Assert.DoesNotContain("aventure_pods", Dons.Draw(p, Content, 8, 20, Dons.KindAdventure).Cards);
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 10, Content), 6);
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
    [InlineData(50, 1)]
    [InlineData(100, 20)]
    [InlineData(125, 42)]
    [InlineData(150, 64)]
    [InlineData(200, 150)]
    [InlineData(225, 400)]
    [InlineData(250, 700)]
    [InlineData(275, 1050)]
    [InlineData(300, 1450)]
    [InlineData(320, 1450)]
    public void Eclats_follow_the_wiki_table(int level, long eclats)
        => Assert.Equal(eclats, Ascension.EclatsFor(level, NewProfile(), Content));

    [Fact]
    public void The_first_research_tree_is_refunded_once_and_fortune_is_kept()
    {
        var p = NewProfile();
        p.Research["moisson"] = 2;       // 80 + 160, gone
        p.Research["rattrapage"] = 2;    // 40 + 80, new prices
        p.Research["tempo"] = 1;         // 50, new meaning
        p.Research["fortune"] = 3;       // same research, kept
        p.Research["memoire_route"] = 1; // same research, kept
        Assert.True(Ascension.MigrateResearch(p));
        Assert.Equal(410, p.Eclats);
        Assert.Equal(0, p.RankOf("moisson"));
        Assert.Equal(0, p.RankOf("rattrapage"));
        Assert.Equal(0, p.RankOf("tempo"));
        Assert.Equal(3, p.RankOf("fortune"));
        Assert.Equal(1, p.RankOf("memoire_route"));
        // Once only: a rank bought afterwards at the new price is not refunded again.
        p.Research["rattrapage"] = 1;
        Assert.False(Ascension.MigrateResearch(p));
        Assert.Equal(410, p.Eclats);
        Assert.Equal(1, p.RankOf("rattrapage"));
    }

    [Fact]
    public void The_research_tree_is_the_wikis()
    {
        Assert.Equal(20, Content.Research.Count);
        Assert.Equal(new[] { "Préparation", "Savoir", "Puissance" }, Content.Research.Select(r => r.Branch).Distinct());
        void Costs(string id, params long[] costs) => Assert.Equal(costs, Content.ResearchOf(id)!.Costs);
        Costs("rattrapage", 20, 40, 60);
        Costs("elan", 30, 50);
        Costs("pas_leger", 10, 20, 30);
        Costs("tempo", 10, 20, 30);
        Costs("memoire_route", 40);
        Costs("relance", 40, 80);
        Costs("fortune", 30, 50, 70);
        Costs("quatrieme_carte", 100);
        Costs("seconde_refonte", 50);
        Costs("metamorphose", 500);
        Costs("polyvalence", 500, 500, 500);
        Costs("dons_duo", 60, 60, 60);
        Costs("bi_element", 1000);
        Costs("resonance", 20, 30, 40, 50, 60);
        Costs("endurance", 20, 30, 40);
        Costs("tenacite", 20, 30, 40);
        Costs("action", 150);
        Costs("cadence", 500);
        Costs("ciblage", 400);
        Costs("evolution", 1000);
        Assert.All(new[] { "metamorphose", "polyvalence", "evolution" }, id => Assert.True(Content.ResearchOf(id)!.Permanent));
        Assert.Equal("+8 % PV max", Ascension.EffectText(Content.ResearchOf("endurance")!, 2, Content));
        Assert.Equal("XP de rattrapage x2,25", Ascension.EffectText(Content.ResearchOf("rattrapage")!, 1, Content));
    }

    [Fact]
    public void Power_research_adds_to_the_bonuses()
    {
        var p = NewProfile();
        p.Dons.Add(new ChosenDon { Level = 2, Card = "carac", Stat = Stat.Intelligence, Amount = 100 });
        p.Dons.Add(new ChosenDon { Level = 3, Card = "carac", Stat = Stat.Agility, Amount = 10 });
        p.Research["action"] = 1;
        p.Research["tenacite"] = 3;
        p.Research["resonance"] = 5;
        var b = Dons.BonusesOf(p, Content);
        Assert.Equal(1, b[Stat.ActionPoints]);
        Assert.Equal(3, b[Dons.StatIds["resFeu"]]);
        Assert.Equal(3, b[Dons.StatIds["resNeutre"]]);
        Assert.Equal(110, b[Stat.Intelligence]);  // +10 %
        Assert.Equal(11, b[Stat.Agility]);

        // Bi-élément: the second element (the next strongest, Air, or the one chosen) gets 60 % of the main's.
        p.Research["resonance"] = 0;
        p.Research["bi_element"] = 1;
        b = Dons.BonusesOf(p, Content);
        Assert.Equal(100, b[Stat.Intelligence]);
        Assert.Equal(70, b[Stat.Agility]);
        Assert.Equal("ok", Ascension.ChooseSecondElement(p, "eau"));
        b = Dons.BonusesOf(p, Content);
        Assert.Equal(60, b[Stat.Chance]);
        Assert.Equal(10, b[Stat.Agility]);
        Assert.Equal(Ascension.UnknownElement, Ascension.ChooseSecondElement(p, "lumiere"));
    }

    [Fact]
    public void Speed_life_and_casts_research_reach_their_hooks()
    {
        long id = Interlocked.Increment(ref _nextId);
        var p = ProgressionStore.Of(id);
        long speed = Dons.SpeedPercent(id), animation = Dons.AnimationPercent(id);
        p.Research["pas_leger"] = 3;
        p.Research["tempo"] = 2;
        p.Research["endurance"] = 3;
        p.Research["cadence"] = 1;
        p.Research["ciblage"] = 1;
        Assert.Equal(speed + 15, Dons.SpeedPercent(id));
        Assert.Equal(animation + 10, Dons.AnimationPercent(id));
        Assert.Equal(12, Ascension.LifePercent(id));

        var fighter = new Jondo.Unity.World.Fights.Fighter { Id = 1 };
        Ascension.ApplyResearchTo(fighter, id);
        Assert.Equal(1, fighter.EverySpell[Jondo.Unity.World.Fights.SpellAspect.CastsPerTurnUp]);
        Assert.Equal(1, fighter.EverySpell[Jondo.Unity.World.Fights.SpellAspect.CastsPerTargetUp]);
    }

    [Fact]
    public void Ordinary_research_is_refunded_in_full_and_permanent_research_is_not()
    {
        var p = NewProfile();
        p.Eclats = 2_000;
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "endurance"));
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "endurance"));
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "metamorphose"));
        Assert.Equal(1_450, p.Eclats);
        Assert.Equal("ok", Ascension.RefundResearch(p, Content, "endurance", out long back));
        Assert.Equal(50, back);
        Assert.Equal(1_500, p.Eclats);
        Assert.Equal(0, p.RankOf("endurance"));
        Assert.Equal(Ascension.NothingToRefund, Ascension.RefundResearch(p, Content, "endurance", out _));
        Assert.Equal(Ascension.PermanentResearch, Ascension.RefundResearch(p, Content, "metamorphose", out _));
        Assert.True(Ascension.CanMetamorphose(p, Content));
        // The Évolution is not bought here: it is the Ascension tab's, and its rank is the evolved flag.
        Assert.Equal(Ascension.EvolutionHere, Ascension.BuyResearch(p, Content, "evolution"));
        Assert.Equal(0, Ascension.RankOf(p, Content.ResearchOf("evolution")!));
        p.Evolved = true;
        Assert.Equal(-1, Ascension.NextCost(p, Content.ResearchOf("evolution")!));
    }

    [Fact]
    public void An_ascension_resets_to_level_one_pays_eclats_and_clears_the_dons()
    {
        var p = NewProfile();
        var state = new SessionState { CharacterId = p.CharacterId, CharacterLevel = 120, StatStrength = 300, ScrolledStrength = 101, Kamas = 5 };
        p.Dons.Add(new ChosenDon { Level = 2, Card = "carac", Stat = Stat.Strength, Amount = 3 });
        p.Pending.Add(new DonOffer { Level = 3, Kind = Dons.KindLevel, Cards = { "pods" } });
        p.Research["action"] = 1;

        Assert.Equal("niveau", Ascension.CannotAscend(p, 49, Content));
        Assert.Null(Ascension.CannotAscend(p, 120, Content));
        var outcome = Ascension.Ascend(state, p, Content);

        Assert.Equal((120, 1, 37L), (outcome.FromLevel, outcome.ToLevel, outcome.Eclats));
        Assert.Equal(37, p.Eclats);
        Assert.Equal(1, p.Ascensions);
        Assert.Equal(120, p.BestLevel);
        Assert.Empty(p.Dons);
        Assert.Empty(p.Pending);
        Assert.Equal(0, state.StatStrength);
        Assert.Equal(101, state.ScrolledStrength);
        Assert.Equal(5, state.Kamas);
        Assert.Equal(CharacteristicRules.TotalCapitalForLevel(1), state.CharacterRemainingPoints);
        // The research stays: Action still adds its PA.
        Assert.Equal(1, Dons.BonusesOf(p, Content)[Stat.ActionPoints]);
    }

    [Fact]
    public void Catch_up_doubles_experience_until_the_best_level_and_rattrapage_and_elan_raise_it()
    {
        var p = NewProfile();
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 10, Content));
        p.Ascensions = 1;
        p.BestLevel = 100;
        Assert.Equal(2.0, Dons.ExperienceFactor(p, 10, Content));
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 100, Content));
        p.Research["rattrapage"] = 1;
        Assert.Equal(2.25, Dons.ExperienceFactor(p, 99, Content));
        p.Research["rattrapage"] = 3;
        Assert.Equal(3.0, Dons.ExperienceFactor(p, 99, Content));
        // Élan: the catch-up runs 20 levels past the best one.
        p.Research["elan"] = 2;
        Assert.Equal(3.0, Dons.ExperienceFactor(p, 119, Content));
        Assert.Equal(1.0, Dons.ExperienceFactor(p, 120, Content));
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
        Assert.Equal(500, p.Eclats);
        Assert.Equal("rang-max", Ascension.BuyResearch(p, Content, "quatrieme_carte"));
        Assert.Equal("recherche-inconnue", Ascension.BuyResearch(p, Content, "nope"));
        Assert.Equal("ok", Ascension.BuyResearch(p, Content, "rattrapage"));
        Assert.Equal(480, p.Eclats);
        Assert.Equal(40, Ascension.NextCost(p, Content.ResearchOf("rattrapage")!));
        Assert.Equal(2.25, Ascension.CatchUpMultiplier(p, Content));
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
        p.Pending.Add(new DonOffer { Level = 300, Kind = Dons.KindLevel, Cards = { "pods" } });
        Assert.Equal(Ascension.PendingDons, Ascension.CannotEvolve(p, 300, Content));
        p.Pending.Clear();

        p.Dons.Add(new ChosenDon { Level = 150, Card = "pods" });
        p.Dons.Add(new ChosenDon { Level = 200, Card = "aventure_pods" });
        p.Dons.Add(new ChosenDon { Level = 225, Card = "acc_pa" });
        var outcome = Ascension.Evolve(state, p, Content);

        Assert.Equal((300, 200, 2), (outcome.FromLevel, outcome.ToLevel, outcome.DonsKept));
        Assert.True(p.Evolved);
        // 1000 spent, an Ascension 300's 1450 paid.
        Assert.Equal(1_650, p.Eclats);
        Assert.Equal(450, outcome.Eclats);
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
