using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Sanctuary (spec §2, §21): the shared hub one click away and one click back. Its map,
    /// its NPCs and the Stasis Merchant's stock are in content/world/sanctuary.json.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map is the Forbidden Inn's Great Hall (211292160, [23,-32]): an existing indoor room of
    /// the client, without monsters, without NPCs and named by no other content file, wide enough
    /// (163 walkable cells) for the Merchant, the Raffineur, the zone counter and a banker. The 3.6
    /// client has no «+» menu, so «.sanctuaire» goes there and «.retour» comes back to the very map
    /// and cell left, remembered by character in the table SanctuaryReturns (a relog keeps it).
    /// </para>
    /// <para>
    /// Refused in a fight and inside a dungeon -- the Sanctuary is not a way out of a dungeon's
    /// rooms; the jail and closed floors are refused by TeleportHandler, as every teleport.
    /// </para>
    /// </remarks>
    public static class Sanctuary
    {
        public const string AuthoredFile = "world/sanctuary.json";

        public sealed class Npc
        {
            public int Template { get; set; }
            public int Cell { get; set; }
            public string Role { get; set; } = "";
            public string Name { get; set; } = "";
        }

        public sealed class Consumable
        {
            public int Item { get; set; }
            public long Price { get; set; }
        }

        public sealed class MerchantSettings
        {
            public double PriceFactor { get; set; } = 1.0;
            public int BandWidth { get; set; } = 20;
            public int CatalogueLimit { get; set; } = 25;
            public List<Consumable> Consumables { get; set; } = new();
            public Dictionary<int, string> BreedElements { get; set; } = new();
        }

        public sealed class Settings
        {
            public long MapId { get; set; } = 211292160;
            public int Cell { get; set; } = 316;
            public string Name { get; set; } = "Sanctuaire";
            public bool MerchantOnlyInSanctuary { get; set; } = true;
            public List<Npc> Npcs { get; set; } = new();
            public MerchantSettings Merchant { get; set; } = new();
        }

        private static Settings? _settings;

        public static Settings Current
        {
            get => _settings ??= Read(Paths.ContentFile(AuthoredFile));
            set => _settings = value;
        }

        public static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Fabrication.JsonOptions) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanctuary] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        public static bool IsSanctuary(long mapId) => mapId == Current.MapId;

        /// <summary>The role of an NPC template standing in the Sanctuary, or null.</summary>
        public static string? RoleOf(int template, long mapId)
            => IsSanctuary(mapId) ? Current.Npcs.FirstOrDefault(n => n.Template == template)?.Role : null;

        // ─── Going and coming back ──────────────────────────────────────────────────────

        /// <summary>
        /// Why a character may not go to the Sanctuary, or null: «combat», «donjon», «deja».
        /// </summary>
        public static string? Refusal(bool inFight, long mapId, Func<long, bool> isDungeonRoom)
        {
            if (inFight) return "combat";
            if (IsSanctuary(mapId)) return "deja";
            if (isDungeonRoom(mapId)) return "donjon";
            return null;
        }

        private static readonly ConcurrentDictionary<long, (long Map, int Cell)> _returns = new();
        private static bool _loaded;
        private static readonly object Gate = new();

        /// <summary>Off in the tests: the returns stay in memory.</summary>
        internal static bool Persist { get; set; } = true;

        /// <summary>Remembers where a character leaves from; the Sanctuary itself is never a place to go back to.</summary>
        public static void RecordDeparture(long characterId, long mapId, int cell)
        {
            if (characterId <= 0 || mapId <= 0 || IsSanctuary(mapId)) return;
            EnsureLoaded();
            _returns[characterId] = (mapId, cell);
            Write(characterId, (mapId, cell));
        }

        /// <summary>Where a character came from, without forgetting it.</summary>
        public static (long Map, int Cell)? ReturnOf(long characterId)
        {
            EnsureLoaded();
            return _returns.TryGetValue(characterId, out var back) ? back : null;
        }

        /// <summary>Where a character came from, forgotten once taken.</summary>
        public static (long Map, int Cell)? TakeReturn(long characterId)
        {
            EnsureLoaded();
            if (!_returns.TryRemove(characterId, out var back)) return null;
            Write(characterId, null);
            return back;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                if (!Persist) return;
                try
                {
                    using var connection = Open();
                    using var read = connection.CreateCommand();
                    read.CommandText = "SELECT CharacterId, MapId, CellId FROM SanctuaryReturns;";
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) _returns[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt32(2));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Sanctuary] The returns could not be read: {ex.Message}");
                }
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE IF NOT EXISTS SanctuaryReturns (CharacterId INTEGER PRIMARY KEY, MapId INTEGER NOT NULL, CellId INTEGER NOT NULL);";
            create.ExecuteNonQuery();
            return connection;
        }

        private static void Write(long characterId, (long Map, int Cell)? back)
        {
            if (!Persist) return;
            try
            {
                using var connection = Open();
                using var write = connection.CreateCommand();
                if (back is { } b)
                {
                    write.CommandText = "INSERT OR REPLACE INTO SanctuaryReturns (CharacterId, MapId, CellId) VALUES ($c, $m, $cell);";
                    write.Parameters.AddWithValue("$m", b.Map);
                    write.Parameters.AddWithValue("$cell", b.Cell);
                }
                else write.CommandText = "DELETE FROM SanctuaryReturns WHERE CharacterId = $c;";
                write.Parameters.AddWithValue("$c", characterId);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanctuary] The return of {characterId} could not be saved: {ex.Message}");
            }
        }

        /// <summary>For tests: forgets every return.</summary>
        internal static void ResetForTests()
        {
            _returns.Clear();
            _loaded = !Persist;
        }
    }

    /// <summary>
    /// The Stasis Merchant (spec §2): plain (non-stasified) equipment by level band at its
    /// reference worth, rolled between the middle and the max of each line; consumables; dungeon
    /// keys at the tiers' prices; and «Conseillé», the outfit the bots' planner picks for the
    /// character's level and element. Prices here are kamas-calibrated; the handler converts them.
    /// </summary>
    /// <remarks>
    /// A command and not the client's NPC shop: a shop's price is one per item for every shop
    /// (NpcShops' «precios») and a catalogue tops out near 444 entries, while this one prices any
    /// piece by its worth and in Jondo Coins. The Merchant NPC in the Sanctuary points to it.
    /// </remarks>
    public static class StasisMerchant
    {
        /// <summary>What a plain piece (or a consumable without a written price) costs, in kamas.</summary>
        public static long PriceOf(long referenceWorth)
            => Math.Max(1, (long)Math.Round(referenceWorth * Math.Max(0.01, Sanctuary.Current.Merchant.PriceFactor)));

        public static long PriceOf(int gid)
        {
            var written = Sanctuary.Current.Merchant.Consumables.FirstOrDefault(c => c.Item == gid);
            if (written != null && written.Price > 0) return written.Price;
            return PriceOf(ReferencePrices.Of(gid));
        }

        public static bool IsConsumable(int gid) => Sanctuary.Current.Merchant.Consumables.Any(c => c.Item == gid);

        /// <summary>Whether the Merchant sells this piece: equipment that stasifies and that anybody may wear.</summary>
        public static bool SellsEquipment(int gid)
        {
            var template = Forgemagic.TemplateOf(gid);
            return template != null && Fabrication.Eligible(template) && Bots.Outfitter.PieceOf(gid) != null;
        }

        private static readonly ConcurrentDictionary<int, bool> _odd = new();

        /// <summary>
        /// Pieces a merchant should not sell although they have a recipe -- event, Temporis and
        /// quest-only sets. The pool (Outfitter.Craftable) already drops templates with
        /// «criterions» or a «craftFeasibleCriterion»; this also drops, from the template's Data:
        /// a name containing «[!]» (the game's marker of unfinished / special items); a
        /// «visibilityCriterion» or «craftVisibleCriterion» (a recipe only shown under a condition,
        /// i.e. not learnable by everyone); «craftXpRatio» 0 (a recipe that teaches nothing: quest
        /// and event crafts such as the Knut set); and m_flags without bit 2 (observed on non-tradeable crafts: set on
        /// 994 of the 1040 craftable templates, missing on quest/event outputs). Unreadable: kept.
        /// </summary>
        internal static bool IsOddForMerchant(int gid) => _odd.GetOrAdd(gid, ReadOdd);

        private static bool ReadOdd(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT t.Data, tr.Text FROM ItemTemplates t LEFT JOIN Translations tr " +
                                      "ON tr.Key = CAST(json_extract(t.Data, '$.nameId') AS TEXT) WHERE t.Id = $gid LIMIT 1;";
                command.Parameters.AddWithValue("$gid", gid);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0)) return false;
                string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                using var doc = JsonDocument.Parse(reader.GetString(0));
                var root = doc.RootElement;
                string Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long Number(string key, long fallback) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : fallback;
                if (name.Contains("[!]")) return true;
                if (Text("visibilityCriterion").Length > 0 || Text("craftVisibleCriterion").Length > 0) return true;
                if (Number("craftXpRatio", -1) == 0) return true;
                long flags = Number("m_flags", -1);
                bool tradeable = flags < 0 || (flags & 4) != 0;
                return !tradeable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The band a level falls in: [1-20], [21-40]... with the configured width.</summary>
        public static (int Min, int Max) BandOf(int level)
        {
            int width = Math.Max(1, Sanctuary.Current.Merchant.BandWidth);
            int min = (Math.Max(1, level) - 1) / width * width + 1;
            return (min, min + width - 1);
        }

        /// <summary>The pieces of a pool in a level band, by level then id.</summary>
        internal static List<Bots.Outfitter.Piece> Catalogue(IEnumerable<Bots.Outfitter.Piece> pool, int min, int max)
            => pool.Where(p => p.Level >= min && p.Level <= max)
                   .Where(p => Forgemagic.TemplateOf(p.Gid) is { } t && Fabrication.Eligible(t))
                   .Where(p => !IsOddForMerchant(p.Gid))
                   .GroupBy(p => p.Gid).Select(g => g.First())
                   .OrderBy(p => p.Level).ThenBy(p => p.Gid).ToList();

        /// <summary>The element a breed plays by default here, Strength when the file names none.</summary>
        internal static Bots.Outfitter.Element ElementOf(int breed, string? asked = null)
        {
            string word = (asked ?? "").Trim().ToLowerInvariant();
            var byWord = word switch
            {
                "terre" or "force" => Bots.Outfitter.Element.Strength,
                "feu" or "intelligence" => Bots.Outfitter.Element.Intelligence,
                "eau" or "chance" => Bots.Outfitter.Element.Chance,
                "air" or "agilite" or "agilité" => Bots.Outfitter.Element.Agility,
                _ => (Bots.Outfitter.Element?)null,
            };
            if (byWord != null) return byWord.Value;
            if (Sanctuary.Current.Merchant.BreedElements.TryGetValue(breed, out var name)
                && Enum.TryParse<Bots.Outfitter.Element>(name, true, out var element)) return element;
            return Bots.Outfitter.Element.Strength;
        }

        /// <summary>«Conseillé»: the planner's outfit of a level and element out of a pool, slot by slot.</summary>
        internal static List<Bots.Outfitter.Piece> Recommended(int level, Bots.Outfitter.Element element, IEnumerable<Bots.Outfitter.Piece> pool)
            => Bots.Outfitter.Best(level, element, pool.Where(p => Forgemagic.TemplateOf(p.Gid) is { } t && Fabrication.Eligible(t) && !IsOddForMerchant(p.Gid)))
                   .Slots.OrderBy(s => s.Key).Select(s => s.Value).ToList();

        /// <summary>A plain piece as the Merchant sells it: each rolled line between its middle and its max.</summary>
        public static List<Equipment.ItemEffect> Roll(Forgemagic.Template template, Random dice)
        {
            var perfect = Forgemagic.Perfect(template);
            var result = new List<Equipment.ItemEffect>(perfect.Count);
            foreach (var e in perfect)
            {
                var line = template.Lines.FirstOrDefault(l => l.Effect == e.Effect);
                bool plain = e.Text == null && e.DiceNum == 0 && e.DiceSide == 0 && e.Value > 0;
                if (plain && line.Max > line.Min && line.Min >= 0 && e.Value == line.Max)
                {
                    int middle = (line.Min + line.Max + 1) / 2;
                    result.Add(new Equipment.ItemEffect(e.Effect, dice.Next(middle, line.Max + 1), 0, 0));
                }
                else result.Add(e);
            }
            return result;
        }
    }
}
