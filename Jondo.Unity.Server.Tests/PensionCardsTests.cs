using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis Pension and monster cards (Managers/Stasis/Pension, MonsterCards): incubation
/// places and times, rarity rolls, breeding rules, study experience, the dons «Pension», the
/// binder's packs, glue, completion and family bonus -- plus one read of each content file.
/// </summary>
public sealed class PensionCardsTests
{
    private static string ProgressionDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (File.Exists(Path.Combine(candidate, "pension.json"))) return candidate;
        }
        throw new InvalidOperationException("content/progression not found");
    }

    private static PensionFile File_() => Pension.Load(Path.Combine(ProgressionDir(), "pension.json"));

    private static Breeding.Ride Ride(int id, int species, int generation)
        => new(id, "r" + id, species, generation, 1, 1000 + id, 0, 0, 0, 0);

    [Fact]
    public void The_content_file_reads_with_the_spec_numbers()
    {
        var c = File_();
        Assert.Equal(2, c.Incubation.StartSlots);
        Assert.Equal(8, c.Incubation.MaxSlots);
        Assert.Equal(3, c.Study.Places);
        Assert.Equal(5, c.Study.Percent);
        Assert.Equal(0.70, c.Crate.MountChance);
        Assert.Equal(0, c.Litters["10"]);
        Assert.Equal(10000, c.BreedOrbes["10"]);
        Assert.All(Pension.Rarities, r => Assert.NotEmpty(c.Pets[r]));
    }

    [Fact]
    public void Slots_grow_with_level_100_and_purchases_never_past_the_cap()
    {
        var c = File_();
        var a = new PensionAccount { AccountId = 1 };
        Assert.Equal(2, Pension.Slots(a, c));
        a.Level100 = true;
        a.BoughtSlots = 9;
        Assert.Equal(5, Pension.Slots(a, c));
        c.Incubation.MaxSlots = 4;
        Assert.Equal(4, Pension.Slots(a, c));
    }

    [Fact]
    public void Incubation_fills_places_then_refuses_and_withdraw_loses_the_time()
    {
        var c = File_();
        var a = new PensionAccount { AccountId = 1 };
        for (int i = 1; i <= 3; i++) a.Eggs.Add(new PensionEgg { Id = i, Kind = Pension.KindPet, Rarity = Pension.Rare });
        Assert.Equal("ok", Pension.Incubate(a, c, 1, 1000));
        Assert.Equal(1000 + 30 * 60, a.Eggs[0].ReadyAt);
        Assert.Equal("ok", Pension.Incubate(a, c, 2, 1000));
        Assert.Equal("places-pleines", Pension.Incubate(a, c, 3, 1000));
        Assert.Equal("ok", Pension.SpeedUp(a, c, 1, 1000));
        Assert.Equal(1000 + 15 * 60, a.Eggs[0].ReadyAt);
        Assert.Empty(Pension.Ready(a, 1000));
        Assert.Single(Pension.Ready(a, 1000 + 15 * 60));
        Assert.Equal("ok", Pension.Withdraw(a, 2));
        Assert.Equal(0, a.Eggs[1].ReadyAt);
        Assert.Equal("ok", Pension.Incubate(a, c, 3, 1000));
        Assert.Equal(2, a.Eggs[2].Slot);
    }

    [Fact]
    public void Hatch_times_follow_rarity_and_generation()
    {
        var c = File_();
        Assert.Equal(5, Pension.HatchMinutes(new PensionEgg { Kind = Pension.KindPet, Rarity = Pension.Starter }, c));
        Assert.Equal(60, Pension.HatchMinutes(new PensionEgg { Kind = Pension.KindPet, Rarity = Pension.Legendary }, c));
        Assert.Equal(5, Pension.HatchMinutes(new PensionEgg { Kind = Pension.KindMount, Generation = 1 }, c));
        Assert.Equal(59, Pension.HatchMinutes(new PensionEgg { Kind = Pension.KindMount, Generation = 10 }, c));
    }

    [Fact]
    public void Rarity_goes_up_only_at_its_chance_and_chains()
    {
        var c = File_();
        c.RarityUpChance = 0;
        Assert.Equal(Pension.Common, Pension.HatchRarity(Pension.Common, c, new Random(1)));
        c.RarityUpChance = 1;
        Assert.Equal(Pension.Legendary, Pension.HatchRarity(Pension.Starter, c, new Random(1)));
    }

    [Fact]
    public void Breeding_needs_one_species_one_generation_and_litters_and_always_gives_the_next_generation()
    {
        var c = File_();
        var a = new PensionAccount { AccountId = 1 };
        Assert.Equal("especes", Pension.CanBreed(a, c, (1, Ride(1, 1, 3)), (2, Ride(2, 2, 3))));
        Assert.Equal("generations", Pension.CanBreed(a, c, (1, Ride(1, 1, 3)), (2, Ride(2, 1, 4))));
        Assert.Equal("sterile", Pension.CanBreed(a, c, (1, Ride(1, 1, 10)), (2, Ride(2, 1, 10))));
        Assert.Equal("meme-monture", Pension.CanBreed(a, c, (1, Ride(1, 1, 3)), (1, Ride(1, 1, 3))));

        var x = (Uid: 1L, Ride: Ride(1, 1, 8));
        var y = (Uid: 2L, Ride: Ride(2, 1, 8));
        Assert.Equal("ok", Pension.CanBreed(a, c, x, y));
        var egg = Pension.Breed(a, c, x, y);
        Assert.Equal(9, egg.Generation);
        Assert.Equal(1, egg.Species);
        Assert.Equal(Pension.KindMount, egg.Kind);
        Assert.Equal("portees", Pension.CanBreed(a, c, x, y));
    }

    [Fact]
    public void Study_gives_five_percent_doubled_under_half_level_and_equipped_adds_a_quarter()
    {
        var c = File_();
        Assert.Equal(500, Pension.StudyExperience(10000, 60, 100, false, c));
        Assert.Equal(1000, Pension.StudyExperience(10000, 40, 100, true, c));
        Assert.Equal(2500, Pension.EquippedExperience(10000, false, c));
        Assert.Equal(0, Pension.StudyExperience(0, 1, 100, false, c));
    }

    [Fact]
    public void Pension_gifts_come_once_per_character_and_level()
    {
        var c = File_();
        var a = new PensionAccount { AccountId = 1 };
        // Live, the gifts are dons «Pension» chosen in the level-up offers (Dons): GiftsDue leaves them.
        Assert.All(c.Gifts, g => Assert.True(g.Offer));
        Assert.Empty(Pension.GiftsDue(a, c, 7, 60, new Random(1)));
        foreach (var gift in c.Gifts) gift.Offer = false;
        Assert.Empty(Pension.GiftsDue(a, c, 7, 10, new Random(1)));
        var first = Pension.GiftsDue(a, c, 7, 55, new Random(1));
        Assert.Equal(2, first.Count);
        Assert.Equal(Pension.Starter, first[0].Rarity);
        Assert.True(first.All(e => e.Gift));
        Assert.Empty(Pension.GiftsDue(a, c, 7, 55, new Random(1)));
        Assert.Single(Pension.GiftsDue(a, c, 7, 60, new Random(1)));
        Assert.Equal(3, Pension.GiftsDue(a, c, 8, 60, new Random(1)).Count);
    }

    [Fact]
    public void The_crate_gives_generation_one_mounts_or_rare_and_legendary_pets()
    {
        var c = File_();
        var dice = new Random(3);
        var eggs = Enumerable.Range(0, 400).Select(_ => Pension.FromCrate(c, new[] { 1, 2, 3 }, dice)).ToList();
        Assert.All(eggs.Where(e => e.Kind == Pension.KindMount), e => Assert.Equal(1, e.Generation));
        Assert.All(eggs.Where(e => e.Kind == Pension.KindPet), e => Assert.Contains(e.Rarity, new[] { Pension.Rare, Pension.Legendary }));
        int mounts = eggs.Count(e => e.Kind == Pension.KindMount);
        Assert.InRange(mounts, 230, 330);
    }

    // ─── Cards ──────────────────────────────────────────────────────────────────────────

    private static CardsFile Cards() => MonsterCards.Load(Path.Combine(ProgressionDir(), "cards.json"));

    private static List<CardFamily> Families(CardsFile c) => MonsterCards.Group(new[]
    {
        new CardMonster(1, "Bouftou", 5, 10), new CardMonster(2, "Bouftou Royal", 12, 10), new CardMonster(3, "Boufton", 3, 10),
        new CardMonster(4, "Larve", 60, 11), new CardMonster(5, "Larve bleue", 62, 11), new CardMonster(6, "Larve verte", 64, 11),
        new CardMonster(7, "Seul", 100, 12),
    }, c);

    [Fact]
    public void Families_group_by_race_leaving_the_small_ones_out()
    {
        var c = Cards();
        var families = Families(c);
        Assert.Equal(2, families.Count);
        Assert.Equal("Famille Boufton", families[0].Name);
    }

    [Fact]
    public void Packs_draw_in_the_level_band()
    {
        var c = Cards();
        var drawn = MonsterCards.DrawPack(61, Families(c), c, new Random(2));
        Assert.Equal(c.CardsPerPack, drawn.Count);
        Assert.All(drawn, id => Assert.InRange(id, 4, 6));
    }

    [Fact]
    public void Gluing_consumes_and_a_complete_family_gives_its_bonus()
    {
        var c = Cards();
        var families = Families(c);
        var binder = new CardAccount { AccountId = 1 };
        MonsterCards.AddLoose(binder, new[] { 1, 2, 3, 3 });
        Assert.Equal("carte-absente", MonsterCards.Glue(binder, 4));
        Assert.Equal("ok", MonsterCards.Glue(binder, 1));
        Assert.Equal("ok", MonsterCards.Glue(binder, 2));
        Assert.Empty(MonsterCards.BonusesOf(binder, families, c));
        Assert.Equal("ok", MonsterCards.Glue(binder, 3));
        Assert.Equal(1, MonsterCards.Duplicates(binder, 3));
        Assert.Equal("deja-collee", MonsterCards.Glue(binder, 3));
        Assert.True(MonsterCards.IsComplete(binder, families[0]));
        var bonus = MonsterCards.BonusOf(families[0], c)!;
        var total = MonsterCards.BonusesOf(binder, families, c);
        Assert.Equal(bonus.Value, total[Dons.StatIds[bonus.Stat]]);
        Assert.Equal((0, 3), MonsterCards.Completion(binder, families[1]));
    }
}
