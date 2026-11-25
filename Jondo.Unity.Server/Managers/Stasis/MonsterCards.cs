using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>content/progression/cards.json (see <see cref="MonsterCards.Content"/>).</summary>
    internal sealed class CardsFile
    {
        public int CardsPerPack { get; set; } = 5;
        public double PackDropChance { get; set; } = 0.03;
        public int LevelBand { get; set; } = 15;
        public long PackTokens { get; set; } = 5;
        public long SellBaseOrbes { get; set; } = 50;
        public long SellPerLevelOrbes { get; set; } = 10;
        public int MinFamilySize { get; set; } = 3;
        public int MaxFamilySize { get; set; } = 40;
        public List<DonEffect> FamilyBonuses { get; set; } = new();
        /// <summary>The level a card's monster needs for each rarity: Commune, Rare, Épique, Légendaire.</summary>
        public List<int> RarityMinLevels { get; set; } = new() { 1, 60, 120, 180 };
        /// <summary>The Raffineur: at most this many cards in one lot.</summary>
        public int RefinerMaxCards { get; set; } = 10;
        /// <summary>The Raffineur's best chance, in %, for a card of Rare, Épique, Légendaire.</summary>
        public List<double> RefinerCaps { get; set; } = new() { 75, 45, 25 };
    }

    /// <summary>A card: one monster of a family (a monster race).</summary>
    internal sealed record CardMonster(int Id, string Name, int Level, int Family);

    /// <summary>A family of the binder: a race, its name and its monsters.</summary>
    internal sealed record CardFamily(int Race, string Name, IReadOnlyList<CardMonster> Monsters);

    /// <summary>One account's binder: glued cards, loose cards, unopened packs.</summary>
    internal sealed class CardAccount
    {
        public long AccountId { get; init; }
        public HashSet<int> Glued { get; } = new();
        public Dictionary<int, int> Loose { get; } = new();
        /// <summary>The unopened packs, each by the level its cards are drawn around.</summary>
        public List<int> Packs { get; } = new();
        internal object Gate { get; } = new();
    }

    /// <summary>
    /// The Stasis monster cards (docs/stasis-gameplay-spec.md §14): a binder per account, packs
    /// earned in fights (or bought with event tokens), one card per monster, a family per monster
    /// race; a card glued is consumed, and a family complete gives every character of the account
    /// a small permanent bonus, read through <see cref="Dons.BonusesOf(long)"/>. Kept across
    /// Ascension: it is the account's.
    /// </summary>
    /// <remarks>
    /// Cards and packs are virtual (tables CardBinder, CardLoose, CardPacks): the client has no
    /// card item. Duplicates are sold to the server for orbes (no client item can go to the
    /// marketplace). The families are built once from the monsters the world spawns
    /// (MobSpawnManager.AllMonsters) and their race (Almanax.RaceOf).
    /// </remarks>
    internal static class MonsterCards
    {
        private static CardsFile? _content;
        private static readonly object _lock = new();

        internal static CardsFile Content
        {
            get
            {
                if (_content != null) return _content;
                lock (_lock) return _content ??= Load(Path.Combine(Jondo.Unity.Launcher.Paths.ContentFile("progression"), "cards.json"));
            }
            set { _content = value; _bonusCache.Clear(); }
        }

        internal static CardsFile Load(string path)
        {
            try
            {
                if (File.Exists(path)) return ProgressionContent.Parse<CardsFile>(File.ReadAllText(path)) ?? new CardsFile();
                Console.WriteLine($"[Cards] {path} is missing: the cards run on their defaults.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cards] {path} could not be read: {ex.Message}");
            }
            return new CardsFile();
        }

        // ─── Catalogue ──────────────────────────────────────────────────────────────────

        private static IReadOnlyList<CardFamily>? _families;

        /// <summary>The families of the binder (tests install their own).</summary>
        internal static IReadOnlyList<CardFamily> Families
        {
            get
            {
                if (_families != null) return _families;
                lock (_lock) return _families ??= BuildFamilies(Content);
            }
            set { _families = value; _bonusCache.Clear(); }
        }

        internal static CardMonster? CardOf(int monsterId)
            => Families.SelectMany(f => f.Monsters).FirstOrDefault(m => m.Id == monsterId);

        internal static CardFamily? FamilyOf(int race) => Families.FirstOrDefault(f => f.Race == race);

        /// <summary>The families from the world's monsters: by race, the small ones out, the big ones cut by level.</summary>
        internal static List<CardFamily> Group(IEnumerable<CardMonster> monsters, CardsFile content)
        {
            var families = new List<CardFamily>();
            foreach (var race in monsters.Where(m => m.Family > 0).GroupBy(m => m.Family).OrderBy(g => g.Key))
            {
                var list = race.GroupBy(m => m.Id).Select(g => g.First())
                               .OrderBy(m => m.Level).ThenBy(m => m.Id).Take(Math.Max(1, content.MaxFamilySize)).ToList();
                if (list.Count < content.MinFamilySize) continue;
                families.Add(new CardFamily(race.Key, "Famille " + list[0].Name, list));
            }
            return families;
        }

        private static List<CardFamily> BuildFamilies(CardsFile content)
        {
            try
            {
                var monsters = MobSpawnManager.AllMonsters.Where(m => m.Grades.Count > 0).ToList();
                var names = new Dictionary<long, string>();
                using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Key, Text FROM Translations WHERE Key = $k;";
                    var key = command.Parameters.Add("$k", SqliteType.Text);
                    foreach (var m in monsters)
                    {
                        key.Value = m.NameId.ToString();
                        using var r = command.ExecuteReader();
                        if (r.Read() && !r.IsDBNull(1)) names[m.Id] = r.GetString(1);
                    }
                }
                var cards = monsters.Where(m => names.ContainsKey(m.Id))
                                    .Select(m => new CardMonster(m.Id, names[m.Id], m.Grades[0].Level, Almanax.RaceOf(m.Id)));
                var families = Group(cards, content);
                Console.WriteLine($"[Cards] {families.Count} families, {families.Sum(f => f.Monsters.Count)} cards.");
                return families;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cards] The families could not be built: {ex.Message}");
                return new List<CardFamily>();
            }
        }

        // ─── Rules ──────────────────────────────────────────────────────────────────────

        /// <summary>A pack's cards: monsters around its level, any when none is in the band.</summary>
        internal static List<int> DrawPack(int level, IReadOnlyList<CardFamily> families, CardsFile content, Random dice)
        {
            var all = families.SelectMany(f => f.Monsters).ToList();
            if (all.Count == 0) return new List<int>();
            var band = all.Where(m => Math.Abs(m.Level - level) <= content.LevelBand).ToList();
            if (band.Count == 0) band = all;
            return Enumerable.Range(0, Math.Max(1, content.CardsPerPack)).Select(_ => band[dice.Next(band.Count)].Id).ToList();
        }

        /// <summary>A loose card glued into the binder (consumed). "ok", or why not.</summary>
        internal static string Glue(CardAccount account, int monsterId)
        {
            if (!account.Loose.TryGetValue(monsterId, out int n) || n <= 0) return "carte-absente";
            if (account.Glued.Contains(monsterId)) return "deja-collee";
            Take(account, monsterId, 1);
            account.Glued.Add(monsterId);
            return "ok";
        }

        /// <summary>The loose cards the binder already has: the ones that can only be sold.</summary>
        internal static int Duplicates(CardAccount account, int monsterId)
            => account.Loose.TryGetValue(monsterId, out int n) ? (account.Glued.Contains(monsterId) ? n : Math.Max(0, n - 1)) : 0;

        internal static void Take(CardAccount account, int monsterId, int count)
        {
            int left = account.Loose.GetValueOrDefault(monsterId) - count;
            if (left > 0) account.Loose[monsterId] = left;
            else account.Loose.Remove(monsterId);
        }

        internal static void AddLoose(CardAccount account, IEnumerable<int> cards)
        {
            foreach (int id in cards) account.Loose[id] = account.Loose.GetValueOrDefault(id) + 1;
        }

        internal static (int Glued, int Total) Completion(CardAccount account, CardFamily family)
            => (family.Monsters.Count(m => account.Glued.Contains(m.Id)), family.Monsters.Count);

        internal static bool IsComplete(CardAccount account, CardFamily family)
            => family.Monsters.Count > 0 && family.Monsters.All(m => account.Glued.Contains(m.Id));

        internal static DonEffect? BonusOf(CardFamily family, CardsFile content)
            => content.FamilyBonuses.Count == 0 ? null : content.FamilyBonuses[Math.Abs(family.Race) % content.FamilyBonuses.Count];

        internal static long SellPrice(CardMonster card, CardsFile content) => content.SellBaseOrbes + content.SellPerLevelOrbes * card.Level;

        /// <summary>What an account's complete families give, characteristic id to amount.</summary>
        internal static Dictionary<int, long> BonusesOf(CardAccount account, IReadOnlyList<CardFamily> families, CardsFile content)
        {
            var total = new Dictionary<int, long>();
            if (account.Glued.Count == 0) return total;
            foreach (var family in families)
            {
                if (!IsComplete(account, family) || BonusOf(family, content) is not { } bonus) continue;
                if (!Dons.StatIds.TryGetValue(bonus.Stat, out int id) || id == Dons.XpPercent) continue;
                total[id] = total.GetValueOrDefault(id) + bonus.Value;
            }
            return total;
        }

        private static readonly ConcurrentDictionary<long, long> _accountOf = new();
        private static readonly ConcurrentDictionary<long, Dictionary<int, long>> _bonusCache = new();

        /// <summary>The bonus of a character's account (the dons' third source; empty when it has none).</summary>
        internal static IReadOnlyDictionary<int, long> BonusesOfCharacter(long characterId)
        {
            if (characterId <= 0 || InMemoryOnly && !_accountOf.ContainsKey(characterId)) return new Dictionary<int, long>();
            long account = _accountOf.GetOrAdd(characterId, id =>
            {
                try { return DatabaseManager.AccountIdOfCharacter(id); }
                catch { return 0; }
            });
            if (account <= 0) return new Dictionary<int, long>();
            return _bonusCache.GetOrAdd(account, a =>
            {
                var binder = Of(a);
                lock (binder.Gate) return binder.Glued.Count == 0 ? new Dictionary<int, long>() : BonusesOf(binder, Families, Content);
            });
        }

        /// <summary>For the tests: the account a character belongs to.</summary>
        internal static void Bind(long characterId, long accountId) => _accountOf[characterId] = accountId;

        // ─── Raffineur de cartes ──────────────────────────────────────────────────────────

        internal static readonly string[] RarityNames = { "Commune", "Rare", "Épique", "Légendaire" };

        /// <summary>A card's rarity, 0 (Commune) to 3 (Légendaire), by its monster's level.</summary>
        internal static int RarityOf(CardMonster card, CardsFile content)
        {
            int rarity = 0;
            var levels = content.RarityMinLevels;
            for (int i = 1; i < Math.Min(levels.Count, RarityNames.Length); i++)
                if (card.Level >= levels[i]) rarity = i;
            return rarity;
        }

        internal static string RarityName(int rarity) => RarityNames[Math.Clamp(rarity, 0, RarityNames.Length - 1)];

        /// <summary>
        /// The Raffineur's chance, in %, that a lot of <paramref name="count"/> copies of
        /// <paramref name="card"/> gives one card of the next rarity: the rarity's cap (Rare 75,
        /// Épique 45, Légendaire 25) times the lot's worth against a full lot of the next rarity's
        /// weakest cards (RefinerMaxCards x their sell price). 0 for a Légendaire or an empty lot.
        /// </summary>
        internal static double RefineChance(CardMonster card, int count, CardsFile content)
        {
            int target = RarityOf(card, content) + 1;
            if (count <= 0 || target >= RarityNames.Length || target - 1 >= content.RefinerCaps.Count) return 0;
            count = Math.Min(count, Math.Max(1, content.RefinerMaxCards));
            double cap = content.RefinerCaps[target - 1];
            int floor = target < content.RarityMinLevels.Count ? content.RarityMinLevels[target] : card.Level;
            double full = Math.Max(1, content.RefinerMaxCards) * (double)(content.SellBaseOrbes + content.SellPerLevelOrbes * floor);
            double lot = count * (double)SellPrice(card, content);
            return full <= 0 ? cap : Math.Round(cap * Math.Min(1.0, lot / full), 2);
        }

        /// <summary>
        /// The Raffineur: stakes <paramref name="count"/> duplicates (1 to RefinerMaxCards) of a card;
        /// on success one card of the next rarity (of its family when it has one) comes into hand,
        /// on failure the lot is lost. Result "ok"/"echec" with the card won, or why not (nothing taken).
        /// </summary>
        internal static (string Result, int Won, double Chance) Refine(CardAccount account, int monsterId, int count,
            IReadOnlyList<CardFamily> families, CardsFile content, Random dice)
        {
            var card = families.SelectMany(f => f.Monsters).FirstOrDefault(m => m.Id == monsterId);
            if (card == null) return ("carte-inconnue", 0, 0);
            if (count < 1 || count > Math.Max(1, content.RefinerMaxCards)) return ("lot-taille", 0, 0);
            int target = RarityOf(card, content) + 1;
            if (target >= RarityNames.Length) return ("legendaire", 0, 0);
            if (Duplicates(account, monsterId) < count) return ("doublons", 0, 0);
            var pool = families.Where(f => f.Race == card.Family).SelectMany(f => f.Monsters)
                               .Where(m => RarityOf(m, content) == target).ToList();
            if (pool.Count == 0) pool = families.SelectMany(f => f.Monsters).Where(m => RarityOf(m, content) == target)
                                                .GroupBy(m => m.Id).Select(g => g.First()).ToList();
            if (pool.Count == 0) return ("aucune-cible", 0, 0);
            double chance = RefineChance(card, count, content);
            Take(account, monsterId, count);
            if (dice.NextDouble() * 100.0 >= chance) return ("echec", 0, chance);
            int won = pool[dice.Next(pool.Count)].Id;
            AddLoose(account, new[] { won });
            return ("ok", won, chance);
        }

        internal static string RefusalText(string reason) => reason switch
        {
            "lot-taille" => $"Le Raffineur prend de 1 à {Math.Max(1, Content.RefinerMaxCards)} cartes.",
            "legendaire" => "Une carte Légendaire ne se raffine pas.",
            "aucune-cible" => "Aucune carte de la rareté supérieure n'existe.",
            "doublons" => "Pas assez de doublons de cette carte : seuls les doublons se misent (pas l'exemplaire à coller).",
            "carte-absente" => "Vous n'avez pas cette carte (voir .cartes).",
            "deja-collee" => "Cette carte est déjà dans le classeur : le doublon se vend (.cartes vendre).",
            "aucun-paquet" => "Vous n'avez aucun paquet de cartes.",
            "carte-inconnue" => "Carte inconnue.",
            "aucun-doublon" => "Aucun doublon de cette carte à vendre.",
            "jetons" => "Pas assez de jetons Event.",
            _ => reason,
        };

        // ─── Storage ────────────────────────────────────────────────────────────────────

        internal static bool InMemoryOnly { get; set; }

        private static readonly ConcurrentDictionary<long, CardAccount> _cache = new();
        private static bool _tableReady;
        private static readonly object _tableLock = new();

        internal static CardAccount Of(long accountId) => _cache.GetOrAdd(accountId, Read);

        internal static void Forget()
        {
            _cache.Clear();
            _bonusCache.Clear();
            _accountOf.Clear();
        }

        private static CardAccount Read(long accountId)
        {
            var account = new CardAccount { AccountId = accountId };
            if (InMemoryOnly || accountId <= 0) return account;
            try
            {
                EnsureTables();
                using var connection = Open();
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT MonsterId FROM CardBinder WHERE AccountId = $a;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) account.Glued.Add(r.GetInt32(0));
                }
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT MonsterId, Count FROM CardLoose WHERE AccountId = $a;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) account.Loose[r.GetInt32(0)] = r.GetInt32(1);
                }
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT Level FROM CardPacks WHERE AccountId = $a ORDER BY Id;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) account.Packs.Add(r.GetInt32(0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cards] Could not read the binder of account {accountId}: {ex.Message}");
            }
            return account;
        }

        internal static void Save(CardAccount p)
        {
            _bonusCache.TryRemove(p.AccountId, out _);
            if (InMemoryOnly || p.AccountId <= 0) return;
            try
            {
                EnsureTables();
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                void Run(string sql, params (string Name, object Value)[] values)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
                    command.ExecuteNonQuery();
                }
                Run("DELETE FROM CardBinder WHERE AccountId = $a;", ("$a", p.AccountId));
                foreach (int id in p.Glued) Run("INSERT INTO CardBinder (AccountId, MonsterId) VALUES ($a, $m);", ("$a", p.AccountId), ("$m", id));
                Run("DELETE FROM CardLoose WHERE AccountId = $a;", ("$a", p.AccountId));
                foreach (var (id, n) in p.Loose)
                    Run("INSERT INTO CardLoose (AccountId, MonsterId, Count) VALUES ($a, $m, $n);", ("$a", p.AccountId), ("$m", id), ("$n", n));
                Run("DELETE FROM CardPacks WHERE AccountId = $a;", ("$a", p.AccountId));
                for (int i = 0; i < p.Packs.Count; i++)
                    Run("INSERT INTO CardPacks (AccountId, Id, Level) VALUES ($a, $i, $l);", ("$a", p.AccountId), ("$i", i), ("$l", p.Packs[i]));
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cards] Could not store the binder of account {p.AccountId}: {ex.Message}");
            }
        }

        private static void EnsureTables()
        {
            if (_tableReady) return;
            lock (_tableLock)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CardBinder (AccountId INTEGER NOT NULL, MonsterId INTEGER NOT NULL, PRIMARY KEY (AccountId, MonsterId));
                    CREATE TABLE IF NOT EXISTS CardLoose (AccountId INTEGER NOT NULL, MonsterId INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (AccountId, MonsterId));
                    CREATE TABLE IF NOT EXISTS CardPacks (AccountId INTEGER NOT NULL, Id INTEGER NOT NULL, Level INTEGER NOT NULL, PRIMARY KEY (AccountId, Id));";
                create.ExecuteNonQuery();
                _tableReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }
    }
}
