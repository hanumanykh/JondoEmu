using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>A don kept at a level: the card, and for the characteristic card the characteristic and how much.</summary>
    internal sealed class ChosenDon
    {
        public int Level { get; set; }
        public string Card { get; set; } = "";
        /// <summary>The characteristic the characteristic card went to (Stat ids), 0 for any other card.</summary>
        public int Stat { get; set; }
        /// <summary>What the characteristic card gave, fixed when it was taken; 0 for any other card.</summary>
        public int Amount { get; set; }
    }

    /// <summary>An offer waiting for its pick: the level that earned it, its kind and its cards.</summary>
    internal sealed class DonOffer
    {
        public int Level { get; set; }
        public string Kind { get; set; } = "";
        public List<string> Cards { get; set; } = new();
        public bool Rerolled { get; set; }
    }

    /// <summary>
    /// Everything the Stasis progression remembers about one character: Éclats, Ascensions, the
    /// best level ever reached, the evolved flag, the dons, the offers waiting and the research.
    /// </summary>
    /// <remarks>
    /// One object per character, cached, and guarded by its own lock (<see cref="Gate"/>): an offer
    /// is queued from a fight's end while the window may be picking one from the control API's
    /// thread. Every change ends with <see cref="ProgressionStore.Save"/>.
    /// </remarks>
    internal sealed class ProgressionProfile
    {
        public long CharacterId { get; init; }
        public long Eclats { get; set; }
        public int Ascensions { get; set; }
        public int BestLevel { get; set; }
        public bool Evolved { get; set; }
        /// <summary>Free rerolls spent in this Ascension cycle.</summary>
        public int RerollsUsed { get; set; }
        /// <summary>Characteristic re-picks (refontes) spent in this Ascension cycle.</summary>
        public int RepicksUsed { get; set; }
        public List<ChosenDon> Dons { get; } = new();
        public List<DonOffer> Pending { get; } = new();
        public Dictionary<string, int> Research { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal object Gate { get; } = new();

        public int RankOf(string research) => Research.TryGetValue(research, out int rank) ? rank : 0;
    }

    /// <summary>Where profiles are read from and written to: world.db live, memory in the tests.</summary>
    internal interface IProgressionStore
    {
        ProgressionProfile Load(long characterId);
        void Save(ProgressionProfile profile);
    }

    /// <summary>
    /// The profiles, cached, over an <see cref="IProgressionStore"/>. Live it is world.db's four
    /// tables (<see cref="SqliteProgressionStore"/>); a test installs a <see cref="MemoryProgressionStore"/>.
    /// </summary>
    internal static class ProgressionStore
    {
        private static IProgressionStore _backend = new SqliteProgressionStore();
        private static readonly ConcurrentDictionary<long, ProgressionProfile> _cache = new();

        /// <summary>The backend; setting it forgets every cached profile.</summary>
        internal static IProgressionStore Backend
        {
            get => _backend;
            set
            {
                _backend = value;
                _cache.Clear();
            }
        }

        internal static ProgressionProfile Of(long characterId) => _cache.GetOrAdd(characterId, id => _backend.Load(id));

        /// <summary>The profile only if it is cached or the character has one: for the hot paths (stats every fight).</summary>
        internal static ProgressionProfile? Peek(long characterId) => _cache.TryGetValue(characterId, out var p) ? p : null;

        internal static void Save(ProgressionProfile profile) => _backend.Save(profile);

        internal static void Forget(long characterId) => _cache.TryRemove(characterId, out _);
    }

    /// <summary>Profiles in memory only, for the tests.</summary>
    internal sealed class MemoryProgressionStore : IProgressionStore
    {
        public int Saves { get; private set; }
        public ProgressionProfile Load(long characterId) => new() { CharacterId = characterId };
        public void Save(ProgressionProfile profile) => Saves++;
    }

    /// <summary>
    /// world.db's side: StasisCharacters (one row of numbers per character), StasisDons (the don
    /// of each level), StasisOffers (the offers waiting, their cards as a comma list) and
    /// StasisResearch (a rank per research). Created on first use, as CharacterAlterations is.
    /// </summary>
    /// <remarks>
    /// A save rewrites the character's rows in one transaction: a profile has at most a few hundred
    /// dons, and the whole-character rewrite keeps the store free of diffing bugs (a removed don
    /// after an Evolution, a reroll's new cards) for a cost nobody can measure.
    /// </remarks>
    internal sealed class SqliteProgressionStore : IProgressionStore
    {
        private static bool _tableReady;
        private static readonly object _tableLock = new();

        public ProgressionProfile Load(long characterId)
        {
            var profile = new ProgressionProfile { CharacterId = characterId };
            try
            {
                EnsureTables();
                using var connection = Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Eclats, Ascensions, BestLevel, Evolved, RerollsUsed, RepicksUsed " +
                                          "FROM StasisCharacters WHERE CharacterId = $c;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        profile.Eclats = reader.GetInt64(0);
                        profile.Ascensions = reader.GetInt32(1);
                        profile.BestLevel = reader.GetInt32(2);
                        profile.Evolved = reader.GetInt32(3) != 0;
                        profile.RerollsUsed = reader.GetInt32(4);
                        profile.RepicksUsed = reader.GetInt32(5);
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Level, Card, Stat, Amount FROM StasisDons WHERE CharacterId = $c ORDER BY Level;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        profile.Dons.Add(new ChosenDon
                        {
                            Level = reader.GetInt32(0), Card = reader.GetString(1), Stat = reader.GetInt32(2), Amount = reader.GetInt32(3),
                        });
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Level, Kind, Cards, Rerolled FROM StasisOffers WHERE CharacterId = $c ORDER BY Level;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        profile.Pending.Add(new DonOffer
                        {
                            Level = reader.GetInt32(0), Kind = reader.GetString(1),
                            Cards = reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                            Rerolled = reader.GetInt32(3) != 0,
                        });
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT ResearchId, Rank FROM StasisResearch WHERE CharacterId = $c;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) profile.Research[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Progression] Could not read the progression of {characterId}: {ex.Message}");
            }
            return profile;
        }

        public void Save(ProgressionProfile p)
        {
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

                Run("INSERT INTO StasisCharacters (CharacterId, Eclats, Ascensions, BestLevel, Evolved, RerollsUsed, RepicksUsed) " +
                    "VALUES ($c, $e, $a, $b, $v, $r, $p) ON CONFLICT(CharacterId) DO UPDATE SET Eclats = excluded.Eclats, " +
                    "Ascensions = excluded.Ascensions, BestLevel = excluded.BestLevel, Evolved = excluded.Evolved, " +
                    "RerollsUsed = excluded.RerollsUsed, RepicksUsed = excluded.RepicksUsed;",
                    ("$c", p.CharacterId), ("$e", p.Eclats), ("$a", p.Ascensions), ("$b", p.BestLevel),
                    ("$v", p.Evolved ? 1 : 0), ("$r", p.RerollsUsed), ("$p", p.RepicksUsed));
                Run("DELETE FROM StasisDons WHERE CharacterId = $c;", ("$c", p.CharacterId));
                foreach (var don in p.Dons)
                    Run("INSERT INTO StasisDons (CharacterId, Level, Card, Stat, Amount) VALUES ($c, $l, $k, $s, $a);",
                        ("$c", p.CharacterId), ("$l", don.Level), ("$k", don.Card), ("$s", don.Stat), ("$a", don.Amount));
                Run("DELETE FROM StasisOffers WHERE CharacterId = $c;", ("$c", p.CharacterId));
                foreach (var offer in p.Pending)
                    Run("INSERT INTO StasisOffers (CharacterId, Level, Kind, Cards, Rerolled) VALUES ($c, $l, $k, $x, $r);",
                        ("$c", p.CharacterId), ("$l", offer.Level), ("$k", offer.Kind), ("$x", string.Join(",", offer.Cards)),
                        ("$r", offer.Rerolled ? 1 : 0));
                Run("DELETE FROM StasisResearch WHERE CharacterId = $c;", ("$c", p.CharacterId));
                foreach (var (id, rank) in p.Research)
                    Run("INSERT INTO StasisResearch (CharacterId, ResearchId, Rank) VALUES ($c, $i, $r);",
                        ("$c", p.CharacterId), ("$i", id), ("$r", rank));
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Progression] Could not store the progression of {p.CharacterId}: {ex.Message}");
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
                    CREATE TABLE IF NOT EXISTS StasisCharacters (
                        CharacterId INTEGER PRIMARY KEY,
                        Eclats      INTEGER NOT NULL DEFAULT 0,
                        Ascensions  INTEGER NOT NULL DEFAULT 0,
                        BestLevel   INTEGER NOT NULL DEFAULT 0,
                        Evolved     INTEGER NOT NULL DEFAULT 0,
                        RerollsUsed INTEGER NOT NULL DEFAULT 0,
                        RepicksUsed INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS StasisDons (
                        CharacterId INTEGER NOT NULL,
                        Level       INTEGER NOT NULL,
                        Card        TEXT    NOT NULL,
                        Stat        INTEGER NOT NULL DEFAULT 0,
                        Amount      INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, Level));
                    CREATE TABLE IF NOT EXISTS StasisOffers (
                        CharacterId INTEGER NOT NULL,
                        Level       INTEGER NOT NULL,
                        Kind        TEXT    NOT NULL,
                        Cards       TEXT    NOT NULL,
                        Rerolled    INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, Level));
                    CREATE TABLE IF NOT EXISTS StasisResearch (
                        CharacterId INTEGER NOT NULL,
                        ResearchId  TEXT    NOT NULL,
                        Rank        INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, ResearchId));";
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
