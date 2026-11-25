using System.Collections.Generic;
using System.Text.Json;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The «Éveil» tab of the F9 window: /api/stasis/eveil draws the six Dofus of the Éveil
    /// Partie I (stage, copies, route, final challenge, reward) and runs the server's .eveil
    /// actions, answering the tab again with the action's text, shown in the status line.
    /// </summary>
    public static partial class ProgressionUi
    {
        private static JsonElement? _eveil;

        private static void AskEveil(string args)
            => Ask("stasis/eveil", new Dictionary<string, object> { ["args"] = args ?? "" }, root =>
            {
                _eveil = root;
                string said = Clean(Text(root, "text"));
                if (said.Length > 0) Say(said);
                if (_tab == "eveil" && _state is JsonElement s) Show(s);
            }, quiet: string.IsNullOrEmpty(args));

        private static void ShowEveil()
        {
            if (_eveil is not JsonElement root || !root.TryGetProperty("dofus", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                _page.Add(Hint("Chargement de l'Éveil…"));
                return;
            }
            _page.Add(Title("Éveil des Dofus — Partie I"));
            bool needHardcore = root.TryGetProperty("requireHardcore", out var rh) && rh.ValueKind == JsonValueKind.True;
            bool evolved = root.TryGetProperty("evolved", out var ev) && ev.ValueKind == JsonValueKind.True;
            bool hardcore = root.TryGetProperty("hardcore", out var hc) && hc.ValueKind == JsonValueKind.True;
            _page.Add(Hint("Sacrifiez des copies, battez les boss du parcours" +
                           (needHardcore ? " en hardcore (victoires après le début seulement)" : "") +
                           ", puis le défi final en solo. Récompense : le Dofus stasifié." +
                           (needHardcore ? (hardcore ? " Hardcore : activé." : evolved ? " Hardcore : désactivé (« .hardcore on »)." : " Hardcore réservé aux personnages évolués.") : "")));

            foreach (var d in list.EnumerateArray())
            {
                string id = Text(d, "id");
                string stage = Text(d, "stage");
                string head = $"{Text(d, "name")} (niv. {Int(d, "level")})";
                string body = stage switch
                {
                    "route" => $"parcours {Int(d, "done")}/{Int(d, "total")} — restants : {Join(d, "remaining")}",
                    "challenge" => $"parcours terminé — défi : {Text(d, "boss")} niv. {Int(d, "bossLevel")}",
                    "done" => "éveillé ✓",
                    _ => $"{Int(d, "held")}/{Int(d, "copies")} copies en sac — défi final : {Text(d, "boss")}",
                };
                string text = $"{head} : {body}";
                if (stage == "none" || stage == "challenge") text += $" · bonus : {Join(d, "effects")}";
                switch (stage)
                {
                    case "none":
                        _page.Add(Line(text, ($"Sacrifier {Int(d, "copies")}", Int(d, "held") >= Int(d, "copies"), () => AskEveil(id + " commencer"))));
                        break;
                    case "challenge":
                        _page.Add(Line(text, ("Lancer le défi", true, () => AskEveil(id + " defi"))));
                        break;
                    default:
                        _page.Add(Line(text));
                        break;
                }
            }
        }

        private static string Join(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return "";
            var parts = new List<string>();
            foreach (var item in array.EnumerateArray()) parts.Add(item.GetString() ?? "");
            return string.Join(", ", parts);
        }
    }
}
