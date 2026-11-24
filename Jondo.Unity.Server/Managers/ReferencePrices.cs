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
    /// What an item is worth on a marketplace, as near as the game's own data can tell, for the
    /// simulated players who sell and buy (Bots.SimulatedPlayer.Market). Their prices were the
    /// template's NPC price doubled: a boss's rare resource went for twenty thousand kamas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no public record of the official marketplaces' prices to copy, so the worth is
    /// reckoned from how hard a thing is to get. A resource from its droppers: the level of the
    /// lowest monster that drops it and its best chance of falling, a boss's resource
    /// (a dungeon's) six times dearer and an archmonster's twelve. Something crafted: what its
    /// ingredients are worth and 5% more -- near what crafting it costs. Anything else (sold by NPCs, a quest's): its NPC
    /// price twice over, as before.
    /// </para>
    /// <para>
    /// content/market/reference_prices.json wins over all of it: the prices written there by hand
    /// -- the official ones one knows -- are taken as they are.
    /// </para>
    /// </remarks>
    public static class ReferencePrices
    {
        public const string AuthoredFile = "market/reference_prices.json";

        /// <summary>The best chance taken into account: a drop rarer than one in a thousand is not dearer for it.</summary>
        private const double RarestChance = 0.001;

        private const double BossFactor = 6, ArchmonsterFactor = 12;

        /// <summary>One way a resource falls: the dropper's level, its best chance, and what the dropper is.</summary>
        internal sealed record Source(int Level, double Chance, bool Boss, bool Archmonster);

        private static readonly Lazy<Dictionary<int, List<Source>>> Sources = new(ReadSources);
        private static readonly Lazy<Dictionary<int, long>> Written = new(() => ReadWritten(Paths.ContentFile(AuthoredFile)));
        private static readonly ConcurrentDictionary<int, long> _worth = new();

        /// <summary>
        /// Whether an item is something a marketplace deals in: written by hand, dropped by a
        /// monster outside any quest, or crafted.
        /// </summary>
        public static bool IsTraded(int gid)
            => Written.Value.ContainsKey(gid) || Sources.Value.ContainsKey(gid) || RecipeManager.TryGetByResult(gid, out _);

        /// <summary>The worth of one of an item.</summary>
        public static long Of(int gid) => Of(gid, 0);

        private static long Of(int gid, int depth)
        {
            if (Written.Value.TryGetValue(gid, out long written)) return written;
            if (_worth.TryGetValue(gid, out long known)) return known;
            long worth = Reckon(gid, depth);
            if (depth == 0) _worth[gid] = worth;
            return worth;
        }

        private static long Reckon(int gid, int depth)
        {
            long npc = Math.Max(1, Bots.Outfitter.BasePrice(gid));
            if (Sources.Value.TryGetValue(gid, out var sources) && sources.Count > 0)
                return Math.Max(npc, FromDrops(sources));
            if (depth < 3 && RecipeManager.TryGetByResult(gid, out var recipe))
                return Math.Max(npc, recipe.Ingredients.Sum(i => Of(i.ItemId, depth + 1) * i.Quantity) * 105 / 100);
            // A piece of equipment no recipe and no monster gives (a shop's, a quest's, a chest's)
            // is still worth what its level is: never less than 25 x level^1.7 -- 26 000 kamas at
            // level 60, 200 000 at level 200 -- rather than its NPC price.
            var template = Forgemagic.TemplateOf(gid);
            if (template != null && Bots.Outfitter.SlotsOf(template.Type).Count > 0)
                return Math.Max(2 * npc, EquipmentFloor(template.Level));
            return 2 * npc;
        }

        internal static long EquipmentFloor(int level) => (long)(25 * Math.Pow(Math.Max(1, level), 1.7));

        /// <summary>
        /// A dropped resource's worth: 2 x level^1.25 / chance^0.8, from the easiest way to get it;
        /// a resource that only a boss or an archmonster drops is dearer.
        /// </summary>
        internal static long FromDrops(IReadOnlyList<Source> sources)
        {
            double best = double.MaxValue;
            foreach (var source in sources)
            {
                double chance = Math.Clamp(source.Chance, RarestChance, 1);
                double worth = 2 * Math.Pow(Math.Max(1, source.Level), 1.25) / Math.Pow(chance, 0.8);
                if (source.Archmonster) worth *= ArchmonsterFactor;
                else if (source.Boss) worth *= BossFactor;
                best = Math.Min(best, worth);
            }
            return best == double.MaxValue ? 1 : Math.Max(1, (long)Math.Round(best));
        }

        /// <summary>
        /// Every dropped item's ways of falling, from the monsters' templates: drops that need a
        /// quest or an item (a criterion) are a quest's, not the market's, and left out.
        /// </summary>
        private static Dictionary<int, List<Source>> ReadSources()
        {
            var sources = new Dictionary<int, List<Source>>();
            try
            {
                var bosses = DungeonManager.All.Values.SelectMany(d => d.Bosses).ToHashSet();
                var monsters = new List<(int Id, int Level, JsonElement Drops, int MiniBoss)>();
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Data FROM MonsterTemplates;";
                using var reader = command.ExecuteReader();
                var archmonsters = new HashSet<int>();
                var docs = new List<JsonDocument>();
                while (reader.Read())
                {
                    var doc = JsonDocument.Parse(reader.GetString(1));
                    docs.Add(doc);
                    var root = doc.RootElement;
                    int miniBoss = root.TryGetProperty("correspondingMiniBossId", out var m) && m.TryGetInt32(out int mb) ? mb : 0;
                    if (miniBoss > 0) archmonsters.Add(miniBoss);
                    int level = 0;
                    if (root.TryGetProperty("grades", out var g) && g.TryGetProperty("Array", out var grades))
                        foreach (var grade in grades.EnumerateArray())
                            if (grade.TryGetProperty("level", out var l) && l.TryGetInt32(out int lv) && (level == 0 || lv < level)) level = lv;
                    if (root.TryGetProperty("drops", out var d) && d.TryGetProperty("Array", out var drops))
                        monsters.Add((reader.GetInt32(0), level, drops, miniBoss));
                }
                foreach (var (id, level, drops, _) in monsters)
                {
                    foreach (var drop in drops.EnumerateArray())
                    {
                        if (drop.TryGetProperty("hasCriterions", out var hc) && hc.TryGetInt32(out int has) && has != 0) continue;
                        if (!drop.TryGetProperty("objectId", out var o) || !o.TryGetInt32(out int gid)) continue;
                        double chance = 0;
                        for (int i = 1; i <= 5; i++)
                            if (drop.TryGetProperty($"percentDropForGrade{i}", out var p) && p.TryGetDouble(out double pc))
                                chance = Math.Max(chance, pc / 100.0);
                        if (chance <= 0) continue;
                        (sources.TryGetValue(gid, out var list) ? list : sources[gid] = new List<Source>())
                            .Add(new Source(level, chance, bosses.Contains(id), archmonsters.Contains(id)));
                    }
                }
                foreach (var doc in docs) doc.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] The drops could not be read for the reference prices: {ex.Message}");
            }
            return sources;
        }

        internal static Dictionary<int, long> ReadWritten(string path)
        {
            var written = new Dictionary<int, long>();
            try
            {
                if (!File.Exists(path)) return written;
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object) return written;
                foreach (var row in prices.EnumerateObject())
                    if (int.TryParse(row.Name, out int gid) && row.Value.TryGetInt64(out long price) && price > 0)
                        written[gid] = price;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] {AuthoredFile} could not be read: {ex.Message}");
            }
            return written;
        }
    }
}
