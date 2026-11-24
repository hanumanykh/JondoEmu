using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>What a character's spell dons do to one of his spells, summed over the dons and their ranks.</summary>
    public readonly record struct SpellDonModifiers(
        int DamagePercent, int HealPercent, int Critical, int Range, int Cooldown, int ApCost, int CastsPerTurn)
    {
        public bool IsEmpty => DamagePercent == 0 && HealPercent == 0 && Critical == 0 && Range == 0
                               && Cooldown == 0 && ApCost == 0 && CastsPerTurn == 0;
    }

    /// <summary>
    /// The spell dons: the Stasis server's "points de sort", cut down to the owner's selection --
    /// five to ten dons per class (content/progression/spell_dons.json), each on one real spell of
    /// the class, each rank one point -- and the ONE place that says what they do to a spell:
    /// <see cref="ModifiersOf(long, int)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the dons go into the fighter rather than being asked at every rule. The fight already
    /// has a road for "this fighter's spell is modified the whole fight": a dream's "Sorts : +1
    /// Portée maximale" sits in Fighter.EverySpell, and range, AP cost, casts per turn, cast
    /// interval and base damage all read it through Fighter.AllSpellsModifier -- and, which is the
    /// part that matters, the fight opening tells the CLIENT about it with one hnd per spell
    /// (FightHandler.SendEverySpellModifiersAsync), which is what makes the client draw the longer
    /// range and allow the cheaper cast instead of refusing it from its own spell data. A spell don
    /// is the same thing for one spell, so <see cref="ApplyTo"/> puts it on the fighter
    /// (Fighter.OwnSpellModifiers) when the character's fighter is built, and every one of those
    /// rules honours it without a line of its own. The two kinds no aspect of the catalogue
    /// carries -- percent damage and percent healing of one spell -- get their own maps on the
    /// fighter, read where the blow and the heal take their final multipliers.
    /// </para>
    /// <para>
    /// What the client is told and what it is not: AP cost (modifier 5), casts per turn (10),
    /// maximum range (12) and critical (7) go out as hnd at the fight opening, as the captures show
    /// the real server doing. The cast interval (286) has no hnd in any capture: the server keeps
    /// the shorter wait, and after a cast it sends the caster his cooldowns (jxc) so his bar does
    /// not keep the spell greyed for the turn the don took off. Percent damage and healing are the
    /// server's alone, as a dream's "% dommages" is; the client's damage preview does not show them.
    /// </para>
    /// <para>
    /// The caps are the Stasis ones, from the file: so many points in all (14), so many spell
    /// families (5, opened at levels 10/30/50/70/90), and per family no more points than the level
    /// allows (1, then 2 from 70, 3 from 90, 4 from 110, 5 from 130). A family is a spell and its
    /// variant (<see cref="SpellTable.Pair"/>): they share their points, as on Stasis. Bots and
    /// monsters have no dons: nothing is put on their fighters.
    /// </para>
    /// </remarks>
    public static class SpellDons
    {
        internal const string Damage = "degats", Heal = "soins", Critical = "critique", Range = "portee",
                              Cooldown = "relance", ApCost = "pa", CastsPerTurn = "lancers";

        /// <summary>A class's spell dons, as the file lists them.</summary>
        internal static IReadOnlyList<SpellDonDefinition> ListOf(int breed, ProgressionContent content)
            => content.SpellDons.Classes.TryGetValue(breed.ToString(), out var list) ? list : new List<SpellDonDefinition>();

        /// <summary>A spell don by its id, whatever the class, with the breed it belongs to; or null.</summary>
        internal static (SpellDonDefinition Don, int Breed)? DefinitionOf(string id, ProgressionContent content)
        {
            foreach (var (breed, list) in content.SpellDons.Classes)
                foreach (var don in list)
                    if (string.Equals(don.Id, id, StringComparison.OrdinalIgnoreCase))
                        return (don, int.TryParse(breed, out int b) ? b : 0);
            return null;
        }

        internal static bool IsSpellDon(string id, ProgressionContent content) => DefinitionOf(id, content) != null;

        // ─── What they do ───────────────────────────────────────────────────────────────

        /// <summary>
        /// What a character's spell dons do to one of his spells. Empty for no character (id 0, the
        /// tests' and the bots' sessions) and for a spell no don names.
        /// </summary>
        public static SpellDonModifiers ModifiersOf(long characterId, int spellId)
        {
            if (characterId <= 0 || spellId <= 0) return default;
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return AllOf(profile, ProgressionContent.Current).GetValueOrDefault(spellId);
        }

        /// <summary>Every spell the character's spell dons touch, with what they do to it.</summary>
        internal static Dictionary<int, SpellDonModifiers> AllOf(ProgressionProfile profile, ProgressionContent content)
        {
            var sums = new Dictionary<int, int[]>();
            foreach (var chosen in profile.Dons)
            {
                if (DefinitionOf(chosen.Card, content) is not { } found) continue;
                var don = found.Don;
                if (!sums.TryGetValue(don.Spell, out var s)) sums[don.Spell] = s = new int[7];
                int slot = don.Kind switch
                {
                    Damage => 0, Heal => 1, Critical => 2, Range => 3, Cooldown => 4, ApCost => 5, CastsPerTurn => 6,
                    _ => -1,
                };
                if (slot >= 0) s[slot] += don.Value;
            }
            // AP: never more than one off a spell, whatever the file says. The file only offers it
            // on spells that cost 4 or more at every grade, so the cast still costs 3; the cap keeps
            // a careless edit from making a spell free.
            return sums.ToDictionary(kv => kv.Key, kv => new SpellDonModifiers(
                kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], kv.Value[4], Math.Min(1, kv.Value[5]), kv.Value[6]));
        }

        /// <summary>
        /// Puts a character's spell dons on the fighter he fights with: the aspects on
        /// Fighter.OwnSpellModifiers, the percents on their maps. Called once, where
        /// FightHandler builds the character's fighter; not for a monster he fights as.
        /// </summary>
        public static int ApplyTo(Fighter fighter, long characterId)
        {
            if (fighter == null || characterId <= 0) return 0;
            var profile = ProgressionStore.Of(characterId);
            Dictionary<int, SpellDonModifiers> all;
            lock (profile.Gate) all = AllOf(profile, ProgressionContent.Current);
            Put(fighter, all);
            return all.Count;
        }

        internal static void Put(Fighter fighter, IReadOnlyDictionary<int, SpellDonModifiers> all)
        {
            foreach (var (spell, m) in all)
            {
                void Aspect(SpellAspect aspect, int value)
                {
                    if (value != 0) fighter.OwnSpellModifiers[(spell, aspect)] = fighter.OwnSpellModifiers.GetValueOrDefault((spell, aspect)) + value;
                }
                Aspect(SpellAspect.CriticalUp, m.Critical);
                Aspect(SpellAspect.AlcanceMaximo, m.Range);
                Aspect(SpellAspect.CastIntervalDown, m.Cooldown);
                Aspect(SpellAspect.ApCostDown, m.ApCost);
                Aspect(SpellAspect.CastsPerTurnUp, m.CastsPerTurn);
                if (m.DamagePercent != 0) fighter.SpellDamagePercent[spell] = fighter.SpellDamagePercent.GetValueOrDefault(spell) + m.DamagePercent;
                if (m.HealPercent != 0) fighter.SpellHealPercent[spell] = fighter.SpellHealPercent.GetValueOrDefault(spell) + m.HealPercent;
            }
        }

        /// <summary>
        /// A blow of one of the fighter's spells with his spell dons' percent on it, rounded as a
        /// dream's "% dommages" is (DamageCalculator.RoundedPercent). The weapon (spell 0) and a
        /// spell no don names come back as they went.
        /// </summary>
        public static int WithDamage(Fighter caster, int spell, int damage)
        {
            if (caster == null || spell == 0 || damage <= 0
                || !caster.SpellDamagePercent.TryGetValue(spell, out int percent) || percent == 0) return damage;
            return Math.Max(0, DamageCalculator.RoundedPercent(damage, 100 + percent));
        }

        /// <summary>The same for a heal of one of his spells: «+6 % de soins».</summary>
        public static int WithHeal(Fighter caster, int spell, int points)
        {
            if (caster == null || spell == 0 || points <= 0
                || !caster.SpellHealPercent.TryGetValue(spell, out int percent) || percent == 0) return points;
            return Math.Max(0, DamageCalculator.RoundedPercent(points, 100 + percent));
        }

        // ─── The caps ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The family of a spell: its pair (base and variant share their points), or the spell
        /// itself when the tables do not know it.
        /// </summary>
        internal static Func<int, int> FamilyOf = spell => SpellTable.PairOf(spell) is { } pair ? pair.Id : -spell;

        /// <summary>The spell points spent: one per rank of every spell don kept.</summary>
        internal static int PointsSpent(ProgressionProfile profile, ProgressionContent content)
            => profile.Dons.Count(d => IsSpellDon(d.Card, content));

        /// <summary>Points spent per family.</summary>
        internal static Dictionary<int, int> PointsPerFamily(ProgressionProfile profile, ProgressionContent content)
        {
            var families = new Dictionary<int, int>();
            foreach (var chosen in profile.Dons)
                if (DefinitionOf(chosen.Card, content) is { } found)
                {
                    int family = FamilyOf(found.Don.Spell);
                    families[family] = families.GetValueOrDefault(family) + 1;
                }
            return families;
        }

        /// <summary>How many families a level may hold: one per slot level reached, never more than the file's maximum.</summary>
        internal static int FamiliesAllowed(int level, ProgressionContent content)
        {
            var f = content.SpellDons;
            int opened = f.FamilySlotLevels.Count == 0 ? f.MaxFamilies : f.FamilySlotLevels.Count(l => l <= level);
            return Math.Min(f.MaxFamilies, opened);
        }

        /// <summary>How many points one family may hold at a level: the highest step reached.</summary>
        internal static int PointsPerFamilyAllowed(int level, ProgressionContent content)
        {
            int best = 0, bestFrom = int.MinValue;
            foreach (var (from, points) in content.SpellDons.PointsPerFamilyByLevel)
                if (int.TryParse(from, out int l) && l <= level && l > bestFrom) { bestFrom = l; best = points; }
            return content.SpellDons.PointsPerFamilyByLevel.Count == 0 ? 5 : best;
        }

        /// <summary>Whether one more rank of this don may be taken at this level, every cap considered.</summary>
        internal static bool CanTake(SpellDonDefinition don, ProgressionProfile profile, ProgressionContent content, int level)
        {
            if (don.Level > level) return false;
            if (Dons.RankOf(profile, don.Id) >= Math.Max(1, don.MaxRank)) return false;
            if (PointsSpent(profile, content) >= content.SpellDons.MaxPoints) return false;
            var families = PointsPerFamily(profile, content);
            int family = FamilyOf(don.Spell);
            int inFamily = families.GetValueOrDefault(family);
            if (inFamily == 0 && families.Count >= FamiliesAllowed(level, content)) return false;
            return inFamily + 1 <= PointsPerFamilyAllowed(level, content);
        }

        /// <summary>The class's spell dons that may still be taken at this level.</summary>
        internal static List<SpellDonDefinition> Takeable(ProgressionProfile profile, ProgressionContent content, int breed, int level)
            => ListOf(breed, content).Where(d => CanTake(d, profile, content, level)).ToList();

        // ─── Texts ──────────────────────────────────────────────────────────────────────

        /// <summary>What one rank of a don does, in French: «+5 % de dégâts».</summary>
        internal static string EffectText(SpellDonDefinition don, int value) => don.Kind switch
        {
            Damage => $"+{value} % de dégâts",
            Heal => $"+{value} % de soins",
            Critical => $"+{value} % de coups critiques",
            Range => $"+{value} PO",
            Cooldown => $"-{value} tour{(value > 1 ? "s" : "")} de relance",
            ApCost => $"-{value} PA",
            CastsPerTurn => $"+{value} lancer{(value > 1 ? "s" : "")} par tour",
            _ => don.Kind,
        };

        /// <summary>A spell don card's name and description, as the window and the commands show it.</summary>
        internal static (string Name, string Description) Describe(SpellDonDefinition don, ProgressionProfile profile)
        {
            int rank = Dons.RankOf(profile, don.Id);
            int max = Math.Max(1, don.MaxRank);
            string name = $"{don.SpellName} : {EffectText(don, don.Value)}" + (max > 1 ? $" (rang {rank}/{max})" : "");
            string description = rank > 0
                ? $"Améliore le sort {don.SpellName} : {EffectText(don, don.Value)} par rang (actuellement {EffectText(don, don.Value * rank)})."
                : $"Améliore le sort {don.SpellName} : {EffectText(don, don.Value)}" + (max > 1 ? $" par rang, jusqu'à {max} rangs." : ".");
            return (name, description);
        }
    }
}
