using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Stasis class passives in a fight (docs/stasis-gameplay-spec.md §10, «Passifs de classe»):
    /// the one a character chose at level 50 among the nineteen classes, armed on him at the start
    /// of a PvM fight, and what it does on the fight's triggers -- turn start, turn end, a blow, a
    /// heal, a kill, a mortal blow, a removal of AP/MP, a push, a teleport, a summon, a critical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file is the RULES, kept free of the network so the tests can drive them: what each
    /// passive grants, for how long, under which caps. The bonuses that live on a characteristic of
    /// the fighter (range, power, AP/MP removal, push damage, resistances, critical, summons) are
    /// written on the <see cref="Fighter"/> at once and taken back when they run out; the damage and
    /// heal percents are read by the blow (<see cref="DamagePercent"/>, <see cref="HealPercent"/>);
    /// and what the board has to be told -- AP or MP given, a shield, a heal, invisibility,
    /// invulnerability -- comes back as <see cref="PassiveAction"/>s, which FightHandler
    /// (Handlers/FightStasisPassives.cs) carries out with their frames.
    /// </para>
    /// <para>
    /// Durations are «N tours» of the bearer: a grant made in round R lasts until the bearer's turn
    /// start of round R + N (<see cref="Timed.ExpiresRound"/>). The per-turn caps («cap 25 %/tour»)
    /// count what the holder gained since his own last turn start.
    /// </para>
    /// <para>
    /// Approximations (also written on each card of content/progression/dons.json, «approximation»):
    /// the damage and heal % are a server-side last multiplier with no buff row on the client's panel;
    /// a temporary range bonus is a characteristic on the server (the client's range preview may lag
    /// one turn); Osamodas' «déclencheur» is read as one of his summons dying; Eniripsa's two stacks
    /// are capped at 30 % each (the spec gives no cap); Ouginak counts at most 6 cells; Huppermage's
    /// «+2 % carac» is 2 % of the best element's characteristic, rounded down, at least 1.
    /// </para>
    /// </remarks>
    internal static class StasisPassives
    {
        /// <summary>The passives, by the breed of the class they come from.</summary>
        internal enum Kind
        {
            None = 0, Feca = 1, Osamodas = 2, Enutrof = 3, Sram = 4, Xelor = 5, Ecaflip = 6, Eniripsa = 7,
            Iop = 8, Cra = 9, Sadida = 10, Sacrieur = 11, Pandawa = 12, Roublard = 13, Zobal = 14,
            Steamer = 15, Eliotrope = 16, Huppermage = 17, Ouginak = 18, Forgelance = 20,
        }

        /// <summary>What a timed grant touches.</summary>
        internal enum Stat
        {
            /// <summary>% of final damage dealt.</summary>
            DamagePct,
            /// <summary>% of final damage dealt from next door (distance 1).</summary>
            MeleeDamagePct,
            /// <summary>% of final heals given.</summary>
            HealPct,
            /// <summary>% more damage the bearer TAKES (a mark the holder put on an enemy).</summary>
            TakenPct,
            /// <summary>% less damage the bearer takes; <see cref="Timed.Element"/> -1 for every element.</summary>
            ResistPct,
            /// <summary>AP given at each of the bearer's turn starts while it lasts.</summary>
            ApEachTurn,
            /// <summary>MP given at each of the bearer's turn starts while it lasts.</summary>
            MpEachTurn,
            // Characteristics written on the fighter while the grant lasts.
            ApRemoval, MpRemoval, PushDamage, Range, Power, Summons, Critical, ElementStat,
        }

        internal const int RetraitPA = 82, RetraitPM = 83, Invocations = 26;

        /// <summary>A grant: on whom, what, how much, until when, and from which rule (for the caps).</summary>
        internal sealed class Timed
        {
            public Fighter Bearer { get; init; } = null!;
            public Stat Stat { get; init; }
            public int Amount { get; set; }
            public int ExpiresRound { get; init; }
            public string Source { get; init; } = "";
            /// <summary>For <see cref="Stat.ResistPct"/> and <see cref="Stat.ElementStat"/>: the element (0-4), -1 all.</summary>
            public int Element { get; init; } = -1;
        }

        internal enum ActionKind { GiveAp, GiveMp, Shield, HealTo, Invisible, Invulnerable }

        /// <summary>What the board must be told: <paramref name="Amount"/> points (AP, MP, shield, life), until a round.</summary>
        internal readonly record struct PassiveAction(ActionKind Kind, Fighter Who, int Amount, int UntilRound = 0);

        /// <summary>A holder's passive in one fight, and everything it has going.</summary>
        internal sealed class State
        {
            public Kind Kind { get; init; }
            public Fighter Holder { get; init; } = null!;
            public List<Timed> Grants { get; } = new();
            /// <summary>The holder's own turns started in this fight (1 at his first).</summary>
            public int OwnTurns { get; set; }
            /// <summary>What each rule gained since the holder's last turn start, for the per-turn caps.</summary>
            public Dictionary<string, int> GainedThisTurn { get; } = new();
            /// <summary>Enutrof: the unused MP kept for the next turn.</summary>
            public int KeptMp { get; set; }
            /// <summary>Sacrieur: the allies who already survived their first mortal blow.</summary>
            public HashSet<long> Survived { get; } = new();
            public Random Dice { get; set; } = Random.Shared;
        }

        private static readonly ConditionalWeakTable<Fighter, State> _states = new();

        // ─── Arming ─────────────────────────────────────────────────────────────────────

        /// <summary>The passive a card stands for: its class's, or none for an unknown card.</summary>
        internal static Kind KindOf(string card, ProgressionContent content)
        {
            int breed = Dons.PassiveBreedOf(card, content);
            return Enum.IsDefined(typeof(Kind), breed) ? (Kind)breed : Kind.None;
        }

        /// <summary>The passive card a character chose at level 50, or null.</summary>
        internal static string? ChosenCard(ProgressionProfile profile, ProgressionContent content)
        {
            int key = Dons.PassiveLevelOffset + content.Dons.PassiveLevel;
            return profile.Dons.FirstOrDefault(d => d.Level == key && Dons.IsPassiveCard(d.Card, content))?.Card;
        }

        /// <summary>Arms a passive on a fighter (the fight's start). Null for none.</summary>
        internal static State? Arm(Fighter fighter, Kind kind, Random? dice = null)
        {
            _states.Remove(fighter);
            if (kind == Kind.None) return null;
            var state = new State { Kind = kind, Holder = fighter, Dice = dice ?? Random.Shared };
            _states.Add(fighter, state);
            return state;
        }

        internal static State? Of(Fighter? fighter)
            => fighter != null && _states.TryGetValue(fighter, out var s) ? s : null;

        /// <summary>The holders among these fighters.</summary>
        internal static IEnumerable<State> Holders(IEnumerable<Fighter> fighters)
        {
            foreach (var f in fighters)
                if (f != null && _states.TryGetValue(f, out var s)) yield return s;
        }

        // ─── Grants ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Grants <paramref name="amount"/> of a stat for <paramref name="turns"/> turns, cut by the
        /// rule's per-turn cap and its cap on what may be active at once (0: none). What was granted.
        /// </summary>
        internal static int Grant(State s, Fighter bearer, Stat stat, int amount, int turns, int round, string source,
                                  int capPerTurn = 0, int capActive = 0, int element = -1)
        {
            if (amount <= 0) return 0;
            if (capPerTurn > 0)
            {
                s.GainedThisTurn.TryGetValue(source, out int gained);
                amount = Math.Min(amount, capPerTurn - gained);
            }
            if (capActive > 0)
            {
                int active = s.Grants.Where(g => g.Bearer == bearer && g.Source == source && g.Stat == stat).Sum(g => g.Amount);
                amount = Math.Min(amount, capActive - active);
            }
            if (amount <= 0) return 0;
            if (capPerTurn > 0) s.GainedThisTurn[source] = (s.GainedThisTurn.TryGetValue(source, out int g0) ? g0 : 0) + amount;
            var grant = new Timed { Bearer = bearer, Stat = stat, Amount = amount, ExpiresRound = round + Math.Max(1, turns), Source = source, Element = element };
            s.Grants.Add(grant);
            Write(grant, +1);
            return amount;
        }

        /// <summary>The characteristics a grant lives on, put on (+1) or taken off (-1).</summary>
        private static void Write(Timed g, int sign)
        {
            var f = g.Bearer;
            int v = g.Amount * sign;
            switch (g.Stat)
            {
                case Stat.ApRemoval: f.Otras[RetraitPA] = f.Otra(RetraitPA) + v; break;
                case Stat.MpRemoval: f.Otras[RetraitPM] = f.Otra(RetraitPM) + v; break;
                case Stat.Summons: f.Otras[Invocations] = f.Otra(Invocations) + v; break;
                case Stat.PushDamage: f.PushDamage += v; break;
                case Stat.Range: f.Range += v; break;
                case Stat.Power: f.Power += v; break;
                case Stat.Critical: f.CriticalBonus += v; break;
                case Stat.ElementStat:
                    switch ((ElementType)g.Element)
                    {
                        case ElementType.Earth: f.Strength += v; break;
                        case ElementType.Fire: f.Intelligence += v; break;
                        case ElementType.Water: f.Chance += v; break;
                        case ElementType.Air: f.Agility += v; break;
                    }
                    break;
            }
        }

        private static void Drop(State s, Timed g)
        {
            if (s.Grants.Remove(g)) Write(g, -1);
        }

        /// <summary>Everything the holder put on this fighter, gone (his death, the fight's end).</summary>
        internal static void Disarm(Fighter fighter)
        {
            if (Of(fighter) is { } s)
                foreach (var g in s.Grants.ToList()) Drop(s, g);
            _states.Remove(fighter);
        }

        private static int Sum(State s, Fighter bearer, Stat stat, int element = -2)
            => s.Grants.Where(g => g.Bearer == bearer && g.Stat == stat && (element == -2 || g.Element == -1 || g.Element == element))
                       .Sum(g => g.Amount);

        // ─── Reading the blow ───────────────────────────────────────────────────────────

        /// <summary>
        /// The % the passives add to a blow of <paramref name="caster"/> on <paramref name="target"/>
        /// at <paramref name="distance"/>: the caster's own grants, the marks any holder put on the
        /// target, the Roublard's third turn, and minus the target's resistances of the element.
        /// </summary>
        internal static int DamagePercent(IEnumerable<State> holders, Fighter caster, Fighter target, int distance, int element)
        {
            int percent = 0;
            foreach (var s in holders)
            {
                percent += Sum(s, caster, Stat.DamagePct);
                if (distance <= 1) percent += Sum(s, caster, Stat.MeleeDamagePct);
                if (caster.TeamId != target.TeamId) percent += Sum(s, target, Stat.TakenPct);
                percent -= Sum(s, target, Stat.ResistPct, element);
                if (s.Kind == Kind.Roublard && s.Holder == caster && RoublardPhase(s) == 3 && distance <= 2
                    && caster.TeamId != target.TeamId)
                    percent += 50;
            }
            return Math.Max(-100, percent);
        }

        /// <summary>The % the passives add to a heal of <paramref name="caster"/>.</summary>
        internal static int HealPercent(IEnumerable<State> holders, Fighter caster)
            => holders.Sum(s => Sum(s, caster, Stat.HealPct));

        /// <summary>A value with a % on top (rounded as the engine's own last multipliers).</summary>
        internal static int Apply(int value, int percent)
            => percent == 0 || value <= 0 ? value : Math.Max(0, DamageCalculator.RoundedPercent(value, 100 + percent));

        /// <summary>1, 2 or 3: where the Roublard stands in his three-turn cycle.</summary>
        internal static int RoublardPhase(State s) => s.OwnTurns <= 0 ? 1 : (s.OwnTurns - 1) % 3 + 1;

        // ─── Turn start / end ───────────────────────────────────────────────────────────

        /// <summary>What a turn start sees around the one who starts it.</summary>
        internal sealed class Surroundings
        {
            public int EnemiesInSight { get; init; }
            public int EnemiesOutOfSight { get; init; }
            public int EnemiesAdjacent { get; init; }
            public int EnemiesAtRange { get; init; }
            /// <summary>Distance to the nearest enemy, 0 when none.</summary>
            public int NearestEnemy { get; init; }
        }

        /// <summary>
        /// Any fighter's turn start: every holder's grants on him that ran out go; and if he is a
        /// holder, his passive's turn-start rule.
        /// </summary>
        internal static List<PassiveAction> TurnStart(IEnumerable<State> holders, Fighter starting, int round, Func<Surroundings> around)
        {
            var actions = new List<PassiveAction>();
            foreach (var s in holders)
            {
                foreach (var g in s.Grants.Where(g => g.Bearer == starting && round >= g.ExpiresRound).ToList()) Drop(s, g);
                // What still runs gives its points again: Ecaflip's +3 AP / +3 MP for two turns.
                int ap = Sum(s, starting, Stat.ApEachTurn), mp = Sum(s, starting, Stat.MpEachTurn);
                if (ap > 0) actions.Add(new PassiveAction(ActionKind.GiveAp, starting, ap));
                if (mp > 0) actions.Add(new PassiveAction(ActionKind.GiveMp, starting, mp));
            }
            var mine = Of(starting);
            if (mine == null || !starting.IsAlive) return actions;
            mine.OwnTurns++;
            mine.GainedThisTurn.Clear();
            OwnTurnStart(mine, round, around(), actions);
            return actions;
        }

        private static void OwnTurnStart(State s, int round, Surroundings around, List<PassiveAction> actions)
        {
            var me = s.Holder;
            switch (s.Kind)
            {
                case Kind.Enutrof:
                    if (s.KeptMp > 0) actions.Add(new PassiveAction(ActionKind.GiveMp, me, s.KeptMp));
                    s.KeptMp = 0;
                    break;
                case Kind.Ecaflip:
                    Roulette(s, round, actions);
                    break;
                case Kind.Cra:
                    Grant(s, me, Stat.DamagePct, 5 * around.EnemiesInSight, 1, round, "cra");
                    Grant(s, me, Stat.Range, 2 * around.EnemiesOutOfSight, 1, round, "cra-po");
                    break;
                case Kind.Pandawa:
                {
                    int ap = Math.Min(4, around.EnemiesAdjacent);
                    int mp = Math.Min(4 - ap, around.EnemiesAtRange);
                    if (ap > 0) actions.Add(new PassiveAction(ActionKind.GiveAp, me, ap));
                    if (mp > 0) actions.Add(new PassiveAction(ActionKind.GiveMp, me, mp));
                    break;
                }
                case Kind.Roublard:
                    Grant(s, me, Stat.DamagePct, RoublardPhase(s) switch { 1 => 7, 2 => 15, _ => 25 }, 1, round, "roublard");
                    break;
                case Kind.Zobal:
                    if (s.OwnTurns % 2 == 1)
                        actions.Add(new PassiveAction(ActionKind.Shield, me, Math.Max(1, me.MaxHP * 25 / 100), round + 1));
                    else
                        Grant(s, me, Stat.DamagePct, 25, 1, round, "zobal");
                    break;
                case Kind.Ouginak:
                {
                    int cells = Math.Min(6, Math.Max(0, around.NearestEnemy - 1));
                    if (cells > 0)
                    {
                        actions.Add(new PassiveAction(ActionKind.GiveMp, me, cells));
                        Grant(s, me, Stat.MeleeDamagePct, 5 * cells, 1, round, "ouginak");
                    }
                    break;
                }
            }
        }

        /// <summary>The Ecaflip's roulette: one of six for two turns, the two latest kept.</summary>
        private static void Roulette(State s, int round, List<PassiveAction> actions)
        {
            var me = s.Holder;
            // Two results at most: the oldest goes for the new one.
            var spins = s.Grants.Where(g => g.Source.StartsWith("roulette", StringComparison.Ordinal))
                                .GroupBy(g => g.Source).OrderBy(g => g.Min(x => x.ExpiresRound)).ToList();
            if (spins.Count >= 2)
                foreach (var g in spins[0].ToList()) Drop(s, g);
            int face = s.Dice.Next(6) + 1;
            string source = $"roulette{face}-{round}";
            switch (face)
            {
                case 1:
                    Grant(s, me, Stat.DamagePct, 25, 2, round, source);
                    Grant(s, me, Stat.PushDamage, 250, 2, round, source);
                    break;
                case 2:
                    Grant(s, me, Stat.HealPct, 50, 2, round, source);
                    Grant(s, me, Stat.Critical, 777, 2, round, source);
                    break;
                case 3:
                    Grant(s, me, Stat.ResistPct, 25, 2, round, source);
                    break;
                case 4:
                    Grant(s, me, Stat.Range, 5, 2, round, source);
                    Grant(s, me, Stat.Summons, 5, 2, round, source);
                    break;
                case 5:
                    Grant(s, me, Stat.ApEachTurn, 3, 2, round, source);
                    actions.Add(new PassiveAction(ActionKind.GiveAp, me, 3));
                    break;
                default:
                    Grant(s, me, Stat.MpEachTurn, 3, 2, round, source);
                    actions.Add(new PassiveAction(ActionKind.GiveMp, me, 3));
                    break;
            }
        }

        /// <summary>The holder's turn end: Féca's shields, Pandawa's unused points, Enutrof's kept MP.</summary>
        internal static List<PassiveAction> TurnEnd(State s, int round, IReadOnlyList<Fighter> allies)
        {
            var actions = new List<PassiveAction>();
            var me = s.Holder;
            if (!me.IsAlive) return actions;
            switch (s.Kind)
            {
                case Kind.Feca:
                    // Him and every ally standing in line with him, for a turn.
                    foreach (var ally in allies.Where(a => a.IsAlive && (a == me || Aligned(a.CellId, me.CellId))))
                        actions.Add(new PassiveAction(ActionKind.Shield, ally, Math.Max(1, me.MaxHP * 50 / 100), round + 1));
                    break;
                case Kind.Pandawa:
                {
                    int ap = Math.Max(0, me.CurrentAP), mp = Math.Max(0, me.CurrentMP);
                    // For his next turn: granted now, it lives until the turn start after it.
                    Grant(s, me, Stat.DamagePct, Math.Min(15, 5 * ap), 2, round, "pandawa-pa");
                    Grant(s, me, Stat.DamagePct, Math.Min(15, 5 * mp), 2, round, "pandawa-pm");
                    Grant(s, me, Stat.HealPct, 2 * (ap + mp), 2, round, "pandawa-soins");
                    break;
                }
                case Kind.Enutrof:
                    s.KeptMp = Math.Min(6, Math.Max(0, me.CurrentMP));
                    break;
            }
            return actions;
        }

        /// <summary>Two cells in line: the same column or row of the map's diagonal grid.</summary>
        internal static bool Aligned(int a, int b)
        {
            var (ax, ay) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(a);
            var (bx, by) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(b);
            if (ax == int.MinValue || bx == int.MinValue) return false;
            return ax == bx || ay == by;
        }

        // ─── Blows, heals, kills ────────────────────────────────────────────────────────

        /// <summary>
        /// A blow landed (damage worked out, above zero): the stacks it feeds. <paramref name="caster"/>
        /// dealt it, <paramref name="target"/> took it.
        /// </summary>
        internal static void BlowLanded(IEnumerable<State> holders, Fighter caster, Fighter target, int distance, int element, int round)
        {
            foreach (var s in holders)
            {
                var me = s.Holder;
                if (caster == me && target.TeamId != me.TeamId)
                {
                    switch (s.Kind)
                    {
                        case Kind.Iop when distance <= 2:
                            Grant(s, me, Stat.DamagePct, 5, 2, round, "iop", capPerTurn: 30);
                            break;
                        case Kind.Eniripsa:
                            Grant(s, me, Stat.HealPct, 3, 2, round, "eni-soins", capActive: 30);
                            break;
                        case Kind.Huppermage when element is >= 1 and <= 4:
                        {
                            var (best, value) = BestElement(me);
                            // The % are of the characteristic without what this passive already added.
                            int already = s.Grants.Where(g => g.Bearer == me && g.Stat == Stat.ElementStat && g.Element == best).Sum(g => g.Amount);
                            int baseValue = Math.Max(0, value - already);
                            int step = Math.Max(1, baseValue * 2 / 100), cap = Math.Max(1, baseValue * 8 / 100);
                            Grant(s, me, Stat.ElementStat, step, 2, round, "hupper", capActive: cap, element: best);
                            break;
                        }
                    }
                }
                if (target == me && caster.TeamId != me.TeamId && s.Kind == Kind.Huppermage && element is >= 0 and <= 4)
                    Grant(s, me, Stat.ResistPct, 2, 2, round, "hupper-res" + element, capActive: 10, element: element);
            }
        }

        /// <summary>The best of the four elemental characteristics, and its value.</summary>
        internal static (int Element, int Value) BestElement(Fighter f)
        {
            var all = new[] { (1, f.Strength), (2, f.Intelligence), (3, f.Chance), (4, f.Agility) };
            var best = all.OrderByDescending(x => x.Item2).First();
            return (best.Item1, Math.Max(0, best.Item2));
        }

        /// <summary>A heal given by <paramref name="caster"/>: Eniripsa's damage stack.</summary>
        internal static void HealGiven(IEnumerable<State> holders, Fighter caster, int round)
        {
            foreach (var s in holders)
                if (s.Holder == caster && s.Kind == Kind.Eniripsa)
                    Grant(s, caster, Stat.DamagePct, 3, 2, round, "eni-dmg", capActive: 30);
        }

        /// <summary>An enemy finished by <paramref name="killer"/>: Sram's damage and invisibility.</summary>
        internal static List<PassiveAction> Killed(IEnumerable<State> holders, Fighter killer, Fighter dead, int round)
        {
            var actions = new List<PassiveAction>();
            foreach (var s in holders)
            {
                if (s.Holder != killer || s.Kind != Kind.Sram || dead.TeamId == killer.TeamId || !killer.IsAlive) continue;
                Grant(s, killer, Stat.DamagePct, 5, 3, round, "sram");
                actions.Add(new PassiveAction(ActionKind.Invisible, killer, 1, round + 1));
            }
            return actions;
        }

        /// <summary>
        /// A blow that would take <paramref name="victim"/>'s last life: a Sacrieur on his side lets
        /// him live, once a fight -- half his life back, +25 % damage for a turn, invulnerable until
        /// his turn. Empty when nobody saves him.
        /// </summary>
        internal static List<PassiveAction> MortalBlow(IEnumerable<State> holders, Fighter victim, int round)
        {
            var actions = new List<PassiveAction>();
            if (victim.EsInvocado) return actions;
            foreach (var s in holders)
            {
                if (s.Kind != Kind.Sacrieur || s.Holder.TeamId != victim.TeamId) continue;
                if (!s.Survived.Add(victim.Id)) continue;
                actions.Add(new PassiveAction(ActionKind.HealTo, victim, Math.Max(1, victim.MaxHP / 2)));
                Grant(s, victim, Stat.DamagePct, 25, 1, round, "sacri");
                actions.Add(new PassiveAction(ActionKind.Invulnerable, victim, 1, round + 1));
                break;
            }
            return actions;
        }

        // ─── What a cast does ───────────────────────────────────────────────────────────

        /// <summary>An attempt by <paramref name="caster"/> to take AP (<paramref name="ap"/>) or MP from an enemy, and how many landed.</summary>
        internal static List<PassiveAction> RemovalAttempt(IEnumerable<State> holders, Fighter caster, bool ap, int landed, Fighter target, int round)
        {
            var actions = new List<PassiveAction>();
            foreach (var s in holders)
            {
                if (s.Holder != caster || caster.TeamId == target.TeamId) continue;
                if (s.Kind == Kind.Xelor && ap)
                {
                    Grant(s, caster, Stat.ApRemoval, 10, 3, round, "xel-ret");
                    Grant(s, caster, Stat.DamagePct, 5, 3, round, "xel-dmg", capPerTurn: 25);
                    int give = landed <= 0 ? 0 : Math.Min(landed, 5 - (s.GainedThisTurn.TryGetValue("xel-pa", out int g) ? g : 0));
                    if (give > 0)
                    {
                        s.GainedThisTurn["xel-pa"] = (s.GainedThisTurn.TryGetValue("xel-pa", out int g2) ? g2 : 0) + give;
                        actions.Add(new PassiveAction(ActionKind.GiveAp, caster, give));
                    }
                }
                else if (s.Kind == Kind.Sadida && !ap)
                {
                    Grant(s, caster, Stat.MpRemoval, 10, 3, round, "sadi-ret", capActive: 50);
                    Grant(s, caster, Stat.DamagePct, 5, 2, round, "sadi-dmg", capPerTurn: 25);
                    Grant(s, target, Stat.TakenPct, 5, 1, round, "sadi-cible", capActive: 5);
                    int give = landed <= 0 ? 0 : Math.Min(landed, 4 - (s.GainedThisTurn.TryGetValue("sadi-pm", out int g) ? g : 0));
                    if (give > 0)
                    {
                        s.GainedThisTurn["sadi-pm"] = (s.GainedThisTurn.TryGetValue("sadi-pm", out int g2) ? g2 : 0) + give;
                        actions.Add(new PassiveAction(ActionKind.GiveMp, caster, give));
                    }
                }
            }
            return actions;
        }

        /// <summary>Steps walked by <paramref name="mover"/>: Enutrof's damage.</summary>
        internal static void Walked(IEnumerable<State> holders, Fighter mover, int steps, int round)
        {
            foreach (var s in holders)
                if (s.Holder == mover && s.Kind == Kind.Enutrof)
                    Grant(s, mover, Stat.DamagePct, 3 * steps, 2, round, "enu", capPerTurn: 25);
        }

        /// <summary>A push, pull or swap by <paramref name="caster"/> (Steamer).</summary>
        internal static void Displaced(IEnumerable<State> holders, Fighter caster, int round)
        {
            foreach (var s in holders)
            {
                if (s.Holder != caster || s.Kind != Kind.Steamer) continue;
                if (Grant(s, caster, Stat.DamagePct, 5, 2, round, "steam", capPerTurn: 30) > 0)
                    Grant(s, caster, Stat.PushDamage, 30, 2, round, "steam-pou");
            }
        }

        /// <summary>An ally of <paramref name="caster"/> teleported or swapped by him (Eliotrope).</summary>
        internal static List<PassiveAction> AllyTeleported(IEnumerable<State> holders, Fighter caster, Fighter ally, int round)
        {
            var actions = new List<PassiveAction>();
            foreach (var s in holders)
            {
                if (s.Holder != caster || s.Kind != Kind.Eliotrope || ally.TeamId != caster.TeamId) continue;
                actions.Add(new PassiveAction(ActionKind.Shield, ally, Math.Max(1, ally.MaxHP * 10 / 100), round + 3));
                Grant(s, caster, Stat.DamagePct, 5, 2, round, "elio", capPerTurn: 30);
            }
            return actions;
        }

        /// <summary>A creature summoned by <paramref name="summoner"/>, with the enemies next to it.</summary>
        internal static void Summoned(IEnumerable<State> holders, Fighter summoner, IEnumerable<Fighter> enemiesAround, int round)
        {
            foreach (var s in holders)
            {
                if (s.Holder != summoner) continue;
                if (s.Kind == Kind.Osamodas) OsamodasStack(s, round);
                else if (s.Kind == Kind.Forgelance)
                    foreach (var enemy in enemiesAround)
                        Grant(s, enemy, Stat.TakenPct, 10, 1, round, "forge-cible", capActive: 40);
            }
        }

        private static void OsamodasStack(State s, int round)
        {
            Grant(s, s.Holder, Stat.DamagePct, 5, 2, round, "osa-dmg", capPerTurn: 25);
            Grant(s, s.Holder, Stat.HealPct, 5, 2, round, "osa-soins", capPerTurn: 25);
        }

        /// <summary>A fighter died: a holder's summon (Forgelance, Osamodas), and the dead holder's own grants.</summary>
        internal static void Died(IEnumerable<State> holders, Fighter dead, int round)
        {
            foreach (var s in holders.ToList())
            {
                if (dead.EsInvocado && dead.Invocador == s.Holder.Id && s.Holder.IsAlive)
                {
                    if (s.Kind == Kind.Forgelance) Grant(s, s.Holder, Stat.DamagePct, 3, 3, round, "forge-mort");
                    else if (s.Kind == Kind.Osamodas) OsamodasStack(s, round);
                }
                // What any holder put on the dead goes with him.
                foreach (var g in s.Grants.Where(g => g.Bearer == dead).ToList()) Drop(s, g);
            }
        }

        /// <summary>A critical cast by <paramref name="caster"/>: Ecaflip's +50 power for two turns, twice at most.</summary>
        internal static void Critical(IEnumerable<State> holders, Fighter caster, int round)
        {
            foreach (var s in holders)
                if (s.Holder == caster && s.Kind == Kind.Ecaflip)
                    Grant(s, caster, Stat.Power, 50, 2, round, "eca-cc", capActive: 100);
        }
    }
}
