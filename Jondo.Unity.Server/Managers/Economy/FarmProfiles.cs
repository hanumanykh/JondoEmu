using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>One subarea's gauge for one character.</summary>
    public sealed class GaugeState
    {
        public int Progress { get; set; }
        /// <summary>Guaranteed items taken in the current window: the threshold's step.</summary>
        public int Completions { get; set; }
        /// <summary>When the current window started; the thresholds fall back to the base past its end.</summary>
        public DateTime WindowStart { get; set; }
        /// <summary>The item chosen, 0 for none; kept from one guaranteed item to the next.</summary>
        public int Target { get; set; }
    }

    /// <summary>What the farming loop remembers of one character: his gauges and his counter's buffs.</summary>
    public sealed class FarmProfile
    {
        public long CharacterId { get; set; }
        public Dictionary<int, GaugeState> Gauges { get; set; } = new();
        /// <summary>subarea → buff → when it ends (UTC). The clock runs offline too, as the spec says.</summary>
        public Dictionary<int, Dictionary<string, DateTime>> Buffs { get; set; } = new();

        internal object Gate { get; } = new();
    }

    /// <summary>
    /// The profiles, cached, and written to world.db as one JSON row per character (table
    /// FarmProfiles) -- a handful of numbers per zone visited, not worth a schema. The tests turn
    /// <see cref="Persist"/> off and keep everything in memory.
    /// </summary>
    public static class FarmProfiles
    {
        public static bool Persist { get; set; } = true;
        private static readonly ConcurrentDictionary<long, FarmProfile> _cache = new();
        private static bool _tableReady;

        internal static void Forget() => _cache.Clear();

        public static FarmProfile Of(long characterId) => _cache.GetOrAdd(characterId, Load);

        private static FarmProfile Load(long characterId)
        {
            if (Persist)
            {
                try
                {
                    using var connection = Open();
                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT Data FROM FarmProfiles WHERE CharacterId = $id;";
                    read.Parameters.AddWithValue("$id", characterId);
                    if (read.ExecuteScalar() is string json && json.Length > 0)
                    {
                        var profile = JsonSerializer.Deserialize<FarmProfile>(json);
                        if (profile != null)
                        {
                            profile.CharacterId = characterId;
                            return profile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Farm] The profile of {characterId} could not be read: {ex.Message}");
                }
            }
            return new FarmProfile { CharacterId = characterId };
        }

        public static void Save(FarmProfile profile)
        {
            if (!Persist) return;
            try
            {
                string json;
                lock (profile.Gate) json = JsonSerializer.Serialize(profile);
                using var connection = Open();
                var write = connection.CreateCommand();
                write.CommandText = "INSERT OR REPLACE INTO FarmProfiles (CharacterId, Data) VALUES ($id, $data);";
                write.Parameters.AddWithValue("$id", profile.CharacterId);
                write.Parameters.AddWithValue("$data", json);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Farm] The profile of {profile.CharacterId} could not be written: {ex.Message}");
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            if (!_tableReady)
            {
                var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS FarmProfiles (CharacterId INTEGER PRIMARY KEY, Data TEXT NOT NULL);";
                create.ExecuteNonQuery();
                _tableReady = true;
            }
            return connection;
        }
    }
}
