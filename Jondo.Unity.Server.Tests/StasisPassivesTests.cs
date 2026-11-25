using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;
using P = Jondo.Unity.Server.Managers.StasisPassives;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Stasis class passives (Managers/Stasis/StasisPassives.cs, dons.json «passives»): the level-50
/// offer -- three among the nineteen classes, two free rerolls of three new ones, nine seen at most --
/// and the triggers of the passives in a fight, driven on bare fighters.
/// </summary>
public sealed class StasisPassivesTests
{
    private static readonly ProgressionContent Content = LoadContent();
    private static long _nextId = 9_200_000_000L;

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

    public StasisPassivesTests() => Assert.NotNull(Content);

    private static ProgressionProfile NewProfile() => new() { CharacterId = Interlocked.Increment(ref _nextId) };

    private static Fighter F(long id, int team, int cell = 100, int hp = 1000)
        => new() { Id = id, TeamId = team, CellId = cell, MaxHP = hp, CurrentHP = hp, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };

    private static P.Surroundings None() => new();

    // ─── The offer ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void There_is_one_passive_per_class_and_each_names_its_class()
    {
        var all = Dons.AllPassives(Content);
        Assert.Equal(19, all.Count);
        Assert.Equal(19, all.Select(c => P.KindOf(c.Id, Content)).Where(k => k != P.Kind.None).Distinct().Count());
        Assert.Equal(P.Kind.Iop, P.KindOf("passif_iop", Content));
        Assert.Equal(P.Kind.Forgelance, P.KindOf("passif_forgelance", Content));
        Assert.All(all, c => Assert.Equal("class", Dons.IconOf(c.Id, Content).Kind));
        Assert.All(all, c => Assert.Empty(c.Effects));
    }

    [Fact]
    public void Passive_offer_shows_three_and_two_free_rerolls_show_six_new_ones()
    {
        var p = NewProfile();
        Dons.QueueOffers(p, Content, 8, 49, 50);
        int at = p.Pending.FindIndex(o => o.Kind == Dons.KindPassive);
        // Bring it to the front, as the older offers would have been answered.
        var offer = p.Pending[at];
        p.Pending.RemoveAt(at);
        p.Pending.Insert(0, offer);

        var seen = offer.Cards.ToList();
        Assert.Equal(3, seen.Count);
        Assert.Equal(2, Dons.PassiveRerollsLeft(p, offer, Content));

        long kamas = 0;
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal("ok", Dons.Reroll(p, Content, 8, ref kamas, out long paid));
            Assert.Equal(0, paid);
            Assert.Equal(3, p.Pending[0].Cards.Count);
            Assert.Empty(p.Pending[0].Cards.Intersect(seen));
            seen.AddRange(p.Pending[0].Cards);
        }
        Assert.Equal(9, seen.Distinct().Count());
        Assert.Equal(0, Dons.PassiveRerollsLeft(p, p.Pending[0], Content));
        Assert.Equal("passif-plus-de-relance", Dons.Reroll(p, Content, 8, ref kamas, out _));
        Assert.Equal(0, kamas);
        Assert.Equal(0, p.RerollsUsed);   // the cycle's Relance rerolls are not touched

        // The chosen one is what the fight arms.
        Assert.Equal("ok", Dons.Pick(p, Content, 2, null, out var kept));
        Assert.Equal(kept!.Card, P.ChosenCard(p, Content));
    }

    [Fact]
    public void An_old_stat_passive_is_dropped_and_offered_again()
    {
        var p = NewProfile();
        p.Dons.Add(new ChosenDon { Level = Dons.PassiveLevelOffset + 50, Card = "iop_terre" });
        Dons.EnsureOffers(p, Content, 8, 60);
        Assert.DoesNotContain(p.Dons, d => d.Card == "iop_terre");
        Assert.Contains(p.Pending, o => o.Kind == Dons.KindPassive && o.Cards.Count == 3);
    }

    // ─── The triggers ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Iop_stacks_five_percent_per_close_blow_up_to_thirty_a_turn()
    {
        var iop = F(1, 0, 100);
        var foe = F(2, 1, 101);
        var s = P.Arm(iop, P.Kind.Iop)!;
        P.TurnStart(new[] { s }, iop, 1, None);
        for (int i = 0; i < 8; i++) P.BlowLanded(new[] { s }, iop, foe, 2, 1, 1);
        Assert.Equal(30, P.DamagePercent(new[] { s }, iop, foe, 2, 1));
        // From afar nothing more.
        s.GainedThisTurn.Clear();
        P.BlowLanded(new[] { s }, iop, foe, 5, 1, 1);
        Assert.Equal(30, P.DamagePercent(new[] { s }, iop, foe, 5, 1));
        // Two turns: gone at the turn start of round 3.
        P.TurnStart(new[] { s }, iop, 2, None);
        Assert.Equal(30, P.DamagePercent(new[] { s }, iop, foe, 1, 1));
        P.TurnStart(new[] { s }, iop, 3, None);
        Assert.Equal(0, P.DamagePercent(new[] { s }, iop, foe, 1, 1));
        Assert.Equal(1300, P.Apply(1000, 30));
    }

    [Fact]
    public void Zobal_shields_on_odd_turns_and_hits_harder_on_even_ones()
    {
        var zob = F(1, 0, hp: 2000);
        var foe = F(2, 1);
        var s = P.Arm(zob, P.Kind.Zobal)!;
        var first = P.TurnStart(new[] { s }, zob, 1, None);
        var shield = Assert.Single(first);
        Assert.Equal((P.ActionKind.Shield, 500, 2), (shield.Kind, shield.Amount, shield.UntilRound));
        Assert.Equal(0, P.DamagePercent(new[] { s }, zob, foe, 3, 1));
        Assert.Empty(P.TurnStart(new[] { s }, zob, 2, None));
        Assert.Equal(25, P.DamagePercent(new[] { s }, zob, foe, 3, 1));
        Assert.Single(P.TurnStart(new[] { s }, zob, 3, None));
        Assert.Equal(0, P.DamagePercent(new[] { s }, zob, foe, 3, 1));
    }

    [Fact]
    public void Roublard_cycles_seven_fifteen_twentyfive_and_doubles_down_close_on_the_third()
    {
        var rou = F(1, 0);
        var foe = F(2, 1);
        var s = P.Arm(rou, P.Kind.Roublard)!;
        P.TurnStart(new[] { s }, rou, 1, None);
        Assert.Equal(7, P.DamagePercent(new[] { s }, rou, foe, 2, 2));
        P.TurnStart(new[] { s }, rou, 2, None);
        Assert.Equal(15, P.DamagePercent(new[] { s }, rou, foe, 2, 2));
        P.TurnStart(new[] { s }, rou, 3, None);
        Assert.Equal(75, P.DamagePercent(new[] { s }, rou, foe, 2, 2));
        Assert.Equal(25, P.DamagePercent(new[] { s }, rou, foe, 3, 2));
        P.TurnStart(new[] { s }, rou, 4, None);
        Assert.Equal(7, P.DamagePercent(new[] { s }, rou, foe, 2, 2));
    }

    [Fact]
    public void Sacrieur_saves_each_ally_once_with_half_life_damage_and_invulnerability()
    {
        var sacri = F(1, 0);
        var ally = F(3, 0, hp: 800);
        var foe = F(2, 1);
        var s = P.Arm(sacri, P.Kind.Sacrieur)!;
        var saved = P.MortalBlow(new[] { s }, ally, 4);
        Assert.Contains(saved, a => a.Kind == P.ActionKind.HealTo && a.Amount == 400 && a.Who == ally);
        Assert.Contains(saved, a => a.Kind == P.ActionKind.Invulnerable && a.Who == ally);
        Assert.Equal(25, P.DamagePercent(new[] { s }, ally, foe, 3, 1));
        Assert.Empty(P.MortalBlow(new[] { s }, ally, 5));
        Assert.Empty(P.MortalBlow(new[] { s }, foe, 5));   // not for the other side
        Assert.NotEmpty(P.MortalBlow(new[] { s }, sacri, 5));
    }

    [Fact]
    public void Xelor_removal_raises_ap_removal_and_gives_back_up_to_five_ap_a_turn()
    {
        var xel = F(1, 0);
        var foe = F(2, 1);
        var s = P.Arm(xel, P.Kind.Xelor)!;
        P.TurnStart(new[] { s }, xel, 1, None);
        var given = P.RemovalAttempt(new[] { s }, xel, ap: true, landed: 3, foe, 1)
                     .Concat(P.RemovalAttempt(new[] { s }, xel, ap: true, landed: 3, foe, 1)).ToList();
        Assert.Equal(5, given.Where(a => a.Kind == P.ActionKind.GiveAp).Sum(a => a.Amount));
        Assert.Equal(20, xel.Otra(P.RetraitPA));
        Assert.Equal(10, P.DamagePercent(new[] { s }, xel, foe, 3, 1));
        // An MP removal is the Sadida's, not his.
        Assert.Empty(P.RemovalAttempt(new[] { s }, xel, ap: false, landed: 2, foe, 1));
        // Three turns, then the removal points go back.
        P.TurnStart(new[] { s }, xel, 4, None);
        Assert.Equal(0, xel.Otra(P.RetraitPA));
    }

    [Fact]
    public void Sadida_marks_the_target_and_takes_back_mp()
    {
        var sadi = F(1, 0);
        var foe = F(2, 1);
        var other = F(4, 0);
        var s = P.Arm(sadi, P.Kind.Sadida)!;
        P.TurnStart(new[] { s }, sadi, 1, None);
        var given = P.RemovalAttempt(new[] { s }, sadi, ap: false, landed: 2, foe, 1);
        Assert.Equal(2, given.Single(a => a.Kind == P.ActionKind.GiveMp).Amount);
        Assert.Equal(10, sadi.Otra(P.RetraitPM));
        // Its ally hits the marked target 5 % harder.
        Assert.Equal(5, P.DamagePercent(new[] { s }, other, foe, 3, 1));
    }

    [Fact]
    public void Pandawa_gets_points_from_enemies_around_and_damage_from_what_he_left()
    {
        var pan = F(1, 0);
        var foe = F(2, 1);
        var s = P.Arm(pan, P.Kind.Pandawa)!;
        var start = P.TurnStart(new[] { s }, pan, 1, () => new P.Surroundings { EnemiesAdjacent = 3, EnemiesAtRange = 3 });
        Assert.Equal(3, start.Single(a => a.Kind == P.ActionKind.GiveAp).Amount);
        Assert.Equal(1, start.Single(a => a.Kind == P.ActionKind.GiveMp).Amount);
        pan.CurrentAP = 4;
        pan.CurrentMP = 1;
        P.TurnEnd(s, 1, new[] { pan });
        P.TurnStart(new[] { s }, pan, 2, None);
        Assert.Equal(20, P.DamagePercent(new[] { s }, pan, foe, 3, 1));   // 15 (AP, capped) + 5 (MP)
        Assert.Equal(10, P.HealPercent(new[] { s }, pan));
    }

    [Fact]
    public void Enutrof_walks_for_damage_and_keeps_up_to_six_mp()
    {
        var enu = F(1, 0);
        var foe = F(2, 1);
        var s = P.Arm(enu, P.Kind.Enutrof)!;
        P.TurnStart(new[] { s }, enu, 1, None);
        P.Walked(new[] { s }, enu, 10, 1);
        Assert.Equal(25, P.DamagePercent(new[] { s }, enu, foe, 3, 1));
        enu.CurrentMP = 9;
        P.TurnEnd(s, 1, new[] { enu });
        var next = P.TurnStart(new[] { s }, enu, 2, None);
        Assert.Equal(6, next.Single(a => a.Kind == P.ActionKind.GiveMp).Amount);
    }

    [Fact]
    public void Feca_shields_himself_and_the_allies_in_line()
    {
        var feca = F(1, 0, 300, hp: 1000);
        var cells = Enumerable.Range(0, 560).Where(c => c != 300 && Jondo.Unity.World.Maps.MapGeometry.IsValid(c)).ToList();
        var inLine = F(3, 0, cells.First(c => P.Aligned(c, 300)));
        var offLine = F(4, 0, cells.First(c => !P.Aligned(c, 300)));
        var s = P.Arm(feca, P.Kind.Feca)!;
        var shields = P.TurnEnd(s, 2, new[] { feca, inLine, offLine });
        Assert.Equal(new[] { feca, inLine }, shields.Select(a => a.Who));
        Assert.All(shields, a => Assert.Equal((P.ActionKind.Shield, 500, 3), (a.Kind, a.Amount, a.UntilRound)));
    }

    [Fact]
    public void Ecaflip_roulette_keeps_two_results_and_critical_adds_power_twice()
    {
        var eca = F(1, 0);
        var s = P.Arm(eca, P.Kind.Ecaflip, new Random(7))!;
        for (int round = 1; round <= 6; round++)
        {
            P.TurnStart(new[] { s }, eca, round, None);
            int spins = s.Grants.Where(g => g.Source.StartsWith("roulette")).Select(g => g.Source).Distinct().Count();
            Assert.InRange(spins, 1, 2);
        }
        int power = eca.Power;
        for (int i = 0; i < 3; i++) P.Critical(new[] { s }, eca, 6);
        Assert.Equal(power + 100, eca.Power);
    }

    [Fact]
    public void Forgelance_summon_marks_enemies_around_and_its_death_feeds_the_owner()
    {
        var forge = F(1, 0);
        var foe = F(2, 1);
        var summon = new Fighter { Id = 5, TeamId = 0, Invocador = 1, MaxHP = 10, CurrentHP = 0 };
        var s = P.Arm(forge, P.Kind.Forgelance)!;
        for (int i = 0; i < 5; i++) P.Summoned(new[] { s }, forge, new[] { foe }, 1);
        Assert.Equal(40, P.DamagePercent(new[] { s }, forge, foe, 3, 1) - 0);
        P.Died(new[] { s }, summon, 1);
        Assert.Equal(43, P.DamagePercent(new[] { s }, forge, foe, 3, 1));
    }

    [Fact]
    public void Huppermage_resists_the_element_it_is_hit_by_up_to_ten()
    {
        var hup = F(1, 0);
        var foe = F(2, 1);
        hup.Intelligence = 500;
        var s = P.Arm(hup, P.Kind.Huppermage)!;
        for (int i = 0; i < 7; i++) P.BlowLanded(new[] { s }, foe, hup, 3, 2, 1);
        Assert.Equal(-10, P.DamagePercent(new[] { s }, foe, hup, 3, 2));
        Assert.Equal(0, P.DamagePercent(new[] { s }, foe, hup, 3, 1));
        // Its own elemental spells: +2 % of its best element, 8 % at most.
        for (int i = 0; i < 6; i++) P.BlowLanded(new[] { s }, hup, foe, 3, 2, 1);
        Assert.Equal(540, hup.Intelligence);
    }

    [Fact]
    public void Sram_kill_gives_damage_and_invisibility()
    {
        var sram = F(1, 0);
        var foe = F(2, 1);
        var s = P.Arm(sram, P.Kind.Sram)!;
        var acts = P.Killed(new[] { s }, sram, foe, 2);
        Assert.Contains(acts, a => a.Kind == P.ActionKind.Invisible && a.UntilRound == 3);
        Assert.Equal(5, P.DamagePercent(new[] { s }, sram, F(6, 1), 3, 1));
    }
}
