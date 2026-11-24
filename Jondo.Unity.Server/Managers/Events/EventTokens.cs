using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The «Jetons Event» of the Stasis rules (docs/stasis-gameplay-spec.md §16): a per-character
    /// purse the daily events (<see cref="StasisEvents"/>) and the Fissure (<see cref="Fissure"/>)
    /// fill, and the Gardien de la Fissure's shop empties (CommandHandler.Events.cs, «.jetons»).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two tables of the world database, created here when missing: <c>EventTokens</c>, one row per
    /// character with its balance, and <c>EventRunes</c>, one row per item uid with the spell damage
    /// percent the «Rune Do Sort cumulable» put on it. Both are read once and kept in memory, every
    /// change written through at once -- the same shape as Stasis's ItemStasisTiers.
    /// </para>
    /// <para>
    /// THE RUNE. The spec's rune adds +1 % spell damage at 100 % success, up to 4 % per piece. The
    /// client has no effect line for a flat «% spell damage» that the fight engine reads off an
    /// item, and writing an invented line into an item would show garbage in its tooltip. So the
    /// bonus is kept server-side against the item's uid, capped at <see cref="RuneCapPerItem"/>, and
    /// the fight reads it when it is built (<see cref="RuneBonusOf"/>): the sum over the worn pieces
    /// is added to the fighter's DamageDealtPercent. Simplified, and said so: DamageDealtPercent
    /// counts weapon blows too, not only spells; and an item re-created under a new uid (the bank,
    /// a trade, the marketplace) leaves its rune behind, the uid being the only key there is.
    /// </para>
    /// </remarks>
    public static class EventTokens
    {
        /// <summary>What one rune adds, in percent.</summary>
        public const int RunePercent = 1;

        /// <summary>The most one piece may carry, in percent: «jusqu'à 4 % par pièce».</summary>
        public const int RuneCapPerItem = 4;

        private static readonly ConcurrentDictionary<long, long> _balances = new();
        private static readonly ConcurrentDictionary<long, int> _runes = new();
        private static readonly object Gate = new();
        private static bool _loaded;

        /// <summary>For the tests: no database, everything in memory.</summary>
        internal static bool InMemoryOnly { get; set; }

        // ─── Pure rules ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What a rune leaves on a piece that has <paramref name="current"/> percent: one more,
        /// never past the cap. Null when the piece is already full and the rune would be wasted.
        /// </summary>
        public static int? AfterRune(int current)
            => current >= RuneCapPerItem ? null : Math.Min(RuneCapPerItem, Math.Max(0, current) + RunePercent);

        /// <summary>Whether a purse can pay a price, and what is left: (false, balance) when it cannot.</summary>
        public static (bool Paid, long Left) Spend(long balance, long price)
            => price < 0 || balance < price ? (false, balance) : (true, balance - price);

        // ─── The purse ──────────────────────────────────────────────────────────────────────

        /// <summary>A character's balance.</summary>
        public static long Balance(long characterId)
        {
            EnsureLoaded();
            return _balances.TryGetValue(characterId, out long v) ? v : 0;
        }

        /// <summary>Tokens given (positive) to a character, connected or not. Returns the new balance.</summary>
        public static long Add(long characterId, long amount)
        {
            if (characterId <= 0 || amount == 0) return Balance(characterId);
            EnsureLoaded();
            long now = _balances.AddOrUpdate(characterId, Math.Max(0, amount), (_, old) => Math.Max(0, old + amount));
            Write(characterId, now);
            return now;
        }

        /// <summary>Takes a price out of a character's purse; false, and nothing taken, when it is short.</summary>
        public static bool TrySpend(long characterId, long price)
        {
            EnsureLoaded();
            lock (Gate)
            {
                var (paid, left) = Spend(Balance(characterId), price);
                if (!paid) return false;
                _balances[characterId] = left;
                Write(characterId, left);
                return true;
            }
        }

        // ─── The rune ───────────────────────────────────────────────────────────────────────

        /// <summary>The spell damage percent the runes put on one item uid.</summary>
        public static int RuneOf(long uid)
        {
            EnsureLoaded();
            return _runes.TryGetValue(uid, out int v) ? v : 0;
        }

        /// <summary>One rune on an item: its new percent, or null when it was already at the cap.</summary>
        public static int? ApplyRune(long uid)
        {
            EnsureLoaded();
            lock (Gate)
            {
                var next = AfterRune(RuneOf(uid));
                if (next == null) return null;
                _runes[uid] = next.Value;
                WriteRune(uid, next.Value);
                return next;
            }
        }

        /// <summary>The bonus of the runes on the pieces worn: the sum over their uids.</summary>
        public static int RuneBonusOf(IEnumerable<long> wornUids) => wornUids.Distinct().Sum(RuneOf);

        // ─── Storage ────────────────────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                if (InMemoryOnly) return;
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    var create = connection.CreateCommand();
                    create.CommandText =
                        "CREATE TABLE IF NOT EXISTS EventTokens (CharacterId INTEGER PRIMARY KEY, Tokens INTEGER NOT NULL);" +
                        "CREATE TABLE IF NOT EXISTS EventRunes (Uid INTEGER PRIMARY KEY, Percent INTEGER NOT NULL);";
                    create.ExecuteNonQuery();
                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT CharacterId, Tokens FROM EventTokens;";
                    using (var reader = read.ExecuteReader())
                        while (reader.Read()) _balances[reader.GetInt64(0)] = reader.GetInt64(1);
                    var runes = connection.CreateCommand();
                    runes.CommandText = "SELECT Uid, Percent FROM EventRunes;";
                    using (var reader = runes.ExecuteReader())
                        while (reader.Read()) _runes[reader.GetInt64(0)] = reader.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EventTokens] The purses could not be read: {ex.Message}");
                }
            }
        }

        private static void Write(long characterId, long tokens)
            => Exec("INSERT OR REPLACE INTO EventTokens (CharacterId, Tokens) VALUES ($a, $b);", characterId, tokens);

        private static void WriteRune(long uid, int percent)
            => Exec("INSERT OR REPLACE INTO EventRunes (Uid, Percent) VALUES ($a, $b);", uid, percent);

        private static void Exec(string sql, long a, long b)
        {
            if (InMemoryOnly) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var write = connection.CreateCommand();
                write.CommandText = sql;
                write.Parameters.AddWithValue("$a", a);
                write.Parameters.AddWithValue("$b", b);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventTokens] A write failed ({a}, {b}): {ex.Message}");
            }
        }

        /// <summary>For the tests: forget everything.</summary>
        internal static void Forget()
        {
            lock (Gate)
            {
                _balances.Clear();
                _runes.Clear();
                _loaded = false;
            }
        }
    }
}
