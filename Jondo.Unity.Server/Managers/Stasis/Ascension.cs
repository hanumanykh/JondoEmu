using System;
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
    /// An EVOLUTION, at level 300 with 1000 Éclats and the tier-300 dungeons done in hardcore
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

        /// <summary>What every research of a kind adds, rank times perRank.</summary>
        internal static int ResearchSum(ProgressionProfile profile, ProgressionContent content, string kind)
            => content.Research.Where(r => r.Kind == kind).Sum(r => profile.RankOf(r.Id) * r.PerRank);

        /// <summary>The catch-up multiplier: the base, plus Rattrapage.</summary>
        internal static int CatchUpMultiplier(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(1, content.Ascension.CatchUpBase + ResearchSum(profile, content, "catchUp"));

        /// <summary>The price of a research's next rank, or -1 at its last.</summary>
        internal static long NextCost(ProgressionProfile profile, ResearchDefinition research)
        {
            int rank = profile.RankOf(research.Id);
            return rank < research.MaxRank ? research.Costs[rank] : -1;
        }

        /// <summary>Buys a research's next rank with Éclats. "ok", or why not.</summary>
        internal static string BuyResearch(ProgressionProfile profile, ProgressionContent content, string id)
        {
            var research = content.ResearchOf(id);
            if (research == null) return "recherche-inconnue";
            long cost = NextCost(profile, research);
            if (cost < 0) return "rang-max";
            if (profile.Eclats < cost) return "eclats";
            profile.Eclats -= cost;
            profile.Research[research.Id] = profile.RankOf(research.Id) + 1;
            return "ok";
        }

        // ─── Ascension ──────────────────────────────────────────────────────────────────

        /// <summary>The Éclats an Ascension from <paramref name="level"/> pays, Moisson included.</summary>
        internal static long EclatsFor(int level, ProgressionProfile profile, ProgressionContent content)
        {
            var a = content.Ascension;
            if (level < a.MinLevel || a.EclatsReferenceLevel <= 0) return 0;
            double raw = Math.Floor(a.EclatsBase * Math.Pow(level / a.EclatsReferenceLevel, a.EclatsExponent));
            double bonus = 1.0 + ResearchSum(profile, content, "eclats") / 100.0;
            return (long)Math.Floor(raw * bonus);
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

        /// <summary>Why the character cannot evolve now, or null.</summary>
        internal static string? CannotEvolve(ProgressionProfile profile, int level, ProgressionContent content)
        {
            var a = content.Ascension;
            if (profile.Evolved) return "deja-evolue";
            if (level < a.EvolutionLevel) return "niveau";
            if (profile.Eclats < a.EvolutionEclats) return "eclats";
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
            profile.Eclats -= a.EvolutionEclats;
            profile.Evolved = true;
            profile.BestLevel = Math.Max(profile.BestLevel, from);
            profile.Dons.RemoveAll(d => d.Level > a.EvolutionKeepDonsUpTo);
            profile.Pending.RemoveAll(o => o.Level > a.EvolutionReturnLevel);
            profile.RerollsUsed = 0;
            profile.RepicksUsed = 0;
            ResetCharacteristics(state, a.EvolutionReturnLevel);
            return new RebirthOutcome(from, a.EvolutionReturnLevel, -a.EvolutionEclats, profile.Dons.Count);
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
