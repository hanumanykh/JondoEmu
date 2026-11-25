using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>What an Ascension or an Evolution did: the level it started from, the one to go to, the Éclats moved.</summary>
    internal readonly record struct RebirthOutcome(int FromLevel, int ToLevel, long Eclats, int DonsKept);

    /// <summary>
    /// The Ascension, the Evolution and the research tree, after the Stasis server's rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ASCENSION, from level 50: back to level 1 -- experience 0, every characteristic point
    /// given back, the spells of a level 1 -- keeping the items and the kamas, losing the dons, and
    /// paid in Éclats for the level reached (ascension.json). Until the character is back at the
    /// best level he ever reached, the experience he earns is multiplied (catch-up, x2 and up to x9
    /// with Rattrapage): the hook is <see cref="Dons.ScaleExperience"/>, called from the one door
    /// experience goes through, CharacterProgression.AddExperience.
    /// </para>
    /// <para>
    /// An EVOLUTION, at level 300 with 1000 Éclats (spent; an Ascension 300's Éclats are paid), no don choice waiting and the tier-300 dungeons done in hardcore
    /// (<see cref="TierDungeonsDone"/>, filled in by the dungeons' side): back to level 200 for
    /// good, evolved (<see cref="IsEvolved"/>, read by the stasified items), keeping the dons of
    /// levels 1 to 200. An evolved character does not ascend: his floor is 200.
    /// </para>
    /// <para>
    /// The RESEARCH is bought with Éclats and survives both (research.json). Its effects are read
    /// where they matter: <see cref="ResearchSum"/> by kind.
    /// </para>
    /// <para>
    /// The methods here only change the profile and the session's characteristics; moving the
    /// level and telling the client (level-up window, sheet, spells and their bar) is
    /// CommandHandler.SetLevelAsync's, which the command and the control route call with
    /// <see cref="RebirthOutcome.ToLevel"/> right after -- the same path «.level» uses, so the
    /// client is refreshed by code already proved against captures.
    /// </para>
    /// </remarks>
    public static class Ascension
    {
        /// <summary>
        /// Whether a character has done the tier-300 dungeons in hardcore, the Evolution's last
        /// condition. Open until the dungeons' side sets it.
        /// </summary>
        public static Func<long, bool> TierDungeonsDone { get; set; } = _ => true;

        /// <summary>Whether a character has evolved: the stasified items' S3/S4 rules ask this.</summary>
        public static bool IsEvolved(long characterId)
        {
            if (characterId <= 0) return false;
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return profile.Evolved;
        }

        /// <summary>At start-up: the files read, and the stasified items told who is evolved.</summary>
        internal static void Load()
        {
            var content = ProgressionContent.Current;
            Stasis.IsEvolved = IsEvolved;
            Console.WriteLine($"[Progression] Dons: {content.Dons.Level.Count} level, {content.Dons.Adventure.Count} adventure, " +
                              $"{content.Dons.Passives.Count} passive list(s), {content.Dons.Accomplishments.Count} accomplishment(s); " +
                              $"{content.Research.Count} research(es).");
        }

        // ─── Research ───────────────────────────────────────────────────────────────────
        //
        // Where each kind acts (research.json):
        //   catchUp, catchUpLevels   Dons.ExperienceFactor through CatchUpMultiplier / CatchUpCeiling
        //   speed, animation         Dons.SpeedPercent / AnimationPercent (/api/stasis/speed)
        //   missionXp                CommandHandler.Missions
        //   freeRerolls, repicks, cards, rarity   Dons (offers)
        //   stat, biElement, donPercent, resistance   AddResearchBonuses, from Dons.BonusesOf: the sheet and the fight
        //   lifePercent              CombatCharacteristics.GetPlayerMaxHp (the fight's max life)
        //   castsPerTurn, castsPerTarget   ApplyResearchTo: Fighter.EverySpell, which SpellModifiers adds
        //                            only to the spells that have such a cap, and which is told to the client
        //   evolution                the Evolution itself (rank 1 once evolved; done from the Ascension tab)
        //   metamorphose, configs, duoCards   bought, kept and shown; the engine has no class change,
        //                            configuration or two-stat card path yet (CanMetamorphose,
        //                            ConfigurationsAllowed and DuoCardChance are there for them).

        /// <summary>The key, in the research ranks, of the Bi-élément's chosen element (1-4, <see cref="Elements"/>).</summary>
        internal const string BiElementChoiceKey = "@bi_element";

        /// <summary>The key marking a profile whose first-tree research was converted (<see cref="MigrateResearch"/>).</summary>
        internal const string SchemaKey = "@research_v2";

        /// <summary>The four elements: key, label, characteristic and damage, as Dons.StatIds names them.</summary>
        internal static readonly (string Key, string Label, string Stat, string Damage)[] Elements =
        {
            ("terre", "Terre", "force", "dommagesTerre"),
            ("feu", "Feu", "intelligence", "dommagesFeu"),
            ("eau", "Eau", "chance", "dommagesEau"),
            ("air", "Air", "agilite", "dommagesAir"),
        };

        /// <summary>What every research of a kind adds, at its rank (rounded).</summary>
        internal static int ResearchSum(ProgressionProfile profile, ProgressionContent content, string kind)
            => (int)Math.Round(ResearchValue(profile, content, kind));

        /// <summary>What every research of a kind adds, at its rank.</summary>
        internal static double ResearchValue(ProgressionProfile profile, ProgressionContent content, string kind)
            => content.Research.Where(r => r.Kind == kind).Sum(r => r.ValueAt(RankOf(profile, r)));

        /// <summary>A research's rank, never above its last; the Évolution's is the evolved flag.</summary>
        internal static int RankOf(ProgressionProfile profile, ResearchDefinition research)
            => research.Kind == "evolution" ? (profile.Evolved ? 1 : 0) : Math.Min(profile.RankOf(research.Id), research.MaxRank);

        /// <summary>The catch-up multiplier: the base, plus Rattrapage (x2,25 / x2,5 / x3), at most catchUpMax.</summary>
        internal static double CatchUpMultiplier(ProgressionProfile profile, ProgressionContent content)
        {
            double max = content.Ascension.CatchUpMax > 0 ? content.Ascension.CatchUpMax : double.MaxValue;
            return Math.Clamp(content.Ascension.CatchUpBase + ResearchValue(profile, content, "catchUp"), 1, max);
        }

        /// <summary>The level under which the catch-up runs: the best ever reached (300 at most), plus Élan.</summary>
        internal static int CatchUpCeiling(ProgressionProfile profile, ProgressionContent content)
            => Math.Min(profile.BestLevel, content.Ascension.EvolutionLevel) + ResearchSum(profile, content, "catchUpLevels");

        /// <summary>The percent more max life in fight (Endurance).</summary>
        public static int LifePercent(long characterId)
        {
            if (characterId <= 0) return 0;
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return ResearchSum(profile, ProgressionContent.Current, "lifePercent");
        }

        /// <summary>Whether the Métamorphose is bought (class change at level 1 after an Ascension).</summary>
        internal static bool CanMetamorphose(ProgressionProfile profile, ProgressionContent content)
            => ResearchSum(profile, content, "metamorphose") > 0;

        /// <summary>The configurations allowed: 1, or Polyvalence's 2 / 3 / 4.</summary>
        internal static int ConfigurationsAllowed(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(1, ResearchSum(profile, content, "configs"));

        /// <summary>The chance (0-1) that a combat offer carries a two-stat card (Dons duo).</summary>
        internal static double DuoCardChance(ProgressionProfile profile, ProgressionContent content)
            => Math.Clamp(ResearchValue(profile, content, "duoCards") / 100.0, 0, 1);

        /// <summary>
        /// The Bi-élément's second element (index in <see cref="Elements"/>): the one chosen, or
        /// else the strongest after the main; -1 without the research. The main element is the
        /// one the dons give the most (characteristic, plus five times its damage).
        /// </summary>
        internal static int SecondElement(ProgressionProfile profile, ProgressionContent content, IReadOnlyDictionary<int, long> dons, out int main)
        {
            main = -1;
            if (ResearchValue(profile, content, "biElement") <= 0) return -1;
            long Weight(int i) => dons.GetValueOrDefault(Dons.StatIds[Elements[i].Stat]) + dons.GetValueOrDefault(Dons.StatIds[Elements[i].Damage]) * 5;
            var order = Enumerable.Range(0, Elements.Length).OrderByDescending(Weight).ThenBy(i => i).ToList();
            main = order[0];
            int chosen = profile.RankOf(BiElementChoiceKey) - 1;
            if (chosen >= 0 && chosen < Elements.Length && chosen != main) return chosen;
            return order[1];
        }

        /// <summary>
        /// The research's share of the characteristic bonuses, added to <paramref name="total"/>,
        /// which holds the dons' when it is called: Résonance (+% of every don characteristic),
        /// Bi-élément (the second element gets a share of the main's characteristic and damage),
        /// Ténacité (the five resistances) and the plain stats (Action: +1 PA).
        /// </summary>
        internal static void AddResearchBonuses(ProgressionProfile profile, ProgressionContent content, Dictionary<int, long> total)
        {
            var dons = new Dictionary<int, long>(total);
            void Add(int id, long value)
            {
                if (value == 0) return;
                total[id] = total.GetValueOrDefault(id) + value;
            }

            double resonance = ResearchValue(profile, content, "donPercent");
            if (resonance > 0)
                foreach (var (id, value) in dons)
                    if (id >= 0) Add(id, (long)Math.Floor(value * resonance / 100.0));

            int second = SecondElement(profile, content, dons, out int main);
            if (second >= 0 && main >= 0)
            {
                double share = ResearchValue(profile, content, "biElement") / 100.0;
                Add(Dons.StatIds[Elements[second].Stat], (long)Math.Floor(dons.GetValueOrDefault(Dons.StatIds[Elements[main].Stat]) * share));
                Add(Dons.StatIds[Elements[second].Damage], (long)Math.Floor(dons.GetValueOrDefault(Dons.StatIds[Elements[main].Damage]) * share));
            }

            long resistance = ResearchSum(profile, content, "resistance");
            if (resistance > 0)
                foreach (string res in new[] { "resTerre", "resFeu", "resEau", "resAir", "resNeutre" })
                    Add(Dons.StatIds[res], resistance);

            foreach (var research in content.Research.Where(r => r.Kind == "stat"))
                if (Dons.StatIds.TryGetValue(research.Stat, out int id)) Add(id, (long)research.ValueAt(RankOf(profile, research)));
        }

        /// <summary>Cadence and Ciblage on a character's fighter: +1 cast per turn / per target on the capped spells.</summary>
        public static void ApplyResearchTo(Jondo.Unity.World.Fights.Fighter fighter, long characterId)
        {
            if (fighter == null || characterId <= 0) return;
            var profile = ProgressionStore.Of(characterId);
            int perTurn, perTarget;
            lock (profile.Gate)
            {
                perTurn = ResearchSum(profile, ProgressionContent.Current, "castsPerTurn");
                perTarget = ResearchSum(profile, ProgressionContent.Current, "castsPerTarget");
            }
            void Add(Jondo.Unity.World.Fights.SpellAspect aspect, int value)
            {
                if (value != 0) fighter.EverySpell[aspect] = fighter.EverySpell.GetValueOrDefault(aspect) + value;
            }
            Add(Jondo.Unity.World.Fights.SpellAspect.CastsPerTurnUp, perTurn);
            Add(Jondo.Unity.World.Fights.SpellAspect.CastsPerTargetUp, perTarget);
        }

        /// <summary>The price of a research's next rank, or -1 at its last.</summary>
        internal static long NextCost(ProgressionProfile profile, ResearchDefinition research)
        {
            int rank = RankOf(profile, research);
            return rank < research.MaxRank ? research.Costs[rank] : -1;
        }

        /// <summary>What a research's ranks cost in all: what a refund gives back.</summary>
        internal static long SpentOn(ProgressionProfile profile, ResearchDefinition research)
            => research.Costs.Take(RankOf(profile, research)).Sum();

        /// <summary>A rank's effect text («+8 % PV max»), or "" at rank 0.</summary>
        internal static string EffectText(ResearchDefinition research, int rank, ProgressionContent content)
        {
            if (rank <= 0) return "";
            if (string.IsNullOrEmpty(research.Effect)) return research.Description;
            double v = research.ValueAt(rank);
            if (research.Kind == "catchUp") v += content.Ascension.CatchUpBase;
            string value = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            return research.Effect.Replace("{v}", value);
        }

        internal const string EvolutionHere = "L'Évolution se lance depuis l'onglet Ascension (ou .evolution).";
        internal const string PermanentResearch = "Cette recherche est permanente : elle ne se rembourse pas.";
        internal const string NothingToRefund = "Aucun rang à rembourser dans cette recherche.";
        internal const string UnknownElement = "Élément inconnu : terre, feu, eau ou air.";

        /// <summary>Buys a research's next rank with Éclats. "ok", or why not.</summary>
        internal static string BuyResearch(ProgressionProfile profile, ProgressionContent content, string id)
        {
            var research = content.ResearchOf(id);
            if (research == null) return "recherche-inconnue";
            if (research.Kind == "evolution") return EvolutionHere;
            long cost = NextCost(profile, research);
            if (cost < 0) return "rang-max";
            if (profile.Eclats < cost) return "eclats";
            profile.Eclats -= cost;
            profile.Research[research.Id] = RankOf(profile, research) + 1;
            return "ok";
        }

        /// <summary>
        /// Gives back every rank of an ordinary research, its Éclats in full (spec §9: reassigned
        /// freely out of a fight; the caller checks the fight). "ok", or the refusal.
        /// </summary>
        internal static string RefundResearch(ProgressionProfile profile, ProgressionContent content, string id, out long refunded)
        {
            refunded = 0;
            var research = content.ResearchOf(id);
            if (research == null) return "recherche-inconnue";
            if (research.Permanent || research.Kind == "evolution") return PermanentResearch;
            if (RankOf(profile, research) <= 0) return NothingToRefund;
            refunded = SpentOn(profile, research);
            profile.Eclats += refunded;
            profile.Research.Remove(research.Id);
            return "ok";
        }

        /// <summary>Chooses the Bi-élément's second element by its key (terre, feu, eau, air) or its characteristic.</summary>
        internal static string ChooseSecondElement(ProgressionProfile profile, string element)
        {
            int index = Array.FindIndex(Elements, e => string.Equals(e.Key, element, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(e.Stat, element, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return UnknownElement;
            profile.Research[BiElementChoiceKey] = index + 1;
            return "ok";
        }

        /// <summary>The first tree's researches whose price or meaning changed, id to what their ranks cost then.</summary>
        private static readonly Dictionary<string, long[]> LegacyCosts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rattrapage"] = new long[] { 40, 80, 140, 220, 320, 450, 600 },
            ["quatrieme_carte"] = new long[] { 500 },
            ["relance"] = new long[] { 60, 120, 240 },
            ["seconde_refonte"] = new long[] { 250 },
            ["tempo"] = new long[] { 50, 100, 150, 200, 300 },
            ["polyvalence"] = new long[] { 200, 450 },
            ["moisson"] = new long[] { 80, 160, 320 },
        };

        /// <summary>
        /// Once per profile (<see cref="SchemaKey"/>): the ranks bought in the first tree whose
        /// price or meaning changed, or which is gone (Moisson), are refunded at what they cost
        /// then, so the player rebuys at the wiki's prices. Fortune and Mémoire de la Route kept
        /// theirs and stay. True when Éclats were refunded (the caller saves it).
        /// </summary>
        internal static bool MigrateResearch(ProgressionProfile profile)
        {
            if (profile.Research.ContainsKey(SchemaKey)) return false;
            long refund = 0;
            foreach (var (id, costs) in LegacyCosts)
            {
                int rank = profile.RankOf(id);
                if (rank <= 0) continue;
                refund += costs.Take(rank).Sum();
                profile.Research.Remove(id);
            }
            profile.Eclats += refund;
            profile.Research[SchemaKey] = 1;
            if (refund == 0) return false; // the mark is saved with the profile's next change
            Console.WriteLine($"[Progression] {profile.CharacterId}: first research tree refunded, +{refund} Éclats.");
            return true;
        }

        // ─── Ascension ──────────────────────────────────────────────────────────────────

        /// <summary>The Éclats an Ascension from <paramref name="level"/> pays: the wiki's table (300: 1450), linear between its rows.</summary>
        internal static long EclatsFor(int level, ProgressionProfile profile, ProgressionContent content)
        {
            var a = content.Ascension;
            if (level < a.MinLevel) return 0;
            double raw;
            if (a.EclatsByLevel.Count > 0)
            {
                var rows = a.EclatsByLevel.OrderBy(kv => kv.Key).ToList();
                if (level <= rows[0].Key) raw = rows[0].Value;
                else if (level >= rows[^1].Key) raw = rows[^1].Value;
                else
                {
                    int i = rows.FindIndex(kv => kv.Key >= level);
                    var (lo, hi) = (rows[i - 1], rows[i]);
                    raw = lo.Value + (hi.Value - lo.Value) * (level - lo.Key) / (double)(hi.Key - lo.Key);
                }
            }
            else
            {
                if (a.EclatsReferenceLevel <= 0) return 0;
                raw = a.EclatsBase * Math.Pow(level / a.EclatsReferenceLevel, a.EclatsExponent);
            }
            // A research of kind "eclats" (none in the wiki's tree) would still add its percent.
            double bonus = 1.0 + ResearchValue(profile, content, "eclats") / 100.0;
            return (long)Math.Floor(Math.Floor(raw) * bonus);
        }

        /// <summary>Why the character cannot ascend now, or null.</summary>
        internal static string? CannotAscend(ProgressionProfile profile, int level, ProgressionContent content)
        {
            if (profile.Evolved) return "evolue";
            if (level < content.Ascension.MinLevel) return "niveau";
            return null;
        }

        /// <summary>
        /// The Ascension's state: Éclats paid, dons and offers cleared, the cycle's rerolls and
        /// refontes given back, characteristics reset to the capital of level 1. The level itself
        /// is the caller's to move to <see cref="RebirthOutcome.ToLevel"/> (see the remarks).
        /// </summary>
        internal static RebirthOutcome Ascend(SessionState state, ProgressionProfile profile, ProgressionContent content)
        {
            int from = state.CharacterLevel;
            long eclats = EclatsFor(from, profile, content);
            profile.Eclats += eclats;
            profile.Ascensions++;
            profile.BestLevel = Math.Max(profile.BestLevel, from);
            profile.Dons.Clear();
            profile.Pending.Clear();
            profile.RerollsUsed = 0;
            profile.RepicksUsed = 0;
            ResetCharacteristics(state, 1);
            // «Missions de route» are part of what an Ascension resets (spec §9).
            Missions.ResetRoute(profile.CharacterId);
            return new RebirthOutcome(from, 1, eclats, 0);
        }

        // ─── Evolution ──────────────────────────────────────────────────────────────────

        internal const string PendingDons = "Choisissez d'abord vos dons en attente.";

        /// <summary>Why the character cannot evolve now, or null.</summary>
        internal static string? CannotEvolve(ProgressionProfile profile, int level, ProgressionContent content)
        {
            var a = content.Ascension;
            if (profile.Evolved) return "deja-evolue";
            if (level < a.EvolutionLevel) return "niveau";
            if (profile.Eclats < a.EvolutionEclats) return "eclats";
            if (profile.Pending.Count > 0) return PendingDons;
            if (!TierDungeonsDone(profile.CharacterId)) return "donjons";
            return null;
        }

        /// <summary>
        /// The Evolution's state: Éclats spent, evolved for good, the dons past level 200 and their
        /// offers dropped, characteristics reset to the capital of level 200. The level is the
        /// caller's to move, as for the Ascension.
        /// </summary>
        internal static RebirthOutcome Evolve(SessionState state, ProgressionProfile profile, ProgressionContent content)
        {
            var a = content.Ascension;
            int from = state.CharacterLevel;
            // It costs 1000 Éclats and pays what an Ascension from this level would (spec §9).
            long paid = EclatsFor(from, profile, content);
            profile.Eclats += paid - a.EvolutionEclats;
            profile.Evolved = true;
            profile.BestLevel = Math.Max(profile.BestLevel, from);
            profile.Dons.RemoveAll(d => Dons.RealLevel(d.Level) > a.EvolutionKeepDonsUpTo);
            profile.Pending.RemoveAll(o => Dons.RealLevel(o.Level) > a.EvolutionReturnLevel);
            profile.RerollsUsed = 0;
            profile.RepicksUsed = 0;
            ResetCharacteristics(state, a.EvolutionReturnLevel);
            return new RebirthOutcome(from, a.EvolutionReturnLevel, paid - a.EvolutionEclats, profile.Dons.Count);
        }

        /// <summary>
        /// Every point spent given back, at the capital of the level to come: the scrolled
        /// characteristics stay (they were bought with scrolls, not points).
        /// </summary>
        private static void ResetCharacteristics(SessionState state, int level)
        {
            state.StatStrength = state.StatIntelligence = state.StatChance = 0;
            state.StatAgility = state.StatVitality = state.StatWisdom = 0;
            state.CharacterRemainingPoints = CharacteristicRules.TotalCapitalForLevel(level);
        }
    }
}
