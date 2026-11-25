using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The bosses' Dofus at the Stasis rates (docs/stasis-gameplay-spec.md §13, content/economy/dofus_drops.json):
    /// a fixed base percent per boss, the same at every grade, multiplied by prospecting like the
    /// monster's own rows. Any row of these Dofus in the client's tables is skipped
    /// (<see cref="IsFixed"/>): only the file makes them drop. FightHandler.RollLoot rolls them,
    /// GroupPreview shows them.
    /// </summary>
    public static class DofusDrops
    {
        public const string AuthoredFile = "economy/dofus_drops.json";

        /// <summary>One row: beating <see cref="Monster"/> drops <see cref="Item"/> at <see cref="Percent"/> % base.</summary>
        public sealed record Entry(int Monster, int Item, double Percent);

        private static IReadOnlyList<Entry>? _all;
        private static HashSet<int>? _items;
        private static readonly object _lock = new();

        /// <summary>The rows, read once (tests install their own).</summary>
        public static IReadOnlyList<Entry> All
        {
            get
            {
                if (_all != null) return _all;
                lock (_lock) return _all ??= Read(Paths.ContentFile(AuthoredFile));
            }
            set { lock (_lock) { _all = value; _items = null; } }
        }

        /// <summary>Whether the item is one of the file's Dofus: its table rows do not count.</summary>
        public static bool IsFixed(int item)
        {
            var items = _items;
            if (items == null) lock (_lock) items = _items ??= All.Select(e => e.Item).ToHashSet();
            return items.Contains(item);
        }

        /// <summary>The Dofus this monster may drop.</summary>
        public static IEnumerable<Entry> For(int monster) => All.Where(e => e.Monster == monster);

        /// <summary>The chance of one row, base times the prospecting multiplier (FightHandler.DropMultiplier), at most 100.</summary>
        public static double Chance(Entry entry, double prospectingMultiplier)
            => Math.Clamp(entry.Percent * Math.Max(1.0, prospectingMultiplier), 0.0, 100.0);

        internal static IReadOnlyList<Entry> Read(string path)
        {
            var rows = new List<Entry>();
            try
            {
                if (!File.Exists(path)) return rows;
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                foreach (var row in doc.RootElement.GetProperty("drops").EnumerateArray())
                {
                    int monster = row.GetProperty("monster").GetInt32();
                    int item = row.GetProperty("item").GetInt32();
                    double percent = row.GetProperty("percent").GetDouble();
                    if (monster > 0 && item > 0 && percent > 0) rows.Add(new Entry(monster, item, percent));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DofusDrops] {AuthoredFile} could not be read: {ex.Message}");
            }
            return rows;
        }
    }
}
