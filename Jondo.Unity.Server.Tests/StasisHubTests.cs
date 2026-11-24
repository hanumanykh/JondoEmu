using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// Stasis fabrication, the Stasis Merchant and the Sanctuary (Managers/Stasis/Fabrication.cs,
/// Sanctuary.cs; CommandHandler.Hub.cs): recipe costs, the perfect S1 roll, quotes, the
/// merchant's prices and rolls, and the Sanctuary's round trip -- on settings written here.
/// </summary>
[Collection("StasisHub")]
public sealed class StasisHubTests : IDisposable
{
    private const int Strength = 118, Vitality = 125;
    private const int Hat = 990001, Boots = 990002, Drop = 990003, HighHat = 990004, LowHat = 990005;
    private const int Wool = 990101, Leather = 990102, Essence = 990201, HighEssence = 990202, Fragment = 990301;

    public StasisHubTests()
    {
        Stasis.Current = new Stasis.Settings();
        Forgemagic.Declare(Strength, 1);
        Forgemagic.Declare(Vitality, 0.2);
        Outfitter.Declare(Strength, 10);
        Outfitter.Declare(Vitality, 11);
        Fabrication.Eligible = t => t.Gid >= 990000 && t.Gid < 990100;
        Declare(Hat, 60, 16);
        Declare(Boots, 120, 11);
        Declare(Drop, 30, 16);
        Declare(HighHat, 195, 16);
        Declare(LowHat, 40, 16);
        RecipeManager.Declare(new RecipeDefinition { ResultId = Hat, ResultLevel = 60 },
                              new RecipeIngredient(Wool, 5), new RecipeIngredient(Leather, 3));
        RecipeManager.Declare(new RecipeDefinition { ResultId = HighHat, ResultLevel = 195 }, new RecipeIngredient(Wool, 10));
        Fabrication.Current = new Fabrication.Settings
        {
            IngredientFactor = 1.5,
            CostPerLevel = 60,
            CostMinimum = 500,
            FormulaEssenceFactor = 2,
            Essences =
            {
                new Fabrication.Essence { MinLevel = 1, MaxLevel = 150, Item = Essence, Quantity = 1 },
                new Fabrication.Essence { MinLevel = 151, MaxLevel = 9999, Item = HighEssence, Quantity = 2 },
            },
            Legendary = new Fabrication.Legendary { FromLevel = 190, Fragment = Fragment, FragmentQuantity = 5, CostFactor = 4 },
        };
        Sanctuary.Current = new Sanctuary.Settings
        {
            MapId = 211292160,
            Cell = 316,
            Npcs = { new Sanctuary.Npc { Template = 660, Cell = 303, Role = "marchand" } },
            Merchant = new Sanctuary.MerchantSettings
            {
                PriceFactor = 1.5,
                BandWidth = 20,
                Consumables = { new Sanctuary.Consumable { Item = 548, Price = 200 } },
                BreedElements = { [9] = "Agility" },
            },
        };
        Sanctuary.Persist = false;
        Sanctuary.ResetForTests();
    }

    public void Dispose()
    {
        Fabrication.Eligible = t => Stasis.Applies(t);
        Fabrication.Current = new Fabrication.Settings();
        Sanctuary.Current = new Sanctuary.Settings();
        Sanctuary.ResetForTests();
    }

    private static void Declare(int gid, int level, int type)
        => Forgemagic.Declare(new Forgemagic.Template
        {
            Gid = gid, Level = level, Type = type,
            Lines = new[] { new Forgemagic.TemplateLine(Vitality, 51, 100, 0), new Forgemagic.TemplateLine(Strength, 21, 40, 0) },
        });

    // ─── Recipes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_generated_recipe_scales_the_game_recipe_and_adds_the_band_essence()
    {
        var recipe = Fabrication.RecipeFor(Hat)!;
        Assert.Equal(1, recipe.Tier);
        Assert.Equal(new[] { (Wool, 8), (Leather, 5), (Essence, 1) }, recipe.Ingredients.ToArray());
        Assert.Equal(60 * 60, recipe.Cost);
    }

    [Fact]
    public void The_cost_has_a_floor_and_a_piece_without_recipe_takes_the_formula()
    {
        var recipe = Fabrication.RecipeFor(Drop)!;
        Assert.Equal(Math.Max(500, 60 * 30), recipe.Cost);
        Assert.Equal(500, Fabrication.CostOf(1, 1));
        Assert.Equal(new[] { (Essence, 2) }, recipe.Ingredients.ToArray());
    }

    [Fact]
    public void The_legendary_recipe_gives_S2_for_fragments_and_costs_more_but_only_high()
    {
        Assert.Null(Fabrication.RecipeFor(Hat, 2));
        var legendary = Fabrication.RecipeFor(HighHat, 2)!;
        Assert.Equal(2, legendary.Tier);
        Assert.Contains((Fragment, 5), legendary.Ingredients);
        Assert.Contains((HighEssence, 2), legendary.Ingredients);
        Assert.Equal(Fabrication.RecipeFor(HighHat)!.Cost * 4, legendary.Cost);
    }

    [Fact]
    public void An_override_wins_over_the_generated_recipe()
    {
        Fabrication.Current.Overrides.Add(new Fabrication.Override
        {
            Result = Hat, Cost = 1234, NoEssence = true, Ingredients = { new Fabrication.Ingredient { Item = Leather, Quantity = 2 } },
        });
        var recipe = Fabrication.RecipeFor(Hat)!;
        Assert.Equal(new[] { (Leather, 2) }, recipe.Ingredients.ToArray());
        Assert.Equal(1234, recipe.Cost);
        Assert.False(recipe.Generated);
    }

    [Fact]
    public void Non_equipment_has_no_recipe()
        => Assert.Null(Fabrication.RecipeFor(Wool));

    [Fact]
    public void The_result_is_the_perfect_roll_at_S1()
    {
        var template = Forgemagic.TemplateOf(Hat)!;
        var effects = Fabrication.Result(template, 1);
        Assert.Equal(125, effects.Single(e => e.Effect == Vitality).Value);
        Assert.Equal(50, effects.Single(e => e.Effect == Strength).Value);
        Assert.Equal(1, Stasis.TierInEffects(effects));
    }

    // ─── Quotes ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quote_says_what_is_owned_and_missing()
    {
        var recipe = Fabrication.RecipeFor(Hat)!;
        var bag = new Dictionary<int, long> { [Wool] = 10, [Leather] = 2 };
        var quote = Fabrication.QuoteFor(recipe, bag, plainPieces: 0, cost: 3600, orbes: 5000);
        Assert.False(quote.Ready);
        Assert.False(quote.BaseOwned);
        Assert.Equal(0, quote.Lines.Single(l => l.Item == Wool).Missing);
        Assert.Equal(3, quote.Lines.Single(l => l.Item == Leather).Missing);
        Assert.Equal(1, quote.Lines.Single(l => l.Item == Essence).Missing);
        Assert.Equal(3, quote.MissingKinds);

        bag[Leather] = 5;
        bag[Essence] = 1;
        Assert.True(Fabrication.QuoteFor(recipe, bag, 1, 3600, 5000).Ready);
        Assert.False(Fabrication.QuoteFor(recipe, bag, 1, 3600, 100).Ready);
    }

    // ─── The Merchant ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_merchant_prices_by_reference_worth_and_written_consumables()
    {
        Assert.Equal(1500, StasisMerchant.PriceOf(1000L));
        Assert.Equal(200, StasisMerchant.PriceOf(548));
        Assert.True(StasisMerchant.IsConsumable(548));
    }

    [Theory]
    [InlineData(1, 1, 20)]
    [InlineData(20, 1, 20)]
    [InlineData(21, 21, 40)]
    [InlineData(200, 181, 200)]
    public void Level_bands(int level, int min, int max)
        => Assert.Equal((min, max), StasisMerchant.BandOf(level));

    [Fact]
    public void A_sold_piece_rolls_between_the_middle_and_the_max()
    {
        var template = Forgemagic.TemplateOf(Hat)!;
        var dice = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            var effects = StasisMerchant.Roll(template, dice);
            Assert.InRange(effects.Single(e => e.Effect == Vitality).Value, 76, 100);
            Assert.InRange(effects.Single(e => e.Effect == Strength).Value, 31, 40);
            Assert.Equal(0, Stasis.TierInEffects(effects));
        }
    }

    private static Outfitter.Piece Piece(int gid, int type, int level)
        => new(gid, type, level, 0, false, 0, Forgemagic.TemplateOf(gid)!.Lines);

    [Fact]
    public void The_catalogue_is_a_band_and_the_advice_the_best_wearable_per_slot()
    {
        var pool = new[] { Piece(Hat, 16, 60), Piece(LowHat, 16, 40), Piece(HighHat, 16, 195), Piece(Boots, 11, 120) };
        Assert.Equal(new[] { Hat }, StasisMerchant.Catalogue(pool, 41, 60).Select(p => p.Gid).ToArray());
        var advice = StasisMerchant.Recommended(70, Outfitter.Element.Strength, pool);
        Assert.Contains(advice, p => p.Gid == Hat || p.Gid == LowHat);
        Assert.DoesNotContain(advice, p => p.Level > 70);
    }

    [Fact]
    public void The_advice_element_comes_from_the_word_then_the_breed()
    {
        Assert.Equal(Outfitter.Element.Intelligence, StasisMerchant.ElementOf(9, "feu"));
        Assert.Equal(Outfitter.Element.Agility, StasisMerchant.ElementOf(9));
        Assert.Equal(Outfitter.Element.Strength, StasisMerchant.ElementOf(42));
    }

    // ─── The Sanctuary ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sanctuary_refuses_fights_dungeons_and_itself()
    {
        Assert.Equal("combat", Sanctuary.Refusal(true, 100, _ => false));
        Assert.Equal("donjon", Sanctuary.Refusal(false, 100, _ => true));
        Assert.Equal("deja", Sanctuary.Refusal(false, 211292160, _ => false));
        Assert.Null(Sanctuary.Refusal(false, 100, _ => false));
    }

    [Fact]
    public void The_round_trip_remembers_the_origin_once()
    {
        Sanctuary.RecordDeparture(42, 191105026, 300);
        Sanctuary.RecordDeparture(42, 211292160, 316);
        Assert.Equal((191105026L, 300), Sanctuary.ReturnOf(42));
        Assert.Equal((191105026L, 300), Sanctuary.TakeReturn(42));
        Assert.Null(Sanctuary.TakeReturn(42));
    }

    [Fact]
    public void Only_the_sanctuary_npcs_have_a_role_there()
    {
        Assert.Equal("marchand", Sanctuary.RoleOf(660, 211292160));
        Assert.Null(Sanctuary.RoleOf(660, 191105026));
        Assert.Null(Sanctuary.RoleOf(1, 211292160));
    }

    [Fact]
    public void Quantities_split_off_the_last_word()
    {
        Assert.Equal(("Coiffe du Bouftou", 3), CommandHandler.SplitQuantity("Coiffe du Bouftou 3"));
        Assert.Equal(("548", 1), CommandHandler.SplitQuantity("548"));
    }

    [Fact]
    public void The_shipped_files_parse()
    {
        var root = Jondo.Unity.Launcher.Paths.ContentFile("");
        var fab = Fabrication.Read(System.IO.Path.Combine(root, "items", "fabrication.json"));
        var sanctuary = Sanctuary.Read(System.IO.Path.Combine(root, "world", "sanctuary.json"));
        if (!System.IO.File.Exists(System.IO.Path.Combine(root, "world", "sanctuary.json"))) return;
        Assert.Equal(5, fab.Essences.Count);
        Assert.Equal(211292160, sanctuary.MapId);
        Assert.Contains(sanctuary.Npcs, n => n.Role == "marchand");
        Assert.Contains(sanctuary.Npcs, n => n.Role == "banque");
    }
}
