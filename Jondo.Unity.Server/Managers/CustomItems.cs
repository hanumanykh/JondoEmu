using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Items made over an existing one (content/items/custom.json): a template of the client, for
    /// its name, its icon and its type, born with the effects written here instead of its own
    /// rolls, and worn with another item's skin. The client only draws what it knows, so a custom
    /// item is always one of its templates; what the server decides -- the effects every new one
    /// is born with, the look of whoever wears it -- is what changes.
    /// </summary>
    /// <remarks>
    /// Every road that makes an item goes through the two places this answers in: the fixed
    /// effects (DatabaseManager.EffectsOfTemplate: the .item command, the admin window, NPC shops,
    /// barters) and the rolled ones (Forgemagic.Roll: crafts, drops, the random grant). Items made
    /// before the file said so keep what they had.
    /// </remarks>
    public static class CustomItems
    {
        public const string AuthoredFile = "items/custom.json";

        /// <summary>One custom item: its effects, already as items carry them, and the skin it is worn with (0: its own).</summary>
        /// Effects null: the template's own, rolled as ever.
        public sealed record Custom(int Base, int Skin, IReadOnlyList<Equipment.ItemEffect>? Effects, string? Json);

        /// <summary>The characteristics by the names the file may use, and their effect ids.</summary>
        private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["vitalite"] = 125, ["vitalité"] = 125, ["vie"] = 125,
            ["sagesse"] = 124,
            ["force"] = 118, ["terre"] = 118,
            ["intelligence"] = 126, ["feu"] = 126,
            ["chance"] = 123, ["eau"] = 123,
            ["agilite"] = 119, ["agilité"] = 119, ["air"] = 119,
            ["puissance"] = 138,
            ["pa"] = 111, ["pm"] = 128, ["po"] = 117,
            ["dommages"] = 112, ["soins"] = 178,
            ["initiative"] = 174, ["prospection"] = 176,
            ["invocations"] = 182, ["critique"] = 115,
            ["tacle"] = 753, ["fuite"] = 752,
            ["pods"] = 158,
        };

        private static readonly object Gate = new();
        private static Dictionary<int, Custom>? _items;
        private static Dictionary<string, Custom>? _unique;

        /// <summary>
        /// A unique copy (a row with an «id»): made over its base but given one by one, each with
        /// its own effects and look -- several of them can share one base. Only the items it was
        /// given as are it (CustomInstances); the base stays itself for everybody else.
        /// </summary>
        public static bool TryGetUnique(string id, out Custom custom)
        {
            custom = null!;
            All();
            lock (Gate) return _unique!.TryGetValue(id, out custom!);
        }

        /// <summary>Every unique copy, by its id.</summary>
        public static IReadOnlyDictionary<string, Custom> Unique()
        {
            All();
            lock (Gate) return _unique!;
        }

        /// <summary>The custom item made over a template, if there is one.</summary>
        public static bool TryGet(int gid, out Custom custom)
        {
            custom = null!;
            var items = All();
            return items.TryGetValue(gid, out custom!);
        }

        /// <summary>Every custom item, by its base template, read once from the file.</summary>
        public static IReadOnlyDictionary<int, Custom> All()
        {
            lock (Gate)
            {
                if (_items != null) return _items;
                string path = Paths.ContentFile(AuthoredFile);
                _unique = LoadUnique(path);
                _items = Load(path);
                if (_items.Count + _unique.Count > 0)
                    Console.WriteLine($"[Items] {_items.Count} custom item(s) and {_unique.Count} unique copie(s) from {AuthoredFile}.");
                return _items;
            }
        }

        /// <summary>Forgets what was read: the next question reads the file again.</summary>
        public static void Reload()
        {
            lock (Gate) { _items = null; _unique = null; }
        }

        /// <summary>The rows without an «id»: custom items of every new item of their base.</summary>
        internal static Dictionary<int, Custom> Load(string path)
        {
            var items = new Dictionary<int, Custom>();
            foreach (var (id, custom) in Rows(path)) if (id == null) items[custom.Base] = custom;
            return items;
        }

        /// <summary>The rows with an «id»: unique copies.</summary>
        internal static Dictionary<string, Custom> LoadUnique(string path)
        {
            var items = new Dictionary<string, Custom>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, custom) in Rows(path)) if (id != null) items[id] = custom;
            return items;
        }

        private static List<(string? Id, Custom Custom)> Rows(string path)
        {
            var items = new List<(string?, Custom)>();
            try
            {
                if (!File.Exists(path)) return items;
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array) return items;
                foreach (var row in list.EnumerateArray())
                {
                    if (!row.TryGetProperty("base", out var b) || !b.TryGetInt32(out int gid) || gid <= 0) continue;
                    string? key = row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                                  && (i.GetString() ?? "").Trim().Length > 0 ? i.GetString()!.Trim() : null;
                    int skin = 0;
                    if (row.TryGetProperty("skin", out var s) && s.TryGetInt32(out int own)) skin = own;
                    else if (row.TryGetProperty("skinOf", out var o) && o.TryGetInt32(out int other)) skin = EquipmentSkins.All.TryGetValue(other, out int measured) ? measured : 0; // not SkinOf: it asks this class back

                    List<Equipment.ItemEffect>? effects = null;
                    if (row.TryGetProperty("effects", out var e) && e.ValueKind == JsonValueKind.Object)
                    {
                        effects = new List<Equipment.ItemEffect>();
                        foreach (var stat in e.EnumerateObject())
                        {
                            if (!stat.Value.TryGetInt64(out long value) || value == 0) continue;
                            int id = int.TryParse(stat.Name, out int raw) ? raw
                                   : Named.TryGetValue(stat.Name, out int named) ? named : 0;
                            if (id <= 0)
                            {
                                Console.WriteLine($"[Items] {AuthoredFile}: item {gid}, unknown characteristic «{stat.Name}».");
                                continue;
                            }
                            effects.Add(new Equipment.ItemEffect(id, value, 0, 0));
                        }
                    }
                    string? json = effects == null ? null
                        : "[" + string.Join(",", effects.Select(x => $"[{x.Effect},{x.Value},0,0]")) + "]";
                    items.Add((key, new Custom(gid, skin, effects, json)));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Items] {AuthoredFile} could not be read: {ex.Message}");
            }
            return items;
        }
    }
}
