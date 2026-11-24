using System;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Which items are unique copies (CustomItems.TryGetUnique): the item's uid and the copy's id,
    /// kept in world.db so that a copy given stays one across restarts. Its effects travel with
    /// the item as any item's do; this only tells the look which skin it is worn with.
    /// </summary>
    public static class CustomInstances
    {
        private static readonly ConcurrentDictionary<long, string> _byUid = new();
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
                    create.CommandText = "CREATE TABLE IF NOT EXISTS CustomItemInstances (Uid INTEGER PRIMARY KEY, CustomId TEXT NOT NULL);";
                    create.ExecuteNonQuery();
                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT Uid, CustomId FROM CustomItemInstances;";
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) _byUid[reader.GetInt64(0)] = reader.GetString(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Items] The unique copies could not be read: {ex.Message}");
                }
                _loaded = true;
            }
        }

        /// <summary>The unique copy an item is, or null.</summary>
        public static string? IdOf(long uid)
        {
            EnsureLoaded();
            return _byUid.TryGetValue(uid, out var id) ? id : null;
        }

        /// <summary>Marks an item as a unique copy.</summary>
        public static void Mark(long uid, string id)
        {
            EnsureLoaded();
            _byUid[uid] = id;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var write = connection.CreateCommand();
                write.CommandText = "INSERT OR REPLACE INTO CustomItemInstances (Uid, CustomId) VALUES ($uid, $id);";
                write.Parameters.AddWithValue("$uid", uid);
                write.Parameters.AddWithValue("$id", id);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Items] Item {uid} could not be marked as the unique copy «{id}»: {ex.Message}");
            }
        }

        /// <summary>
        /// The template the look should draw for an item worn: the base itself, or -- for a unique
        /// copy with a skin of its own -- that skin as a negative number, which
        /// <see cref="EquipmentSkins.SkinOf"/> reads as a skin and not a template.
        /// </summary>
        public static int LookTemplate(long uid, int template)
        {
            string? id = IdOf(uid);
            return id != null && CustomItems.TryGetUnique(id, out var custom) && custom.Skin > 0 ? -custom.Skin : template;
        }
    }
}
