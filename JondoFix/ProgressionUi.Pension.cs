using System.Collections.Generic;
using System.Text.Json;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The «Pension» and «Cartes» tabs of the F9 window: /api/pension/state draws them,
    /// /api/pension/do runs the server's .pension / .caisse / .cartes actions and answers the
    /// view again with the action's text, shown in the status line.
    /// </summary>
    public static partial class ProgressionUi
    {
        private static JsonElement? _pension;

        private static void AskPension(string route, Dictionary<string, object> body)
            => Ask(route, body, root =>
            {
                _pension = root;
                string said = Text(root, "message");
                if (said.Length > 0) Say(said);
                if ((_tab == "pension" || _tab == "cartes") && _state is JsonElement s) Show(s);
            }, quiet: route == "pension/state");

        private static void PensionDo(string command, string args)
            => AskPension("pension/do", new Dictionary<string, object> { ["command"] = command, ["args"] = args });

        private static VisualElement Line(string text, params (string Label, bool Primary, System.Action Click)[] buttons)
        {
            var row = AdminItemsUi.Row();
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            row.style.marginBottom = new StyleLength(6f);
            row.style.paddingLeft = new StyleLength(8f);
            row.style.backgroundColor = new StyleColor(AdminItemsUi.RowPlain);
            var label = AdminItemsUi.Label(text);
            label.style.flexGrow = new StyleFloat(1f);
            label.style.flexShrink = new StyleFloat(1f);
            row.Add(label);
            foreach (var (l, p, c) in buttons) row.Add(Button(l, p, c));
            return row;
        }

        private static long _breedFirst;

        private static void ShowPension()
        {
            if (_pension is not JsonElement root || !root.TryGetProperty("pension", out var p))
            {
                _page.Add(Hint("Chargement de la pension…"));
                return;
            }
            string currency = Text(p, "currency");
            _page.Add(Title($"Incubation — {Int(p, "slots")} places"));
            _page.Add(Hint($"Jetons Event : {Long(p, "tokens")} · accélération -15 min : {Long(p, "speedUpTokens")} jetons."));
            var top = AdminItemsUi.Row();
            top.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            top.Add(Button("Faire éclore", true, () => PensionDo("pension", "eclore")));
            top.Add(Button($"Ouvrir une caisse ({Int(p, "crates")})", false, () => PensionDo("caisse", "")));
            top.Add(Button($"Acheter une caisse ({Long(p, "cratePrice")} {currency})", false, () => PensionDo("caisse", "acheter")));
            if (p.GetProperty("canBuySlot").GetBoolean())
                top.Add(Button($"Place d'incubation ({Long(p, "slotTokens")} jetons)", false, () => PensionDo("pension", "place")));
            _page.Add(top);

            foreach (var egg in p.GetProperty("eggs").EnumerateArray())
            {
                string id = Int(egg, "id").ToString();
                string name = $"#{id} {Text(egg, "name")}" + (egg.GetProperty("gift").GetBoolean() ? " (don)" : "");
                if (Int(egg, "slot") == 0)
                    _page.Add(Line(name, ("Incuber", true, () => PensionDo("pension", "incuber " + id))));
                else
                    _page.Add(Line($"{name} — place {Int(egg, "slot")}, {Text(egg, "left")}",
                        ("Accélérer", false, () => PensionDo("pension", "accelerer " + id)),
                        ("Retirer", false, () => PensionDo("pension", "annuler " + id))));
            }

            _page.Add(Title($"Animaux — salle d'étude ({Int(p, "studyPlaces")} places)"));
            _page.Add(Hint("Étudiés : 5 % de l'XP de vos combats gagnés (x2 sous la moitié de votre niveau). Accoupler : deux montures de même espèce et génération."));
            foreach (var a in p.GetProperty("animals").EnumerateArray())
            {
                long uid = Long(a, "uid");
                bool mount = a.GetProperty("mount").GetBoolean();
                string text = $"{Text(a, "name")} niv. {Int(a, "level")}" + (mount ? $" · gén. {Int(a, "generation")} · {Int(a, "litters")} portée(s)" : "") +
                              (a.GetProperty("worn").GetBoolean() ? " · équipé" : "");
                var buttons = new List<(string, bool, System.Action)>();
                if (a.GetProperty("studied").GetBoolean()) buttons.Add(("Retirer de l'étude", false, () => PensionDo("pension", "retirer " + uid)));
                else buttons.Add(("Étudier", true, () => PensionDo("pension", "etude " + uid)));
                if (mount)
                {
                    if (_breedFirst == 0) buttons.Add(("Accoupler…", false, () => { _breedFirst = uid; if (_state is JsonElement s) Show(s); }));
                    else if (_breedFirst == uid) buttons.Add(("Annuler", false, () => { _breedFirst = 0; if (_state is JsonElement s) Show(s); }));
                    else buttons.Add(("Avec celle-ci", true, () => { long first = _breedFirst; _breedFirst = 0; PensionDo("pension", $"accoupler {first} {uid}"); }));
                }
                _page.Add(Line(text, buttons.ToArray()));
            }
        }

        private static void ShowCards()
        {
            if (_pension is not JsonElement root || !root.TryGetProperty("cards", out var c))
            {
                _page.Add(Hint("Chargement du classeur…"));
                return;
            }
            _page.Add(Title($"Cartes de monstres — {Int(c, "packs")} paquet(s)"));
            var top = AdminItemsUi.Row();
            top.Add(Button("Ouvrir un paquet", true, () => PensionDo("cartes", "ouvrir")));
            top.Add(Button("Tout coller", false, () => PensionDo("cartes", "coller tout")));
            top.Add(Button($"Acheter un paquet ({Long(c, "packTokens")} jetons)", false, () => PensionDo("cartes", "acheter")));
            _page.Add(top);

            _page.Add(Title("Classeur"));
            foreach (var f in c.GetProperty("families").EnumerateArray())
            {
                int glued = Int(f, "glued"), total = Int(f, "total");
                _page.Add(Line($"{Text(f, "name")} — {glued}/{total} ({glued * 100 / System.Math.Max(1, total)} %)" +
                               (Text(f, "bonus").Length > 0 ? $" · bonus {Text(f, "bonus")}" + (glued == total ? " (actif)" : "") : "")));
            }
            _page.Add(Title("En main"));
            foreach (var card in c.GetProperty("loose").EnumerateArray())
            {
                string id = Int(card, "id").ToString();
                bool already = card.GetProperty("glued").GetBoolean();
                _page.Add(Line($"{Text(card, "name")} x{Int(card, "count")}" + (already ? " (déjà collée)" : ""),
                    already ? ("Vendre un doublon", false, () => PensionDo("cartes", "vendre " + id))
                            : ("Coller", true, () => PensionDo("cartes", "coller " + id))));
            }
        }
    }
}
