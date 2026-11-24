using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Stasification: the item system of the Stasis private server, its gameplay rules and not its
    /// code. Every piece of equipment that drops comes at its max roll and with a tier, S1 to S4,
    /// each multiplying the max of its characteristics (x1.25, x1.5, x2, x3); three identical pieces
    /// fuse into one of the next tier; the raffineur buys back what is left over; and the market
    /// maker's equipment lots are stasified as drops are, priced by what fusing up to them costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why max roll and a multiplier, rather than the official roll: a server lived alone with two
    /// hundred bots has no market of rolls to hunt through, and smithing every drop to its jet is a
    /// chore that does not scale. Stasis answers by making every drop good and the tier the thing
    /// to chase; the tiers above S2 belong to the evolved characters (<see cref="IsEvolved"/>),
    /// which is where the long game goes.
    /// </para>
    /// <para>
    /// What is never multiplied: AP, MP, range, critical, summons and every resistance, fixed or
    /// percent -- a x3 on them would break fights outright, not make a piece better --, maluses
    /// (a tier must not make a piece worse), weapon damage (it travels as a range and the weapon
    /// hit reads it; doubling it is a weapon rebalance, not a piece's) and anything that is not a
    /// number. The ids are in content/items/stasis.json, so a missed one is a line of JSON.
    /// </para>
    /// <para>
    /// Where the tier lives: in the table ItemStasisTiers (uid → tier), as CustomInstances keeps
    /// its unique copies, AND in the item's own effects, as the text of a 988 «Fabricado por: #4»
    /// line («Stasis S2», or «Name · Stasis S2» when a crafter signed it). The second is what makes
    /// the tier survive every road that re-creates an item under a new uid -- the bank, a trade,
    /// a marketplace sale --: they all carry the effects as they are, and <see cref="TierOf"/>
    /// reads the label back and writes the new uid into the table the first time it is asked.
    /// The label is also what the player sees on the tooltip, so the table can never say one thing
    /// and the item another.
    /// </para>
    /// </remarks>
    public static class Stasis
    {
        public const string AuthoredFile = "items/stasis.json";

        /// <summary>«Fabricado por: #4»: the line that carries the tier's label as its text.</summary>
        public const int LabelEffect = Forgemagic.CraftedBy;

        public const int MinTier = 1, MaxTier = 4;

        /// <summary>
        /// Whether a character is evolved: its drops then follow the evolved distribution and it may
        /// wear S3 and S4. Evolution belongs to another part of the server, which connects itself
        /// here; until it does, nobody is.
        /// </summary>
        public static Func<long, bool> IsEvolved { get; set; } = _ => false;

        internal sealed class Settings
        {
            public bool Enabled { get; set; } = true;
            public Dictionary<int, double> Multipliers { get; set; } = new() { [1] = 1.25, [2] = 1.5, [3] = 2.0, [4] = 3.0 };
            public List<int> NeverMultiplied { get; set; } = new()
            {
                111, 128, 117, 115, 182, 116, 168, 169, 171,
                210, 211, 212, 213, 214, 215, 216, 217, 218, 219,
                240, 241, 242, 243, 244, 245, 246, 247, 248, 249,
                250, 251, 252, 253, 254, 255, 256, 257, 258, 259,
                260, 261, 262, 263, 264, 416, 417, 420, 421,
            };
            public int NormalDropTier { get; set; } = 1;
            public Dictionary<int, int> EvolvedDropWeights { get; set; } = new() { [1] = 66, [2] = 30, [3] = 3, [4] = 1 };
            public int EvolvedOnlyFromTier { get; set; } = 3;
            public int CraftedTier { get; set; } = 1;
            public long FusionCostPerLevel { get; set; } = 50;
            public long FusionCostMinimum { get; set; } = 1000;
            public Dictionary<int, double> FusionCostTierFactor { get; set; } = new() { [2] = 1, [3] = 4, [4] = 12 };
            public Dictionary<int, double> MarketPriceFactor { get; set; } = new() { [1] = 1.25, [2] = 3.75, [3] = 11, [4] = 33 };
            public Dictionary<int, int> RaffineurPercent { get; set; } = new() { [1] = 10, [2] = 15, [3] = 20, [4] = 25 };
            public Dictionary<int, int> MarketTierWeights { get; set; } = new() { [1] = 80, [2] = 15, [3] = 4, [4] = 1 };
        }

        private static Settings? _settings;
        private static HashSet<int>? _never;

        internal static Settings Current
        {
            get => _settings ??= Read(Paths.ContentFile(AuthoredFile));
            set { _settings = value; _never = null; }
        }

        private static HashSet<int> Never => _never ??= Current.NeverMultiplied.ToHashSet();

        internal static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stasis] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        public static bool Enabled => Current.Enabled;

        public static int Clamp(int tier) => Math.Clamp(tier, MinTier, MaxTier);

        /// <summary>«S2».</summary>
        public static string Name(int tier) => $"S{Clamp(tier)}";

        // ─── Which items ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a template is equipment that stasifies: of an equipment family (hats, rings,
        /// weapons... RandomLoot's), not a pet, not a dofus or a creature or a cosmetic, not one of
        /// the server's custom items (born with the effects their file writes), and with at least one
        /// line a tier would multiply -- a piece with nothing to multiply would only carry a label.
        /// </summary>
        public static bool Applies(Forgemagic.Template? template)
        {
            if (template == null || !Enabled) return false;
            if (!RandomLoot.IsEquipmentType(template.Type)) return false;
            if (Pets.IsPet(template.Gid) || CustomItems.TryGet(template.Gid, out _)) return false;
            return template.Lines.Any(l => Multiplies(l.Effect));
        }

        /// <summary>Whether a tier multiplies this effect: a rolled characteristic, a bonus, not one of the never-multiplied.</summary>
        public static bool Multiplies(int effect)
        {
            if (effect <= 0 || Never.Contains(effect)) return false;
            var info = Forgemagic.InfoOf(effect);
            if (info.IsWeaponDamage || info.IsMalus) return false;
            return info.UseDice || info.Weight != 0;
        }

        // ─── The multiplication ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Effects at a tier: each plain characteristic line that <see cref="Multiplies"/> times the
        /// tier's multiplier, rounded half up and at least one more than it was when it was positive
        /// (a +1 wisdom at S1 is 1.25, and staying 1 would make the tier say nothing), the rest as it
        /// is; and the label. <paramref name="effects"/> is the roll it starts from: the max roll
        /// for a drop, the rolled one for a craft.
        /// </summary>
        public static List<Equipment.ItemEffect> Apply(IEnumerable<Equipment.ItemEffect> effects, int tier)
        {
            tier = Clamp(tier);
            double factor = Current.Multipliers.TryGetValue(tier, out double f) ? f : 1.0;
            var result = new List<Equipment.ItemEffect>();
            foreach (var e in effects)
            {
                bool plain = e.Text == null && e.DiceNum == 0 && e.DiceSide == 0;
                if (plain && e.Value > 0 && Multiplies(e.Effect))
                {
                    long raised = (long)Math.Round(e.Value * factor, MidpointRounding.AwayFromZero);
                    if (factor > 1 && raised <= e.Value) raised = e.Value + 1;
                    result.Add(new Equipment.ItemEffect(e.Effect, raised, 0, 0));
                }
                else result.Add(e);
            }
            return Labelled(result, tier);
        }

        /// <summary>A piece at a tier from its template: the max roll, multiplied, labelled.</summary>
        public static List<Equipment.ItemEffect> AtMax(Forgemagic.Template template, int tier)
            => Apply(Forgemagic.Perfect(template), tier);

        // ─── The label ──────────────────────────────────────────────────────────────────────

        private static readonly Regex LabelPattern = new(@"Stasis S([1-4])", RegexOptions.Compiled);
        private const string Separator = " · ";

        /// <summary>
        /// The effects with the tier written on their 988 line: alone, «Stasis S2»; after a crafter's
        /// name, «Name · Stasis S2». One line, because the client draws one «Fabricado por».
        /// A label already there is replaced, not added to.
        /// </summary>
        public static List<Equipment.ItemEffect> Labelled(IEnumerable<Equipment.ItemEffect> effects, int tier)
        {
            var list = effects.ToList();
            int at = list.FindIndex(e => e.Effect == LabelEffect);
            string label = "Stasis " + Name(tier);
            if (at < 0)
            {
                list.Add(new Equipment.ItemEffect(LabelEffect, 0, 0, 0, label));
                return list;
            }
            string signer = StripLabel(list[at].Text);
            list[at] = new Equipment.ItemEffect(LabelEffect, list[at].Value, list[at].DiceNum, list[at].DiceSide,
                                                signer.Length == 0 ? label : signer + Separator + label);
            return list;
        }

        /// <summary>A 988 text without its tier: the crafter's name, or nothing.</summary>
        internal static string StripLabel(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string stripped = LabelPattern.Replace(text, "");
            stripped = stripped.Replace(Separator, "").Trim();
            return stripped;
        }

        /// <summary>The tier an item's effects say, or 0 when they say none.</summary>
        public static int TierInEffects(IEnumerable<Equipment.ItemEffect> effects)
        {
            foreach (var e in effects)
            {
                if (e.Effect != LabelEffect || e.Text == null) continue;
                var match = LabelPattern.Match(e.Text);
                if (match.Success) return match.Groups[1].Value[0] - '0';
            }
            return 0;
        }

        // ─── The table ──────────────────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<long, int> _byUid = new();
        private static readonly object Gate = new();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    var create = connection.CreateCommand();
                    create.CommandText = "CREATE TABLE IF NOT EXISTS ItemStasisTiers (Uid INTEGER PRIMARY KEY, Tier INTEGER NOT NULL);";
                    create.ExecuteNonQuery();
                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT Uid, Tier FROM ItemStasisTiers;";
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) _byUid[reader.GetInt64(0)] = reader.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Stasis] The tiers could not be read: {ex.Message}");
                }
                _loaded = true;
            }
        }

        /// <summary>
        /// An item's tier, 0 for none: the table's, or the label's -- which is then written for this
        /// uid, so an item the bank, a trade or a marketplace re-created keeps its tier in the table too.
        /// </summary>
        public static int TierOf(long uid, IEnumerable<Equipment.ItemEffect> effects)
        {
            int labelled = TierInEffects(effects);
            EnsureLoaded();
            if (_byUid.TryGetValue(uid, out int known) && (labelled == 0 || labelled == known)) return known;
            if (labelled != 0 && uid > 0) Mark(uid, labelled);
            return labelled;
        }

        /// <summary>Records an item's tier.</summary>
        public static void Mark(long uid, int tier)
        {
            EnsureLoaded();
            tier = Clamp(tier);
            _byUid[uid] = tier;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var write = connection.CreateCommand();
                write.CommandText = "INSERT OR REPLACE INTO ItemStasisTiers (Uid, Tier) VALUES ($uid, $tier);";
                write.Parameters.AddWithValue("$uid", uid);
                write.Parameters.AddWithValue("$tier", tier);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stasis] Item {uid} could not be marked {Name(tier)}: {ex.Message}");
            }
        }

        // ─── Drops and wearing ──────────────────────────────────────────────────────────────

        /// <summary>The tier a piece dropped for this character comes at.</summary>
        public static int DropTier(long characterId, Random dice)
            => IsEvolved(characterId) ? Draw(Current.EvolvedDropWeights, dice) : Clamp(Current.NormalDropTier);

        /// <summary>A tier drawn from weights by tier (percent or any scale).</summary>
        internal static int Draw(IReadOnlyDictionary<int, int> weights, Random dice)
        {
            int total = weights.Where(kv => kv.Value > 0).Sum(kv => kv.Value);
            if (total <= 0) return MinTier;
            int roll = dice.Next(total);
            foreach (var (tier, weight) in weights.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
            {
                if (roll < weight) return Clamp(tier);
                roll -= weight;
            }
            return MinTier;
        }

        /// <summary>Whether a character may wear a piece of this tier: S3 and S4 only once evolved.</summary>
        public static bool MayWear(long characterId, int tier)
            => !Enabled || tier < Current.EvolvedOnlyFromTier || IsEvolved(characterId);

        // ─── Fusion ─────────────────────────────────────────────────────────────────────────

        public const int FusionCount = 3;

        /// <summary>What fusing three pieces of this level into one of <paramref name="targetTier"/> costs.</summary>
        public static long FusionCost(int level, int targetTier)
        {
            var s = Current;
            double factor = s.FusionCostTierFactor.TryGetValue(targetTier, out double f) ? f : 1;
            long cost = (long)Math.Round(s.FusionCostPerLevel * Math.Max(1, level) * factor);
            return Math.Max(s.FusionCostMinimum, cost);
        }

        /// <summary>
        /// The exos of a piece: the plain numeric lines its template does not roll (an AP, an MP, a
        /// range added by smithmagic or the random loot), the label and internal lines aside.
        /// </summary>
        public static List<Equipment.ItemEffect> ExosOf(Forgemagic.Template template, IEnumerable<Equipment.ItemEffect> effects)
            => effects.Where(e => e.Effect > 0 && e.Effect != LabelEffect && e.Text == null
                                  && e.DiceNum == 0 && e.DiceSide == 0 && e.Value > 0
                                  && template.MaxOf(e.Effect) == null && Forgemagic.InfoOf(e.Effect).Weight != 0)
                      .ToList();

        /// <summary>
        /// The piece three pieces of <paramref name="fromTier"/> fuse into: the next tier at its max,
        /// with the exos of the first that has any -- except into S3, which wipes them, as Stasis
        /// does. Overs are lost either way: the new tier's max is the roll. Null at S4.
        /// </summary>
        public static List<Equipment.ItemEffect>? Fused(Forgemagic.Template template, int fromTier,
                                                        IEnumerable<IReadOnlyList<Equipment.ItemEffect>> sources)
        {
            if (fromTier >= MaxTier) return null;
            int target = Clamp(fromTier + 1);
            var result = AtMax(template, target);
            if (target != 3)
            {
                var exos = sources.Select(s => ExosOf(template, s)).FirstOrDefault(x => x.Count > 0);
                if (exos != null) result.InsertRange(Math.Max(0, result.Count - 1), exos);
            }
            return result;
        }

        // ─── Worth ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A tier's worth against the plain item's reference price: S1 its multiplier, each next
        /// three of the one below -- three of them is what fusing one costs.
        /// </summary>
        public static double PriceFactor(int tier)
            => tier <= 0 ? 1.0 : Current.MarketPriceFactor.TryGetValue(Clamp(tier), out double f) ? f : 1.0;

        /// <summary>What an item of this tier is worth, from the plain one's reference worth.</summary>
        public static long Worth(long reference, int tier) => Math.Max(1, (long)Math.Round(reference * PriceFactor(tier)));

        /// <summary>What the raffineur pays for one piece.</summary>
        public static long BuybackPrice(long reference, int tier)
        {
            int percent = Current.RaffineurPercent.TryGetValue(Clamp(tier), out int p) ? p : 10;
            return Math.Max(1, Worth(reference, tier) * percent / 100);
        }

        /// <summary>The tier of a piece the market maker puts on sale.</summary>
        public static int MarketTier(Random dice) => Draw(Current.MarketTierWeights, dice);
    }
}
