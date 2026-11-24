using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The «Progression» window of the Stasis rules: the level-up don offered now as three or four
    /// cards, its reroll and the refonte; the research tree paid with Éclats; the Ascension and
    /// the Evolution with what they give and a confirmation. F9 opens and closes it, and it opens
    /// by itself when the server has an offer waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the client's own components, as the admin windows are (AdminItemsUi, AdminWorldUi):
    /// a WindowFigma with its frame, title bar and close button, DofusLabel texts in the client's
    /// text classes and DofusButtonCustom buttons, laid on the layer the client opens its own
    /// windows on (AdminItemsUi.FindLayer) and keeping its clicks from reaching the map.
    /// </para>
    /// <para>
    /// It talks to the server's control API (/api/stasis/state, pick, reroll, repick, research,
    /// ascend, evolve) with the session's own token, JONDO_PLAYER_TOKEN, as the Heroes and the
    /// Cazacabezas do: it is every player's window, not the staff's. Every answer is the whole
    /// state, and the window is redrawn from it; it keeps nothing of its own but which tab is shown.
    /// </para>
    /// <para>
    /// Opening on its own: every <see cref="PollEvery"/> seconds, while closed, the state is asked
    /// quietly; an offer waiting opens the window, unless the player closed it on that very offer
    /// (then it waits for the next one). F9 was free: F7 is the Cazacabezas's, F10 the item window's,
    /// F11 the staff console's.
    /// </para>
    /// </remarks>
    public static class ProgressionUi
    {
        private const float Width = 980f, Height = 700f;
        private const float PollEvery = 8f;

        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();
        private static WindowFigma _window;
        private static VisualElement _page;
        private static DofusLabel _status;
        private static string _tab = "dons";
        private static JsonElement? _state;
        private static float _lastPoll = -100f;
        private static bool _polling;
        private static int _dismissedLevel;
        private static string _confirm = "";
        private static int _repickFrom, _repickTo = 3;
        private static string _token;

        private static string Token
            => _token ??= (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                           ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();

        // ─── Every frame ─────────────────────────────────────────────────────────────────

        /// <summary>From the mod's OnUpdate: the answers, F9, and the quiet look for a waiting offer.</summary>
        public static void Tick()
        {
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Progression: {ex.Message}"); }
            }
            if (Token.Length == 0) return;

            if (F9Pressed())
            {
                if (_window != null) Close();
                else Open();
            }

            float now = UnityEngine.Time.unscaledTime;
            if (_window == null && !_polling && now - _lastPoll >= PollEvery)
            {
                _lastPoll = now;
                _polling = true;
                Ask("stasis/state", new Dictionary<string, object>(), root =>
                {
                    _polling = false;
                    int level = OfferLevel(root);
                    if (level > 0 && level != _dismissedLevel && _window == null)
                    {
                        _tab = "dons";
                        Open();
                        Show(root);
                    }
                }, quiet: true, failed: () => _polling = false);
            }
        }

        private static bool F9Pressed()
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                return keyboard != null && keyboard.f9Key.wasPressedThisFrame;
            }
            catch
            {
                return false;
            }
        }

        private static int OfferLevel(JsonElement root)
            => root.TryGetProperty("offer", out var offer) && offer.ValueKind == JsonValueKind.Object
                ? offer.GetProperty("level").GetInt32() : 0;

        // ─── The window ──────────────────────────────────────────────────────────────────

        public static void Open()
        {
            try
            {
                var layer = AdminItemsUi.FindLayer();
                if (layer == null) return;

                var window = new WindowFigma(true, "Progression", "", true);
                window.showCloseButton = true;
                window.name = "JondoProgression";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(Width);
                window.style.height = new StyleLength(Height);
                float lw = layer.resolvedStyle.width, lh = layer.resolvedStyle.height;
                window.style.left = new StyleLength(float.IsNaN(lw) || lw <= 0 ? 470f : Math.Max(0f, (lw - Width) / 2f));
                window.style.top = new StyleLength(float.IsNaN(lh) || lh <= 0 ? 190f : Math.Max(0f, (lh - Height) / 2f));

                var tabs = AdminItemsUi.Row();
                tabs.style.paddingLeft = new StyleLength(24f);
                tabs.style.paddingTop = new StyleLength(12f);
                tabs.Add(Button("Dons", true, () => ShowTab("dons")));
                tabs.Add(Button("Recherche", false, () => ShowTab("recherche")));
                tabs.Add(Button("Ascension et Évolution", false, () => ShowTab("ascension")));
                window.Add(tabs);

                var scroll = new ScrollView(ScrollViewMode.Vertical);
                scroll.style.flexGrow = new StyleFloat(1f);
                scroll.style.paddingLeft = new StyleLength(24f);
                scroll.style.paddingRight = new StyleLength(24f);
                _page = new VisualElement();
                scroll.Add(_page);
                window.Add(scroll);

                _status = AdminItemsUi.Label("");
                _status.style.paddingLeft = new StyleLength(24f);
                _status.style.paddingBottom = new StyleLength(14f);
                _status.style.color = new StyleColor(new UnityEngine.Color(1f, 0.85f, 0.55f, 1f));
                window.Add(_status);

                layer.Add(window);
                window.BringToFront();
                AdminItemsUi.KeepEvents(window);
                var headerClose = window.header?.rightButton;
                if (headerClose != null) headerClose.add_Clicked(AdminItemsUi.Do(Close));
                _window = window;
                _confirm = "";
                Refresh();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Progression window could not be opened: {ex}");
                Close();
            }
        }

        public static void Close()
        {
            // Closed on an offer: not opened again by itself for that one.
            if (_state is JsonElement state) _dismissedLevel = OfferLevel(state);
            try { _window?.RemoveFromHierarchy(); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Closing the progression window: {ex.Message}"); }
            _window = null;
            _page = null;
            _status = null;
        }

        private static void ShowTab(string tab)
        {
            _tab = tab;
            _confirm = "";
            if (_state is JsonElement state) Show(state);
        }

        private static void Refresh() => Ask("stasis/state", new Dictionary<string, object>(), Show, quiet: true);

        private static void Say(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        // ─── Drawing the state ───────────────────────────────────────────────────────────

        private static void Show(JsonElement root)
        {
            _state = root;
            if (_page == null) return;
            _page.Clear();
            _page.Add(Summary(root));
            switch (_tab)
            {
                case "recherche": ShowResearch(root); break;
                case "ascension": ShowRebirths(root); break;
                default: ShowOffer(root); break;
            }
        }

        private static VisualElement Summary(JsonElement root)
        {
            string text = $"Niveau {Int(root, "level")} (record {Int(root, "best")}) · {Long(root, "eclats")} Éclats · " +
                          $"{Int(root, "ascensions")} Ascension(s)" + (root.GetProperty("evolved").GetBoolean() ? " · évolué" : "");
            int catchUp = Int(root, "catchUp");
            if (catchUp > 1) text += $" · rattrapage x{catchUp}";
            var label = AdminItemsUi.Label(text, true);
            label.style.marginTop = new StyleLength(10f);
            label.style.marginBottom = new StyleLength(10f);
            return label;
        }

        private static void ShowOffer(JsonElement root)
        {
            if (!root.TryGetProperty("offer", out var offer) || offer.ValueKind != JsonValueKind.Object)
            {
                _page.Add(Hint("Aucune offre de don en attente. Chaque niveau gagné jusqu'au 200 en apporte une."));
                ShowSpellPoints(root);
                ShowRepick(root);
                return;
            }
            int pending = Int(root, "pending");
            _page.Add(Title($"{offer.GetProperty("kindLabel").GetString()} — niveau {(offer.TryGetProperty("shownLevel", out _) ? Int(offer, "shownLevel") : Int(offer, "level"))}" +
                            (pending > 1 ? $" ({pending} offres en attente)" : "")));

            var cards = AdminItemsUi.Row();
            cards.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            foreach (var card in offer.GetProperty("cards").EnumerateArray())
                cards.Add(Card(root, card));
            _page.Add(cards);

            if (offer.GetProperty("canReroll").GetBoolean())
            {
                long cost = Long(offer, "rerollCost");
                _page.Add(Button(cost > 0 ? $"Relancer l'offre ({cost} kamas)" : "Relancer l'offre (gratuit)", false,
                    () => Ask("stasis/reroll", new Dictionary<string, object>(), Show)));
            }
            else _page.Add(Hint(offer.GetProperty("rerolled").GetBoolean() ? "Offre déjà relancée." : "Cette offre ne se relance pas."));
            ShowSpellPoints(root);
            ShowRepick(root);
        }

        /// <summary>The spell points: how many spent, the families open, and the spell dons kept.</summary>
        private static void ShowSpellPoints(JsonElement root)
        {
            if (!root.TryGetProperty("spellPoints", out var points) || points.ValueKind != JsonValueKind.Object) return;
            _page.Add(Title($"Dons de sorts — {Int(points, "spent")}/{Int(points, "max")} points"));
            _page.Add(Hint($"Familles de sorts : {Int(points, "families")}/{Int(points, "maxFamilies")} · " +
                           $"au plus {Int(points, "perFamily")} point(s) par famille à ce niveau. Offerts aux niveaux de sort (10, 30, 70...)."));
            foreach (var owned in points.GetProperty("owned").EnumerateArray())
            {
                int maxRank = Int(owned, "maxRank");
                _page.Add(AdminItemsUi.Label($"• {Text(owned, "spellName")} : {Text(owned, "effect")}" +
                                             (maxRank > 1 ? $" (rang {Int(owned, "rank")}/{maxRank})" : "")));
            }
        }

        private static VisualElement Card(JsonElement root, JsonElement card)
        {
            var box = new VisualElement();
            box.style.width = new StyleLength(285f);
            box.style.minHeight = new StyleLength(170f);
            box.style.marginRight = new StyleLength(14f);
            box.style.marginBottom = new StyleLength(14f);
            box.style.paddingLeft = new StyleLength(12f);
            box.style.paddingRight = new StyleLength(12f);
            box.style.paddingTop = new StyleLength(10f);
            box.style.paddingBottom = new StyleLength(10f);
            box.style.backgroundColor = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.07f));
            // A spell don card (SpellDons on the server): the spell's name as the title, the effect
            // under it in the accent of a bonus, the rank it brings, then the sentence. Older
            // servers send no "spell" field and the card draws as before.
            string spellName = Text(card, "spellName");
            if (Int(card, "spell") > 0 && spellName.Length > 0)
            {
                box.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.35f, 0.55f, 1f, 0.12f));
                box.Add(AdminItemsUi.Label("Sort : " + spellName, true));
                var effect = AdminItemsUi.Label(Text(card, "effect"), true);
                effect.style.color = new StyleColor(new UnityEngine.Color(0.55f, 0.9f, 0.55f, 1f));
                effect.style.marginTop = new StyleLength(4f);
                box.Add(effect);
                int maxRank = Int(card, "maxRank");
                if (maxRank > 1) box.Add(AdminItemsUi.Label($"Rang {Int(card, "rank") + 1}/{maxRank}"));
            }
            else box.Add(AdminItemsUi.Label(card.GetProperty("name").GetString(), true));
            var description = AdminItemsUi.Label(card.GetProperty("description").GetString());
            description.style.marginTop = new StyleLength(6f);
            description.style.marginBottom = new StyleLength(10f);
            description.style.flexGrow = new StyleFloat(1f);
            box.Add(description);

            int index = Int(card, "index");
            if (card.GetProperty("characteristic").GetBoolean())
            {
                var choices = AdminItemsUi.Row();
                choices.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
                foreach (var stat in root.GetProperty("characteristics").EnumerateArray())
                {
                    string key = stat.GetProperty("key").GetString();
                    choices.Add(Button(stat.GetProperty("label").GetString(), true, () => Pick(index, key)));
                }
                box.Add(choices);
            }
            else box.Add(Button("Choisir", true, () => Pick(index, "")));
            return box;
        }

        private static void Pick(int index, string stat)
            => Ask("stasis/pick", new Dictionary<string, object> { ["card"] = index, ["stat"] = stat }, root =>
            {
                Show(root);
                Say("Don obtenu.");
            });

        /// <summary>The refonte: two characteristics cycled with a click, and the button that moves the points.</summary>
        private static void ShowRepick(JsonElement root)
        {
            int left = Int(root, "repicks");
            var stats = new List<(string Key, string Label)>();
            foreach (var stat in root.GetProperty("characteristics").EnumerateArray())
                stats.Add((stat.GetProperty("key").GetString(), stat.GetProperty("label").GetString()));
            if (stats.Count < 2) return;
            _repickFrom %= stats.Count;
            _repickTo %= stats.Count;

            _page.Add(Title($"Refonte ({left} restante(s) ce cycle)"));
            _page.Add(Hint("Déplace tous les points de la carte « Caractéristique au choix » d'une caractéristique vers une autre."));
            var row = AdminItemsUi.Row();
            row.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            row.Add(Button("De : " + stats[_repickFrom].Label, false, () => { _repickFrom = (_repickFrom + 1) % stats.Count; Show(root); }));
            row.Add(Button("Vers : " + stats[_repickTo].Label, false, () => { _repickTo = (_repickTo + 1) % stats.Count; Show(root); }));
            if (left > 0)
                row.Add(Button("Refondre", true, () => Ask("stasis/repick", new Dictionary<string, object>
                {
                    ["from"] = stats[_repickFrom].Key, ["to"] = stats[_repickTo].Key,
                }, r => { Show(r); Say("Refonte faite."); })));
            _page.Add(row);
        }

        private static void ShowResearch(JsonElement root)
        {
            _page.Add(Title("Recherche"));
            _page.Add(Hint("Payée en Éclats, gardée à travers les Ascensions et l'Évolution."));
            long eclats = Long(root, "eclats");
            foreach (var research in root.GetProperty("research").EnumerateArray())
            {
                var line = AdminItemsUi.Row();
                line.style.alignItems = new StyleEnum<Align>(Align.Center);
                line.style.marginBottom = new StyleLength(8f);
                line.style.paddingLeft = new StyleLength(8f);
                line.style.backgroundColor = new StyleColor(AdminItemsUi.RowPlain);
                long cost = Long(research, "cost");
                var label = AdminItemsUi.Label($"{research.GetProperty("name").GetString()} {Int(research, "rank")}/{Int(research, "max")} — " +
                                               research.GetProperty("description").GetString());
                label.style.flexGrow = new StyleFloat(1f);
                label.style.flexShrink = new StyleFloat(1f);
                line.Add(label);
                string id = research.GetProperty("id").GetString();
                if (cost >= 0)
                    line.Add(Button($"Acheter ({cost} Éclats)", cost <= eclats, () =>
                        Ask("stasis/research", new Dictionary<string, object> { ["id"] = id }, r => { Show(r); Say("Recherche achetée."); })));
                else line.Add(AdminItemsUi.Label("Max"));
                _page.Add(line);
            }
        }

        private static void ShowRebirths(JsonElement root)
        {
            var asc = root.GetProperty("ascension");
            _page.Add(Title("Ascension"));
            _page.Add(Hint($"Dès le niveau {Int(asc, "minLevel")} : retour au niveau 1 (expérience, caractéristiques et sorts de niveau 1), " +
                           $"objets et kamas conservés, dons perdus. Rapporte {Long(asc, "eclats")} Éclats maintenant, puis expérience " +
                           $"x{Int(asc, "catchUp")} jusqu'à votre record."));
            Rebirth(asc, "ascension", "Faire l'Ascension", "stasis/ascend");

            var evo = root.GetProperty("evolution");
            _page.Add(Title("Évolution"));
            _page.Add(Hint($"Au niveau {Int(evo, "level")}, avec {Long(evo, "cost")} Éclats et les 9 donjons de palier 300 : " +
                           $"retour définitif au niveau {Int(evo, "returnLevel")}, évolué, en gardant les dons jusqu'au niveau 200. " +
                           "Un personnage évolué peut porter les objets stasifiés S3 et S4 et ne fait plus d'Ascension."));
            Rebirth(evo, "evolution", "Évoluer", "stasis/evolve");
        }

        private static void Rebirth(JsonElement what, string key, string label, string route)
        {
            if (!what.GetProperty("can").GetBoolean())
            {
                _page.Add(Hint("Impossible pour l'instant : " + what.GetProperty("why").GetString()));
                return;
            }
            var row = AdminItemsUi.Row();
            if (_confirm != key)
            {
                row.Add(Button(label, true, () => { _confirm = key; if (_state is JsonElement s) Show(s); }));
            }
            else
            {
                row.Add(AdminItemsUi.Label("Sûr ? C'est irréversible.", true));
                row.Add(Button("Confirmer", true, () =>
                {
                    _confirm = "";
                    Ask(route, new Dictionary<string, object> { ["confirm"] = true }, r =>
                    {
                        Show(r);
                        Say(key == "ascension" ? "Ascension accomplie." : "Évolution accomplie.");
                    });
                }));
                row.Add(Button("Annuler", false, () => { _confirm = ""; if (_state is JsonElement s) Show(s); }));
            }
            _page.Add(row);
        }

        // ─── Pieces ──────────────────────────────────────────────────────────────────────

        private static VisualElement Title(string text)
        {
            var label = new DofusLabel();
            label.text = text;
            label.AddToClassList("title_large");
            label.AddToClassList("textColor_white_white100");
            label.style.marginTop = new StyleLength(14f);
            label.style.marginBottom = new StyleLength(8f);
            return label;
        }

        private static DofusLabel Hint(string text)
        {
            var label = AdminItemsUi.Label(text);
            label.style.color = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.7f));
            label.style.marginBottom = new StyleLength(10f);
            return label;
        }

        private static VisualElement Button(string text, bool primary, Action click)
        {
            try
            {
                var button = new DofusButtonCustom(AdminItemsUi.Do(click), text, AdminItemsUi.NoIcon());
                button.mainStyle = primary ? DofusButtonCustom.ComponentStyleEnum.primary : DofusButtonCustom.ComponentStyleEnum.secondary;
                button.style.marginRight = new StyleLength(10f);
                button.style.marginBottom = new StyleLength(8f);
                button.style.flexShrink = new StyleFloat(0f);
                return button;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Progression: the client's button would not build ({ex.Message}); drawing one.");
                return AdminItemsUi.FilterButton(text, primary, true, click);
            }
        }

        private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

        // ─── Talking to the server ───────────────────────────────────────────────────────

        /// <summary>A control request off the frame; the answer (or its refusal, said) back on it.</summary>
        private static void Ask(string route, Dictionary<string, object> body, Action<JsonElement> then, bool quiet = false, Action failed = null)
        {
            if (Token.Length == 0) return;
            body["token"] = Token;
            if (!quiet) Say("…");
            Task.Run(async () =>
            {
                var (code, text) = await AdminItemsUi.PostAsync(route, body);
                _toUnity.Enqueue(() =>
                {
                    if (code != 200)
                    {
                        failed?.Invoke();
                        if (!quiet || _window != null) Say(Refusal(code, text));
                        return;
                    }
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (!quiet) Say("");
                        then(doc.RootElement.Clone());
                    }
                    catch (Exception ex)
                    {
                        failed?.Invoke();
                        Say("Erreur : " + ex.Message);
                    }
                });
            });
        }

        private static string Refusal(int code, string body)
        {
            if (code == 0) return "Serveur injoignable.";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    string reason = error.GetString() ?? "";
                    return reason switch
                    {
                        "world" => "Votre personnage n'est pas en jeu.",
                        "sesion" => "Session non reconnue.",
                        "personaje-ocupado" => "Personnage occupé, réessayez.",
                        _ => reason,
                    };
                }
            }
            catch { }
            return "Refusé (" + code.ToString(CultureInfo.InvariantCulture) + ").";
        }
    }
}
