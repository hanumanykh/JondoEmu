using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>content/progression/tiers.json: the tier dungeons, the key prices, the hardcore factors.</summary>
    internal sealed class TierFile
    {
        public int GateFromLevel { get; set; } = 200;
        public List<TierDefinition> Tiers { get; set; } = new();
        public bool EvolutionNeedsHardcore { get; set; }
        public HardcoreFactors Hardcore { get; set; } = new();
        public bool KeysNeverDrop { get; set; } = true;
        public List<int> KeyDropExceptions { get; set; } = new();
        public List<KeyBracket> KeyBrackets { get; set; } = new();

        /// <summary>The tier a dungeon belongs to, or null.</summary>
        internal TierDefinition? TierOf(int dungeonId) => Tiers.FirstOrDefault(t => t.Dungeons.Contains(dungeonId));

        /// <summary>The tier of exactly this level, or null.</summary>
        internal TierDefinition? Tier(int level) => Tiers.FirstOrDefault(t => t.Level == level);

        /// <summary>The bracket a dungeon level falls in, or null past the last one.</summary>
        internal KeyBracket? BracketFor(int level)
            => KeyBrackets.FirstOrDefault(b => level >= b.Min && level <= b.Max)
               ?? (level > 0 && KeyBrackets.Count > 0 && level > KeyBrackets.Max(b => b.Max) ? KeyBrackets.MaxBy(b => b.Max) : null);
    }

    internal sealed class TierDefinition
    {
        public int Level { get; set; }
        public List<int> Dungeons { get; set; } = new();
    }

    internal sealed class HardcoreFactors
    {
        public double Life { get; set; } = 3;
        public double Damage { get; set; } = 2.25;
        public double Drop { get; set; } = 2;
    }

    internal sealed class KeyBracket
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public long Price { get; set; }
        public string Label => $"[{Min}-{Max}]";
    }

    /// <summary>Where a character's tier-dungeon completions and hardcore preference are kept.</summary>
    internal interface ITierStore
    {
        /// <summary>The dungeons this character beat the boss of: (dungeon, ascensions at the time, hardcore).</summary>
        IReadOnlyCollection<(int Dungeon, int Ascensions, bool Hardcore)> Completions(long characterId);
        void AddCompletion(long characterId, int dungeonId, int ascensions, bool hardcore);
        bool HardcoreWanted(long characterId);
        void SetHardcoreWanted(long characterId, bool on);
    }

    /// <summary>
    /// The Stasis tiers ("paliers") from level 200 to 300, their dungeons, the hardcore mode of the
    /// evolved characters and the dungeon keys bought for kamas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE GATE. From level 200 the experience stops at each tier's door -- 225, 250, 275, 300 --
    /// until every dungeon of the tier below is done, in any order (spec §8). What is beyond is
    /// LOST, not stored: the spec says «L'XP au-delà est perdue, jamais retirée», so a character
    /// sits at the floor of the next tier and nothing is banked for later; and nothing is taken
    /// back either -- a character already past a gate he has not opened (the server had no gates
    /// before) keeps his level and simply earns nothing more until the dungeons are done. The
    /// lowest unopened tier is the one that counts, whatever the level: a level 190 character with
    /// a huge gain stops at 225 just the same. Tier 300 does not gate (300 is the cap), but its
    /// nine dungeons are the Evolution's condition (<see cref="Ascension.TierDungeonsDone"/>).
    /// The hook is <see cref="LimitExperience"/>, called once from
    /// CharacterProgression.AddExperience after the catch-up multiplier.
    /// </para>
    /// <para>
    /// COMPLETIONS are the boss rooms won (<see cref="OnFightEnded"/>, beside
    /// DungeonHandler.RecordFightProgress, for the master and each of his heroes). Each row keeps
    /// the character's Ascension count at the time and whether the fight was hardcore, and that is
    /// how the resets need no code: «Ascension remet les paliers à zéro» -- rows from an earlier
    /// Ascension count no more --, and «perso évolué : validation uniquement en hardcore» -- an
    /// evolved character counts only hardcore rows, which only an evolved character can make.
    /// </para>
    /// <para>
    /// HARDCORE (spec §11): an evolved leader who typed «.hardcore on» fights his dungeon rooms
    /// with monsters at life x3 (the server setting's own HardcoreStatsFactor), damage x2.25 (all
    /// damages characteristic 150 raised by 125) and loot x2. Only the monsters on the board when
    /// the fight is created are made harder; summons and later waves are as usual. Stasis allows it
    /// only through the dungeon search; here any dungeon fight of such a leader is hardcore.
    /// </para>
    /// <para>
    /// KEYS (spec §11): none drops (<see cref="AdjustLoot"/> takes every item a dungeon door asks
    /// for out of fight loot, the keyring excepted, switchable in tiers.json); one free entry per
    /// dungeon per character per week, reset Tuesday 00:00, is used before any key (the existing
    /// <see cref="DungeonKeyring"/> week, now open to every keyed dungeon and without the keyring);
    /// «.cle» sells the dungeon's own key at its bracket's price -- this client has one key item
    /// per dungeon, not one per bracket, so a bracket is a price, not an item.
    /// </para>
    /// </remarks>
    internal static class Tiers
    {
        private static TierFile? _current;
        private static readonly object _loadLock = new();

        /// <summary>What is in force: content/progression/tiers.json on first use, or what a test installed.</summary>
        internal static TierFile Current
        {
            get
            {
                if (_current != null) return _current;
                lock (_loadLock) return _current ??= Load(Jondo.Unity.Launcher.Paths.ContentFile("progression"));
            }
            set => _current = value;
        }

        /// <summary>The file of a directory, or empty rules (no gate, no prices) when it is missing or broken.</summary>
        internal static TierFile Load(string directory)
        {
            string path = Path.Combine(directory, "tiers.json");
            try
            {
                if (File.Exists(path)) return ProgressionContent.Parse<TierFile>(File.ReadAllText(path)) ?? new TierFile();
                Console.WriteLine($"[Tiers] {path} is missing: no tier gates, no key prices.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiers] {path} could not be read: {ex.Message}");
            }
            return new TierFile();
        }

        internal static ITierStore Backend { get; set; } = new SqliteTierStore();

        /// <summary>At start-up: the file read, and the Evolution told when the tier-300 dungeons are done.</summary>
        internal static void Load()
        {
            var file = Current;
            Ascension.TierDungeonsDone = id => TierDone(id, Current.Tiers.Count == 0 ? 0 : Current.Tiers.Max(t => t.Level), Current.EvolutionNeedsHardcore);
            Console.WriteLine($"[Tiers] {file.Tiers.Count} tier(s): " +
                              string.Join(", ", file.Tiers.Select(t => $"{t.Level}={t.Dungeons.Count}")) +
                              $"; {file.KeyBrackets.Count} key bracket(s).");
        }

        // ─── Completions ────────────────────────────────────────────────────────────────

        /// <summary>The dungeons of a tier that count for this character now (see the remarks).</summary>
        internal static HashSet<int> DoneDungeons(long characterId, bool forceHardcore = false)
        {
            var done = new HashSet<int>();
            if (characterId <= 0) return done;
            var profile = ProgressionStore.Of(characterId);
            int ascensions;
            bool evolved;
            lock (profile.Gate) { ascensions = profile.Ascensions; evolved = profile.Evolved; }
            foreach (var (dungeon, asc, hardcore) in Backend.Completions(characterId))
                if (asc == ascensions && (hardcore || !(evolved || forceHardcore))) done.Add(dungeon);
            return done;
        }

        /// <summary>Whether every dungeon of the tier of <paramref name="level"/> is done. A tier not in the file is done.</summary>
        internal static bool TierDone(long characterId, int level, bool hardcore = false)
        {
            var tier = Current.Tier(level);
            if (tier == null || tier.Dungeons.Count == 0) return true;
            var done = DoneDungeons(characterId, hardcore);
            return tier.Dungeons.All(done.Contains);
        }

        /// <summary>The dungeons of a tier still to do.</summary>
        internal static List<int> Remaining(TierDefinition tier, ISet<int> done) => tier.Dungeons.Where(d => !done.Contains(d)).ToList();

        // ─── The gate ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The experience a gain may add, pure: the lowest gating tier not done whose next floor the
        /// total would reach caps it at that floor. Tiers below <paramref name="gateFrom"/> and the
        /// last tier (the cap) do not gate. Never negative: a total already past the cap adds nothing.
        /// </summary>
        internal static long Allowed(long experience, long gain, IReadOnlyList<int> tierLevels, int gateFrom,
                                     Func<int, bool> tierDone, Func<int, long> floor)
        {
            if (gain <= 0) return 0;
            long total = gain > long.MaxValue - experience ? long.MaxValue : experience + gain;
            var levels = tierLevels.OrderBy(l => l).ToList();
            for (int i = 0; i + 1 < levels.Count; i++)
            {
                int tier = levels[i];
                if (tier < gateFrom) continue;
                long next = floor(levels[i + 1]);
                if (total < next) break;
                if (tierDone(tier)) continue;
                return Math.Max(0, next - experience);
            }
            return gain;
        }

        /// <summary>Which tier closed the gate, for the message: the lowest gating tier not done that the total reaches.</summary>
        internal static int GatingTier(long total, IReadOnlyList<int> tierLevels, int gateFrom, Func<int, bool> tierDone, Func<int, long> floor)
        {
            var levels = tierLevels.OrderBy(l => l).ToList();
            for (int i = 0; i + 1 < levels.Count; i++)
            {
                if (levels[i] < gateFrom) continue;
                if (total < floor(levels[i + 1])) return 0;
                if (!tierDone(levels[i])) return levels[i];
            }
            return 0;
        }

        private static readonly ConcurrentDictionary<long, int> _told = new();

        /// <summary>
        /// The hook CharacterProgression.AddExperience calls: <paramref name="experience"/> cut to
        /// what the tier gates let through, and the player told once per tier which dungeons remain.
        /// A state without a character (bots, tests) passes untouched; so does a server without tiers.
        /// </summary>
        internal static long LimitExperience(SessionState state, long experience)
        {
            if (state == null || state.CharacterId <= 0 || experience <= 0) return experience;
            var file = Current;
            if (file.Tiers.Count < 2) return experience;
            try
            {
                var levels = file.Tiers.Select(t => t.Level).ToList();
                HashSet<int>? done = null;
                bool Done(int level)
                {
                    var tier = file.Tier(level);
                    if (tier == null) return true;
                    done ??= DoneDungeons(state.CharacterId);
                    return tier.Dungeons.All(done.Contains);
                }
                long allowed = Allowed(state.Experience, experience, levels, file.GateFromLevel, Done, ExperienceTable.LevelFloor);
                if (allowed < experience)
                {
                    long total = experience > long.MaxValue - state.Experience ? long.MaxValue : state.Experience + experience;
                    int gate = GatingTier(total, levels, file.GateFromLevel, Done, ExperienceTable.LevelFloor);
                    if (gate != 0 && _told.TryGetValue(state.CharacterId, out int told) && told == gate) return allowed;
                    if (gate != 0)
                    {
                        _told[state.CharacterId] = gate;
                        TellGate(state.CharacterId, gate, done ?? new HashSet<int>());
                    }
                }
                return allowed;
            }
            catch (Exception ex)
            {
                // Open on a failure, as the keyring: a broken store must not freeze everybody's experience.
                Console.WriteLine($"[Tiers] Gate check failed for {state.CharacterId}: {ex.Message}");
                return experience;
            }
        }

        /// <summary>The gate's French message, with the dungeons still to do.</summary>
        internal static string GateText(int tier, IEnumerable<string> remaining)
            => $"Palier {tier} : votre expérience est bloquée au niveau {tier + 25} tant que les donjons du palier ne sont pas tous terminés " +
               $"(l'expérience au-delà est perdue). Restants : {string.Join(", ", remaining)}. Tapez .paliers pour le détail.";

        private static void TellGate(long characterId, int gate, ISet<int> done)
        {
            var tier = Current.Tier(gate);
            if (tier == null) return;
            var names = Remaining(tier, done).Select(DungeonName);
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            string text = GateText(gate, names);
            _ = session.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)))
                       .ContinueWith(t => Console.WriteLine($"[Tiers] Gate notice failed: {t.Exception?.GetBaseException().Message}"),
                                     System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }

        internal static string DungeonName(int id) => DungeonManager.Get(id)?.Name ?? $"donjon {id}";

        // ─── Fights ─────────────────────────────────────────────────────────────────────

        private static readonly ConditionalWeakTable<FightInstance, object> _hardcoreFights = new();

        /// <summary>Whether a fight was made hardcore when it was created.</summary>
        internal static bool IsHardcore(FightInstance fight) => fight != null && _hardcoreFights.TryGetValue(fight, out _);

        /// <summary>Whether this character's dungeon fights are hardcore: evolved and asked for it.</summary>
        internal static bool WantsHardcore(long characterId)
        {
            if (characterId <= 0 || !Ascension.IsEvolved(characterId)) return false;
            try { return Backend.HardcoreWanted(characterId); }
            catch (Exception ex) { Console.WriteLine($"[Tiers] Could not read the hardcore choice: {ex.Message}"); return false; }
        }

        /// <summary>
        /// At a fight's creation (FightHandler, with the other OnFightCreated): a dungeon room's fight
        /// started by an evolved leader in hardcore gets its monsters at life x3 and damage x2.25.
        /// </summary>
        internal static void OnFightCreated(FightInstance fight, long leaderId)
        {
            // An Éveil challenge's boss, scaled to its Dofus level (Eveil.cs).
            Eveil.OnFightCreated(fight);
            if (fight == null || !DungeonManager.IsRoom(fight.RoleplayMapId) || !WantsHardcore(leaderId)) return;
            var factors = Current.Hardcore;
            _hardcoreFights.AddOrUpdate(fight, new object());
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster))
            {
                // The server-wide setting already triples them: not twice.
                if (!ServerSettings.Current.Hardcore)
                {
                    monster.MaxHP = (int)Math.Min(int.MaxValue, Math.Round(monster.MaxHP * factors.Life));
                    monster.CurrentHP = monster.MaxHP;
                }
                int extra = (int)Math.Round((factors.Damage - 1.0) * 100);
                monster.Otras[EffectEngine.TodosLosDanos] = monster.Otra(EffectEngine.TodosLosDanos) + extra;
            }
            Console.WriteLine($"[Tiers] Hardcore fight on {fight.RoleplayMapId}: {fight.Rojo.Count} monster(s) x{factors.Life} life, x{factors.Damage} damage.");
        }

        /// <summary>
        /// At a fight's end, for one character (the master or a hero): a won boss room of a tier
        /// dungeon is a completion, hardcore when the fight was.
        /// </summary>
        internal static void OnFightEnded(FightInstance fight, long characterId, bool won)
        {
            if (!won || fight == null || characterId <= 0) return;
            try
            {
                var dungeon = DungeonManager.OfRoom(fight.RoleplayMapId);
                if (dungeon == null) return;
                // A boss chosen from another dungeon's transition room ends that run.
                var credited = DungeonManager.ChosenFrom(dungeon) ?? dungeon;
                bool boss = credited != dungeon || DungeonManager.NextRoom(dungeon, fight.RoleplayMapId) == 0
                            || fight.RoleplayMapId == dungeon.LastRoom;
                if (!boss) return;
                // The Éveil routes count the same boss rooms, hardcore ones only (Eveil.cs).
                Eveil.OnDungeonWon(characterId, credited.Id, IsHardcore(fight));
                RecordCompletion(characterId, credited.Id, IsHardcore(fight));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiers] Could not record the dungeon for {characterId}: {ex.Message}");
            }
        }

        /// <summary>Notes a boss beaten, if the dungeon is a tier's.</summary>
        internal static void RecordCompletion(long characterId, int dungeonId, bool hardcore)
        {
            if (Current.TierOf(dungeonId) == null) return;
            var profile = ProgressionStore.Of(characterId);
            int ascensions;
            lock (profile.Gate) ascensions = profile.Ascensions;
            Backend.AddCompletion(characterId, dungeonId, ascensions, hardcore);
            _told.TryRemove(characterId, out _);
            Console.WriteLine($"[Tiers] {characterId} completes {DungeonName(dungeonId)}{(hardcore ? " in hardcore" : "")}.");
        }

        // ─── Loot ───────────────────────────────────────────────────────────────────────

        /// <summary>The items dungeon doors ask for, keyring excepted: never dropped when keysNeverDrop.</summary>
        internal static HashSet<int> KeyItems()
        {
            var keys = new HashSet<int>();
            foreach (var dungeon in DungeonManager.All.Values)
                foreach (var (item, _) in dungeon.Required)
                    if (item != Handlers.DungeonHandler.Keyring) keys.Add(item);
            keys.ExceptWith(Current.KeyDropExceptions);
            return keys;
        }

        private static HashSet<int>? _keys;

        /// <summary>
        /// The loot of a fight, adjusted: no dungeon key, and in a hardcore fight twice each item
        /// (the server's coin excepted, which is the fight's pay, not a drop).
        /// </summary>
        internal static void AdjustLoot(FightInstance fight, Dictionary<int, int> loot)
        {
            if (loot == null || loot.Count == 0) return;
            var file = Current;
            if (file.KeysNeverDrop)
            {
                _keys ??= KeyItems();
                foreach (int key in loot.Keys.Where(_keys.Contains).ToList()) loot.Remove(key);
            }
            if (IsHardcore(fight) && file.Hardcore.Drop > 1)
                foreach (int item in loot.Keys.Where(i => i != JondoCoin.TemplateId).ToList())
                    loot[item] = (int)Math.Min(int.MaxValue, Math.Round(loot[item] * file.Hardcore.Drop));
        }

        // ─── Keys ───────────────────────────────────────────────────────────────────────

        /// <summary>The level a dungeon's key is priced at: its tier's when it has one, else its optimal level.</summary>
        internal static int KeyLevel(DungeonManager.Dungeon dungeon)
            => Current.TierOf(dungeon.Id)?.Level ?? Math.Max(dungeon.OptimalLevel, dungeon.MinLevel);

        /// <summary>A dungeon's key price, or -1 when it has no key or no bracket.</summary>
        internal static long KeyPrice(DungeonManager.Dungeon dungeon)
        {
            if (KeyItem(dungeon) == 0) return -1;
            return Current.BracketFor(KeyLevel(dungeon))?.Price ?? -1;
        }

        /// <summary>The dungeon's own key item, or 0 when its door asks nothing.</summary>
        internal static int KeyItem(DungeonManager.Dungeon dungeon)
            => dungeon.Required.Select(r => r.Item).FirstOrDefault(i => i != Handlers.DungeonHandler.Keyring);

        /// <summary>
        /// Whether the weekly free entry opens this door for the character: a keyed dungeon whose
        /// entry of this week (Tuesday to Monday) is not spent. Used before any key (spec §11).
        /// </summary>
        internal static bool FreeEntryOpens(DungeonManager.Dungeon dungeon, long characterId, DateTime now)
            => dungeon.Required.Count > 0 && characterId > 0 && DungeonKeyring.FreeEntryLeft(characterId, dungeon.Id, now);

        /// <summary>The dungeon a player means by a name or an id: exact id, then accent-free name match.</summary>
        internal static DungeonManager.Dungeon? FindDungeon(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return null;
            if (int.TryParse(query, out int id)) return DungeonManager.Get(id);
            string q = Fold(query);
            var candidates = DungeonManager.All.Values.Where(d => d.BaseId == 0 && d.Rooms.Count > 0 && d.Required.Count > 0).ToList();
            return candidates.FirstOrDefault(d => Fold(d.Name) == q)
                   ?? candidates.Where(d => Fold(d.Name).Contains(q)).OrderBy(d => d.Name.Length).FirstOrDefault();
        }

        internal static string Fold(string text)
        {
            var normalized = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (char c in normalized)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }
    }

    /// <summary>The tier tables in the world database, created on first use.</summary>
    internal sealed class SqliteTierStore : ITierStore
    {
        private static bool _ensured;
        private static readonly object _ensureLock = new();

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            if (_ensured) return connection;
            lock (_ensureLock)
            {
                if (_ensured) return connection;
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterTierDungeons (
                        CharacterId INTEGER NOT NULL,
                        DungeonId   INTEGER NOT NULL,
                        Ascensions  INTEGER NOT NULL,
                        Hardcore    INTEGER NOT NULL,
                        CompletedAt TEXT NOT NULL,
                        PRIMARY KEY (CharacterId, DungeonId, Ascensions, Hardcore)
                    );
                    CREATE TABLE IF NOT EXISTS CharacterHardcore (
                        CharacterId INTEGER NOT NULL PRIMARY KEY,
                        Enabled     INTEGER NOT NULL
                    );";
                command.ExecuteNonQuery();
                _ensured = true;
            }
            return connection;
        }

        public IReadOnlyCollection<(int Dungeon, int Ascensions, bool Hardcore)> Completions(long characterId)
        {
            var list = new List<(int, int, bool)>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DungeonId, Ascensions, Hardcore FROM CharacterTierDungeons WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0));
            return list;
        }

        public void AddCompletion(long characterId, int dungeonId, int ascensions, bool hardcore)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO CharacterTierDungeons (CharacterId, DungeonId, Ascensions, Hardcore, CompletedAt) " +
                                  "VALUES ($c, $d, $a, $h, $t);";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$d", dungeonId);
            command.Parameters.AddWithValue("$a", ascensions);
            command.Parameters.AddWithValue("$h", hardcore ? 1 : 0);
            command.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.ExecuteNonQuery();
        }

        public bool HardcoreWanted(long characterId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Enabled FROM CharacterHardcore WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", characterId);
            return command.ExecuteScalar() is long on && on != 0;
        }

        public void SetHardcoreWanted(long characterId, bool on)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CharacterHardcore (CharacterId, Enabled) VALUES ($c, $e) " +
                                  "ON CONFLICT (CharacterId) DO UPDATE SET Enabled = $e;";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$e", on ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The tier store in memory, for the tests.</summary>
    internal sealed class MemoryTierStore : ITierStore
    {
        private readonly ConcurrentDictionary<long, List<(int, int, bool)>> _rows = new();
        private readonly ConcurrentDictionary<long, bool> _hardcore = new();

        public IReadOnlyCollection<(int Dungeon, int Ascensions, bool Hardcore)> Completions(long characterId)
        {
            if (!_rows.TryGetValue(characterId, out var list)) return Array.Empty<(int, int, bool)>();
            lock (list) return list.ToList();
        }

        public void AddCompletion(long characterId, int dungeonId, int ascensions, bool hardcore)
        {
            var list = _rows.GetOrAdd(characterId, _ => new());
            lock (list) if (!list.Contains((dungeonId, ascensions, hardcore))) list.Add((dungeonId, ascensions, hardcore));
        }

        public bool HardcoreWanted(long characterId) => _hardcore.TryGetValue(characterId, out bool on) && on;
        public void SetHardcoreWanted(long characterId, bool on) => _hardcore[characterId] = on;
    }
}
