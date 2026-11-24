using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>content/progression/pension.json, read once (see <see cref="Pension.Content"/>).</summary>
    public sealed class PensionFile
    {
        public PensionIncubation Incubation { get; set; } = new();
        public PensionSpeedUp SpeedUp { get; set; } = new();
        public Dictionary<string, int> HatchMinutes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [Pension.Starter] = 5, [Pension.Common] = 10, [Pension.Rare] = 30, [Pension.Legendary] = 60,
        };
        public int MountHatchBase { get; set; } = 5;
        public int MountHatchPerGeneration { get; set; } = 6;
        public int MountHatchMax { get; set; } = 60;
        public PensionStudy Study { get; set; } = new();
        public PensionCrate Crate { get; set; } = new();
        public double RarityUpChance { get; set; } = 0.05;
        public int MaxGeneration { get; set; } = 10;
        public Dictionary<string, int> Litters { get; set; } = new();
        public Dictionary<string, long> BreedOrbes { get; set; } = new();
        public Dictionary<string, string> Species { get; set; } = new();
        public List<PensionGift> Gifts { get; set; } = new();
        public Dictionary<string, List<int>> Pets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PensionIncubation
    {
        public int StartSlots { get; set; } = 2;
        public int Level100Slots { get; set; } = 1;
        public int Level100 { get; set; } = 100;
        public int BuyableSlots { get; set; } = 2;
        public long SlotTokens { get; set; } = 30;
        public int MaxSlots { get; set; } = 8;
    }

    public sealed class PensionSpeedUp
    {
        public int Minutes { get; set; } = 15;
        public long Tokens { get; set; } = 2;
    }

    public sealed class PensionStudy
    {
        public int Places { get; set; } = 3;
        public double Percent { get; set; } = 5;
        public double LowLevelMultiplier { get; set; } = 2;
        public double EquippedBonusPercent { get; set; } = 25;
        public double PetFactor { get; set; } = 1;
        public double MountFactor { get; set; } = 1;
    }

    public sealed class PensionCrate
    {
        public long PriceOrbes { get; set; } = 50000;
        public double DropChance { get; set; } = 0.01;
        public double MountChance { get; set; } = 0.7;
        public double LegendaryShare { get; set; } = 0.25;
    }

    public sealed class PensionGift
    {
        public int Level { get; set; }
        public string Kind { get; set; } = Pension.KindMount;
        public string Rarity { get; set; } = "";
    }

    /// <summary>An egg of the pension: virtual (the client has no mount or mystery egg item).</summary>
    public sealed class PensionEgg
    {
        public int Id { get; set; }
        /// <summary><see cref="Pension.KindMount"/> or <see cref="Pension.KindPet"/>.</summary>
        public string Kind { get; set; } = Pension.KindMount;
        /// <summary>For a mount egg: the Breeding species (1 dragoturkey, 2 muldo, 3 volkorne).</summary>
        public int Species { get; set; }
        public int Generation { get; set; }
        /// <summary>For a pet egg: Starter, Commun, Rare or Legendaire.</summary>
        public string Rarity { get; set; } = "";
        /// <summary>An egg of the dons (Dons «Pension»).</summary>
        public bool Gift { get; set; }
        /// <summary>The incubation place it is in, from 1; 0 when it waits in the pension.</summary>
        public int Slot { get; set; }
        /// <summary>When it hatches, in unix seconds; 0 when not incubating.</summary>
        public long ReadyAt { get; set; }
    }

    /// <summary>What one account's pension remembers.</summary>
    public sealed class PensionAccount
    {
        public long AccountId { get; init; }
        public int BoughtSlots { get; set; }
        public bool Level100 { get; set; }
        public int Crates { get; set; }
        /// <summary>The dons «Pension» already given, as "character:level".</summary>
        public HashSet<string> Gifts { get; } = new(StringComparer.Ordinal);
        public List<PensionEgg> Eggs { get; } = new();
        /// <summary>The item uids in the study room.</summary>
        public List<long> Study { get; } = new();
        /// <summary>Litters left by mount item uid; a mount not listed has its generation's full count.</summary>
        public Dictionary<long, int> Litters { get; } = new();
        internal object Gate { get; } = new();

        public int NextEggId() => Eggs.Count == 0 ? 1 : Eggs.Max(e => e.Id) + 1;
    }

    /// <summary>
    /// The Stasis «Pension» (docs/stasis-gameplay-spec.md §15), shared by the account: incubation
    /// places where eggs hatch in real time, the study room that gives the studied animals a share
    /// of every won fight's experience, the breeding of two mounts of one species and generation
    /// into an egg of the next, and the egg crate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IS REAL AND WHAT IS NOT. The client has no mount egg nor mystery egg item (its «Huevo de
    /// …» are resources), so the eggs and the crates are rows of the pension -- virtual. What
    /// hatches is real: a mount is a ride item of its species and generation (Breeding's RidesData,
    /// Rides.Fresh: level 1, rideable in slot 8), a pet a pet item of the client (Pets.Fresh, the
    /// growth Pets reads). The study room and the litters refer to those items by uid.
    /// </para>
    /// <para>
    /// Every number is pension.json's; this class is rules and storage. The tables
    /// (PensionAccounts, PensionEggs, PensionStudy, PensionLitters) are created on first use, and
    /// each change rewrites the account's rows, as ProgressionStore does.
    /// </para>
    /// </remarks>
    public static class Pension
    {
        public const string KindMount = "monture", KindPet = "familier";
        public const string Starter = "Starter", Common = "Commun", Rare = "Rare", Legendary = "Legendaire";

        /// <summary>The rarities in order, each one up from the one before.</summary>
        public static readonly IReadOnlyList<string> Rarities = new[] { Starter, Common, Rare, Legendary };

        /// <summary>An egg hatched: the character's id. For the missions («faire éclore un œuf»).</summary>
        public static event Action<long>? EggHatched;

        internal static void RaiseHatched(long characterId)
        {
            try { EggHatched?.Invoke(characterId); }
            catch (Exception ex) { Console.WriteLine($"[Pension] An EggHatched listener failed: {ex.Message}"); }
        }

        // ─── Content ────────────────────────────────────────────────────────────────────

        private static PensionFile? _content;
        private static readonly object _contentLock = new();

        public static PensionFile Content
        {
            get
            {
                if (_content != null) return _content;
                lock (_contentLock) return _content ??= Load(Path.Combine(Jondo.Unity.Launcher.Paths.ContentFile("progression"), "pension.json"));
            }
            set => _content = value;
        }

        internal static PensionFile Load(string path)
        {
            try
            {
                if (File.Exists(path)) return ProgressionContent.Parse<PensionFile>(File.ReadAllText(path)) ?? new PensionFile();
                Console.WriteLine($"[Pension] {path} is missing: the pension runs on its defaults.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pension] {path} could not be read: {ex.Message}");
            }
            return new PensionFile();
        }

        // ─── Rules ──────────────────────────────────────────────────────────────────────

        public static string RarityLabel(string rarity) => rarity switch
        {
            Legendary => "Légendaire",
            _ => rarity,
        };

        public static string SpeciesName(int species, PensionFile content)
            => content.Species.TryGetValue(species.ToString(), out var name) ? name : Breeding.SpeciesOf(species)?.Name ?? $"Espèce {species}";

        public static string Describe(PensionEgg egg, PensionFile content)
            => egg.Kind == KindMount
                ? $"Œuf de {SpeciesName(egg.Species, content)} gén. {egg.Generation}"
                : $"Œuf mystère de familier {RarityLabel(egg.Rarity)}";

        /// <summary>The incubation places: the start, the level-100 one, the bought ones, never past the cap.</summary>
        public static int Slots(PensionAccount account, PensionFile content)
        {
            var i = content.Incubation;
            int slots = i.StartSlots + (account.Level100 ? i.Level100Slots : 0) + Math.Min(account.BoughtSlots, i.BuyableSlots);
            return Math.Clamp(slots, 0, i.MaxSlots);
        }

        public static int HatchMinutes(PensionEgg egg, PensionFile content)
        {
            if (egg.Kind == KindMount)
                return Math.Min(content.MountHatchMax, content.MountHatchBase + Math.Max(0, egg.Generation - 1) * content.MountHatchPerGeneration);
            return content.HatchMinutes.TryGetValue(egg.Rarity, out int minutes) ? minutes : 10;
        }

        public static PensionEgg? EggOf(PensionAccount account, int id) => account.Eggs.FirstOrDefault(e => e.Id == id);

        /// <summary>An egg into the first free place. "ok", or why not.</summary>
        public static string Incubate(PensionAccount account, PensionFile content, int eggId, long now)
        {
            var egg = EggOf(account, eggId);
            if (egg == null) return "oeuf-inconnu";
            if (egg.Slot != 0) return "deja-incube";
            int slots = Slots(account, content);
            var taken = account.Eggs.Where(e => e.Slot != 0).Select(e => e.Slot).ToHashSet();
            int free = Enumerable.Range(1, slots).FirstOrDefault(s => !taken.Contains(s));
            if (free == 0) return "places-pleines";
            egg.Slot = free;
            egg.ReadyAt = now + HatchMinutes(egg, content) * 60L;
            return "ok";
        }

        /// <summary>«Retirer» an egg from its place: it is kept, its time is lost.</summary>
        public static string Withdraw(PensionAccount account, int eggId)
        {
            var egg = EggOf(account, eggId);
            if (egg == null) return "oeuf-inconnu";
            if (egg.Slot == 0) return "pas-incube";
            egg.Slot = 0;
            egg.ReadyAt = 0;
            return "ok";
        }

        /// <summary>The speed-up: so many minutes off an incubating egg. "ok", or why not.</summary>
        public static string SpeedUp(PensionAccount account, PensionFile content, int eggId, long now)
        {
            var egg = EggOf(account, eggId);
            if (egg == null) return "oeuf-inconnu";
            if (egg.Slot == 0) return "pas-incube";
            if (egg.ReadyAt <= now) return "deja-pret";
            egg.ReadyAt = Math.Max(now, egg.ReadyAt - content.SpeedUp.Minutes * 60L);
            return "ok";
        }

        /// <summary>The incubating eggs whose time has come.</summary>
        public static List<PensionEgg> Ready(PensionAccount account, long now)
            => account.Eggs.Where(e => e.Slot != 0 && e.ReadyAt <= now).OrderBy(e => e.Slot).ToList();

        /// <summary>The rarity an egg hatches into: each step up at the file's chance, chained.</summary>
        public static string HatchRarity(string rarity, PensionFile content, Random dice)
        {
            int at = Math.Max(0, Rarities.ToList().FindIndex(r => string.Equals(r, rarity, StringComparison.OrdinalIgnoreCase)));
            while (at < Rarities.Count - 1 && dice.NextDouble() < content.RarityUpChance) at++;
            return Rarities[at];
        }

        /// <summary>A pet of a rarity, drawn from the file's list; 0 when it lists none.</summary>
        public static int DrawPet(string rarity, PensionFile content, Random dice)
        {
            if (!content.Pets.TryGetValue(rarity, out var pool) || pool.Count == 0)
                pool = content.Pets.Values.FirstOrDefault(p => p.Count > 0) ?? new List<int>();
            return pool.Count == 0 ? 0 : pool[dice.Next(pool.Count)];
        }

        /// <summary>A ride of a species and generation, its colour drawn; null when the client has none.</summary>
        public static Breeding.Ride? DrawRide(int species, int generation, Random dice)
        {
            var pool = Breeding.AllRides.Where(r => r.Species == species && r.Generation == generation && r.Item > 0)
                               .OrderBy(r => r.Id).ToList();
            return pool.Count == 0 ? null : pool[dice.Next(pool.Count)];
        }

        /// <summary>What the egg crate holds: a generation-1 mount egg, or a rare or legendary mystery pet egg.</summary>
        public static PensionEgg FromCrate(PensionFile content, IReadOnlyList<int> species, Random dice)
        {
            if (dice.NextDouble() < content.Crate.MountChance && species.Count > 0)
                return new PensionEgg { Kind = KindMount, Species = species[dice.Next(species.Count)], Generation = 1 };
            return new PensionEgg { Kind = KindPet, Rarity = dice.NextDouble() < content.Crate.LegendaryShare ? Legendary : Rare };
        }

        public static IReadOnlyList<int> SpeciesIds(PensionFile content)
        {
            var ids = content.Species.Keys.Select(k => int.TryParse(k, out int v) ? v : 0).Where(v => v > 0).OrderBy(v => v).ToList();
            return ids.Count > 0 ? ids : new[] { 1, 2, 3 };
        }

        /// <summary>The litters a mount of a generation is born with (10: sterile).</summary>
        public static int LittersAtBirth(int generation, PensionFile content)
            => content.Litters.TryGetValue(generation.ToString(), out int n) ? n : generation >= content.MaxGeneration ? 0 : 1;

        public static int LittersLeft(PensionAccount account, long uid, int generation, PensionFile content)
            => account.Litters.TryGetValue(uid, out int left) ? left : LittersAtBirth(generation, content);

        public static long BreedCost(int generation, PensionFile content)
            => content.BreedOrbes.TryGetValue(generation.ToString(), out long cost) ? cost : 1000L * generation;

        /// <summary>
        /// Whether two mounts may breed: two of them, one species, one generation under the last, a
        /// litter left each. "ok", or why not. The Stasis rule: no sex nor gauge to fill.
        /// </summary>
        public static string CanBreed(PensionAccount account, PensionFile content,
                                      (long Uid, Breeding.Ride? Ride) a, (long Uid, Breeding.Ride? Ride) b)
        {
            if (a.Ride == null || b.Ride == null) return "pas-monture";
            if (a.Uid == b.Uid) return "meme-monture";
            if (a.Ride.Species != b.Ride.Species) return "especes";
            if (a.Ride.Generation != b.Ride.Generation) return "generations";
            if (a.Ride.Generation >= content.MaxGeneration || a.Ride.Generation < 1) return "sterile";
            if (LittersLeft(account, a.Uid, a.Ride.Generation, content) <= 0
                || LittersLeft(account, b.Uid, b.Ride.Generation, content) <= 0) return "portees";
            return "ok";
        }

        /// <summary>Two mounts bred: a litter off each, and the egg of the next generation (always).</summary>
        public static PensionEgg Breed(PensionAccount account, PensionFile content, (long Uid, Breeding.Ride Ride) a, (long Uid, Breeding.Ride Ride) b)
        {
            int generation = a.Ride.Generation;
            account.Litters[a.Uid] = LittersLeft(account, a.Uid, generation, content) - 1;
            account.Litters[b.Uid] = LittersLeft(account, b.Uid, generation, content) - 1;
            var egg = new PensionEgg { Id = account.NextEggId(), Kind = KindMount, Species = a.Ride.Species, Generation = generation + 1 };
            account.Eggs.Add(egg);
            return egg;
        }

        /// <summary>
        /// The experience the study room gives an animal from a won fight: the file's percent of it,
        /// doubled under half the character's level, times the animal kind's factor.
        /// </summary>
        public static long StudyExperience(long fightExperience, int animalLevel, int characterLevel, bool mount, PensionFile content)
        {
            if (fightExperience <= 0) return 0;
            var s = content.Study;
            double gain = fightExperience * s.Percent / 100.0;
            if (animalLevel * 2 < characterLevel) gain *= s.LowLevelMultiplier;
            gain *= mount ? s.MountFactor : s.PetFactor;
            return (long)Math.Floor(gain);
        }

        /// <summary>The worn animal's bonus: the file's percent of the fight's experience.</summary>
        public static long EquippedExperience(long fightExperience, bool mount, PensionFile content)
        {
            if (fightExperience <= 0) return 0;
            var s = content.Study;
            return (long)Math.Floor(fightExperience * s.EquippedBonusPercent / 100.0 * (mount ? s.MountFactor : s.PetFactor));
        }

        /// <summary>The dons «Pension» a character has earned and not received: they are marked given.</summary>
        public static List<PensionEgg> GiftsDue(PensionAccount account, PensionFile content, long characterId, int level, Random dice)
        {
            var eggs = new List<PensionEgg>();
            foreach (var gift in content.Gifts.OrderBy(g => g.Level))
            {
                if (level < gift.Level) continue;
                if (!account.Gifts.Add($"{characterId}:{gift.Level}")) continue;
                var species = SpeciesIds(content);
                var egg = gift.Kind == KindPet
                    ? new PensionEgg { Kind = KindPet, Rarity = string.IsNullOrEmpty(gift.Rarity) ? Starter : gift.Rarity }
                    : new PensionEgg { Kind = KindMount, Species = species[dice.Next(species.Count)], Generation = 1 };
                egg.Gift = true;
                egg.Id = account.NextEggId();
                account.Eggs.Add(egg);
                eggs.Add(egg);
            }
            return eggs;
        }

        public static string RefusalText(string reason) => reason switch
        {
            "oeuf-inconnu" => "Cet œuf n'est pas dans votre pension (voir .pension).",
            "deja-incube" => "Cet œuf est déjà en incubation.",
            "pas-incube" => "Cet œuf n'est pas en incubation.",
            "places-pleines" => "Toutes vos places d'incubation sont occupées.",
            "deja-pret" => "Cet œuf est déjà prêt à éclore.",
            "pas-monture" => "Il faut deux montures (dragodinde, muldo ou volkorne) de votre sac.",
            "meme-monture" => "Il faut deux montures différentes.",
            "especes" => "Les deux montures doivent être de la même espèce.",
            "generations" => "Les deux montures doivent être de la même génération.",
            "sterile" => "Une monture de génération 10 est stérile.",
            "portees" => "Une des montures n'a plus de portée.",
            "etude-pleine" => "La salle d'étude est pleine.",
            "deja-etude" => "Cet animal est déjà à l'étude.",
            "pas-etude" => "Cet animal n'est pas à l'étude.",
            "animal-inconnu" => "Aucun familier ni monture de ce nom dans votre inventaire.",
            "places-max" => "Vous avez déjà acheté toutes les places possibles.",
            "jetons" => "Pas assez de jetons Event.",
            "orbes" => "Pas assez d'orbes.",
            "aucune-caisse" => "Vous n'avez aucune caisse d'œufs.",
            _ => reason,
        };

        // ─── Storage ────────────────────────────────────────────────────────────────────

        /// <summary>For the tests: nothing read nor written, everything in memory.</summary>
        internal static bool InMemoryOnly { get; set; }

        private static readonly ConcurrentDictionary<long, PensionAccount> _cache = new();
        private static bool _tableReady;
        private static readonly object _tableLock = new();

        public static PensionAccount Of(long accountId) => _cache.GetOrAdd(accountId, Read);

        internal static void Forget() => _cache.Clear();

        private static PensionAccount Read(long accountId)
        {
            var account = new PensionAccount { AccountId = accountId };
            if (InMemoryOnly || accountId <= 0) return account;
            try
            {
                EnsureTables();
                using var connection = Open();
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT BoughtSlots, Level100, Crates, Gifts FROM PensionAccounts WHERE AccountId = $a;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    if (r.Read())
                    {
                        account.BoughtSlots = r.GetInt32(0);
                        account.Level100 = r.GetInt32(1) != 0;
                        account.Crates = r.GetInt32(2);
                        foreach (var g in r.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries)) account.Gifts.Add(g);
                    }
                }
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT Id, Kind, Species, Generation, Rarity, Gift, Slot, ReadyAt FROM PensionEggs WHERE AccountId = $a ORDER BY Id;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        account.Eggs.Add(new PensionEgg
                        {
                            Id = r.GetInt32(0), Kind = r.GetString(1), Species = r.GetInt32(2), Generation = r.GetInt32(3),
                            Rarity = r.GetString(4), Gift = r.GetInt32(5) != 0, Slot = r.GetInt32(6), ReadyAt = r.GetInt64(7),
                        });
                }
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT Uid FROM PensionStudy WHERE AccountId = $a;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) account.Study.Add(r.GetInt64(0));
                }
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = "SELECT Uid, LittersLeft FROM PensionLitters WHERE AccountId = $a;";
                    c.Parameters.AddWithValue("$a", accountId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) account.Litters[r.GetInt64(0)] = r.GetInt32(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pension] Could not read the pension of account {accountId}: {ex.Message}");
            }
            return account;
        }

        public static void Save(PensionAccount p)
        {
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

                Run("INSERT INTO PensionAccounts (AccountId, BoughtSlots, Level100, Crates, Gifts) VALUES ($a, $b, $l, $c, $g) " +
                    "ON CONFLICT(AccountId) DO UPDATE SET BoughtSlots = excluded.BoughtSlots, Level100 = excluded.Level100, " +
                    "Crates = excluded.Crates, Gifts = excluded.Gifts;",
                    ("$a", p.AccountId), ("$b", p.BoughtSlots), ("$l", p.Level100 ? 1 : 0), ("$c", p.Crates), ("$g", string.Join(",", p.Gifts)));
                Run("DELETE FROM PensionEggs WHERE AccountId = $a;", ("$a", p.AccountId));
                foreach (var e in p.Eggs)
                    Run("INSERT INTO PensionEggs (AccountId, Id, Kind, Species, Generation, Rarity, Gift, Slot, ReadyAt) " +
                        "VALUES ($a, $i, $k, $s, $g, $r, $f, $l, $t);",
                        ("$a", p.AccountId), ("$i", e.Id), ("$k", e.Kind), ("$s", e.Species), ("$g", e.Generation), ("$r", e.Rarity),
                        ("$f", e.Gift ? 1 : 0), ("$l", e.Slot), ("$t", e.ReadyAt));
                Run("DELETE FROM PensionStudy WHERE AccountId = $a;", ("$a", p.AccountId));
                foreach (long uid in p.Study.Distinct())
                    Run("INSERT INTO PensionStudy (AccountId, Uid) VALUES ($a, $u);", ("$a", p.AccountId), ("$u", uid));
                Run("DELETE FROM PensionLitters WHERE AccountId = $a;", ("$a", p.AccountId));
                foreach (var (uid, left) in p.Litters)
                    Run("INSERT INTO PensionLitters (AccountId, Uid, LittersLeft) VALUES ($a, $u, $l);", ("$a", p.AccountId), ("$u", uid), ("$l", left));
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pension] Could not store the pension of account {p.AccountId}: {ex.Message}");
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
                    CREATE TABLE IF NOT EXISTS PensionAccounts (
                        AccountId   INTEGER PRIMARY KEY,
                        BoughtSlots INTEGER NOT NULL DEFAULT 0,
                        Level100    INTEGER NOT NULL DEFAULT 0,
                        Crates      INTEGER NOT NULL DEFAULT 0,
                        Gifts       TEXT    NOT NULL DEFAULT '');
                    CREATE TABLE IF NOT EXISTS PensionEggs (
                        AccountId  INTEGER NOT NULL,
                        Id         INTEGER NOT NULL,
                        Kind       TEXT    NOT NULL,
                        Species    INTEGER NOT NULL DEFAULT 0,
                        Generation INTEGER NOT NULL DEFAULT 0,
                        Rarity     TEXT    NOT NULL DEFAULT '',
                        Gift       INTEGER NOT NULL DEFAULT 0,
                        Slot       INTEGER NOT NULL DEFAULT 0,
                        ReadyAt    INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (AccountId, Id));
                    CREATE TABLE IF NOT EXISTS PensionStudy (
                        AccountId INTEGER NOT NULL,
                        Uid       INTEGER NOT NULL,
                        PRIMARY KEY (AccountId, Uid));
                    CREATE TABLE IF NOT EXISTS PensionLitters (
                        AccountId   INTEGER NOT NULL,
                        Uid         INTEGER NOT NULL,
                        LittersLeft INTEGER NOT NULL,
                        PRIMARY KEY (AccountId, Uid));";
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
