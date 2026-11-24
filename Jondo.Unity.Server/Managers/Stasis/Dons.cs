using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The level-up dons, after the Stasis server's rules: every level gained up to 200 offers a
    /// few cards and the player keeps one; every fifth level an adventure don, some levels a spell
    /// don (SpellDons, a card on one of the class's spells), level 50 the class passives, and 225/250/275/300 an accomplishment. And the ONE place
    /// that says what the dons and the research add to a character: <see cref="BonusesOf(long)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How the bonuses reach the game. Everything a character is worth is read through three
    /// doors: the sheet (CharacteristicsMessages' backend), the carrying capacity
    /// (ConnectionProtocol.Capacity) and the fight (CombatCharacteristics, which FightHandler's
    /// fighter building, the wisdom of the fight's experience and the prospecting of its loot all
    /// read). Each of them already adds the alterations next to the equipment; the dons are added
    /// right beside them, as a third source of the same kind -- characteristic id to amount -- so
    /// nothing downstream needs to know the dons exist. A +3 Force card is Force for the
    /// initiative, the pods and the damage exactly as a +3 Force amulet would be.
    /// </para>
    /// <para>
    /// The cards themselves are data (content/progression/dons.json, <see cref="ProgressionContent"/>);
    /// this class only knows the kinds of offer and how a card is drawn, kept and counted. What is
    /// kept is the CARD (and for the characteristic card the characteristic and the amount given at
    /// the time), not its numbers: retuning a card in the file retunes every character who has it.
    /// </para>
    /// <para>
    /// Offers queue: a fight that gives three levels queues three offers, and they are answered
    /// oldest first. A level that already has a don or an offer waiting earns nothing again, which
    /// is what keeps an evolved character climbing back from 200 to 300 from being offered the
    /// dons of levels it kept.
    /// </para>
    /// </remarks>
    public static class Dons
    {
        /// <summary>A pseudo characteristic: percent of experience (Découverte). Never sent to the sheet.</summary>
        internal const int XpPercent = -1;

        /// <summary>The offer kinds, as the window and the commands name them.</summary>
        internal const string KindLevel = "niveau", KindSpell = "sort", KindAdventure = "aventure",
                              KindPassive = "passif", KindAccomplishment = "accomplissement";

        /// <summary>
        /// The French names the content files use, to the characteristic ids of the sheet and the
        /// fight (<see cref="Stat"/> and the ones FightHandler reads from CombatCharacteristics:
        /// 16 damage, 86 critical damage, 88-92 elemental damage, 33-37 elemental resistance %, 84
        /// push damage).
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, int> StatIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["force"] = Stat.Strength, ["intelligence"] = Stat.Intelligence, ["chance"] = Stat.Chance,
            ["agilite"] = Stat.Agility, ["vitalite"] = Stat.Vitality, ["sagesse"] = Stat.Wisdom,
            ["puissance"] = Stat.Power, ["critique"] = Stat.Critical, ["po"] = Stat.Range,
            ["pa"] = Stat.ActionPoints, ["pm"] = Stat.MovementPoints, ["invocations"] = Stat.Summons,
            ["dommages"] = 16, ["dommagesCritiques"] = 86,
            ["dommagesTerre"] = 88, ["dommagesFeu"] = 89, ["dommagesEau"] = 90, ["dommagesAir"] = 91, ["dommagesNeutre"] = 92,
            ["resTerre"] = 33, ["resFeu"] = 34, ["resEau"] = 35, ["resAir"] = 36, ["resNeutre"] = 37,
            ["dommagesPoussee"] = 84, ["soins"] = Stat.Heals, ["tacle"] = Stat.Lock, ["fuite"] = Stat.Escape,
            ["initiative"] = Stat.Initiative, ["prospection"] = Stat.Prospecting, ["pods"] = Stat.Pods,
            ["esquivePA"] = Stat.DodgeActionPoints, ["esquivePM"] = Stat.DodgeMovementPoints,
            ["retraitPA"] = Stat.WithdrawActionPoints, ["retraitPM"] = Stat.WithdrawMovementPoints,
            ["decouverte"] = XpPercent,
        };

        /// <summary>The French label of a characteristic card's choice, for the texts.</summary>
        internal static readonly IReadOnlyDictionary<string, string> StatLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["force"] = "Force", ["intelligence"] = "Intelligence", ["chance"] = "Chance",
            ["agilite"] = "Agilité", ["vitalite"] = "Vitalité", ["sagesse"] = "Sagesse",
        };

        internal static string StatName(int id) => StatIds.FirstOrDefault(kv => kv.Value == id).Key ?? id.ToString();

        // ─── What the dons give ─────────────────────────────────────────────────────────

        /// <summary>
        /// What a character's dons and research add, characteristic id to amount: the third source
        /// the sheet, the pods and the fight add beside the equipment and the alterations. Empty
        /// for no character (id 0, the tests' and the bots' sessions).
        /// </summary>
        public static IReadOnlyDictionary<int, long> BonusesOf(long characterId)
        {
            if (characterId <= 0) return new Dictionary<int, long>();
            var profile = ProgressionStore.Of(characterId);
            Dictionary<int, long> total;
            lock (profile.Gate) total = BonusesOf(profile, ProgressionContent.Current);
            // The monster cards' complete families, the account's (MonsterCards): one more source, same shape.
            foreach (var (id, value) in MonsterCards.BonusesOfCharacter(characterId)) total[id] = total.GetValueOrDefault(id) + value;
            return total;
        }

        internal static Dictionary<int, long> BonusesOf(ProgressionProfile profile, ProgressionContent content)
        {
            var total = new Dictionary<int, long>();
            void Add(int id, long value)
            {
                if (id == XpPercent || value == 0) return;
                total[id] = total.GetValueOrDefault(id) + value;
            }

            foreach (var don in profile.Dons)
            {
                if (string.Equals(don.Card, content.Dons.CharacteristicCard.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Add(don.Stat, don.Amount);
                    continue;
                }
                var card = content.CardOf(don.Card);
                if (card == null) continue;
                foreach (var effect in card.Effects)
                    if (StatIds.TryGetValue(effect.Stat, out int id)) Add(id, effect.Value);
            }
            foreach (var research in content.Research.Where(r => r.Kind == "stat"))
                if (StatIds.TryGetValue(research.Stat, out int id)) Add(id, (long)profile.RankOf(research.Id) * research.PerRank);
            return total;
        }

        /// <summary>The Découverte percent the dons give (adventure don «Découverte»).</summary>
        internal static long DiscoveryPercent(ProgressionProfile profile, ProgressionContent content)
        {
            long percent = 0;
            foreach (var don in profile.Dons)
            {
                var card = content.CardOf(don.Card);
                if (card == null) continue;
                foreach (var effect in card.Effects)
                    if (StatIds.TryGetValue(effect.Stat, out int id) && id == XpPercent) percent += effect.Value;
            }
            return percent;
        }

        // ─── The experience hook ────────────────────────────────────────────────────────

        /// <summary>
        /// The experience a gain is really worth to this character: the catch-up multiplier while
        /// he is under the best level he ever reached after an Ascension (x2, up to x9 with the
        /// Rattrapage research), times the Découverte percent. Called by
        /// CharacterProgression.AddExperience and nowhere else.
        /// </summary>
        internal static long ScaleExperience(SessionState state, long experience)
        {
            if (state == null || state.CharacterId <= 0 || experience <= 0) return experience;
            var profile = ProgressionStore.Of(state.CharacterId);
            double factor;
            lock (profile.Gate) factor = ExperienceFactor(profile, state.CharacterLevel, ProgressionContent.Current);
            if (factor == 1.0) return experience;
            double scaled = experience * factor;
            return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
        }

        internal static double ExperienceFactor(ProgressionProfile profile, int level, ProgressionContent content)
        {
            double factor = 1.0;
            if ((profile.Ascensions > 0 || profile.Evolved) && level < profile.BestLevel)
                factor *= Ascension.CatchUpMultiplier(profile, content);
            factor *= 1.0 + DiscoveryPercent(profile, content) / 100.0;
            return factor;
        }

        /// <summary>
        /// The levels <paramref name="oldLevel"/>+1 to <paramref name="newLevel"/> were gained: the
        /// best level moves and every level that earns an offer queues one. Called by
        /// CharacterProgression.AddExperience after the level moved.
        /// </summary>
        internal static void OnLevelsGained(SessionState state, int oldLevel, int newLevel)
        {
            if (state == null || state.CharacterId <= 0 || newLevel <= oldLevel) return;
            var profile = ProgressionStore.Of(state.CharacterId);
            lock (profile.Gate)
            {
                int queued = QueueOffers(profile, ProgressionContent.Current, state.Breed, oldLevel, newLevel);
                profile.BestLevel = Math.Max(profile.BestLevel, newLevel);
                ProgressionStore.Save(profile);
                if (queued > 0)
                    Console.WriteLine($"[Progression] {state.CharacterName} reaches {newLevel}: {queued} offer(s) of dons queued, {profile.Pending.Count} waiting.");
            }
        }

        /// <summary>Where an accomplishment's extra spell offer is kept: this plus its level.</summary>
        internal const int SpellPointLevelOffset = 1000;

        /// <summary>The level an offer belongs to, an accomplishment's spell offer included.</summary>
        internal static int RealLevel(int offerLevel)
            => offerLevel > SpellPointLevelOffset ? offerLevel - SpellPointLevelOffset : offerLevel;

        internal static int QueueOffers(ProgressionProfile profile, ProgressionContent content, int breed, int oldLevel, int newLevel)
        {
            int queued = 0;
            for (int level = Math.Max(2, oldLevel + 1); level <= newLevel; level++)
            {
                string? kind = KindOf(level, content);
                if (kind == null) continue;
                if (profile.Dons.Any(d => d.Level == level) || profile.Pending.Any(o => o.Level == level)) continue;
                var offer = Draw(profile, content, breed, level, kind);
                if (offer.Cards.Count == 0) continue;
                profile.Pending.Add(offer);
                queued++;

                // An accomplishment (225-300) also brings a spell point, as on Stasis: ten spell
                // levels and four accomplishments make its fourteen points. It is a second offer of
                // its own, kept apart under SpellPointLevelOffset + level so that the rule "one
                // offer a level" still holds for the accomplishment itself.
                if (kind == KindAccomplishment && level > content.Dons.LastDonLevel)
                {
                    int key = SpellPointLevelOffset + level;
                    if (profile.Dons.Any(d => d.Level == key) || profile.Pending.Any(o => o.Level == key)) continue;
                    var spell = Draw(profile, content, breed, level, KindSpell);
                    if (spell.Cards.Count == 0) continue;
                    spell.Level = key;
                    profile.Pending.Add(spell);
                    queued++;
                }
            }
            profile.Pending.Sort((a, b) => a.Level.CompareTo(b.Level));
            return queued;
        }

        // ─── The offers ─────────────────────────────────────────────────────────────────

        /// <summary>Which offer a level earns, or null for none.</summary>
        internal static string? KindOf(int level, ProgressionContent content)
        {
            var d = content.Dons;
            if (d.AccomplishmentLevels.Contains(level)) return KindAccomplishment;
            if (level < 2 || level > d.LastDonLevel) return null;
            if (level == d.PassiveLevel) return KindPassive;
            if (d.SpellLevels.Contains(level)) return KindSpell;
            if (d.AdventureEvery > 0 && level % d.AdventureEvery == 0) return KindAdventure;
            return KindLevel;
        }

        /// <summary>How many cards an offer shows: the file's, plus the Quatrième carte research.</summary>
        internal static int CardsPerOffer(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(1, content.Dons.CardsPerOffer + Ascension.ResearchSum(profile, content, "cards"));

        /// <summary>What the characteristic card gives now: the file's, plus the Polyvalence research.</summary>
        internal static int CaracAmount(ProgressionProfile profile, ProgressionContent content)
            => content.Dons.CharacteristicCard.Amount + Ascension.ResearchSum(profile, content, "caracAmount");

        internal static IReadOnlyList<DonCard> PassivesOf(int breed, ProgressionContent content)
            => content.Dons.Passives.TryGetValue(breed.ToString(), out var own) && own.Count > 0 ? own
             : content.Dons.Passives.TryGetValue("default", out var fallback) ? fallback : new List<DonCard>();

        internal static int RankOf(ProgressionProfile profile, string card)
            => profile.Dons.Count(d => string.Equals(d.Card, card, StringComparison.OrdinalIgnoreCase));

        /// <summary>A new offer for a level: its cards drawn at random among those it may still give.</summary>
        internal static DonOffer Draw(ProgressionProfile profile, ProgressionContent content, int breed, int level, string kind)
        {
            var d = content.Dons;
            int count = CardsPerOffer(profile, content);
            var cards = new List<string>();
            IEnumerable<DonCard> pool;
            switch (kind)
            {
                case KindPassive:
                    // All of them: a choice of two or three, and a reroll would change nothing.
                    cards.AddRange(PassivesOf(breed, content).Select(c => c.Id));
                    return new DonOffer { Level = level, Kind = kind, Cards = cards };
                case KindAdventure:
                    pool = d.Adventure.Where(c => RankOf(profile, c.Id) < Math.Max(1, c.MaxRank));
                    break;
                case KindAccomplishment:
                    pool = d.Accomplishments.Where(c => RankOf(profile, c.Id) < Math.Max(1, c.MaxRank));
                    break;
                case KindSpell:
                    // The spell dons of his class (SpellDons): three of those he may still take at
                    // this level -- a new one or the next rank of one he has -- every Stasis cap
                    // considered. When none is left (all taken, every cap reached, or a class the
                    // file does not list) the old spell card comes back: Dofus 3 has no spell points,
                    // and +6 Puissance is what a spell level gave before the spell dons.
                    var spellDons = SpellDons.Takeable(profile, content, breed, level);
                    if (spellDons.Count > 0)
                    {
                        cards.AddRange(spellDons.OrderBy(_ => Random.Shared.Next()).Take(count).Select(s => s.Id));
                        return new DonOffer { Level = level, Kind = kind, Cards = cards };
                    }
                    cards.Add(d.SpellCard.Id);
                    cards.Add(d.CharacteristicCard.Id);
                    pool = d.Level;
                    break;
                default:
                    if (Random.Shared.NextDouble() < d.CharacteristicCard.FirstChance) cards.Add(d.CharacteristicCard.Id);
                    pool = d.Level;
                    break;
            }
            foreach (var card in pool.Where(c => !cards.Contains(c.Id)).OrderBy(_ => Random.Shared.Next()))
            {
                if (cards.Count >= count) break;
                cards.Add(card.Id);
            }
            return new DonOffer { Level = level, Kind = kind, Cards = cards };
        }

        /// <summary>
        /// Keeps card <paramref name="index"/> (from 1) of the oldest offer. The characteristic card
        /// wants <paramref name="stat"/>, one of its French names. "ok", or why not.
        /// </summary>
        internal static string Pick(ProgressionProfile profile, ProgressionContent content, int index, string? stat, out ChosenDon? kept)
        {
            kept = null;
            if (profile.Pending.Count == 0) return "aucune-offre";
            var offer = profile.Pending[0];
            if (index < 1 || index > offer.Cards.Count) return "carte-inconnue";
            string card = offer.Cards[index - 1];
            var don = new ChosenDon { Level = offer.Level, Card = card };
            var carac = content.Dons.CharacteristicCard;
            if (string.Equals(card, carac.Id, StringComparison.OrdinalIgnoreCase))
            {
                string wanted = Normalize(stat);
                if (wanted.Length == 0 || !carac.Stats.Contains(wanted, StringComparer.OrdinalIgnoreCase)
                    || !StatIds.TryGetValue(wanted, out int id))
                    return "carac-requise";
                don.Stat = id;
                don.Amount = CaracAmount(profile, content);
            }
            var spellDon = SpellDons.DefinitionOf(card, content);
            // Offers queue, and two spell offers drawn before either was answered may both hold a
            // card the first answer then puts over a cap (a family at its points for the level).
            if (spellDon is { } asked && !SpellDons.CanTake(asked.Don, profile, content, RealLevel(offer.Level)))
                return "sort-plafond";
            profile.Pending.RemoveAt(0);
            profile.Dons.RemoveAll(d => d.Level == don.Level);
            profile.Dons.Add(don);
            kept = don;
            if (spellDon is { } taken) RedrawStaleSpellOffers(profile, content, taken.Breed);
            return "ok";
        }

        /// <summary>
        /// The spell offers still waiting whose cards the last answer put out of reach are drawn
        /// again, so the player is never left with an offer of nothing he may take.
        /// </summary>
        internal static void RedrawStaleSpellOffers(ProgressionProfile profile, ProgressionContent content, int breed)
        {
            for (int i = 0; i < profile.Pending.Count; i++)
            {
                var waiting = profile.Pending[i];
                if (waiting.Kind != KindSpell) continue;
                bool stale = waiting.Cards.Any(c => SpellDons.DefinitionOf(c, content) is { } s
                                                    && !SpellDons.CanTake(s.Don, profile, content, waiting.Level));
                if (!stale) continue;
                var fresh = Draw(profile, content, breed, waiting.Level, KindSpell);
                fresh.Rerolled = waiting.Rerolled;
                profile.Pending[i] = fresh;
            }
        }

        /// <summary>A characteristic's French name as typed («Agilité», «agi», «vita»), to the file's key, or "".</summary>
        internal static string Normalize(string? typed)
        {
            string word = (typed ?? "").Trim().ToLowerInvariant()
                .Replace("é", "e").Replace("è", "e").Replace("ê", "e");
            if (word.Length < 2) return "";
            foreach (string key in StatLabels.Keys)
                if (key.StartsWith(word, StringComparison.Ordinal)) return key;
            return "";
        }

        /// <summary>Free rerolls left this Ascension cycle (Relance research).</summary>
        internal static int RerollsLeft(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(0, Ascension.ResearchSum(profile, content, "freeRerolls") - profile.RerollsUsed);

        /// <summary>Characteristic re-picks left this cycle: one, plus the Seconde refonte research.</summary>
        internal static int RepicksLeft(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(0, 1 + Ascension.ResearchSum(profile, content, "repicks") - profile.RepicksUsed);

        /// <summary>What rerolling the offer of a level costs in kamas when no free reroll is left.</summary>
        internal static long RerollCost(int level, ProgressionContent content) => Math.Max(0, content.Dons.RerollKamasPerLevel * level);

        /// <summary>
        /// Draws the oldest offer's cards again, once per offer: free while the Relance research
        /// has rerolls left this cycle, else for kamas taken from <paramref name="kamas"/>. "ok", or why not.
        /// </summary>
        internal static string Reroll(ProgressionProfile profile, ProgressionContent content, int breed, ref long kamas, out long paid,
                                    Func<long, long>? price = null)
        {
            paid = 0;
            if (profile.Pending.Count == 0) return "aucune-offre";
            var offer = profile.Pending[0];
            if (offer.Rerolled) return "deja-relancee";
            if (offer.Kind == KindPassive) return "non-relancable";
            bool free = RerollsLeft(profile, content) > 0;
            if (!free)
            {
                paid = RerollCost(RealLevel(offer.Level), content);
                if (price != null) paid = price(paid);
                if (kamas < paid) { paid = 0; return "kamas"; }
                kamas -= paid;
            }
            else profile.RerollsUsed++;
            var fresh = Draw(profile, content, breed, RealLevel(offer.Level), offer.Kind);
            fresh.Level = offer.Level;
            fresh.Rerolled = true;
            profile.Pending[0] = fresh;
            return "ok";
        }

        /// <summary>
        /// A refonte: every characteristic card point in <paramref name="from"/> moves to
        /// <paramref name="to"/>. One per Ascension cycle, two with the research. "ok", or why not.
        /// </summary>
        internal static string Repick(ProgressionProfile profile, ProgressionContent content, string from, string to, out int moved)
        {
            moved = 0;
            string a = Normalize(from), b = Normalize(to);
            if (a.Length == 0 || b.Length == 0 || a == b) return "carac-requise";
            if (RepicksLeft(profile, content) <= 0) return "plus-de-refonte";
            int fromId = StatIds[a], toId = StatIds[b];
            var carac = content.Dons.CharacteristicCard.Id;
            var dons = profile.Dons.Where(d => string.Equals(d.Card, carac, StringComparison.OrdinalIgnoreCase) && d.Stat == fromId).ToList();
            if (dons.Count == 0) return "rien-a-refondre";
            foreach (var don in dons)
            {
                don.Stat = toId;
                moved += don.Amount;
            }
            profile.RepicksUsed++;
            return "ok";
        }

        // ─── Texts ──────────────────────────────────────────────────────────────────────

        /// <summary>A card's name and what it gives, in French, as the window and the commands show it.</summary>
        internal static (string Name, string Description) Describe(string cardId, ProgressionProfile profile, ProgressionContent content)
        {
            var carac = content.Dons.CharacteristicCard;
            if (string.Equals(cardId, carac.Id, StringComparison.OrdinalIgnoreCase))
                return (carac.Name, string.Format(carac.Description, CaracAmount(profile, content)));
            if (SpellDons.DefinitionOf(cardId, content) is { } spellDon)
                return SpellDons.Describe(spellDon.Don, profile);
            var card = content.CardOf(cardId);
            if (card == null) return (cardId, "");
            string rank = card.MaxRank > 1 ? $" (rang {RankOf(profile, card.Id)}/{card.MaxRank})" : "";
            return (card.Name + rank, card.Description);
        }

        internal static string KindLabel(string kind) => kind switch
        {
            KindSpell => "Don de sort",
            KindAdventure => "Don d'aventure",
            KindPassive => "Passif de classe",
            KindAccomplishment => "Accomplissement",
            _ => "Don de niveau",
        };

        internal static string RefusalText(string reason) => reason switch
        {
            "aucune-offre" => "Aucune offre de don en attente.",
            "carte-inconnue" => "Cette carte n'existe pas dans l'offre.",
            "carac-requise" => "Précisez la caractéristique : force, intelligence, chance, agilite, vitalite ou sagesse.",
            "sort-plafond" => "Ce don de sort n'est plus possible (points, familles ou rang au maximum).",
            "deja-relancee" => "Cette offre a déjà été relancée.",
            "non-relancable" => "Les passifs de classe se choisissent tels quels : pas de relance.",
            "kamas" => "Pas assez d'Orbes (Jondo Coins) pour relancer.",
            "plus-de-refonte" => "Plus de refonte disponible pour ce cycle d'Ascension.",
            "rien-a-refondre" => "Aucun point de don dans cette caractéristique.",
            "niveau" => "Niveau insuffisant.",
            "evolue" => "Un personnage évolué ne peut plus faire d'Ascension.",
            "deja-evolue" => "Ce personnage a déjà évolué.",
            "eclats" => "Pas assez d'Éclats.",
            "donjons" => "Il faut d'abord terminer les donjons de palier 300 en mode hardcore.",
            "recherche-inconnue" => "Recherche inconnue.",
            "rang-max" => "Cette recherche est déjà au rang maximum.",
            "combat" => "Impossible pendant un combat.",
            "confirmation" => "Confirmation requise.",
            _ => reason,
        };
    }
}
