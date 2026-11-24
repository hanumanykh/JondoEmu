using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The spell dons (Managers/Stasis/SpellDons.cs, content/progression/spell_dons.json): what they do
/// to a spell in a fight -- through the same Fighter.AllSpellsModifier road a dream's modifiers
/// take, and the two percent maps -- which class's list an offer draws from, and the Stasis caps
/// (points, families, points per family by level, ranks).
/// </summary>
/// <remarks>
/// The real file of the checkout is read, so a balancing edit that breaks a cap or names a don of
/// another class shows here. Ids of Iop's dons are the file's: if one is renamed, rename it here.
/// </remarks>
public sealed class SpellDonsTests
{
    private static readonly ProgressionContent Content = LoadContent();
    private static long _nextId = 9_100_000_000L;

    private const int Iop = 8, Cra = 9;
    private const int Pression = 13106, EpeeDivine = 13110;

    private static ProgressionContent LoadContent()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (File.Exists(Path.Combine(candidate, "spell_dons.json")))
            {
                var content = ProgressionContent.Load(candidate);
                ProgressionContent.Current = content;
                if (ProgressionStore.Backend is not MemoryProgressionStore) ProgressionStore.Backend = new MemoryProgressionStore();
                return content;
            }
        }
        throw new InvalidOperationException("content/progression/spell_dons.json not found above the test binaries.");
    }

    private static ProgressionProfile NewProfile() => new() { CharacterId = Interlocked.Increment(ref _nextId) };

    private static void Keep(ProgressionProfile p, string card, int level) => p.Dons.Add(new ChosenDon { Level = level, Card = card });

    [Fact]
    public void Every_class_has_five_to_ten_dons_each_on_a_known_kind_and_within_safe_values()
    {
        Assert.Equal(19, Content.SpellDons.Classes.Count);   // the 19 class breeds (19 is the common spells')
        Assert.DoesNotContain("19", Content.SpellDons.Classes.Keys);
        var kinds = new[] { SpellDons.Damage, SpellDons.Heal, SpellDons.Critical, SpellDons.Range, SpellDons.Cooldown, SpellDons.ApCost, SpellDons.CastsPerTurn };
        foreach (var (breed, list) in Content.SpellDons.Classes)
        {
            Assert.InRange(list.Count, 5, 10);
            Assert.Equal(list.Count, list.Select(d => d.Id).Distinct().Count());
            foreach (var don in list)
            {
                Assert.Contains(don.Kind, kinds);
                Assert.InRange(don.MaxRank, 1, 3);
                Assert.True(don.Spell > 0 && don.SpellName.Length > 0, don.Id);
                if (don.Kind is SpellDons.ApCost or SpellDons.Cooldown or SpellDons.CastsPerTurn)
                    Assert.True(don.Value == 1 && don.MaxRank == 1, don.Id);
                else Assert.InRange(don.Value * don.MaxRank, 1, 20);
            }
        }
        Assert.Equal(1, Content.SpellDons.Classes.Values.SelectMany(l => l).Select(d => d.Id).GroupBy(i => i).Max(g => g.Count()));
    }

    [Fact]
    public void Damage_crit_and_casts_per_turn_reach_the_fight()
    {
        var p = NewProfile();
        Keep(p, "iop-pression-degats", 10);
        Keep(p, "iop-pression-degats", 70);
        Keep(p, "iop-pression-critique", 90);
        Keep(p, "iop-epee-divine-lancers", 30);

        var all = SpellDons.AllOf(p, Content);
        Assert.Equal(new SpellDonModifiers(10, 0, 5, 0, 0, 0, 0), all[Pression]);
        Assert.Equal(1, all[EpeeDivine].CastsPerTurn);

        var fighter = new Fighter { Id = 9_100_000_001, Name = "Iop" };
        SpellDons.Put(fighter, all);

        Assert.Equal(110, SpellDons.WithDamage(fighter, Pression, 100));
        Assert.Equal(100, SpellDons.WithDamage(fighter, EpeeDivine, 100));   // another spell
        Assert.Equal(100, SpellDons.WithDamage(fighter, 0, 100));            // the weapon
        Assert.Equal(5, SpellModifiers.Critical(fighter, Pression, 1));
        Assert.Equal(0, SpellModifiers.Critical(fighter, EpeeDivine, 1));
        Assert.Equal(3, SpellModifiers.CastsPerTurn(fighter, EpeeDivine, 2, 1));
        Assert.Equal(0, SpellModifiers.CastsPerTurn(fighter, EpeeDivine, 0, 1));   // no cap stays no cap
        Assert.Equal(2, SpellModifiers.CastsPerTurn(fighter, Pression, 2, 1));
    }

    [Fact]
    public void Range_cost_cooldown_and_heal_take_the_existing_modifier_road()
    {
        var fighter = new Fighter { Id = 9_100_000_002, Name = "Test" };
        SpellDons.Put(fighter, new System.Collections.Generic.Dictionary<int, SpellDonModifiers>
        {
            [500] = new SpellDonModifiers(0, 12, 0, 1, 1, 1, 0),
        });
        Assert.Equal((1, 7), SpellModifiers.Range(fighter, 500, 1, 6, 1));
        Assert.Equal(3, SpellModifiers.ApCost(fighter, 500, 4, 1));
        Assert.Equal(2, SpellModifiers.CastInterval(fighter, 500, 3, 1));
        Assert.Equal(112, SpellDons.WithHeal(fighter, 500, 100));
        Assert.Equal(4, SpellModifiers.ApCost(fighter, 501, 4, 1));
    }

    [Fact]
    public void Never_more_than_one_ap_off_a_spell()
    {
        var p = NewProfile();
        Keep(p, "iop-epee-destructrice-pa", 70);
        Keep(p, "iop-epee-destructrice-pa", 90);   // a second rank the file would never offer
        Assert.Equal(1, SpellDons.AllOf(p, Content)[13119].ApCost);
    }

    [Fact]
    public void A_spell_offer_draws_three_dons_of_the_class_the_level_has_learnt()
    {
        var p = NewProfile();
        var offer = Dons.Draw(p, Content, Cra, 10, Dons.KindSpell);
        Assert.Equal(3, offer.Cards.Count);
        foreach (string card in offer.Cards)
        {
            var found = SpellDons.DefinitionOf(card, Content);
            Assert.NotNull(found);
            Assert.Equal(Cra, found!.Value.Breed);
            Assert.True(found.Value.Don.Level <= 10, card);
        }
        var (name, description) = Dons.Describe(offer.Cards[0], p, Content);
        Assert.Contains(SpellDons.DefinitionOf(offer.Cards[0], Content)!.Value.Don.SpellName, name);
        Assert.Contains("sort", description);
    }

    [Fact]
    public void At_level_10_one_family_of_one_point_then_the_old_card_comes_back()
    {
        var p = NewProfile();
        Keep(p, "iop-pression-degats", 10);
        Assert.Equal(1, SpellDons.FamiliesAllowed(10, Content));
        Assert.Equal(1, SpellDons.PointsPerFamilyAllowed(10, Content));
        Assert.Empty(SpellDons.Takeable(p, Content, Iop, 10));
        Assert.Contains("sort", Dons.Draw(p, Content, Iop, 10, Dons.KindSpell).Cards);

        // Level 30 opens a second family, still one point in each: not Pression again.
        var at30 = SpellDons.Takeable(p, Content, Iop, 30);
        Assert.NotEmpty(at30);
        Assert.DoesNotContain(at30, d => d.Spell == Pression);

        // From 70 a family holds two points: Pression's next rank comes back.
        Assert.Contains(SpellDons.Takeable(p, Content, Iop, 70), d => d.Id == "iop-pression-degats");
    }

    [Fact]
    public void Ranks_families_and_points_are_capped()
    {
        var p = NewProfile();
        for (int i = 0; i < 3; i++) Keep(p, "iop-pression-degats", 100 + i);
        Assert.DoesNotContain(SpellDons.Takeable(p, Content, Iop, 200), d => d.Id == "iop-pression-degats");   // rank 3/3

        // Five families: a sixth spell is refused, the five go on.
        var q = NewProfile();
        var spells = SpellDons.ListOf(Iop, Content).GroupBy(d => SpellDons.FamilyOf(d.Spell)).Select(g => g.First()).Take(6).ToList();
        Assert.Equal(6, spells.Count);
        for (int i = 0; i < 5; i++) Keep(q, spells[i].Id, 110 + i);
        Assert.DoesNotContain(SpellDons.Takeable(q, Content, Iop, 200), d => SpellDons.FamilyOf(d.Spell) == SpellDons.FamilyOf(spells[5].Spell));
        Assert.NotEmpty(SpellDons.Takeable(q, Content, Iop, 200));

        // Fourteen points in all: nothing more.
        var r = NewProfile();
        for (int i = 0; i < Content.SpellDons.MaxPoints; i++) Keep(r, "iop-pression-degats", 300 + i);
        Assert.Empty(SpellDons.Takeable(r, Content, Iop, 200));
    }

    [Fact]
    public void Picking_a_don_redraws_a_waiting_offer_it_put_out_of_reach()
    {
        var p = NewProfile();
        p.Pending.Add(new DonOffer { Level = 10, Kind = Dons.KindSpell, Cards = { "iop-pression-degats", "iop-epee-divine-degats" } });
        p.Pending.Add(new DonOffer { Level = 30, Kind = Dons.KindSpell, Cards = { "iop-pression-critique" } });
        Assert.Equal("ok", Dons.Pick(p, Content, 1, null, out _));
        // Pression holds its one point at level 30: the waiting offer no longer shows it.
        Assert.DoesNotContain("iop-pression-critique", p.Pending.Single().Cards);
        Assert.All(p.Pending.Single().Cards, c => Assert.True(
            SpellDons.DefinitionOf(c, Content) is not { } s || SpellDons.CanTake(s.Don, p, Content, 30), c));
    }
}
