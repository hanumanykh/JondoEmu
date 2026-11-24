using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// Stasification's rules (Managers.Stasis): what a tier multiplies and what it never does, the
/// label on the 988 line, fusion, the drop distribution and the prices -- on the defaults, not on
/// content/items/stasis.json, so a tuned file does not break them.
/// </summary>
public sealed class StasisTests
{
    private const int Strength = 118, Vitality = 125, StrengthMalus = 157, WeaponDamage = 96;

    public StasisTests()
    {
        Stasis.Current = new Stasis.Settings();
        Forgemagic.Declare(Strength, 1);
        Forgemagic.Declare(Vitality, 0.2);
        Forgemagic.Declare(StrengthMalus, -1, bonusType: -1);
        Forgemagic.Declare(Forgemagic.ActionPoints, 100);
        Forgemagic.Declare(Forgemagic.MovementPoints, 90);
        Forgemagic.Declare(117, 51);
        Forgemagic.Declare(115, 10);
        Forgemagic.Declare(182, 30);
        Forgemagic.Declare(210, 6);
        Forgemagic.Declare(242, 2);
        Forgemagic.Declare(WeaponDamage, 0, category: 2);
    }

    private static Effect Line(int effect, long value) => new(effect, value, 0, 0);

    [Theory]
    [InlineData(1, 125, 50)]
    [InlineData(2, 150, 60)]
    [InlineData(3, 200, 80)]
    [InlineData(4, 300, 120)]
    public void A_tier_multiplies_the_characteristics(int tier, long vitality, long strength)
    {
        var result = Stasis.Apply(new[] { Line(Vitality, 100), Line(Strength, 40) }, tier);
        Assert.Equal(vitality, result.Single(e => e.Effect == Vitality).Value);
        Assert.Equal(strength, result.Single(e => e.Effect == Strength).Value);
    }

    [Fact]
    public void AP_MP_range_critical_summons_resistances_maluses_and_damage_are_never_multiplied()
    {
        var before = new[]
        {
            Line(Forgemagic.ActionPoints, 1), Line(Forgemagic.MovementPoints, 1), Line(117, 2), Line(115, 5),
            Line(182, 1), Line(210, 8), Line(242, 20), Line(StrengthMalus, 30), new Effect(WeaponDamage, 0, 10, 20),
        };
        var after = Stasis.Apply(before, 4);
        foreach (var e in before)
        {
            var same = after.Single(a => a.Effect == e.Effect);
            Assert.Equal((e.Value, e.DiceNum, e.DiceSide), (same.Value, same.DiceNum, same.DiceSide));
        }
    }

    [Fact]
    public void A_small_line_still_grows_at_S1()
        => Assert.Equal(2, Stasis.Apply(new[] { Line(Strength, 1) }, 1).Single(e => e.Effect == Strength).Value);

    [Fact]
    public void The_tier_is_written_on_the_988_line_and_shares_it_with_a_signature()
    {
        var alone = Stasis.Apply(new[] { Line(Strength, 10) }, 2);
        Assert.Equal("Stasis S2", alone.Single(e => e.Effect == Stasis.LabelEffect).Text);
        Assert.Equal(2, Stasis.TierInEffects(alone));

        var signed = Forgemagic.Signed(new[] { Line(Strength, 10) }, Forgemagic.CraftedBy, "Jondo");
        var both = Stasis.Labelled(Stasis.Apply(signed, 1), 3);
        var label = Assert.Single(both, e => e.Effect == Stasis.LabelEffect);
        Assert.Equal("Jondo · Stasis S3", label.Text);
        Assert.Equal(3, Stasis.TierInEffects(both));
        Assert.Equal("Jondo", Stasis.StripLabel(label.Text));
        Assert.Equal(0, Stasis.TierInEffects(signed));
    }

    [Fact]
    public void Fusion_makes_the_next_tier_at_max_and_wipes_exos_into_S3_only()
    {
        var template = new Forgemagic.Template
        {
            Gid = 990_001, Level = 100, Type = 16,
            Lines = new[] { new Forgemagic.TemplateLine(Vitality, 51, 100, 0), new Forgemagic.TemplateLine(Strength, 21, 40, 0) },
        };
        var exo = Line(Forgemagic.ActionPoints, 1);
        var s1 = Stasis.AtMax(template, 1).Append(exo).ToList();
        var sources = new[] { (IReadOnlyList<Effect>)s1, s1, s1 };

        var s2 = Stasis.Fused(template, 1, sources)!;
        Assert.Equal(150, s2.Single(e => e.Effect == Vitality).Value);
        Assert.Equal(60, s2.Single(e => e.Effect == Strength).Value);
        Assert.Contains(s2, e => e.Effect == Forgemagic.ActionPoints);
        Assert.Equal(2, Stasis.TierInEffects(s2));

        var s3 = Stasis.Fused(template, 2, new[] { (IReadOnlyList<Effect>)s2, s2, s2 })!;
        Assert.Equal(200, s3.Single(e => e.Effect == Vitality).Value);
        Assert.DoesNotContain(s3, e => e.Effect == Forgemagic.ActionPoints);
        Assert.Equal(3, Stasis.TierInEffects(s3));

        Assert.Null(Stasis.Fused(template, 4, sources));
    }

    [Fact]
    public void Fusion_costs_more_with_level_and_tier()
    {
        Assert.Equal(1000, Stasis.FusionCost(1, 2));
        Assert.Equal(10_000, Stasis.FusionCost(200, 2));
        Assert.Equal(40_000, Stasis.FusionCost(200, 3));
        Assert.Equal(120_000, Stasis.FusionCost(200, 4));
    }

    [Fact]
    public void A_normal_character_drops_S1_and_an_evolved_one_the_distribution()
    {
        var dice = new Random(7);
        var saved = Stasis.IsEvolved;
        try
        {
            Stasis.IsEvolved = _ => false;
            Assert.All(Enumerable.Range(0, 200), _ => Assert.Equal(1, Stasis.DropTier(1, dice)));
            Assert.False(Stasis.MayWear(1, 3));
            Assert.True(Stasis.MayWear(1, 2));

            Stasis.IsEvolved = id => id == 2;
            var counts = new int[5];
            for (int i = 0; i < 100_000; i++) counts[Stasis.DropTier(2, dice)]++;
            Assert.InRange(counts[1], 64_500, 67_500);
            Assert.InRange(counts[2], 28_500, 31_500);
            Assert.InRange(counts[3], 2_500, 3_500);
            Assert.InRange(counts[4], 700, 1_300);
            Assert.True(Stasis.MayWear(2, 4));
        }
        finally { Stasis.IsEvolved = saved; }
    }

    [Fact]
    public void Prices_follow_fusion_and_the_raffineur_pays_a_fraction()
    {
        Assert.Equal(1000, Stasis.Worth(1000, 0));
        Assert.Equal(1250, Stasis.Worth(1000, 1));
        Assert.Equal(3750, Stasis.Worth(1000, 2));
        Assert.Equal(11_000, Stasis.Worth(1000, 3));
        Assert.Equal(33_000, Stasis.Worth(1000, 4));
        Assert.Equal(125, Stasis.BuybackPrice(1000, 1));
        Assert.Equal(8250, Stasis.BuybackPrice(1000, 4));
        Assert.Equal(7500, MarketMaker.WorthOf(1000, 2, 2));
    }

    [Fact]
    public void The_market_tiers_are_mostly_S1()
    {
        var dice = new Random(3);
        var counts = new int[5];
        for (int i = 0; i < 100_000; i++) counts[Stasis.MarketTier(dice)]++;
        Assert.True(counts[1] > counts[2] && counts[2] > counts[3] && counts[3] > counts[4] && counts[4] > 0);
    }

    [Theory]
    [InlineData("Coiffe du Bouftou S2", "Coiffe du Bouftou", 2)]
    [InlineData("8463", "8463", 0)]
    [InlineData("anneau s4", "anneau", 4)]
    [InlineData("S5", "S5", 0)]
    public void The_tier_word_of_a_command(string typed, string query, int tier)
        => Assert.Equal((query, tier), CommandHandler.SplitTierWord(typed));

    [Fact]
    public void The_shipped_file_reads()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "content", "items", "stasis.json");
        if (!File.Exists(path)) return;
        var read = Stasis.Read(path);
        Assert.Equal(3.0, read.Multipliers[4]);
        Assert.Contains(111, read.NeverMultiplied);
        Assert.Equal(100, read.EvolvedDropWeights.Values.Sum());
    }
}
