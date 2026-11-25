using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The generated Stasis-like spell upgrades (Managers/Stasis/SpellDonCatalogue.cs): only what
/// changes something on the spell, the caps by level, the prerequisites, the overrides, and what
/// each kind does to the spell in a fight. Spell facts are made up here: no world database.
/// </summary>
public sealed class SpellDonCatalogueTests
{
    private const int Breed = 8;
    private const int Full = 990_001, Plain = 990_002, Late = 990_003, Short = 990_004, Legacy = 990_005;

    private static readonly int LosAndLine = SpellLevelFlags.CastTestLos | SpellLevelFlags.CastInLine;

    private static SpellFacts FullSpell(int id = Full, int level = 1, int maxRange = 6)
        => new(id, Breed, "Sort complet", level, ApCost: 4, CastsPerTurn: 2, CastsPerTarget: 1, Cooldown: 3,
               InitialCooldown: 1, Critical: 5, MinRange: 1, MaxRange: maxRange, Flags: LosAndLine, MaxStack: 2);

    private static readonly SpellFacts PlainSpell =
        new(Plain, Breed, "Sort simple", 1, ApCost: 1, CastsPerTurn: 0, CastsPerTarget: 0, Cooldown: 0,
            InitialCooldown: 0, Critical: 0, MinRange: 0, MaxRange: 0, Flags: SpellLevelFlags.RangeCanBeBoosted, MaxStack: 0);

    private static ProgressionContent ContentWith(IReadOnlyList<SpellFacts> facts, SpellDonOverridesFile? o = null,
                                                  Dictionary<string, List<SpellDonDefinition>>? legacy = null)
        => new()
        {
            SpellDons = new SpellDonsFile
            {
                MaxPoints = 14, MaxFamilies = 5, FamilySlotLevels = new() { 10, 30, 50, 70, 90 },
                PointsPerFamilyByLevel = new() { ["1"] = 1, ["70"] = 2, ["90"] = 3, ["110"] = 4, ["130"] = 5 },
                Classes = legacy ?? new(),
                Facts = () => facts,
            },
            SpellDonOverrides = o ?? new SpellDonOverridesFile(),
        };

    private static ProgressionProfile Profile(params string[] cards)
    {
        var p = new ProgressionProfile { CharacterId = 0 };
        foreach (var c in cards) p.Dons.Add(new ChosenDon { Level = 200, Card = c });
        return p;
    }

    private static string Id(int spell, string kind) => SpellDonCatalogue.IdOf(Breed, spell, kind);

    [Fact]
    public void Only_upgrades_that_change_something_are_generated()
    {
        var o = new SpellDonOverridesFile();
        Assert.Equal(SpellDonCatalogue.Kinds.OrderBy(k => k), SpellDonCatalogue.Generate(FullSpell(), o).Select(d => d.Kind).OrderBy(k => k));
        Assert.Empty(SpellDonCatalogue.Generate(PlainSpell, o));

        // A spell with neither line of sight nor line nor cooldown: no Clairvoyance, Liberté, Cadence, Anticipation.
        var open = FullSpell() with { Flags = 0, Cooldown = 1, InitialCooldown = 0 };
        var kinds = SpellDonCatalogue.Generate(open, o).Select(d => d.Kind).ToList();
        Assert.DoesNotContain(SpellDonCatalogue.Clairvoyance, kinds);
        Assert.DoesNotContain(SpellDonCatalogue.Liberte, kinds);
        Assert.DoesNotContain(SpellDonCatalogue.Cadence, kinds);
        Assert.DoesNotContain(SpellDonCatalogue.Anticipation, kinds);
        Assert.Contains(SpellDonCatalogue.Adaptabilite, kinds);
    }

    [Fact]
    public void Ranks_open_by_level_and_never_before_the_spell_is_learnt()
    {
        var o = new SpellDonOverridesFile();
        var dons = SpellDonCatalogue.Generate(FullSpell(), o).ToDictionary(d => d.Kind);
        Assert.Equal(new[] { 1, 70, 90 }, dons[SpellDonCatalogue.Precision].RankLevels);
        Assert.Equal(10, dons[SpellDonCatalogue.Precision].Value);
        Assert.Equal(new[] { 1, 70 }, dons[SpellDonCatalogue.Enchainement].RankLevels);
        Assert.Equal(new[] { 1, 90 }, dons[SpellDonCatalogue.Insistance].RankLevels);
        Assert.Equal(1, dons[SpellDonCatalogue.Economie].MaxRank);
        Assert.Equal(3, dons[SpellDonCatalogue.Allonge].MaxRank);

        // A short spell stops Allonge a rank earlier; a late one opens every rank at its level.
        Assert.Equal(2, SpellDonCatalogue.Generate(FullSpell(Short, maxRange: 3), o).Single(d => d.Kind == SpellDonCatalogue.Allonge).MaxRank);
        var late = SpellDonCatalogue.Generate(FullSpell(Late, level: 95), o).Single(d => d.Kind == SpellDonCatalogue.Precision);
        Assert.Equal(new[] { 95, 95, 95 }, late.RankLevels);

        // In the caps: Précision rank 2 waits for 70 even with the family's room.
        var content = ContentWith(new[] { FullSpell() });
        var precision = SpellDons.DefinitionOf(Id(Full, SpellDonCatalogue.Precision), content)!.Value.Don;
        var p = Profile(precision.Id);
        Assert.False(SpellDons.CanTake(precision, p, content, 69));
        Assert.True(SpellDons.CanTake(precision, p, content, 70));
    }

    [Fact]
    public void Insistance_max_needs_enchainement_one()
    {
        var content = ContentWith(new[] { FullSpell() });
        var insistance = SpellDons.DefinitionOf(Id(Full, SpellDonCatalogue.Insistance), content)!.Value.Don;
        Assert.True(SpellDons.CanTake(insistance, Profile(), content, 200));            // rank 1: free
        Assert.False(SpellDons.CanTake(insistance, Profile(insistance.Id), content, 200));   // rank 2: needs Enchaînement 1
        Assert.True(SpellDons.CanTake(insistance, Profile(insistance.Id, Id(Full, SpellDonCatalogue.Enchainement)), content, 200));
    }

    [Fact]
    public void Overrides_disable_patch_name_and_add()
    {
        var o = new SpellDonOverridesFile
        {
            Disable = { $"{Full}:{SpellDonCatalogue.Liberte}", Id(Full, SpellDonCatalogue.Persistance) },
            SpellNames = { [Full.ToString()] = "Pression" },
            Patches = { [$"{Full}:{SpellDonCatalogue.Allonge}"] = new SpellDonPatch { Value = 3, RankLevels = new() { 115 } } },
            Extra = { [Breed.ToString()] = new() { new SpellDonDefinition { Id = "extra-1", Spell = Plain, SpellName = "Simple", Kind = SpellDons.Damage, Value = 5, MaxRank = 2 } } },
        };
        var content = ContentWith(new[] { FullSpell(), PlainSpell }, o);
        var list = SpellDons.ListOf(Breed, content);
        Assert.DoesNotContain(list, d => d.Kind is SpellDonCatalogue.Liberte or SpellDonCatalogue.Persistance);
        Assert.All(list.Where(d => d.Spell == Full), d => Assert.Equal("Pression", d.SpellName));
        var allonge = list.Single(d => d.Kind == SpellDonCatalogue.Allonge);
        Assert.Equal((3, 1, 115), (allonge.Value, allonge.MaxRank, allonge.RankLevels[0]));
        Assert.Contains(list, d => d.Id == "extra-1" && d.RankLevels.Count == 2);
        Assert.Equal("Pression — +3 PO", SpellDons.Describe(allonge, Profile()).Name);
        Assert.Contains("Allonge", SpellDons.Describe(allonge, Profile()).Description);

        // The whole generator off: nothing generated, the extras stay.
        var off = ContentWith(new[] { FullSpell() }, new SpellDonOverridesFile { Enabled = false });
        Assert.Empty(SpellDons.ListOf(Breed, off));
    }

    [Fact]
    public void A_legacy_don_the_generator_covers_is_no_longer_offered_but_still_works()
    {
        var legacy = new Dictionary<string, List<SpellDonDefinition>>
        {
            [Breed.ToString()] = new()
            {
                new SpellDonDefinition { Id = "old-crit", Spell = Legacy, SpellName = "Vieux", Kind = SpellDons.Critical, Value = 5, MaxRank = 3 },
                new SpellDonDefinition { Id = "old-dmg", Spell = Legacy, SpellName = "Vieux", Kind = SpellDons.Damage, Value = 5, MaxRank = 3 },
            },
        };
        var content = ContentWith(new[] { FullSpell(Legacy) }, legacy: legacy);
        var takeable = SpellDons.Takeable(Profile(), content, Breed, 200);
        Assert.DoesNotContain(takeable, d => d.Id == "old-crit");
        Assert.Contains(takeable, d => d.Id == "old-dmg");
        Assert.Contains(takeable, d => d.Id == Id(Legacy, SpellDonCatalogue.Precision));
        Assert.Equal(5, SpellDons.AllOf(Profile("old-crit"), content)[Legacy].Critical);
    }

    [Fact]
    public void Every_kind_reaches_the_fight()
    {
        var content = ContentWith(new[] { FullSpell() });
        var p = Profile(SpellDonCatalogue.Kinds.Select(k => Id(Full, k)).ToArray());
        p.Dons.Add(new ChosenDon { Level = 200, Card = Id(Full, SpellDonCatalogue.Precision) });   // two points of Précision
        var m = SpellDons.AllOf(p, content)[Full];
        Assert.Equal(new SpellDonModifiers(0, 0, 20, 1, 1, 1, 1, 1, true, true, true, 1, 1), m);

        var fighter = new Fighter { Id = 9_200_000_001, Name = "Test" };
        SpellDons.Put(fighter, new Dictionary<int, SpellDonModifiers> { [Full] = m });
        Assert.Equal(3, SpellModifiers.ApCost(fighter, Full, 4, 1));                 // Économie
        Assert.Equal(3, SpellModifiers.CastsPerTurn(fighter, Full, 2, 1));           // Enchaînement
        Assert.Equal(2, SpellModifiers.CastsPerTarget(fighter, Full, 1, 1));         // Insistance
        Assert.Equal(20, SpellModifiers.Critical(fighter, Full, 1));                 // Précision
        Assert.Equal((1, 7), SpellModifiers.Range(fighter, Full, 1, 6, 1));          // Allonge
        Assert.True(SpellModifiers.Holds(fighter, Full, SpellAspect.RangeModifiable, 1));   // Adaptabilité
        Assert.True(SpellModifiers.Holds(fighter, Full, SpellAspect.LineOfSightOff, 1));    // Clairvoyance
        Assert.Equal(SpellLevelFlags.CastTestLos, SpellDons.LineFlags(fighter, Full, LosAndLine));   // Liberté
        Assert.Equal(LosAndLine, SpellDons.LineFlags(fighter, Plain, LosAndLine));
        Assert.Equal(2, SpellModifiers.CastInterval(fighter, Full, 3, 1));           // Cadence
        Assert.Equal(0, SpellDons.InitialCooldown(fighter, Full, 1));                // Anticipation
        Assert.Equal(1, SpellDons.ExtraStackOf(new Buff { Quien = fighter.Id, HechizoOrigen = Full }));   // Persistance
        Assert.Equal(0, SpellDons.ExtraStackOf(new Buff { Quien = fighter.Id, HechizoOrigen = Plain }));
        Assert.Null(SpellModifiers.OnTheWire(SpellDons.LineFree));                  // never sent as a hnd
    }

    private const int Summon = 990_010, Strike = 990_011;

    private static SpellFacts SummonSpell => new(Summon, Breed, "Invocation de Bouftou", 1, ApCost: 3, CastsPerTurn: 1, CastsPerTarget: 0,
        Cooldown: 0, InitialCooldown: 0, Critical: 0, MinRange: 1, MaxRange: 2, Flags: 0, MaxStack: 0, Summons: true);

    private static SpellFacts StrikeSpell => new(Strike, Breed, "Crocs du Piranya", 150, ApCost: 4, CastsPerTurn: 2, CastsPerTarget: 0,
        Cooldown: 3, InitialCooldown: 0, Critical: 10, MinRange: 1, MaxRange: 5, Flags: SpellLevelFlags.CastTestLos, MaxStack: 0, Damages: true);

    [Fact]
    public void Impactful_upgrades_of_damage_spells_outweigh_trivial_ones_on_summons()
    {
        var o = new SpellDonOverridesFile();
        var strike = SpellDonCatalogue.Generate(StrikeSpell, o);
        var summon = SpellDonCatalogue.Generate(SummonSpell, o);
        double Of(List<SpellDonDefinition> l, string kind) => SpellDonCatalogue.ImpactOf(l.Single(d => d.Kind == kind));
        Assert.True(Of(strike, SpellDonCatalogue.Economie) > Of(strike, SpellDonCatalogue.Adaptabilite) * 3);
        Assert.True(Of(strike, SpellDonCatalogue.Adaptabilite) > Of(summon, SpellDonCatalogue.Adaptabilite) * 3);
        Assert.True(Of(strike, SpellDonCatalogue.Cadence) > Of(summon, SpellDonCatalogue.Enchainement));
    }

    [Fact]
    public void Card_names_are_short_and_say_what_changes()
    {
        var list = SpellDonCatalogue.Generate(StrikeSpell, new SpellDonOverridesFile());
        SpellDonDefinition K(string kind) => list.Single(d => d.Kind == kind);
        var (name, description) = SpellDons.Describe(K(SpellDonCatalogue.Adaptabilite), Profile());
        Assert.Equal("Crocs du Piranya — Portée modifiable", name);
        Assert.StartsWith("PO 1-5 → PO 1-5 modifiable.", description);
        Assert.StartsWith("4 PA → 3 PA", SpellDons.Describe(K(SpellDonCatalogue.Economie), Profile()).Description);
        Assert.StartsWith("relance 3 → 2 tours", SpellDons.Describe(K(SpellDonCatalogue.Cadence), Profile()).Description);
        Assert.StartsWith("2 → 3 lancers par tour", SpellDons.Describe(K(SpellDonCatalogue.Enchainement), Profile()).Description);
        // The next rank counts the ones held.
        var allonge = K(SpellDonCatalogue.Allonge);
        var (n2, d2) = SpellDons.Describe(allonge, Profile(allonge.Id));
        Assert.Equal("Crocs du Piranya — +1 PO (2/3)", n2);
        Assert.StartsWith("PO 1-6 → PO 1-7", d2);
        Assert.StartsWith("critique 10 % → 20 %", SpellDons.Describe(K(SpellDonCatalogue.Precision), Profile()).Description);
    }

    [Fact]
    public void An_offer_is_drawn_by_impact_and_never_holds_three_of_a_kind()
    {
        var o = new SpellDonOverridesFile();
        var pool = SpellDonCatalogue.Generate(StrikeSpell, o).Concat(SpellDonCatalogue.Generate(SummonSpell, o)).ToList();
        var dice = new System.Random(7);
        int strong = 0, summonTrivial = 0;
        for (int i = 0; i < 2000; i++)
        {
            var offer = SpellDons.DrawOffer(pool, 3, dice);
            Assert.Equal(3, offer.Count);
            Assert.Equal(3, offer.Select(d => d.Id).Distinct().Count());
            Assert.True(offer.GroupBy(d => d.Kind).Max(g => g.Count()) <= 2);
            strong += offer.Count(d => d.Spell == Strike && d.Kind is SpellDonCatalogue.Economie or SpellDonCatalogue.Cadence);
            summonTrivial += offer.Count(d => d.Spell == Summon && d.Kind == SpellDonCatalogue.Adaptabilite);
        }
        Assert.True(strong > summonTrivial * 5, $"strong {strong}, trivial {summonTrivial}");

        // Only one kind left: it still fills the offer.
        var same = Enumerable.Range(0, 4).Select(i => new SpellDonDefinition { Id = "x" + i, Spell = i, Kind = SpellDonCatalogue.Precision, Value = 10 }).ToList();
        Assert.Equal(3, SpellDons.DrawOffer(same, 3, dice).Count);
    }
}
