using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The level-up dons, after the Stasis server's rules (docs/stasis-gameplay-spec.md §10): one
    /// offer a level from 1 to 200 -- level 1 the starting offer, every fifth level an adventure
    /// don, the spell levels (10, 30, 50 ... 190) a spell point (SpellDons), the other levels a
    /// combat don --, plus the class passives at 50, the dons «Pension» at 20, 50 and 60, and the
    /// accomplishments at 225/250/275/300 (each with a spell point). And the ONE place that says
    /// what the dons and the research add to a character: <see cref="BonusesOf(long)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How the bonuses reach the game. Everything a character is worth is read through three
    /// doors: the sheet (CharacteristicsMessages' backend), the carrying capacity
    /// (ConnectionProtocol.Capacity) and the fight (CombatCharacteristics, which FightHandler's
    /// fighter building, the wisdom of the fight's experience and the prospecting of its loot all
    /// read). Each of them already adds the alterations next to the equipment; the dons are added
    /// right beside them, as a third source of the same kind -- characteristic id to amount -- so
    /// nothing downstream needs to know the dons exist. The walk speed dons are a percent the
    /// JondoFix client walks faster by (/api/stasis/speed, <see cref="SpeedPercent"/>).
    /// </para>
    /// <para>
    /// A standard offer (starting, combat, adventure) shows three cards, four with the Quatrième
    /// carte research. Each card has a RARITY drawn on its own -- Commun, Rare or Légendaire, at
    /// dons.json's chances, which the Fortune research raises -- and gives its rarity's values.
    /// The first card is almost always the characteristic of one's choice, the last a don not
    /// taken recently, and after five offers in a row without a Légendaire the next has one (the
    /// counter is kept in the profile). One reroll an offer (combat, adventure, spell), paid in
    /// Orbes growing with the level unless the Relance research has a free one left; the starting
    /// offer, the passives, the dons «Pension» and the accomplishments are not rerolled.
    /// </para>
    /// <para>
    /// The cards themselves are data (content/progression/dons.json, <see cref="ProgressionContent"/>);
    /// what is kept is the CARD and its rarity (and for the characteristic card the characteristic
    /// and the amount given at the time), not its numbers: retuning a card in the file retunes
    /// every character who has it.
    /// </para>
    /// <para>
    /// Offers queue, oldest level first. One offer a level on the main track (the offer's Level is
    /// the character level); the extra offers a level brings are kept under an offset so that the
    /// rule «one offer a level» still holds: <see cref="SpellPointLevelOffset"/> (an
    /// accomplishment's spell point), <see cref="PassiveLevelOffset"/> (the passives),
    /// <see cref="PensionLevelOffset"/> (a don «Pension»). <see cref="RealLevel"/> gives the level
    /// back. A level that already has a don or an offer waiting earns nothing again.
    /// </para>
    /// </remarks>
    public static class Dons
    {
        /// <summary>Pseudo characteristics, never sent to the sheet: percent of experience, of walk speed, of animation speed.</summary>
        internal const int XpPercent = -1, SpeedPercentStat = -2, AnimationPercentStat = -3;

        /// <summary>The offer kinds, as the window and the commands name them.</summary>
        internal const string KindLevel = "niveau", KindSpell = "sort", KindAdventure = "aventure",
                              KindPassive = "passif", KindAccomplishment = "accomplissement",
                              KindStart = "depart", KindPension = "pension";

        /// <summary>The rarities, as kept.</summary>
        internal const int Commun = 0, Rare = 1, Legendaire = 2;

        /// <summary>
        /// The French names the content files use, to the characteristic ids of the sheet and the
        /// fight (<see cref="Stat"/> and the ones FightHandler reads from CombatCharacteristics:
        /// 16 damage, 86 critical damage, 88-92 elemental damage, 33-37 elemental resistance %, 84
        /// push damage). They are the client's own characteristic ids too: the window draws a
        /// card's icon from them.
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
            ["decouverte"] = XpPercent, ["vitesse"] = SpeedPercentStat, ["animation"] = AnimationPercentStat,
        };

        /// <summary>The French label of a characteristic card's choice, for the texts.</summary>
        internal static readonly IReadOnlyDictionary<string, string> StatLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["force"] = "Force", ["intelligence"] = "Intelligence", ["chance"] = "Chance",
            ["agilite"] = "Agilité", ["vitalite"] = "Vitalité", ["sagesse"] = "Sagesse",
        };

        /// <summary>How an effect reads after its value: «+20 Vitalité», «+2 % Résistance Feu».</summary>
        private static readonly IReadOnlyDictionary<string, string> EffectLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["force"] = "Force", ["intelligence"] = "Intelligence", ["chance"] = "Chance", ["agilite"] = "Agilité",
            ["vitalite"] = "Vitalité", ["sagesse"] = "Sagesse", ["puissance"] = "Puissance", ["critique"] = "% Critique",
            ["po"] = "PO", ["pa"] = "PA", ["pm"] = "PM", ["invocations"] = "Invocation", ["dommages"] = "Dommages",
            ["dommagesCritiques"] = "Dommages critiques", ["dommagesTerre"] = "Dommages Terre", ["dommagesFeu"] = "Dommages Feu",
            ["dommagesEau"] = "Dommages Eau", ["dommagesAir"] = "Dommages Air", ["dommagesNeutre"] = "Dommages Neutre",
            ["resTerre"] = "% Résistance Terre", ["resFeu"] = "% Résistance Feu", ["resEau"] = "% Résistance Eau",
            ["resAir"] = "% Résistance Air", ["resNeutre"] = "% Résistance Neutre", ["dommagesPoussee"] = "Dommages de poussée",
            ["soins"] = "Soins", ["tacle"] = "Tacle", ["fuite"] = "Fuite", ["initiative"] = "Initiative",
            ["prospection"] = "Prospection", ["pods"] = "Pods", ["esquivePA"] = "Esquive PA", ["esquivePM"] = "Esquive PM",
            ["retraitPA"] = "Retrait PA", ["retraitPM"] = "Retrait PM", ["decouverte"] = "% d'expérience",
            ["vitesse"] = "% vitesse de déplacement", ["animation"] = "% vitesse d'animation",
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
                if (id < 0 || value == 0) return;
                total[id] = total.GetValueOrDefault(id) + value;
            }

            foreach (var don in profile.Dons)
            {
                if (IsCharacteristicCard(don.Card, content))
                {
                    Add(don.Stat, don.Amount);
                    continue;
                }
                var card = content.CardOf(don.Card);
                if (card == null) continue;
                foreach (var effect in card.Effects)
                    if (StatIds.TryGetValue(effect.Stat, out int id)) Add(id, effect.ValueAt(don.Rarity));
            }
            // The research: Action, Résonance, Bi-élément, Ténacité (Ascension.AddResearchBonuses).
            Ascension.AddResearchBonuses(profile, content, total);
            return total;
        }

        /// <summary>The sum of a pseudo characteristic (a percent) over the dons kept, each at its rarity.</summary>
        internal static long PercentOf(ProgressionProfile profile, ProgressionContent content, int pseudo)
        {
            long percent = 0;
            foreach (var don in profile.Dons)
            {
                var card = content.CardOf(don.Card);
                if (card == null) continue;
                foreach (var effect in card.Effects)
                    if (StatIds.TryGetValue(effect.Stat, out int id) && id == pseudo) percent += effect.ValueAt(don.Rarity);
            }
            return percent;
        }

        /// <summary>The experience percent the dons give (a «decouverte» effect).</summary>
        internal static long DiscoveryPercent(ProgressionProfile profile, ProgressionContent content) => PercentOf(profile, content, XpPercent);

        /// <summary>The out-of-combat walk speed percent the dons give (Vitesse perso, Élan), for /api/stasis/speed.</summary>
        public static long SpeedPercent(long characterId)
        {
            if (characterId <= 0) return 0;
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return PercentOf(profile, ProgressionContent.Current, SpeedPercentStat)
                                       + Ascension.ResearchSum(profile, ProgressionContent.Current, "speed"); // Pas léger
        }

        /// <summary>The animation speed percent the dons give (Vitesse d'animation, Élan): sent, not drawn by the client.</summary>
        public static long AnimationPercent(long characterId)
        {
            if (characterId <= 0) return 0;
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return PercentOf(profile, ProgressionContent.Current, AnimationPercentStat)
                                       + Ascension.ResearchSum(profile, ProgressionContent.Current, "animation"); // Tempo
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
            if ((profile.Ascensions > 0 || profile.Evolved) && level < Ascension.CatchUpCeiling(profile, content))
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

        /// <summary>Where the extra offers of a level are kept: this plus the level (see the remarks).</summary>
        internal const int SpellPointLevelOffset = 1000, PassiveLevelOffset = 2000, PensionLevelOffset = 3000;

        /// <summary>The level an offer or a don belongs to, the extra offers' included.</summary>
        internal static int RealLevel(int offerLevel)
            => offerLevel > SpellPointLevelOffset ? offerLevel % SpellPointLevelOffset : offerLevel;

        private static bool Has(ProgressionProfile profile, int key)
            => profile.Dons.Any(d => d.Level == key) || profile.Pending.Any(o => o.Level == key);

        private static int TryQueue(ProgressionProfile profile, ProgressionContent content, int breed, int key, int level, string kind, Random dice)
        {
            if (Has(profile, key)) return 0;
            var offer = Draw(profile, content, breed, level, kind, dice);
            if (offer.Cards.Count == 0) return 0;
            offer.Level = key;
            profile.Pending.Add(offer);
            return 1;
        }

        /// <summary>Oldest level first; on one level the main offer, then its extras.</summary>
        private static void SortPending(ProgressionProfile profile)
            => profile.Pending.Sort((a, b) =>
            {
                int byLevel = RealLevel(a.Level).CompareTo(RealLevel(b.Level));
                return byLevel != 0 ? byLevel : a.Level.CompareTo(b.Level);
            });

        internal static int QueueOffers(ProgressionProfile profile, ProgressionContent content, int breed, int oldLevel, int newLevel,
                                        Random? dice = null)
        {
            dice ??= Random.Shared;
            Migrate(profile, content);
            var d = content.Dons;
            int queued = 0;
            for (int level = Math.Max(1, oldLevel + 1); level <= newLevel; level++)
            {
                string? kind = KindOf(level, content);
                if (kind != null) queued += TryQueue(profile, content, breed, level, level, kind, dice);
                // An accomplishment (225-300) also brings a spell point, as on Stasis: ten spell
                // levels and four accomplishments make its fourteen points.
                if (kind == KindAccomplishment && level > d.LastDonLevel)
                    queued += TryQueue(profile, content, breed, SpellPointLevelOffset + level, level, KindSpell, dice);
                if (d.PassiveLevel > 0 && level == d.PassiveLevel)
                    queued += TryQueue(profile, content, breed, PassiveLevelOffset + level, level, KindPassive, dice);
                if (PensionDonAt(level, content) != null)
                    queued += TryQueue(profile, content, breed, PensionLevelOffset + level, level, KindPension, dice);
            }
            SortPending(profile);
            return queued;
        }

        /// <summary>
        /// What a character of <paramref name="level"/> should have and may lack, given at login
        /// and whenever the window asks: the starting offer (new characters, and the older ones
        /// made before it existed), the class passives once past their level, the dons «Pension»
        /// past theirs unless the account's pension already gave them
        /// (<paramref name="pensionGiven"/>: a pension offer for a gift already given is dropped).
        /// The number of offers added or dropped.
        /// </summary>
        internal static int EnsureOffers(ProgressionProfile profile, ProgressionContent content, int breed, int level,
                                         Func<int, bool>? pensionGiven = null, Random? dice = null)
        {
            dice ??= Random.Shared;
            int changed = Migrate(profile, content);
            var d = content.Dons;
            if (d.StartingLevel > 0 && level >= d.StartingLevel)
                changed += TryQueue(profile, content, breed, d.StartingLevel, d.StartingLevel, KindStart, dice);
            if (d.PassiveLevel > 0 && level >= d.PassiveLevel)
                changed += TryQueue(profile, content, breed, PassiveLevelOffset + d.PassiveLevel, d.PassiveLevel, KindPassive, dice);
            foreach (var pension in d.Pension)
            {
                if (level < pension.Level) continue;
                int key = PensionLevelOffset + pension.Level;
                if (profile.Dons.Any(x => x.Level == key)) continue;
                if (pensionGiven?.Invoke(pension.Level) == true)
                {
                    changed += profile.Pending.RemoveAll(o => o.Level == key);
                    continue;
                }
                changed += TryQueue(profile, content, breed, key, pension.Level, KindPension, dice);
            }
            if (changed > 0) SortPending(profile);
            return changed;
        }

        /// <summary>
        /// The passive used to be the offer OF level 50; it is now an extra offer of that level
        /// (50 is a spell level, as on Stasis). A profile of before has its passive moved to its
        /// new place. The number of rows moved.
        /// </summary>
        internal static int Migrate(ProgressionProfile profile, ProgressionContent content)
        {
            int at = content.Dons.PassiveLevel, key = PassiveLevelOffset + at, moved = 0;
            // The passives of before (stat cards, own class only) are gone from dons.json: a passive
            // kept or offered that is no longer a passive card is dropped, and EnsureOffers queues
            // the new offer (three among the nineteen classes).
            if (at > 0 && content.Dons.Passives.Count > 0)
            {
                moved += profile.Dons.RemoveAll(d => d.Level == key && !IsPassiveCard(d.Card, content));
                moved += profile.Pending.RemoveAll(o => o.Level == key && o.Cards.Any(c => !IsPassiveCard(c, content)));
                if (moved > 0) return moved;
            }
            if (at <= 0 || Has(profile, key)) return 0;
            foreach (var don in profile.Dons)
                if (don.Level == at && IsPassiveCard(don.Card, content)) { don.Level = key; moved++; break; }
            if (moved == 0)
                foreach (var offer in profile.Pending)
                    if (offer.Level == at && offer.Kind == KindPassive) { offer.Level = key; moved++; break; }
            if (moved > 0) SortPending(profile);
            return moved;
        }

        // ─── The offers ─────────────────────────────────────────────────────────────────

        /// <summary>Which offer a level earns on the main track, or null for none.</summary>
        internal static string? KindOf(int level, ProgressionContent content)
        {
            var d = content.Dons;
            if (d.AccomplishmentLevels.Contains(level)) return KindAccomplishment;
            if (level < 1 || level > d.LastDonLevel) return null;
            if (level == d.StartingLevel) return KindStart;
            if (level < 2) return null;
            if (d.SpellLevels.Contains(level)) return KindSpell;
            if (d.AdventureEvery > 0 && level % d.AdventureEvery == 0) return KindAdventure;
            return KindLevel;
        }

        /// <summary>Whether an offer of this kind may be rerolled (combat, adventure, spell; not the starting offer, passives, Pension, accomplishments).</summary>
        internal static bool IsRerollable(string kind) => kind is KindLevel or KindAdventure or KindSpell;

        /// <summary>How many cards a standard offer shows: the file's, plus the Quatrième carte research.</summary>
        internal static int CardsPerOffer(ProgressionProfile profile, ProgressionContent content)
            => Math.Max(1, content.Dons.CardsPerOffer + Ascension.ResearchSum(profile, content, "cards"));

        /// <summary>What the characteristic card gives at a rarity: the file's, plus the Polyvalence research.</summary>
        internal static int CaracAmount(ProgressionProfile profile, ProgressionContent content, int rarity = Commun)
        {
            var c = content.Dons.CharacteristicCard;
            int amount = rarity switch
            {
                >= Legendaire => c.AmountLegendaire ?? c.AmountRare ?? c.Amount,
                Rare => c.AmountRare ?? c.Amount,
                _ => c.Amount,
            };
            return amount + Ascension.ResearchSum(profile, content, "caracAmount");
        }

        /// <summary>How many passives an offer shows, and its free rerolls (three new each time: nine seen at most).</summary>
        internal const int PassivesPerOffer = 3, PassiveFreeRerolls = 2;

        /// <summary>The passive cards of every class, in the file's order.</summary>
        internal static List<DonCard> AllPassives(ProgressionContent content)
            => content.Dons.Passives.Where(p => int.TryParse(p.Key, out _))
                      .SelectMany(p => p.Value).Where(c => !c.Retired).ToList();

        /// <summary>
        /// The order the passives of an Ascension cycle are shown in: all of them, shuffled once for
        /// the character and the cycle. The offer is the first three, each reroll the next three,
        /// so no passive is seen twice and the rerolls already spent are read off the offer itself.
        /// </summary>
        internal static List<string> PassiveOrder(ProgressionProfile profile, ProgressionContent content)
        {
            var ids = AllPassives(content).Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(id => id, StringComparer.Ordinal).ToList();
            var dice = new Random(unchecked((int)(profile.CharacterId * 1_000_003L + profile.Ascensions * 7_919L + 50)));
            for (int i = ids.Count - 1; i > 0; i--)
            {
                int j = dice.Next(i + 1);
                (ids[i], ids[j]) = (ids[j], ids[i]);
            }
            return ids;
        }

        /// <summary>The passive offer's free rerolls already spent (from where its cards stand in <see cref="PassiveOrder"/>).</summary>
        internal static int PassiveRerollsUsed(ProgressionProfile profile, DonOffer offer, ProgressionContent content)
        {
            if (offer.Cards.Count == 0) return 0;
            int at = PassiveOrder(profile, content).FindIndex(id => string.Equals(id, offer.Cards[0], StringComparison.OrdinalIgnoreCase));
            return at < 0 ? 0 : at / PassivesPerOffer;
        }

        /// <summary>The passive offer's free rerolls left: two, while the shuffled list holds three more.</summary>
        internal static int PassiveRerollsLeft(ProgressionProfile profile, DonOffer offer, ProgressionContent content)
        {
            int used = PassiveRerollsUsed(profile, offer, content);
            int total = PassiveOrder(profile, content).Count;
            int left = PassiveFreeRerolls - used;
            while (left > 0 && (used + left) * PassivesPerOffer >= total) left--;
            return Math.Max(0, left);
        }

        private static List<string> PassiveSlice(ProgressionProfile profile, ProgressionContent content, int reroll)
            => PassiveOrder(profile, content).Skip(reroll * PassivesPerOffer).Take(PassivesPerOffer).ToList();

        internal static IReadOnlyList<DonCard> PassivesOf(int breed, ProgressionContent content)
            => content.Dons.Passives.TryGetValue(breed.ToString(), out var own) && own.Count > 0 ? own
             : content.Dons.Passives.TryGetValue("default", out var fallback) ? fallback : new List<DonCard>();

        internal static int RankOf(ProgressionProfile profile, string card)
            => profile.Dons.Count(d => string.Equals(d.Card, card, StringComparison.OrdinalIgnoreCase));

        internal static bool IsCharacteristicCard(string card, ProgressionContent content)
            => string.Equals(card, content.Dons.CharacteristicCard.Id, StringComparison.OrdinalIgnoreCase);

        internal static bool IsPassiveCard(string card, ProgressionContent content)
            => content.Dons.Passives.Values.Any(list => list.Any(c => string.Equals(c.Id, card, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The breed whose passive list holds a card, or 0.</summary>
        internal static int PassiveBreedOf(string card, ProgressionContent content)
        {
            foreach (var (breed, list) in content.Dons.Passives)
                if (list.Any(c => string.Equals(c.Id, card, StringComparison.OrdinalIgnoreCase)))
                    return int.TryParse(breed, out int id) ? id : 0;
            return 0;
        }

        /// <summary>The don «Pension» of a level, or null.</summary>
        internal static PensionDon? PensionDonAt(int level, ProgressionContent content)
            => content.Dons.Pension.FirstOrDefault(p => p.Level == level && p.Choices.Count > 0);

        private const string PensionPetPrefix = "pension_familier_", PensionMountPrefix = "pension_monture_";

        /// <summary>A Pension card's egg: pet (the pet item id) or mount (the species id). Null for any other card.</summary>
        internal static (string Kind, int Choice)? PensionChoiceOf(string card)
        {
            if (card.StartsWith(PensionPetPrefix, StringComparison.Ordinal) && int.TryParse(card.AsSpan(PensionPetPrefix.Length), out int pet))
                return (Pension.KindPet, pet);
            if (card.StartsWith(PensionMountPrefix, StringComparison.Ordinal) && int.TryParse(card.AsSpan(PensionMountPrefix.Length), out int species))
                return (Pension.KindMount, species);
            return null;
        }

        /// <summary>Whether a card may still be offered: not retired, and under its rank cap (0: no cap).</summary>
        private static bool Offerable(ProgressionProfile profile, DonCard card)
            => !card.Retired && (card.MaxRank <= 0 || RankOf(profile, card.Id) < card.MaxRank);

        /// <summary>The cards of the last <paramref name="window"/> dons of the main track, newest first.</summary>
        internal static HashSet<string> RecentCards(ProgressionProfile profile, int window)
            => profile.Dons.Where(d => d.Level < SpellPointLevelOffset).OrderByDescending(d => d.Level)
                      .Take(Math.Max(0, window)).Select(d => d.Card).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>The rare and legendary chances (0-1): the file's, or the Fortune research's rank's.</summary>
        internal static (double Rare, double Legendary) RarityChances(ProgressionProfile profile, ProgressionContent content)
        {
            var r = content.Dons.Rarity;
            double rare = r.Rare, legendary = r.Legendaire;
            int rank = string.IsNullOrEmpty(r.Research) ? 0 : profile.RankOf(r.Research);
            if (rank > 0 && r.RareByRank.Count > 0) rare = r.RareByRank[Math.Min(rank, r.RareByRank.Count) - 1];
            if (rank > 0 && r.LegendaireByRank.Count > 0) legendary = r.LegendaireByRank[Math.Min(rank, r.LegendaireByRank.Count) - 1];
            return (Math.Clamp(rare / 100.0, 0, 1), Math.Clamp(legendary / 100.0, 0, 1));
        }

        internal static bool HasRarity(string kind, ProgressionContent content)
            => content.Dons.Rarity.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

        /// <summary>A new offer for a level: its cards drawn among those it may still give.</summary>
        internal static DonOffer Draw(ProgressionProfile profile, ProgressionContent content, int breed, int level, string kind,
                                      Random? dice = null)
        {
            dice ??= Random.Shared;
            var d = content.Dons;
            int count = CardsPerOffer(profile, content);
            var cards = new List<string>();
            switch (kind)
            {
                case KindPassive:
                    // Three among the nineteen classes, his own included (spec §10): the first
                    // three of the cycle's shuffled list, the rerolls the next ones.
                    cards.AddRange(PassiveSlice(profile, content, 0));
                    return new DonOffer { Level = level, Kind = kind, Cards = cards };
                case KindPension:
                    if (PensionDonAt(level, content) is { } pension)
                        cards.AddRange(pension.Choices.Select(c => (pension.Kind == Pension.KindPet ? PensionPetPrefix : PensionMountPrefix) + c));
                    return new DonOffer { Level = level, Kind = kind, Cards = cards };
                case KindAccomplishment:
                    cards.AddRange(d.Accomplishments.Where(c => Offerable(profile, c)).OrderBy(_ => dice.Next()).Take(count).Select(c => c.Id));
                    return new DonOffer { Level = level, Kind = kind, Cards = cards };
                case KindSpell:
                    // The spell dons of his class (SpellDons): those he may still take at this
                    // level -- a new one or the next rank of one he has -- every Stasis cap
                    // considered. When none is left (all taken, every cap reached, or a class the
                    // file does not list) the old spell card comes back, with combat dons.
                    var spellDons = SpellDons.Takeable(profile, content, breed, level);
                    if (spellDons.Count > 0)
                    {
                        cards.AddRange(spellDons.OrderBy(_ => dice.Next()).Take(count).Select(s => s.Id));
                        return new DonOffer { Level = level, Kind = kind, Cards = cards };
                    }
                    cards.Add(d.SpellCard.Id);
                    cards.Add(d.CharacteristicCard.Id);
                    foreach (var card in d.Level.Where(c => Offerable(profile, c) && !cards.Contains(c.Id)).OrderBy(_ => dice.Next()))
                    {
                        if (cards.Count >= count) break;
                        cards.Add(card.Id);
                    }
                    return new DonOffer { Level = level, Kind = kind, Cards = cards };
            }
            return DrawStandard(profile, content, level, kind, count, dice);
        }

        /// <summary>
        /// A starting, combat or adventure offer: first card almost always the characteristic of
        /// one's choice (not for adventure), last card a don not taken recently, a rarity per
        /// card, and the pity after five offers without a Légendaire.
        /// </summary>
        private static DonOffer DrawStandard(ProgressionProfile profile, ProgressionContent content, int level, string kind, int count, Random dice)
        {
            var d = content.Dons;
            string carac = d.CharacteristicCard.Id;
            bool adventure = kind == KindAdventure;
            var pool = (adventure ? d.Adventure : d.Level).Where(c => Offerable(profile, c)).ToList();
            var cards = new List<string>();
            if (!adventure && dice.NextDouble() < d.CharacteristicCard.FirstChance) cards.Add(carac);

            // The last card: one not among the last dons kept, else the one kept longest ago.
            var recent = RecentCards(profile, d.RecentWindow);
            string? last = null;
            var fresh = pool.Where(c => !recent.Contains(c.Id)).ToList();
            if (fresh.Count > 0) last = fresh[dice.Next(fresh.Count)].Id;
            else if (pool.Count > 0)
                last = pool.OrderBy(c => profile.Dons.Where(x => string.Equals(x.Card, c.Id, StringComparison.OrdinalIgnoreCase))
                                                     .Select(x => x.Level).DefaultIfEmpty(-1).Max())
                           .ThenBy(_ => dice.Next()).First().Id;

            int middle = count - (last == null ? 0 : 1);
            foreach (var card in pool.OrderBy(_ => dice.Next()))
            {
                if (cards.Count >= middle) break;
                if (card.Id == last || cards.Contains(card.Id)) continue;
                cards.Add(card.Id);
            }
            if (last != null && !cards.Contains(last)) cards.Add(last);
            // A pool too small for the offer: the characteristic card fills it.
            if (cards.Count < count && !adventure && !cards.Contains(carac)) cards.Insert(0, carac);

            var offer = new DonOffer { Level = level, Kind = kind, Cards = cards };
            if (HasRarity(kind, content) && cards.Count > 0)
            {
                var (rare, legendary) = RarityChances(profile, content);
                for (int i = 0; i < cards.Count; i++)
                {
                    double roll = dice.NextDouble();
                    offer.Rarities.Add(roll < legendary ? Legendaire : roll < legendary + rare ? Rare : Commun);
                }
                int pity = d.Rarity.PityAfter;
                if (pity > 0 && !offer.Rarities.Contains(Legendaire) && profile.NoLegendaryStreak >= pity)
                    offer.Rarities[dice.Next(offer.Rarities.Count)] = Legendaire;
                profile.NoLegendaryStreak = offer.Rarities.Contains(Legendaire) ? 0 : profile.NoLegendaryStreak + 1;
            }
            return offer;
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
            var don = new ChosenDon { Level = offer.Level, Card = card, Rarity = offer.RarityAt(index - 1) };
            var carac = content.Dons.CharacteristicCard;
            if (IsCharacteristicCard(card, content))
            {
                string wanted = Normalize(stat);
                if (wanted.Length == 0 || !carac.Stats.Contains(wanted, StringComparer.OrdinalIgnoreCase)
                    || !StatIds.TryGetValue(wanted, out int id))
                    return "carac-requise";
                don.Stat = id;
                don.Amount = CaracAmount(profile, content, don.Rarity);
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
                int level = RealLevel(waiting.Level);
                bool stale = waiting.Cards.Any(c => SpellDons.DefinitionOf(c, content) is { } s
                                                    && !SpellDons.CanTake(s.Don, profile, content, level));
                if (!stale) continue;
                var fresh = Draw(profile, content, breed, level, KindSpell);
                fresh.Level = waiting.Level;
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

        /// <summary>What rerolling the offer of a level costs (kamas-equivalent, OrbePrice turns it into Orbes) when no free reroll is left.</summary>
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
            if (offer.Kind == KindPassive)
            {
                // Two free rerolls, three new passives each time, never paid (spec §10).
                if (PassiveRerollsLeft(profile, offer, content) <= 0) return "passif-plus-de-relance";
                int next = PassiveRerollsUsed(profile, offer, content) + 1;
                offer.Cards = PassiveSlice(profile, content, next);
                offer.Rarities.Clear();
                offer.Rerolled = true;
                return "ok";
            }
            if (!IsRerollable(offer.Kind)) return "non-relancable";
            if (offer.Rerolled) return "deja-relancee";
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
            var dons = profile.Dons.Where(d => IsCharacteristicCard(d.Card, content) && d.Stat == fromId).ToList();
            if (dons.Count == 0) return "rien-a-refondre";
            foreach (var don in dons)
            {
                don.Stat = toId;
                moved += don.Amount;
            }
            profile.RepicksUsed++;
            return "ok";
        }

        // ─── Texts and icons ────────────────────────────────────────────────────────────

        /// <summary>How the window shows an item's name (a pet of a don «Pension»); the handlers set the database's.</summary>
        internal static Func<int, string> ItemNameOf = gid => "#" + gid;

        internal static string RarityKey(int rarity) => rarity switch { >= Legendaire => "legendaire", Rare => "rare", _ => "commun" };

        internal static string RarityLabel(int rarity) => rarity switch { >= Legendaire => "Légendaire", Rare => "Rare", _ => "Commun" };

        /// <summary>«+20 Vitalité, +1 PM»: a card's effects at a rarity; null when one of them has no label.</summary>
        internal static string? EffectsText(DonCard card, int rarity)
        {
            if (card.Effects.Count == 0) return null;
            var parts = new List<string>();
            foreach (var effect in card.Effects)
            {
                if (!EffectLabels.TryGetValue(effect.Stat, out var label)) return null;
                long value = effect.ValueAt(rarity);
                parts.Add($"{(value >= 0 ? "+" : "")}{value} {label}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>A card's name and what it gives at a rarity, in French, as the window and the commands show it.</summary>
        internal static (string Name, string Description) Describe(string cardId, ProgressionProfile profile, ProgressionContent content,
                                                                   int rarity = Commun)
        {
            var carac = content.Dons.CharacteristicCard;
            if (IsCharacteristicCard(cardId, content))
                return (carac.Name, string.Format(carac.Description, CaracAmount(profile, content, rarity)));
            if (SpellDons.DefinitionOf(cardId, content) is { } spellDon)
                return SpellDons.Describe(spellDon.Don, profile);
            if (PensionChoiceOf(cardId) is { } egg)
                return egg.Kind == Pension.KindPet
                    ? ($"Familier : {ItemNameOf(egg.Choice)}", "Un œuf de ce familier, déposé dans la Pension du compte (à incuber).")
                    : ($"Œuf de {MountName(egg.Choice)}", $"Un œuf de {MountName(egg.Choice)} génération 1, déposé dans la Pension du compte (à incuber).");
            var card = content.CardOf(cardId);
            if (card == null) return (cardId, "");
            string rank = card.MaxRank > 1 ? $" (rang {RankOf(profile, card.Id) + 1}/{card.MaxRank})" : "";
            bool generated = content.Dons.Level.Contains(card) || content.Dons.Adventure.Contains(card);
            string description = generated ? EffectsText(card, rarity) ?? card.Description : card.Description;
            return (card.Name + rank, description);
        }

        private static string MountName(int species) => species switch
        {
            1 => "Dragodinde", 2 => "Muldo", 3 => "Volkorne",
            _ => Pension.Content.Species.TryGetValue(species.ToString(), out var name) ? name : $"monture {species}",
        };

        /// <summary>What kind of don a card is, for the label on the card.</summary>
        internal static string CardKindLabel(string cardId, ProgressionContent content)
        {
            if (IsCharacteristicCard(cardId, content)) return "Caractéristique";
            if (SpellDons.DefinitionOf(cardId, content) != null) return "Point de sort";
            if (PensionChoiceOf(cardId) != null) return "Don Pension";
            if (string.Equals(cardId, content.Dons.SpellCard.Id, StringComparison.OrdinalIgnoreCase)) return "Don de sort";
            if (IsPassiveCard(cardId, content)) return "Passif de classe";
            if (content.Dons.Accomplishments.Any(c => string.Equals(c.Id, cardId, StringComparison.OrdinalIgnoreCase))) return "Accomplissement";
            if (content.Dons.Adventure.Any(c => string.Equals(c.Id, cardId, StringComparison.OrdinalIgnoreCase))) return "Don d'aventure";
            return "Don de combat";
        }

        /// <summary>
        /// The icon the window draws on a card, resolved by the client from its own data: "stat"
        /// (a characteristic id: the sheet's picture of it), "spell" (a spell id: its icon),
        /// "item" (an item id: its icon), "class" (a breed: its symbol), or only "glyph" (one of
        /// the client's FigmaIcons). The glyph is also the fallback of the others.
        /// </summary>
        internal static (string Kind, int Id, string Glyph) IconOf(string cardId, ProgressionContent content)
        {
            if (IsCharacteristicCard(cardId, content)) return ("glyph", 0, "character");
            if (SpellDons.DefinitionOf(cardId, content) is { } spellDon) return ("spell", spellDon.Don.Spell, "spell");
            if (PensionChoiceOf(cardId) is { } egg)
                return egg.Kind == Pension.KindPet ? ("item", egg.Choice, "familiar") : ("item", MountItemOf(egg.Choice), "dragoturkey");
            int breed = PassiveBreedOf(cardId, content);
            if (breed > 0) return ("class", breed, "shield");
            var card = content.CardOf(cardId);
            if (card == null) return ("glyph", 0, "star");
            if (card.Icon.Length > 0) return ("glyph", 0, card.Icon);
            foreach (var effect in card.Effects)
            {
                if (!StatIds.TryGetValue(effect.Stat, out int id)) continue;
                return id switch
                {
                    XpPercent => ("glyph", 0, "experience"),
                    SpeedPercentStat => ("glyph", 0, "starBoot"),
                    AnimationPercentStat => ("glyph", 0, "hourglass"),
                    _ => ("stat", id, StatGlyph(id)),
                };
            }
            return ("glyph", 0, "star");
        }

        /// <summary>The FigmaIcons glyph standing for a characteristic when the client has no picture of it.</summary>
        internal static string StatGlyph(int id) => id switch
        {
            Stat.Strength or 88 or 33 => "earth",
            Stat.Intelligence or 89 or 34 => "fire",
            Stat.Chance or 90 or 35 => "water",
            Stat.Agility or 91 or 36 => "wind",
            92 or 37 => "neutral",
            Stat.Vitality => "heart",
            Stat.Wisdom => "experience",
            Stat.Initiative => "initiative",
            Stat.Heals => "heal",
            Stat.Pods => "weight",
            Stat.Prospecting => "treasureMap",
            Stat.Range => "target",
            Stat.Summons => "creature",
            Stat.Critical or 86 => "dice",
            Stat.Lock or Stat.Escape => "move",
            _ => "star",
        };

        /// <summary>A generation-1 ride item of a species, for its icon; 0 when the client data has none.</summary>
        private static int MountItemOf(int species)
        {
            try
            {
                return Breeding.AllRides.Where(r => r.Species == species && r.Generation == 1 && r.Item > 0)
                               .OrderBy(r => r.Id).Select(r => r.Item).FirstOrDefault();
            }
            catch
            {
                return 0;
            }
        }

        internal static string KindLabel(string kind) => kind switch
        {
            KindSpell => "Point de sort",
            KindAdventure => "Don d'aventure",
            KindPassive => "Passif de classe",
            KindAccomplishment => "Accomplissement",
            KindStart => "Offre de départ",
            KindPension => "Don Pension",
            _ => "Don de combat",
        };

        internal static string RefusalText(string reason) => reason switch
        {
            "aucune-offre" => "Aucune offre de don en attente.",
            "carte-inconnue" => "Cette carte n'existe pas dans l'offre.",
            "carac-requise" => "Précisez la caractéristique : force, intelligence, chance, agilite, vitalite ou sagesse.",
            "sort-plafond" => "Ce don de sort n'est plus possible (points, familles ou rang au maximum).",
            "deja-relancee" => "Cette offre a déjà été relancée.",
            "passif-plus-de-relance" => "Plus de relance gratuite pour ce passif (2 maximum).",
            "non-relancable" => "Cette offre ne se relance pas (offre de départ, passif, don Pension ou accomplissement).",
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
