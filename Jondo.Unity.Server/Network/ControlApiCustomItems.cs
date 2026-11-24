using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The admin window's custom items tab (JondoFix): the custom items of content/items/custom.json
    /// listed, written and taken out. The file stays the one truth -- what the tab writes is what a
    /// hand would -- and it is read again at once, so a new item of the base is custom from now on.
    /// </summary>
    public static partial class ControlApi
    {
        private static readonly object CustomFileGate = new();

        /// <summary>{ }: every custom item, with its base's name and its effects by name.</summary>
        private static Respuesta CustomsList()
        {
            var rows = ReadCustomRows();
            var items = rows.Select(row => new
            {
                @base = Int(row, "base"),
                name = ItemOf(Int(row, "base"))?.Name ?? "",
                skinOf = Int(row, "skinOf"),
                skin = Int(row, "skin"),
                worn = CustomItems.TryGet(Int(row, "base"), out var custom) ? custom.Skin : 0,
                note = row["note"]?.GetValue<string>() ?? "",
                effects = row["effects"] is JsonObject effects
                    ? effects.Where(e => e.Value != null).ToDictionary(e => e.Key, e => e.Value!.GetValue<long>())
                    : new Dictionary<string, long>(),
            }).ToList();
            return Bien(new { items });
        }

        /// <summary>
        /// { base, skinOf?, effects?: { name: value }, note? }: the custom item of that base written,
        /// in place of the one it had. A skinOf whose look the server has not measured is refused:
        /// the item would be worn with nothing.
        /// </summary>
        private static Respuesta CustomsSave(string cuerpo, long administrador)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cuerpo) ? "{}" : cuerpo);
            var root = doc.RootElement;
            int gid = root.TryGetProperty("base", out var b) && b.TryGetInt32(out int g) ? g : 0;
            if (gid <= 0 || ItemOf(gid) == null) return Mal(404, "objeto-desconocido");
            int skinOf = root.TryGetProperty("skinOf", out var s) && s.TryGetInt32(out int so) ? so : 0;
            if (skinOf > 0 && !EquipmentSkins.All.ContainsKey(skinOf)) return Mal(400, "sin-skin");

            var row = new JsonObject { ["base"] = gid };
            string note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            row["note"] = note.Length > 0 ? note : ItemOf(gid)?.Name ?? "";
            if (skinOf > 0) row["skinOf"] = skinOf;
            if (root.TryGetProperty("effects", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                var effects = new JsonObject();
                foreach (var stat in e.EnumerateObject())
                    if (stat.Value.TryGetInt64(out long value) && value != 0) effects[stat.Name] = value;
                if (effects.Count > 0) row["effects"] = effects;
            }
            if (skinOf == 0 && row["effects"] == null) return Mal(400, "custom-vacio");

            WriteCustomRows(rows => { rows.RemoveAll(r => Int(r, "base") == gid); rows.Add(row); });
            ActivityJournal.Current.Write("admin.custom.saved", administrador, details: new { gid, skinOf });
            Console.WriteLine($"[Items] Custom item over {gid} written by account {administrador}.");
            return CustomsList();
        }

        /// <summary>{ base }: the custom item of that base taken out; the base is itself again.</summary>
        private static Respuesta CustomsRemove(string cuerpo, long administrador)
        {
            int gid = (int)Numero(cuerpo, "base");
            bool removed = false;
            WriteCustomRows(rows => removed = rows.RemoveAll(r => Int(r, "base") == gid) > 0);
            if (!removed) return Mal(404, "no-esta");
            ActivityJournal.Current.Write("admin.custom.removed", administrador, details: new { gid });
            return CustomsList();
        }

        private static int Int(JsonObject row, string key)
            => row[key] is JsonValue v && v.TryGetValue(out int i) ? i : 0;

        private static List<JsonObject> ReadCustomRows()
        {
            lock (CustomFileGate)
            {
                var root = ReadCustomFile();
                return (root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(o => (JsonObject)o.DeepClone()).ToList();
            }
        }

        private static JsonObject ReadCustomFile()
        {
            string path = Paths.ContentFile(CustomItems.AuthoredFile);
            if (!File.Exists(path)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(path),
                       documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   as JsonObject ?? new JsonObject();
        }

        /// <summary>The file's rows changed and written back, its comment kept; then read again by the server.</summary>
        private static void WriteCustomRows(Action<List<JsonObject>> change)
        {
            lock (CustomFileGate)
            {
                var root = ReadCustomFile();
                var rows = (root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(o => (JsonObject)o.DeepClone()).ToList();
                change(rows);
                var items = new JsonArray();
                foreach (var row in rows.OrderBy(r => Int(r, "base"))) items.Add(row);
                root["items"] = items;
                string path = Paths.ContentFile(CustomItems.AuthoredFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                CustomItems.Reload();
            }
        }
    }
}
