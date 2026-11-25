using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The bosses' Dofus at fixed Stasis rates (Managers/Stasis/DofusDrops, content/economy/dofus_drops.json,
/// spec §13) and the monster cards' Raffineur (MonsterCards.Refine, spec §14).
/// </summary>
public sealed class StasisDofusCardsTests
{
    private static string ContentDir(string sub, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", sub);
            if (File.Exists(Path.Combine(candidate, file))) return Path.Combine(candidate, file);
        }
        throw new InvalidOperationException($"content/{sub}/{file} not found");
    }

    // ─── Dofus ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DofusDrops.Entry> DofusFile() => DofusDrops.Read(ContentDir("economy", "dofus_drops.json"));

    [Fact]
    public void The_dofus_file_reads_with_the_spec_rates()
    {
        var rows = DofusFile();
        double Rate(int monster, int item) => rows.Single(r => r.Monster == monster && r.Item == item).Percent;
        Assert.Equal(1, Rate(147, 19629));     // Argenté, Bouftou Royal
        Assert.Equal(1, Rate(928, 19629));     // Argenté, Mob l'Éponge
        Assert.Equal(1, Rate(799, 19629));     // Argenté, Tournesol Affamé
        Assert.Equal(0.5, Rate(180, 972));     // Cawotte, Wa Wabbit
        Assert.Equal(0.5, Rate(3460, 972));    // Cawotte, Wa Wobot
        Assert.Equal(0.1, Rate(827, 694));     // Pourpre, Minotot
        Assert.Equal(0.05, Rate(121, 694));    // Pourpre, Minotoror
        Assert.Equal(0.3, Rate(2986, 737));    // Émeraude, Koulosse
        Assert.Equal(0.1, Rate(232, 737));     // Émeraude, Meulou
        Assert.Equal(0.3, Rate(5806, 6980));   // Vulbis, Torkélonia
        Assert.Equal(0.08, Rate(854, 6980));   // Vulbis, Crocabulia
        Assert.Equal(0.5, Rate(3416, 7043));   // Glaces, Harebourg
        foreach (int glace in new[] { 3384, 3391, 3397, 3409 }) Assert.Equal(0.3, Rate(glace, 7043));
        Assert.Equal(0.2, Rate(3564, 7114));   // Ébène, Ombre
        Assert.Equal(rows.Count, rows.Select(r => (r.Monster, r.Item)).Distinct().Count());
    }

    [Fact]
    public void A_dofus_chance_is_its_base_times_prospecting_capped_at_100()
    {
        var row = new DofusDrops.Entry(147, 19629, 1);
        Assert.Equal(1, DofusDrops.Chance(row, 1.0));
        Assert.Equal(2.5, DofusDrops.Chance(row, 2.5), 6);
        Assert.Equal(1, DofusDrops.Chance(row, 0.5));    // never below its base
        Assert.Equal(100, DofusDrops.Chance(new DofusDrops.Entry(1, 2, 80), 2.5));
    }

    [Fact]
    public void The_installed_rows_say_which_items_are_fixed_and_who_drops_them()
    {
        var before = DofusDrops.All;
        try
        {
            DofusDrops.All = new[] { new DofusDrops.Entry(147, 19629, 1), new DofusDrops.Entry(121, 694, 0.05) };
            Assert.True(DofusDrops.IsFixed(19629));
            Assert.True(DofusDrops.IsFixed(694));
            Assert.False(DofusDrops.IsFixed(737));
            Assert.Equal(new[] { 19629 }, DofusDrops.For(147).Select(e => e.Item).ToArray());
            Assert.Empty(DofusDrops.For(928));
        }
        finally
        {
            DofusDrops.All = before;
        }
    }

    // ─── Raffineur de cartes ────────────────────────────────────────────────────────────

    private static CardsFile Cards() => MonsterCards.Load(ContentDir("progression", "cards.json"));

    private static List<CardFamily> Families(CardsFile c) => MonsterCards.Group(new[]
    {
        new CardMonster(1, "Bouftou", 5, 10), new CardMonster(2, "Bouftou Royal", 12, 10), new CardMonster(3, "Boufton", 3, 10),
        new CardMonster(4, "Larve", 60, 11), new CardMonster(5, "Larve bleue", 62, 11), new CardMonster(6, "Larve verte", 64, 11),
        new CardMonster(7, "Dragon", 180, 13), new CardMonster(8, "Dragon rouge", 185, 13), new CardMonster(9, "Dragon noir", 190, 13),
    }, c);

    [Fact]
    public void The_cards_file_reads_the_refiner_caps()
    {
        var c = Cards();
        Assert.Equal(10, c.RefinerMaxCards);
        Assert.Equal(new[] { 75.0, 45.0, 25.0 }, c.RefinerCaps.ToArray());
        Assert.Equal(4, c.RarityMinLevels.Count);
    }

    [Fact]
    public void Rarity_follows_the_monster_level()
    {
        var c = new CardsFile();
        Assert.Equal(0, MonsterCards.RarityOf(new CardMonster(1, "a", 5, 1), c));
        Assert.Equal(1, MonsterCards.RarityOf(new CardMonster(1, "a", 60, 1), c));
        Assert.Equal(2, MonsterCards.RarityOf(new CardMonster(1, "a", 150, 1), c));
        Assert.Equal(3, MonsterCards.RarityOf(new CardMonster(1, "a", 200, 1), c));
        Assert.Equal("Légendaire", MonsterCards.RarityName(3));
    }

    [Fact]
    public void The_refine_chance_grows_with_the_lot_and_never_passes_its_cap()
    {
        var c = new CardsFile();
        var commune = new CardMonster(1, "a", 59, 1);
        var rare = new CardMonster(2, "b", 119, 1);
        var epique = new CardMonster(3, "c", 179, 1);
        var legendaire = new CardMonster(4, "d", 200, 1);
        Assert.True(MonsterCards.RefineChance(commune, 1, c) < MonsterCards.RefineChance(commune, 5, c));
        Assert.True(MonsterCards.RefineChance(commune, 5, c) < MonsterCards.RefineChance(commune, 10, c));
        Assert.InRange(MonsterCards.RefineChance(commune, 10, c), 70, 75);
        Assert.InRange(MonsterCards.RefineChance(rare, 10, c), 40, 45);
        Assert.InRange(MonsterCards.RefineChance(epique, 10, c), 20, 25);
        Assert.Equal(MonsterCards.RefineChance(commune, 10, c), MonsterCards.RefineChance(commune, 50, c));   // the lot stops at 10
        Assert.Equal(0, MonsterCards.RefineChance(legendaire, 10, c));
        // A full lot worth the next rarity's floor reaches the cap and no more.
        var flat = new CardsFile { SellPerLevelOrbes = 0 };
        Assert.Equal(75, MonsterCards.RefineChance(commune, 10, flat));
        Assert.Equal(45, MonsterCards.RefineChance(rare, 10, flat));
        Assert.Equal(25, MonsterCards.RefineChance(epique, 10, flat));
    }

    [Fact]
    public void A_failed_refine_loses_the_lot()
    {
        var c = new CardsFile { RefinerCaps = new() { 0, 0, 0 } };
        var families = Families(c);
        var binder = new CardAccount { AccountId = 1 };
        MonsterCards.AddLoose(binder, Enumerable.Repeat(1, 6));
        var (result, won, chance) = MonsterCards.Refine(binder, 1, 5, families, c, new Random(1));
        Assert.Equal("echec", result);
        Assert.Equal(0, won);
        Assert.Equal(0, chance);
        Assert.Equal(1, binder.Loose[1]);      // the one to glue stays
        Assert.Equal(1, binder.Loose.Count);
    }

    [Fact]
    public void A_won_refine_gives_one_card_of_the_next_rarity()
    {
        var c = new CardsFile { SellPerLevelOrbes = 0, RefinerCaps = new() { 100, 100, 100 } };
        var families = Families(c);
        var binder = new CardAccount { AccountId = 1 };
        binder.Glued.Add(1);
        MonsterCards.AddLoose(binder, Enumerable.Repeat(1, 10));
        var (result, won, chance) = MonsterCards.Refine(binder, 1, 10, families, c, new Random(3));
        Assert.Equal("ok", result);
        Assert.Equal(100, chance);
        Assert.InRange(won, 4, 6);             // no Rare in the Bouftou family: any Rare
        Assert.False(binder.Loose.ContainsKey(1));
        Assert.Equal(1, binder.Loose[won]);

        // A Rare of a family with an Épique would stay in its family: here none has one, so the Légendaires are out.
        binder.Glued.Add(4);
        MonsterCards.AddLoose(binder, Enumerable.Repeat(4, 3));
        int held = binder.Loose[4];
        var (again, prize, _) = MonsterCards.Refine(binder, 4, 3, families, c, new Random(3));
        Assert.Equal("aucune-cible", again);
        Assert.Equal(0, prize);
        Assert.Equal(held, binder.Loose[4]);   // nothing taken
    }

    [Fact]
    public void The_refiner_refuses_bad_lots_and_takes_nothing()
    {
        var c = new CardsFile();
        var families = Families(c);
        var binder = new CardAccount { AccountId = 1 };
        MonsterCards.AddLoose(binder, Enumerable.Repeat(1, 3));
        MonsterCards.AddLoose(binder, Enumerable.Repeat(7, 4));
        Assert.Equal("lot-taille", MonsterCards.Refine(binder, 1, 0, families, c, new Random(1)).Result);
        Assert.Equal("lot-taille", MonsterCards.Refine(binder, 1, 11, families, c, new Random(1)).Result);
        Assert.Equal("doublons", MonsterCards.Refine(binder, 1, 3, families, c, new Random(1)).Result);   // 2 duplicates only
        Assert.Equal("legendaire", MonsterCards.Refine(binder, 7, 2, families, c, new Random(1)).Result);
        Assert.Equal("carte-inconnue", MonsterCards.Refine(binder, 99, 1, families, c, new Random(1)).Result);
        Assert.Equal(3, binder.Loose[1]);
        Assert.Equal(4, binder.Loose[7]);
    }
}
