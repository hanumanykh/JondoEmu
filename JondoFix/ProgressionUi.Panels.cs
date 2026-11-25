using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using UnityEngine.UIElements;
using K = JondoFix.UiKit;

namespace JondoFix
{
    /// <summary>
    /// The F9 window's tabs drawn as widgets (UiKit): the farming and endgame tabs from the «view»
    /// of /api/stasis/panel (Jauge, Comptoir, Marées, Paliers, Événements, Fissure, Forge Stasis),
    /// and the Recherche, Ascension, Sanctuaire, Missions, Pension, Cartes and Éveil tabs from
    /// their own routes. Every action is a button, a list or a picker: no chat command is needed.
    /// The header bar with the Jondo Coins is here too. The DONS tab is not (ProgressionUi.cs).
    /// </summary>
    public static partial class ProgressionUi
    {
        // ─── Small JSON readers ──────────────────────────────────────────────────────────

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static bool Obj(JsonElement e, string name, out JsonElement inner)
        {
            inner = default;
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return false;
            inner = v;
            return true;
        }

        private static List<JsonElement> Arr(JsonElement e, string name)
        {
            var list = new List<JsonElement>();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray()) list.Add(item);
            return list;
        }

        private static string F(long n) => K.Number(n);

        /// <summary>Redraws the tab shown from what is known, without asking anything.</summary>
        private static void Redraw()
        {
            if (_state is JsonElement s) Show(s);
        }

        // ─── The server's sentences, without the chat's commands ─────────────────────────

        private static readonly Regex CommandWord = new Regex(@"(^|[\s(«])\.[a-zà-ÿ]{2,}", RegexOptions.IgnoreCase);

        /// <summary>
        /// A server answer as the window says it: the hints «.xxx …» written for the chat are taken
        /// out (the window has buttons for them); the rest is kept. Nothing left: the answer as is.
        /// </summary>
        internal static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            try
            {
                string s = Regex.Replace(text, @"\s*«\s*\.[^»]*»", "");
                s = Regex.Replace(s, @"\s*\([^()]*(^|\s)\.[a-zà-ÿ]{2,}[^()]*\)", "", RegexOptions.IgnoreCase);
                var kept = Regex.Split(s, @"(?<=[.!?])\s+").Where(p => p.Trim().Length > 0 && !CommandWord.IsMatch(p));
                string result = string.Join(" ", kept).Trim();
                return result.Length > 0 ? result : text;
            }
            catch
            {
                return text;
            }
        }

        // ─── The header bar: level, Éclats, coins ────────────────────────────────────────

        private static VisualElement _topBar;
        private static long _coins = -1, _stateCoins = -1;
        private static string _coinLabel = "Jondo Coins";
        private static int _coinItem = K.JondoCoin;

        /// <summary>The bar under the title, above the tabs: built once per opening, refilled by <see cref="UpdateTopBar"/>.</summary>
        private static VisualElement TopBar()
        {
            _topBar = K.Row();
            _topBar.style.paddingLeft = new StyleLength(24f);
            _topBar.style.paddingRight = new StyleLength(24f);
            _topBar.style.paddingTop = new StyleLength(8f);
            _topBar.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            return _topBar;
        }

        /// <summary>The coins any answer carries (coins/orbes and their label), shown at once in the bar.</summary>
        private static void NoteCoins(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("coins", out var c) && c.ValueKind == JsonValueKind.Number) _coins = c.GetInt64();
            else if (e.TryGetProperty("orbes", out var o) && o.ValueKind == JsonValueKind.Number) _coins = o.GetInt64();
            else return;
            string label = Text(e, "coinLabel");
            if (label.Length == 0) label = Text(e, "currency");
            if (label.Length == 0) label = Text(e, "orbeLabel");
            if (label.Length > 0) _coinLabel = label;
            if (e.TryGetProperty("coinItem", out var item) && item.ValueKind == JsonValueKind.Number) _coinItem = item.GetInt32();
            if (_state is JsonElement s) UpdateTopBar(s);
        }

        private static void UpdateTopBar(JsonElement root)
        {
            // The state's coins when they are new to us (a fresh state); a redraw from an older one keeps the newer count.
            if (root.TryGetProperty("coins", out var c) && c.ValueKind == JsonValueKind.Number && (_coins < 0 || c.GetInt64() != _stateCoins))
            {
                _stateCoins = _coins = c.GetInt64();
                if (Text(root, "coinLabel").Length > 0) _coinLabel = Text(root, "coinLabel");
            }
            if (_topBar == null) return;
            _topBar.Clear();
            _topBar.Add(K.Badge($"Niveau {Int(root, "level")}", K.Gold));
            if (Int(root, "best") > Int(root, "level")) _topBar.Add(K.Badge($"Record {Int(root, "best")}", K.Gold, false));
            _topBar.Add(K.Badge($"{F(Long(root, "eclats"))} Éclats", K.Blue));
            _topBar.Add(K.Badge($"{Int(root, "ascensions")} Ascension(s)", K.Purple));
            if (root.TryGetProperty("evolved", out var ev) && ev.ValueKind == JsonValueKind.True) _topBar.Add(K.Badge("Évolué", K.Green));
            if (Dbl(root, "catchUp") > 1) _topBar.Add(K.Badge($"Rattrapage x{Times(root, "catchUp")}", K.Green, false));
            var spacer = new VisualElement();
            spacer.style.flexGrow = new StyleFloat(1f);
            _topBar.Add(spacer);
            if (_coins >= 0)
            {
                var coins = K.Row();
                K.Pad(coins, 10f, 3f);
                coins.style.backgroundColor = new StyleColor(K.Alpha(K.Gold, 0.15f));
                K.Border(coins, 1f, K.GoldSoft);
                K.Round(coins, 12f);
                coins.Add(K.Price(_coins, _coinLabel, _coinItem));
                if (_coinLabel.IndexOf("Jondo", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var unit = K.Small(" Jondo Coins");
                    unit.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                    coins.Add(unit);
                }
                _topBar.Add(coins);
            }
        }

        // ─── The panel tabs ──────────────────────────────────────────────────────────────

        /// <summary>A button of a panel tab: the command, typed by the server as the player would.</summary>
        private static void PanelDo(string command, string args)
        {
            _confirm = "";
            AskPanel("stasis/act", new Dictionary<string, object> { ["tab"] = _tab, ["command"] = command, ["args"] = args });
        }

        /// <summary>A panel tab from its «view»; the old text lines when the server sends none.</summary>
        private static void ShowPanelKit()
        {
            if (!(_panel is JsonElement panel) || Text(panel, "tab") != _tab)
            {
                _page.Add(K.Small("Chargement…"));
                return;
            }
            NoteCoins(panel);
            if (!Obj(panel, "view", out var view))
            {
                ShowPanel();
                return;
            }
            NoteCoins(view);
            switch (_tab)
            {
                case "jauge": ShowGauge(view); break;
                case "comptoir": ShowCounter(view); break;
                case "maree": ShowTides(view); break;
                case "paliers": ShowTiers(view); break;
                case "event": ShowEvents(view); break;
                case "fissure": ShowFissure(view); break;
                case "stasis": ShowForge(view); break;
                default: ShowPanel(); break;
            }
        }

        private static VisualElement Unavailable(string title, string reason)
        {
            var card = K.Card(0f, K.Red);
            card.Add(K.CardHead(K.Glyph("!", K.Red), title, ""));
            card.Add(K.Text(reason));
            return card;
        }

        // ─── Jauge ───────────────────────────────────────────────────────────────────────

        private static void ShowGauge(JsonElement v)
        {
            if (!Bool(v, "available"))
            {
                _page.Add(Unavailable("Jauge de zone", Text(v, "reason")));
                return;
            }
            Obj(v, "zone", out var zone);
            int progress = Int(v, "progress"), threshold = Math.Max(1, Int(v, "threshold"));
            bool full = Bool(v, "full");

            var card = K.Card(0f, full ? K.Gold : K.Green);
            card.Add(K.CardHead(K.Glyph("J", K.Gold), "Jauge — " + Text(zone, "name"), $"Zone de niveau {Int(zone, "level")}",
                full ? K.Badge("Pleine !", K.Gold) : null));
            card.Add(K.ProgressBar(progress / (float)threshold, $"{F(progress)} / {F(threshold)}  ({Int(v, "percent")} %)", full ? K.Gold : K.Green, 24f));
            card.Add(K.Small($"Objets garantis ce cycle : {Int(v, "completions")} · retour au seuil de base dans {Text(v, "resetIn")}."));

            var multipliers = K.Grid();
            multipliers.style.marginTop = new StyleLength(8f);
            foreach (var m in Arr(v, "multipliers"))
            {
                var box = K.Column();
                box.style.marginRight = new StyleLength(14f);
                box.style.marginBottom = new StyleLength(4f);
                bool active = Bool(m, "active");
                box.Add(K.Badge($"{Text(m, "label")} x{Dbl(m, "factor").ToString("0.#", CultureInfo.InvariantCulture)}", active ? K.Blue : K.Muted, active));
                box.Add(K.Small(Text(m, "note")));
                multipliers.Add(box);
            }
            card.Add(multipliers);
            _page.Add(card);

            int target = Int(v, "target");
            _page.Add(K.Section("Choisir la cible", "L'objet garanti que la jauge vous donne quand elle est pleine. Cliquez sur un objet pour le cibler."));
            var current = K.Card(0f, target != 0 ? K.Gold : (UnityEngine.Color?)null);
            current.Add(K.CardHead(K.ItemIcon(target, 56f, target == 0 ? "?" : null), target != 0 ? Text(v, "targetName") : "Aucune cible",
                target != 0 ? "Cible actuelle" : (full ? "La jauge est pleine : choisissez une cible pour recevoir l'objet !" : "Choisissez un objet ci-dessous.")));
            _page.Add(current);

            var grid = K.Grid();
            foreach (var item in Arr(v, "items"))
            {
                bool picked = Bool(item, "target");
                int index = Int(item, "index");
                grid.Add(K.ItemTile(Int(item, "gid"), Text(item, "name"), picked ? "Cible" : "", picked,
                    picked ? (Action)null : () => PanelDo(".jauge", "cible " + index.ToString(CultureInfo.InvariantCulture))));
            }
            _page.Add(grid);
        }

        // ─── Comptoir ────────────────────────────────────────────────────────────────────

        private static (string Glyph, UnityEngine.Color Color) BuffLook(string id) => id switch
        {
            "densite" => ("D", K.Green),
            "recrue" => ("R", K.Purple),
            "etude" => ("É", K.Blue),
            "jauge" => ("J", K.Gold),
            _ => ("?", K.Muted),
        };

        private static void ShowCounter(JsonElement v)
        {
            if (!Bool(v, "available"))
            {
                _page.Add(Unavailable("Comptoir de zone", Text(v, "reason")));
                return;
            }
            Obj(v, "zone", out var zone);
            _page.Add(K.Section($"Comptoir — {Text(zone, "name")} (niv. {Int(zone, "level")})",
                $"Chaque buff dure {Int(v, "durationMinutes")} min dans cette zone ; racheter prolonge. Densité et Recrue ne se cumulent pas."));
            string unit = Text(v, "coinLabel");
            int coinItem = Int(v, "coinItem");
            var grid = K.Grid();
            foreach (var b in Arr(v, "buffs"))
            {
                string id = Text(b, "id");
                var (glyph, color) = BuffLook(id);
                bool active = Bool(b, "active");
                var card = K.Card(290f, active ? color : (UnityEngine.Color?)null);
                card.Add(K.CardHead(K.Glyph(glyph, color), Text(b, "label"), Text(b, "effect"),
                    active ? K.Badge("Actif", K.Green) : null));
                if (active) card.Add(K.Fact("Temps restant", Text(b, "remaining"), K.Green));
                var price = K.Row();
                price.style.marginTop = new StyleLength(6f);
                price.style.marginBottom = new StyleLength(6f);
                price.Add(K.Small("Prix : "));
                price.Add(K.Price(Long(b, "price"), unit, coinItem));
                card.Add(price);
                string why = Bool(b, "canBuy") ? null : Text(b, "why");
                card.Add(K.Button(active ? "Prolonger" : "Acheter", () => PanelDo(".comptoir", id), true, why));
                grid.Add(card);
            }
            _page.Add(grid);
        }

        // ─── Marées ──────────────────────────────────────────────────────────────────────

        private static void ShowTides(JsonElement v)
        {
            bool active = Bool(v, "active");
            var head = K.Card(0f, active ? K.Blue : (UnityEngine.Color?)null);
            head.Add(K.CardHead(K.Glyph("~", K.Blue), active ? "Marée de Stasis en cours" : "Pas de Marée en ce moment",
                active ? $"Encore {Text(v, "left")}" : $"Prochaine Marée dans {Text(v, "left")}"));
            float span = (active ? Int(v, "onMinutes") : Int(v, "offMinutes")) * 60f;
            head.Add(K.ProgressBar(span <= 0 ? 0f : Long(v, "leftSeconds") / span, active ? "Temps restant" : "Avant la prochaine", K.Blue));
            var boosts = K.Grid();
            boosts.Add(K.Badge($"+{Int(v, "experiencePercent")} % d'XP", K.Green));
            boosts.Add(K.Badge("Kamas x2", K.Gold));
            boosts.Add(K.Badge("Jauge x2", K.Blue));
            head.Add(boosts);
            if (!Bool(v, "enabled")) head.Add(K.Small("Les Marées sont désactivées sur ce serveur."));
            _page.Add(head);

            if (Bool(v, "global"))
            {
                _page.Add(K.Small("La Marée couvre le monde entier : toutes les zones en profitent."));
                return;
            }
            _page.Add(K.Section(active ? "Zones sous la Marée" : "Zones de la prochaine Marée",
                "Une zone par tranche de niveaux. Le bonus d'XP vaut pour votre tranche (avec une tolérance)."));
            var grid = K.Grid();
            foreach (var z in Arr(v, "zones"))
            {
                bool mine = Bool(z, "forMe");
                var card = K.Card(290f, mine ? K.Blue : (UnityEngine.Color?)null);
                card.Add(K.CardHead(K.Glyph("~", K.Blue, 40f), Text(z, "name"), $"Niveaux {Int(z, "minLevel")} – {Int(z, "maxLevel")}",
                    mine ? K.Badge("Votre tranche", K.Green) : null, Bool(z, "here") ? K.Badge("Vous y êtes", K.Gold) : null));
                var badges = K.Grid();
                foreach (var boost in Arr(z, "boosts")) badges.Add(K.Badge(boost.GetString() ?? "", K.Blue, false));
                card.Add(badges);
                int subArea = Int(z, "subArea");
                card.Add(K.Button("Y aller", () => PanelDo(".maree", "aller " + subArea.ToString(CultureInfo.InvariantCulture)), true,
                    Bool(z, "canGo") ? null : Text(z, "why")));
                grid.Add(card);
            }
            _page.Add(grid);
        }

        // ─── Paliers ─────────────────────────────────────────────────────────────────────

        /// <summary>A hub command (a key in coins) from another tab: said, then the tab asked again.</summary>
        private static void HubThenPanel(string command, string args)
        {
            string tab = _tab;
            Ask("stasis/hub", new Dictionary<string, object> { ["command"] = command, ["args"] = args }, r =>
            {
                Say(Clean(Text(r, "text")));
                NoteCoins(r);
                if (_tab == tab) AskPanel("stasis/panel", new Dictionary<string, object> { ["tab"] = tab });
            });
        }

        private static void ShowTiers(JsonElement v)
        {
            int gate = Int(v, "gate");
            var top = K.Card(0f, gate != 0 ? K.Red : K.Green);
            top.Add(K.CardHead(K.Glyph("P", K.Gold), $"Paliers — niveau {Int(v, "level")}",
                Bool(v, "evolved") ? "Personnage évolué : seuls les donjons faits en hardcore comptent." : "Terminez les donjons de chaque palier pour monter librement.",
                gate != 0 ? K.Badge($"XP bloquée au niv. {Int(v, "gateCap")}", K.Red) : K.Badge("Aucun blocage d'XP", K.Green)));
            if (gate != 0) top.Add(K.Small($"Terminez le palier {gate} : au-delà du niveau {Int(v, "gateCap")}, l'expérience est perdue."));

            // Hardcore and the full room, as toggles.
            var options = K.Grid();
            options.style.marginTop = new StyleLength(8f);
            var hc = K.Column();
            hc.style.width = new StyleLength(420f);
            hc.style.marginRight = new StyleLength(16f);
            bool hardcore = Bool(v, "hardcore");
            var hcHead = K.Row();
            hcHead.Add(K.Text("Mode hardcore", true));
            hcHead.Add(K.Badge(hardcore ? "Activé" : "Désactivé", hardcore ? K.Red : K.Muted, hardcore));
            hc.Add(hcHead);
            hc.Add(K.Small(Text(v, "hardcoreText")));
            hc.Add(K.Button(hardcore ? "Désactiver le hardcore" : "Activer le hardcore", () => PanelDo(".hardcore", hardcore ? "off" : "on"), !hardcore,
                Bool(v, "canHardcore") ? null : Text(v, "hardcoreWhy")));
            options.Add(hc);
            var room = K.Column();
            room.style.width = new StyleLength(380f);
            bool full = Bool(v, "fullRoom");
            var roomHead = K.Row();
            roomHead.Add(K.Text("Salle complète", true));
            roomHead.Add(K.Badge(full ? "Oui" : "Non", full ? K.Gold : K.Muted, full));
            room.Add(roomHead);
            room.Add(K.Small("Chaque salle se bat comme à 4 : plus dur, plus rentable. Choisi par le chef du groupe."));
            room.Add(K.Button(full ? "Désactiver" : "Activer", () => PanelDo(".donjon", full ? "options salle off" : "options salle on"), false));
            options.Add(room);
            top.Add(options);
            top.Add(K.Small($"Entrées gratuites : une par donjon et par semaine, utilisée avant les clés (retour le mardi {Text(v, "freeReset")})."));
            _page.Add(top);

            _page.Add(K.Section("Parcours des paliers"));
            long kamas = Long(v, "kamas");
            string unit = Text(v, "coinLabel");
            int coinItem = Int(v, "coinItem");
            var tiers = Arr(v, "tiers");
            for (int i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                bool complete = Bool(tier, "complete"), reached = Bool(tier, "reached"), blocking = Bool(tier, "blocking");
                var color = complete ? K.Green : blocking ? K.Red : reached ? K.Gold : K.Muted;

                var line = K.Row();
                line.style.alignItems = new StyleEnum<Align>(Align.Stretch);
                // The track: a round step with the tier's level, and the rail to the next one.
                var rail = K.Column();
                rail.style.width = new StyleLength(64f);
                rail.style.alignItems = new StyleEnum<Align>(Align.Center);
                var step = K.Column();
                step.style.width = new StyleLength(52f);
                step.style.height = new StyleLength(52f);
                step.style.alignItems = new StyleEnum<Align>(Align.Center);
                step.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
                step.style.backgroundColor = new StyleColor(K.Alpha(color, 0.25f));
                K.Border(step, 2f, color);
                K.Round(step, 26f);
                var number = K.Text(Int(tier, "level").ToString(CultureInfo.InvariantCulture), true, 16f, UnityEngine.Color.white);
                number.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
                step.Add(number);
                rail.Add(step);
                if (i < tiers.Count - 1)
                {
                    var bar = new VisualElement();
                    bar.style.width = new StyleLength(4f);
                    bar.style.flexGrow = new StyleFloat(1f);
                    bar.style.minHeight = new StyleLength(16f);
                    bar.style.backgroundColor = new StyleColor(K.Alpha(complete ? K.Green : K.Muted, 0.5f));
                    rail.Add(bar);
                }
                line.Add(rail);

                var card = K.Card(0f, color);
                card.style.flexGrow = new StyleFloat(1f);
                card.style.flexShrink = new StyleFloat(1f);
                card.style.marginLeft = new StyleLength(8f);
                if (!reached && !complete) card.style.opacity = new StyleFloat(0.7f);
                int done = Int(tier, "done"), total = Math.Max(1, Int(tier, "total"));
                card.Add(K.CardHead(null, $"Palier {Int(tier, "level")}", $"{done}/{Int(tier, "total")} donjons terminés",
                    complete ? K.Badge("Terminé", K.Green) : blocking ? K.Badge("Bloquant", K.Red) : !reached ? K.Badge("Verrouillé", K.Muted, false) : K.Badge("En cours", K.Gold)));
                card.Add(K.ProgressBar(done / (float)total, $"{done} / {Int(tier, "total")}", color));
                foreach (var d in Arr(tier, "dungeons"))
                {
                    var row = K.Row();
                    row.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
                    row.style.marginTop = new StyleLength(6f);
                    bool ok = Bool(d, "done");
                    row.Add(K.Glyph(ok ? "✓" : "•", ok ? K.Green : K.Muted, 26f));
                    var text = K.Fill();
                    text.style.marginLeft = new StyleLength(8f);
                    text.Add(K.Text(Text(d, "name"), ok, 15f, ok ? K.Green : UnityEngine.Color.white));
                    var facts = K.Grid();
                    if (Int(d, "minLevel") > 0) facts.Add(K.Badge($"Niv. {Int(d, "minLevel")}+", K.Muted, false));
                    if (Bool(d, "freeEntry")) facts.Add(K.Badge("Entrée gratuite dispo", K.Green, false));
                    text.Add(facts);
                    row.Add(text);
                    int keyItem = Int(d, "keyItem");
                    if (keyItem != 0 && !ok)
                    {
                        int id = Int(d, "id");
                        var keys = K.Column();
                        keys.style.alignItems = new StyleEnum<Align>(Align.FlexEnd);
                        long price = Long(d, "keyPrice");
                        if (price >= 0)
                            keys.Add(K.IconButton(keyItem, $"Clé : {F(price)} kamas", () => PanelDo(".cle", id.ToString(CultureInfo.InvariantCulture)), false,
                                Bool(d, "canBuyKey") ? null : Text(d, "keyWhy")));
                        long coins = Long(d, "keyCoins");
                        if (coins > 0)
                            keys.Add(K.Button($"Clé : {F(coins)} {unit}", () => HubThenPanel(".marchand", "cle " + id.ToString(CultureInfo.InvariantCulture)), false,
                                Bool(d, "canBuyKeyCoins") ? null : $"Il vous manque {F(Math.Max(0, coins - _coins))} {unit}."));
                        row.Add(keys);
                    }
                    card.Add(row);
                }
                line.Add(card);
                _page.Add(line);
            }
            if (tiers.Count == 0) _page.Add(K.Small("Aucun palier n'est configuré sur ce serveur."));
            ShowKeyShop(v, unit);
        }

        // ─── Clés de donjon (server: TiersView «keys», bought with .marchand cle <id> / .cle <id>) ───

        private static string _keyFilter = "";

        private static void ShowKeyShop(JsonElement v, string unit)
        {
            var keys = Arr(v, "keys");
            bool inCoins = Bool(v, "keysInCoins");
            _page.Add(K.Section("Clés de donjon", "Achetez la clé de n'importe quel donjon de votre niveau, d'où que vous soyez."));
            var bar = K.Row();
            bar.style.alignItems = new StyleEnum<Align>(Align.Center);
            var search = new TextInput();
            search.placeholderText = "Rechercher un donjon…";
            search.text = _keyFilter;
            search.style.width = new StyleLength(320f);
            search.style.marginRight = new StyleLength(8f);
            bar.Add(search);
            bar.Add(K.Button("Filtrer", () => { _keyFilter = (search.text ?? "").Trim(); Redraw(); }, false));
            if (_keyFilter.Length > 0) bar.Add(K.Button("Tout voir", () => { _keyFilter = ""; Redraw(); }, false));
            _page.Add(bar);

            string wanted = Fold(_keyFilter);
            var grid = K.Grid();
            int shown = 0;
            foreach (var d in keys)
            {
                string name = Text(d, "name");
                if (wanted.Length > 0 && !Fold(name + " " + Text(d, "keyName")).Contains(wanted)) continue;
                if (++shown > 60) break;
                string id = Int(d, "id").ToString(CultureInfo.InvariantCulture);
                long price = Long(d, "price");
                var card = K.Card(260f);
                card.Add(K.CardHead(K.ItemIcon(Int(d, "keyItem"), 40f), name, $"Niv. {Int(d, "minLevel")}+ · {Text(d, "keyName")}"));
                card.Add(K.Small($"{F(price)} {(inCoins ? unit : "kamas")}"));
                card.Add(K.Button("Acheter", () =>
                {
                    if (inCoins) HubThenPanel(".marchand", "cle " + id);
                    else PanelDo(".cle", id);
                }, true, Bool(d, "canBuy") ? null : "Fonds insuffisants."));
                grid.Add(card);
            }
            _page.Add(grid);
            if (shown == 0) _page.Add(K.Small(keys.Count == 0 ? "Aucun donjon à clé à votre niveau." : "Aucun donjon ne correspond."));
            else if (shown > 60) _page.Add(K.Small("Plus de 60 résultats : affinez la recherche."));
        }

        private static string Fold(string text)
        {
            var normalized = (text ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (char c in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }

        // ─── Événements ──────────────────────────────────────────────────────────────────

        private static bool _runePicking;

        private static void ShowEvents(JsonElement v)
        {
            long tokens = Long(v, "tokens");
            var head = K.Row();
            head.style.marginTop = new StyleLength(8f);
            head.Add(K.Badge($"Jetons Event : {F(tokens)}", K.Purple));
            _page.Add(head);

            if (Obj(v, "running", out var live))
            {
                var card = K.Card(0f, K.Gold);
                card.Add(K.CardHead(K.Glyph("★", K.Gold), $"En cours : {Text(live, "name")}", $"Encore {Int(live, "leftMinutes")} min", K.Badge("En direct", K.Red)));
                card.Add(K.Text(Text(live, "rules")));
                if (Text(live, "clues").Length > 0) card.Add(K.Small(Text(live, "clues")));
                if (Text(live, "kind") == "poutch")
                    card.Add(K.Button("Aller au Poutch", () => PanelDo(".event", "aller"), true, Bool(live, "canGo") ? null : "Indisponible en combat."));
                var ranks = K.Grid();
                foreach (var cat in Arr(live, "categories"))
                {
                    var col = K.Column();
                    col.style.width = new StyleLength(270f);
                    col.style.marginRight = new StyleLength(12f);
                    col.style.marginTop = new StyleLength(6f);
                    col.Add(K.Text($"Catégorie {Text(cat, "category")}", true, 15f, K.Gold));
                    foreach (var e in Arr(cat, "top"))
                        col.Add(K.Text($"{Int(e, "rank")}. {Text(e, "name")} — {Text(e, "score")}", Bool(e, "me"), 14f));
                    if (Int(cat, "myRank") > 0) col.Add(K.Small($"Vous : {Int(cat, "myRank")}e ({Text(cat, "myScore")})", K.Green));
                    ranks.Add(col);
                }
                card.Add(ranks);
                _page.Add(card);
            }

            _page.Add(K.Section("Programme", "Un événement par jour. Récompenses en Jetons Event pour le podium de chaque catégorie (I : jusqu'au 200, II : 201+, III : évolués)."));
            var schedule = K.Grid();
            foreach (var day in Arr(v, "schedule"))
            {
                string status = Text(day, "status");
                var color = status == "En cours" ? K.Red : status == "Aujourd'hui" ? K.Gold : status == "À venir" ? K.Blue : K.Muted;
                var card = K.Card(280f, status == "Aujourd'hui" || status == "En cours" ? color : (UnityEngine.Color?)null);
                card.Add(K.CardHead(null, $"{Text(day, "day")} {Text(day, "date")}", $"{Text(day, "name")} · {Int(day, "hour")}h · {Int(day, "minutes")} min",
                    K.Badge(status, color, status != "Passé" && status != "Terminé")));
                card.Add(K.Small(Text(day, "rules")));
                foreach (var r in Arr(day, "rewards")) card.Add(K.Small(r.GetString() ?? "", K.Purple));
                schedule.Add(card);
            }
            _page.Add(schedule);

            _page.Add(K.Section("Boutique du Gardien de la Fissure", "Payée en Jetons Event."));
            var shop = K.Grid();
            foreach (var a in Arr(v, "shop"))
            {
                string id = Text(a, "id");
                bool rune = Bool(a, "rune");
                var card = K.Card(220f);
                card.style.alignItems = new StyleEnum<Align>(Align.Center);
                card.Add(K.ItemIcon(Int(a, "item"), 56f, rune ? "R" : "?", rune ? K.Purple : (UnityEngine.Color?)null));
                var name = K.Text(Text(a, "name") + (Int(a, "quantity") > 1 && !Text(a, "name").Contains("x") ? $" x{Int(a, "quantity")}" : ""), true, 14f);
                name.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.UpperCenter);
                name.style.marginTop = new StyleLength(6f);
                card.Add(name);
                card.Add(K.Badge($"{Int(a, "price")} jetons", K.Purple));
                string why = Bool(a, "canBuy") ? null : Text(a, "why");
                if (rune)
                    card.Add(K.Button(_runePicking ? "Fermer le choix" : "Choisir l'objet…", () => { _runePicking = !_runePicking; Redraw(); }, !_runePicking, why));
                else
                    card.Add(K.Button("Acheter", () => PanelDo(".jetons", "acheter " + id), true, why));
                shop.Add(card);
            }
            _page.Add(shop);

            if (_runePicking)
            {
                int cap = Int(v, "runeCap");
                _page.Add(K.Section("Rune Do Sort : choisissez l'objet", $"+1 % de dégâts par rune, {cap} % au plus par objet (actif quand il est porté)."));
                var grid = K.Grid();
                foreach (var p in Arr(v, "runeTargets"))
                {
                    long uid = Long(p, "uid");
                    bool fullRune = Bool(p, "full");
                    string line = $"{Int(p, "rune")}/{cap} %" + (Bool(p, "worn") ? " · porté" : "");
                    grid.Add(K.ItemTile(Int(p, "gid"), Text(p, "name"), fullRune ? "Maximum atteint" : line, false,
                        fullRune ? (Action)null : () => { _runePicking = false; PanelDo(".jetons", "acheter rune " + uid.ToString(CultureInfo.InvariantCulture)); }));
                }
                _page.Add(grid);
            }
        }

        // ─── Fissure ─────────────────────────────────────────────────────────────────────

        private static void ShowFissure(JsonElement v)
        {
            if (!Bool(v, "enabled")) _page.Add(Unavailable("Fissure", "La Fissure est fermée sur ce serveur."));
            int needed = Math.Max(1, Int(v, "needed")), eligible = Int(v, "eligible"), owed = Int(v, "owed");
            var progress = K.Card(0f, owed > 0 ? K.Gold : K.Purple);
            progress.Add(K.CardHead(K.Glyph("F", K.Purple), "Fissure — votre progression",
                $"{F(Long(v, "waves"))} vagues gagnées · record {Int(v, "best")}", owed > 0 ? K.Badge($"{owed} Dofus à réclamer", K.Gold) : null));
            progress.Add(K.Small("Vers un Dofus garanti (vagues éligibles) :"));
            progress.Add(K.ProgressBar(eligible / (float)needed, $"{F(eligible)} / {F(needed)}", K.Purple, 22f));
            var dofus = K.Grid();
            foreach (var d in Arr(v, "dofus"))
            {
                string name = Text(d, "name");
                if (owed > 0) dofus.Add(K.IconButton(Int(d, "item"), "Réclamer " + name, () => PanelDo(".fissure", "dofus " + name)));
                else dofus.Add(K.ItemTile(Int(d, "item"), name, "", false, null, 96f));
            }
            progress.Add(dofus);
            _page.Add(progress);

            if (Obj(v, "expedition", out var exp))
            {
                var card = K.Card(0f, K.Red);
                card.Add(K.CardHead(K.Glyph("⚔", K.Red), $"Expédition {Text(exp, "band")} en cours",
                    $"Vague {Int(exp, "wave")} (monstres niveau {Int(exp, "monsterLevel")}) · {Int(exp, "cleared")} vague(s) gagnée(s) · {Int(exp, "fighters")} combattant(s)"));
                var row = K.Grid();
                if (_confirm != "fissure-quit")
                    row.Add(K.Button("Abandonner et sortir", () => { _confirm = "fissure-quit"; Redraw(); }, false));
                else
                {
                    row.Add(K.Text("Abandonner ? Les vagues gagnées restent acquises.", true));
                    row.Add(K.Button("Confirmer", () => PanelDo(".fissure", "quitter")));
                    row.Add(K.Button("Annuler", () => { _confirm = ""; Redraw(); }, false));
                }
                card.Add(row);
                _page.Add(card);
            }
            if (Bool(v, "canJoin"))
                _page.Add(K.Button("Rejoindre l'expédition de mon groupe", () => PanelDo(".fissure", "rejoindre")));

            int fighters = Int(v, "fighters");
            _page.Add(K.Section("Clés de la Fissure",
                $"Une clé par combattant ({fighters} dans votre groupe). Bonus de formation à {fighters} : {Text(v, "formation")}."));
            string unit = Text(v, "coinLabel");
            int coinItem = Int(v, "coinItem");
            var grid = K.Grid();
            foreach (var b in Arr(v, "bands"))
            {
                string id = Text(b, "id");
                var color = id == "abime" ? K.Red : id == "profondeur" ? K.Purple : K.Blue;
                var card = K.Card(300f, color);
                card.Add(K.CardHead(K.Glyph(Text(b, "name").Length > 0 ? Text(b, "name").Substring(0, 1) : "?", color), Text(b, "name"), $"Niveau {Int(b, "minLevel")}+",
                    Bool(b, "dofusEligible") ? K.Badge("Dofus", K.Gold, false) : null));
                card.Add(K.Fact("Monstres", $"niv. {Text(b, "levels")} (+{Int(b, "levelPerWave")} par vague)"));
                card.Add(K.Fact("Validation", $"{Int(b, "validate")} vague(s)"));
                card.Add(K.Fact("Vagues par clé", Int(b, "limit") > 0 ? Int(b, "limit").ToString(CultureInfo.InvariantCulture) : "sans limite"));
                card.Add(K.Fact("Par vague", $"{F(Long(b, "kamasPerWave"))} kamas × vague, {Int(b, "tokensPerWave")} jeton(s) (+{Int(b, "validatedTokens")} à la validation)"));
                var resources = Arr(b, "resources");
                if (resources.Count > 0)
                {
                    var res = K.Grid();
                    res.style.marginTop = new StyleLength(4f);
                    foreach (var r in resources)
                    {
                        var cell = K.Column();
                        cell.style.alignItems = new StyleEnum<Align>(Align.Center);
                        cell.style.marginRight = new StyleLength(6f);
                        cell.Add(K.ItemIcon(Int(r, "item"), 36f));
                        cell.Add(K.Small($"{Int(r, "percent")} %"));
                        res.Add(cell);
                    }
                    card.Add(res);
                }
                var price = K.Row();
                price.style.marginTop = new StyleLength(6f);
                price.style.marginBottom = new StyleLength(6f);
                price.Add(K.Small(fighters > 1 ? $"Clés ({fighters}) : " : "Clé : "));
                price.Add(K.Price(Long(b, "keyPrice"), unit, coinItem));
                card.Add(price);
                card.Add(K.Button("Ouvrir la Fissure", () => PanelDo(".fissure", id), true, Bool(b, "canOpen") ? null : Text(b, "why")));
                grid.Add(card);
            }
            _page.Add(grid);
        }

        // ─── Forge Stasis (fusion, raffineur) ────────────────────────────────────────────

        private static void ShowForge(JsonElement v)
        {
            _page.Add(K.Section("Forge Stasis", Text(v, "rule")));
            var mult = K.Grid();
            foreach (var m in Arr(v, "multipliers"))
                mult.Add(K.Badge($"{Text(m, "tier")} : jet max x{Dbl(m, "factor").ToString("0.##", CultureInfo.InvariantCulture)}", K.Gold, false));
            _page.Add(mult);
            _page.Add(K.Small($"Fusion : {Int(v, "fusionCount")} objets identiques du même palier donnent 1 objet du palier suivant au jet max. " +
                              "Les exos sont perdus en passant en S3. Raffineur : il rachète un objet stasifié en trop."));

            var pieces = Arr(v, "pieces");
            _page.Add(K.Section("Vos objets stasifiés", pieces.Count == 0 ? "Aucun objet stasifié dans votre sac." : "Dans le sac uniquement : un objet porté n'est jamais fusionné ni vendu."));
            string unit = Text(v, "coinLabel");
            int coinItem = Int(v, "coinItem");
            var grid = K.Grid();
            int readyCount = 0;
            foreach (var p in pieces) if (Bool(p, "canFuse")) readyCount++;
            if (readyCount > 0)
                _page.Add(K.Badge($"{readyCount} fusion(s) prête(s)", K.Green));
            pieces.Sort((a, b) => (Bool(b, "canFuse") ? 1 : 0).CompareTo(Bool(a, "canFuse") ? 1 : 0));
            foreach (var p in pieces)
            {
                int gid = Int(p, "gid"), tier = Int(p, "tier");
                string arg = gid.ToString(CultureInfo.InvariantCulture) + " S" + tier.ToString(CultureInfo.InvariantCulture);
                string key = "forge:" + arg;
                var tierColor = TierColor(tier);
                int count = Int(p, "count"), need = Math.Max(1, Int(v, "fusionCount"));
                bool ready = Bool(p, "canFuse");
                var card = K.Card(300f, tierColor);
                if (ready)
                {
                    card.style.backgroundColor = new StyleColor(K.Alpha(tierColor, 0.18f));
                    K.Border(card, 2f, tierColor);
                }
                var counter = K.Badge(Long(p, "fuseCost") >= 0 ? $"{Math.Min(count, need)}/{need}" : "max", count >= need ? K.Green : K.Muted, count >= need);
                card.Add(K.CardHead(K.ItemIcon(gid, 48f), Text(p, "name"), $"Niveau {Int(p, "level")} · {count} exemplaire(s)" + (ready ? " · fusion prête" : ""),
                    K.Badge(Text(p, "tierName"), tierColor), counter));
                if (_confirm == key + ":fuse" || _confirm == key + ":sell")
                {
                    bool fuse = _confirm.EndsWith(":fuse", StringComparison.Ordinal);
                    card.Add(K.Text(fuse ? (Bool(p, "loseExos") ? "Fusionner ? Les exos seront perdus." : "Fusionner ces 3 objets ?") : "Vendre cet objet au raffineur ?", true, 14f, K.Gold));
                    var confirm = K.Grid();
                    confirm.Add(K.Button("Confirmer", () => PanelDo(fuse ? ".fusion" : ".raffiner", arg)));
                    confirm.Add(K.Button("Annuler", () => { _confirm = ""; Redraw(); }, false));
                    card.Add(confirm);
                }
                else
                {
                    long cost = Long(p, "fuseCost");
                    if (cost >= 0)
                    {
                        var price = K.Row();
                        price.Add(K.Small($"Fusion en {Text(p, "fuseTo")} : "));
                        price.Add(K.Price(cost, unit, coinItem));
                        card.Add(price);
                    }
                    var buttons = K.Grid();
                    buttons.style.marginTop = new StyleLength(6f);
                    if (cost >= 0)
                        buttons.Add(K.Button($"Fusionner ({Math.Min(count, need)}/{need}) → {Text(p, "fuseTo")}",
                            () => { if (Bool(p, "loseExos")) { _confirm = key + ":fuse"; Redraw(); } else PanelDo(".fusion", arg); }, true,
                            Bool(p, "canFuse") ? null : Text(p, "fuseWhy")));
                    buttons.Add(K.Button($"Vendre ({F(Long(p, "buyback"))})", () => { _confirm = key + ":sell"; Redraw(); }, false));
                    card.Add(buttons);
                }
                grid.Add(card);
            }
            _page.Add(grid);
        }

        // ─── Recherche ───────────────────────────────────────────────────────────────────

        private static void ShowResearchKit(JsonElement root)
        {
            long eclats = Long(root, "eclats");
            _page.Add(K.Section("Recherche", $"{F(eclats)} Éclats disponibles. Les rangs se gardent à travers les Ascensions ; " +
                                             "les rangs ordinaires se remboursent gratuitement hors combat, Métamorphose, Polyvalence et Évolution sont définitives."));
            var branches = new List<string>();
            foreach (var research in Arr(root, "research"))
            {
                string b = Text(research, "branch");
                if (!branches.Contains(b)) branches.Add(b);
            }
            foreach (string branch in branches)
            {
                var tint = BranchColor(branch);
                var head = K.Row(8f);
                head.style.marginTop = new StyleLength(10f);
                head.style.marginBottom = new StyleLength(4f);
                head.Add(K.Badge(branch.Length > 0 ? branch : "Recherche", tint));
                int owned = 0, total = 0;
                foreach (var research in Arr(root, "research"))
                    if (Text(research, "branch") == branch) { owned += Int(research, "rank"); total += Int(research, "max"); }
                head.Add(K.Small($"{owned} / {total} rangs"));
                _page.Add(head);

                var grid = K.Grid();
                foreach (var research in Arr(root, "research"))
                    if (Text(research, "branch") == branch) grid.Add(ResearchCard(research, eclats, tint));
                _page.Add(grid);
            }
        }

        private static UnityEngine.Color BranchColor(string branch) => branch switch
        {
            "Préparation" => K.Blue,
            "Savoir" => K.Purple,
            "Puissance" => K.Red,
            _ => K.Gold,
        };

        /// <summary>One research: its glyph, rank pips, what it gives now and next, and its buttons.</summary>
        private static VisualElement ResearchCard(JsonElement research, long eclats, UnityEngine.Color tint)
        {
            string id = Text(research, "id"), kind = Text(research, "kind"), name = Text(research, "name");
            int rank = Int(research, "rank"), max = Math.Max(1, Int(research, "max"));
            long cost = Long(research, "cost");
            bool maxed = cost < 0, permanent = Bool(research, "permanent");
            string icon = Text(research, "icon");
            if (icon.Length == 0) icon = name.Length > 0 ? name.Substring(0, 1) : "R";

            var card = K.Card(260f, maxed ? K.Green : rank > 0 ? tint : (UnityEngine.Color?)null);
            var badges = new List<VisualElement>();
            if (maxed) badges.Add(K.Badge("Max", K.Green));
            if (permanent) badges.Add(K.Badge("Définitive", K.Gold, false));
            card.Add(K.CardHead(K.Glyph(icon, maxed ? K.Green : tint), name, $"Rang {rank}/{max}", badges.ToArray()));

            // The ranks as pips, filled up to the one held.
            var pips = K.Row(3f);
            pips.style.marginTop = new StyleLength(4f);
            pips.style.marginBottom = new StyleLength(4f);
            for (int i = 0; i < max; i++)
            {
                var pip = new VisualElement();
                pip.style.flexGrow = new StyleFloat(1f);
                pip.style.height = new StyleLength(7f);
                pip.style.backgroundColor = new StyleColor(i < rank ? (maxed ? K.Green : tint) : K.BarBack);
                K.Round(pip, 3f);
                pips.Add(pip);
            }
            card.Add(pips);

            card.Add(K.Small(Text(research, "description"), UnityEngine.Color.white));
            string now = Text(research, "effectNow"), next = Text(research, "effectNext");
            if (now.Length > 0) card.Add(K.Small("Actuel : " + now, K.Green));
            if (next.Length > 0) card.Add(K.Small((rank > 0 ? "Rang suivant : " : "Rang 1 : ") + next, K.Muted));

            if (kind == "biElement" && rank > 0)
            {
                string element = Text(research, "element");
                card.Add(K.Small($"2e élément : {element}{(Bool(research, "elementChosen") ? "" : " (automatique)")}", K.Gold));
                var choice = K.Grid();
                foreach (string e in new[] { "terre", "feu", "eau", "air" })
                {
                    string pick = e;
                    choice.Add(K.Button(char.ToUpperInvariant(e[0]) + e.Substring(1), () =>
                        Ask("stasis/research", new Dictionary<string, object> { ["id"] = "element:" + pick }, r => { Show(r); Say("Élément choisi."); }),
                        e == element));
                }
                card.Add(choice);
            }

            var buttons = K.Grid();
            buttons.style.marginTop = new StyleLength(6f);
            if (kind == "evolution")
            {
                if (!maxed) buttons.Add(K.Button("Onglet Ascension", () => ShowTab("ascension"), false));
            }
            else if (!maxed)
            {
                buttons.Add(K.Button($"Acheter · {F(cost)} Éclats", () =>
                    Ask("stasis/research", new Dictionary<string, object> { ["id"] = id }, r => { Show(r); Say($"{name} : rang {rank + 1} acquis."); }), true,
                    cost <= eclats ? null : $"Il vous manque {F(cost - eclats)} Éclats."));
            }
            long refund = Long(research, "refund");
            if (!permanent && rank > 0 && refund > 0)
                buttons.Add(K.Button($"Rembourser (+{F(refund)})", () =>
                    Ask("stasis/research", new Dictionary<string, object> { ["id"] = "refund:" + id }, r => { Show(r); Say($"{name} remboursée : +{F(refund)} Éclats."); }), false,
                    (_state is JsonElement st && Bool(st, "fight")) ? "Impossible pendant un combat." : null));
            card.Add(buttons);
            return card;
        }

        // ─── Ascension et Évolution ──────────────────────────────────────────────────────

        private static void ShowRebirthsKit(JsonElement root)
        {
            var grid = K.Grid();
            if (Obj(root, "ascension", out var asc))
            {
                var card = K.Card(440f, K.Purple);
                card.Add(K.CardHead(K.Glyph("A", K.Purple), "Ascension", $"Dès le niveau {Int(asc, "minLevel")}"));
                card.Add(K.Fact("Rapporte", $"{F(Long(asc, "eclats"))} Éclats maintenant", K.Blue));
                card.Add(K.Fact("Ensuite", $"expérience x{Times(asc, "catchUp")} jusqu'à votre record", K.Green));
                card.Add(K.Fact("Conservé", "objets et kamas"));
                card.Add(K.Fact("Perdu", "niveau (retour au 1), caractéristiques, dons", K.Red));
                RebirthButtons(card, asc, "ascension", "Faire l'Ascension", "stasis/ascend");
                grid.Add(card);
            }
            if (Obj(root, "evolution", out var evo))
            {
                var card = K.Card(440f, K.Gold);
                card.Add(K.CardHead(K.Glyph("É", K.Gold), "Évolution", $"Au niveau {Int(evo, "level")}"));
                card.Add(K.Fact("Coût", $"{F(Long(evo, "cost"))} Éclats + les 9 donjons de palier 300", K.Blue));
                card.Add(K.Fact("Retour", $"définitif au niveau {Int(evo, "returnLevel")}, en gardant les dons jusqu'au 200"));
                card.Add(K.Fact("Débloque", "les objets stasifiés S3 et S4", K.Green));
                card.Add(K.Fact("Fin", "plus d'Ascension ensuite", K.Red));
                RebirthButtons(card, evo, "evolution", "Évoluer", "stasis/evolve");
                grid.Add(card);
            }
            _page.Add(K.Section("Ascension et Évolution"));
            _page.Add(grid);
        }

        private static void RebirthButtons(VisualElement card, JsonElement what, string key, string label, string route)
        {
            var row = K.Grid();
            row.style.marginTop = new StyleLength(8f);
            bool can = Bool(what, "can");
            if (!can || _confirm != key)
                row.Add(K.Button(label, () => { _confirm = key; Redraw(); }, true, can ? null : Text(what, "why")));
            else
            {
                row.Add(K.Text("Sûr ? C'est irréversible.", true, 15f, K.Red));
                row.Add(K.Button("Confirmer", () =>
                {
                    _confirm = "";
                    Ask(route, new Dictionary<string, object> { ["confirm"] = true }, r =>
                    {
                        Show(r);
                        Say(key == "ascension" ? "Ascension accomplie." : "Évolution accomplie.");
                    });
                }));
                row.Add(K.Button("Annuler", () => { _confirm = ""; Redraw(); }, false));
            }
            card.Add(row);
        }

        // ─── Sanctuaire ──────────────────────────────────────────────────────────────────

        private static void ShowHubKit()
        {
            if (!_hubAsked) Hub("", "");
            if (!(_hub is JsonElement hub))
            {
                _page.Add(K.Small("Chargement du Sanctuaire…"));
                return;
            }
            NoteCoins(hub);
            string currency = Text(hub, "currency");
            int coinItem = hub.TryGetProperty("coinItem", out _) ? Int(hub, "coinItem") : K.JondoCoin;
            bool inside = Bool(hub, "inSanctuary"), back = Bool(hub, "canReturn");

            var head = K.Card(0f, K.Gold);
            head.Add(K.CardHead(K.Glyph("S", K.Gold), Text(hub, "sanctuary"),
                inside ? "Vous êtes au Sanctuaire : le Marchand de Stasis, le Raffineur, le comptoir et la banque sont ici." : "Le Marchand de Stasis vous attend au Sanctuaire."));
            var big = K.Grid();
            if (!inside) big.Add(K.Button("Aller au Sanctuaire", () => Hub(".sanctuaire", "")));
            else big.Add(K.Button("Retour", () => Hub(".retour", ""), true, back ? null : "Aucun point de départ en mémoire : utilisez un zaap."));
            big.Add(K.Button("Forge Stasis", () => ShowTab("stasis"), false));
            big.Add(K.Button("Actualiser", () => Hub("", ""), false));
            head.Add(big);
            _page.Add(head);

            string notInside = inside ? null : "Au Sanctuaire uniquement.";
            var recommended = Arr(hub, "recommended");
            _page.Add(K.Section("Conseillé pour vous", "Les pièces stasifiées de votre niveau que le Marchand propose."));
            var grid = K.Grid();
            foreach (var piece in recommended)
            {
                string gid = Int(piece, "gid").ToString(CultureInfo.InvariantCulture);
                var card = K.Card(230f);
                card.Add(K.CardHead(K.ItemIcon(Int(piece, "gid"), 44f), Text(piece, "name"), $"Niveau {Int(piece, "level")}"));
                card.Add(K.Price(Long(piece, "price"), currency, coinItem));
                card.Add(K.Button("Acheter", () => Hub(".marchand", "acheter " + gid), true, notInside));
                grid.Add(card);
            }
            _page.Add(grid);
            if (recommended.Count > 0)
                _page.Add(K.Button("Acheter tout le conseillé", () => Hub(".marchand", "acheter conseil"), true, notInside));

            var consumables = Arr(hub, "consumables");
            if (consumables.Count > 0)
            {
                _page.Add(K.Section("Consommables"));
                var cgrid = K.Grid();
                foreach (var c in consumables)
                {
                    string gid = Int(c, "gid").ToString(CultureInfo.InvariantCulture);
                    var card = K.Card(200f);
                    card.style.alignItems = new StyleEnum<Align>(Align.Center);
                    card.Add(K.ItemIcon(Int(c, "gid"), 48f));
                    var name = K.Text(Text(c, "name"), true, 14f);
                    name.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.UpperCenter);
                    card.Add(name);
                    card.Add(K.Price(Long(c, "price"), currency, coinItem));
                    card.Add(K.Button("Acheter", () => Hub(".marchand", "acheter " + gid), true, notInside));
                    cgrid.Add(card);
                }
                _page.Add(cgrid);
            }

            _page.Add(K.Section("Fabrication Stasis", $"Objet de base non stasifié + ressources + essence de gardien + {currency}. Hors combat."));
            var recipes = Arr(hub, "recipes");
            var rgrid = K.Grid();
            foreach (var recipe in recipes)
            {
                string arg = Int(recipe, "gid").ToString(CultureInfo.InvariantCulture) + (Int(recipe, "tier") >= 2 ? " S2" : "");
                bool ready = Bool(recipe, "ready");
                var card = K.Card(300f, ready ? K.Green : (UnityEngine.Color?)null);
                card.Add(K.CardHead(K.ItemIcon(Int(recipe, "gid"), 44f), Text(recipe, "name"), $"Niveau {Int(recipe, "level")}",
                    K.Badge(Int(recipe, "tier") >= 2 ? "S2" : "S1", K.Gold), ready ? K.Badge("Prêt", K.Green) : K.Badge($"{Int(recipe, "missing")} manque(s)", K.Red, false)));
                card.Add(K.Price(Long(recipe, "cost"), currency, coinItem));
                var buttons = K.Grid();
                buttons.Add(K.Button("Fabriquer", () => Hub(".fabriquer", arg), true, ready ? null : "Il manque des ingrédients."));
                buttons.Add(K.Button("Voir le devis", () => Hub(".devis", arg), false));
                card.Add(buttons);
                rgrid.Add(card);
            }
            _page.Add(rgrid);
            if (recipes.Count == 0) _page.Add(K.Small("Aucune recette en vue : il faut l'objet de base non stasifié ou ses ingrédients."));
        }

        // ─── Missions ────────────────────────────────────────────────────────────────────

        private static void ShowMissionsKit()
        {
            if (!(_missions is JsonElement m))
            {
                _page.Add(K.Small("Chargement des missions…"));
                return;
            }
            NoteCoins(m);
            int done = Int(m, "chaptersDone"), chapters = Math.Max(1, Int(m, "chapters"));
            var head = K.Card(0f, K.Gold);
            head.Add(K.CardHead(K.Glyph("✦", K.Gold), "Le Système", $"Nouvelles pistes tous les {Int(m, "trackEvery")} niveaux (prochain lot au niveau {Int(m, "nextBatch")})."));
            head.Add(K.ProgressBar(done / (float)chapters, $"Chapitres : {done} / {Int(m, "chapters")}", K.Gold));
            var buttons = K.Grid();
            buttons.Add(K.Button("Tout récupérer", () => AskMissions("stasis/missions/claim", new Dictionary<string, object> { ["slot"] = "" })));
            buttons.Add(K.Button("Actualiser", () => AskMissions("stasis/missions", new Dictionary<string, object>()), false));
            head.Add(buttons);
            _page.Add(head);

            _page.Add(K.Section("Mission de chapitre"));
            if (Obj(m, "chapter", out var chapter)) MissionCard(chapter, true);
            else _page.Add(K.Small("Tous les chapitres sont terminés."));

            _page.Add(K.Section("Pistes"));
            var tracks = Arr(m, "tracks");
            var grid = K.Grid();
            foreach (var track in tracks) grid.Add(MissionCard(track, false, 440f));
            _page.Add(grid);
            if (tracks.Count == 0) _page.Add(K.Small("Aucune piste pour l'instant."));
        }

        private static VisualElement MissionCard(JsonElement mission, bool chapter, float width = 0f)
        {
            bool complete = Bool(mission, "complete");
            var card = K.Card(width, complete ? K.Green : chapter ? K.Gold : (UnityEngine.Color?)null);
            string slot = Text(mission, "slot");
            int step = Int(mission, "step"), steps = Int(mission, "steps");
            card.Add(K.CardHead(null, Text(mission, "source"), step > 0 && steps > 0 ? $"Étape {step}/{steps}" : "",
                Bool(mission, "last") ? K.Badge("Dernière : bonus !", K.Gold) : null, complete ? K.Badge("Terminée", K.Green) : null));
            string description = Text(mission, "description");
            if (description.Length > 0) card.Add(K.Small(description));
            if (Bool(mission, "done"))
            {
                card.Add(K.Small("Piste terminée : la suivante arrive avec le prochain lot."));
            }
            else
            {
                card.Add(K.Text(Text(mission, "title"), true, 15f));
                long progress = Long(mission, "progress"), need = Math.Max(1, Long(mission, "need"));
                card.Add(K.ProgressBar(progress / (float)need, $"{F(progress)} / {F(need)}", complete ? K.Gold : K.Green));
                var row = K.Grid();
                if (complete) row.Add(K.Button("Récupérer", () => AskMissions("stasis/missions/claim", new Dictionary<string, object> { ["slot"] = slot })));
                if (Bool(mission, "canGo"))
                    row.Add(K.Button("GO", () => AskMissions("stasis/missions/go", new Dictionary<string, object> { ["slot"] = slot }), !complete));
                card.Add(row);
            }
            if (width <= 0f) _page.Add(card);
            return card;
        }

        // ─── Pension ─────────────────────────────────────────────────────────────────────

        private static void ShowPensionKit()
        {
            if (!(_pension is JsonElement root) || !Obj(root, "pension", out var p))
            {
                _page.Add(K.Small("Chargement de la pension…"));
                return;
            }
            string currency = Text(p, "currency");
            long tokens = Long(p, "tokens");
            var head = K.Card(0f, K.Gold);
            head.Add(K.CardHead(K.Glyph("Œ", K.Gold), $"Incubation — {Int(p, "slots")} places", $"Jetons Event : {F(tokens)} · accélération -15 min : {Long(p, "speedUpTokens")} jetons"));
            var top = K.Grid();
            top.Add(K.Button("Faire éclore", () => PensionDo("pension", "eclore")));
            top.Add(K.Button($"Ouvrir une caisse ({Int(p, "crates")})", () => PensionDo("caisse", ""), false, Int(p, "crates") > 0 ? null : "Aucune caisse."));
            top.Add(K.Button($"Acheter une caisse ({F(Long(p, "cratePrice"))} {currency})", () => PensionDo("caisse", "acheter"), false));
            if (Bool(p, "canBuySlot"))
                top.Add(K.Button($"Place d'incubation ({Long(p, "slotTokens")} jetons)", () => PensionDo("pension", "place"), false,
                    tokens >= Long(p, "slotTokens") ? null : "Pas assez de jetons."));
            head.Add(top);
            _page.Add(head);

            var eggs = Arr(p, "eggs");
            _page.Add(K.Section("Œufs", eggs.Count == 0 ? "Aucun œuf : ouvrez une caisse." : null));
            var egrid = K.Grid();
            foreach (var egg in eggs)
            {
                string id = Int(egg, "id").ToString(CultureInfo.InvariantCulture);
                bool incubating = Int(egg, "slot") != 0, ready = Bool(egg, "ready");
                var card = K.Card(280f, ready ? K.Green : incubating ? K.Blue : (UnityEngine.Color?)null);
                card.Add(K.CardHead(K.Glyph("Œ", ready ? K.Green : K.Gold, 40f), Text(egg, "name"), $"Œuf #{id}",
                    Bool(egg, "gift") ? K.Badge("Don", K.Purple, false) : null,
                    ready ? K.Badge("Prêt", K.Green) : incubating ? K.Badge($"Place {Int(egg, "slot")}", K.Blue, false) : null));
                if (incubating && !ready) card.Add(K.Fact("Éclosion dans", Text(egg, "left"), K.Blue));
                var b = K.Grid();
                if (!incubating) b.Add(K.Button("Incuber", () => PensionDo("pension", "incuber " + id)));
                else
                {
                    if (!ready) b.Add(K.Button("Accélérer", () => PensionDo("pension", "accelerer " + id), false,
                        tokens >= Long(p, "speedUpTokens") ? null : "Pas assez de jetons."));
                    b.Add(K.Button("Retirer", () => PensionDo("pension", "annuler " + id), false));
                }
                card.Add(b);
                egrid.Add(card);
            }
            _page.Add(egrid);

            _page.Add(K.Section($"Animaux — salle d'étude ({Int(p, "studyPlaces")} places)",
                "Étudiés : 5 % de l'XP de vos combats gagnés (x2 sous la moitié de votre niveau). Accoupler : deux montures de même espèce et génération."));
            var agrid = K.Grid();
            foreach (var a in Arr(p, "animals"))
            {
                long uid = Long(a, "uid");
                bool mount = Bool(a, "mount"), studied = Bool(a, "studied");
                var card = K.Card(300f, studied ? K.Blue : _breedFirst == uid ? K.Gold : (UnityEngine.Color?)null);
                card.Add(K.CardHead(K.ItemIcon(Int(a, "gid"), 44f, "A"), Text(a, "name"),
                    $"Niveau {Int(a, "level")}" + (mount ? $" · génération {Int(a, "generation")} · {Int(a, "litters")} portée(s)" : ""),
                    Bool(a, "worn") ? K.Badge("Équipé", K.Gold, false) : null, studied ? K.Badge("À l'étude", K.Blue) : null));
                var b = K.Grid();
                if (studied) b.Add(K.Button("Retirer de l'étude", () => PensionDo("pension", "retirer " + uid), false));
                else b.Add(K.Button("Étudier", () => PensionDo("pension", "etude " + uid)));
                if (mount)
                {
                    if (_breedFirst == 0) b.Add(K.Button("Accoupler…", () => { _breedFirst = uid; Redraw(); }, false, Int(a, "litters") > 0 ? null : "Plus de portée."));
                    else if (_breedFirst == uid) b.Add(K.Button("Annuler", () => { _breedFirst = 0; Redraw(); }, false));
                    else b.Add(K.Button("Avec celle-ci", () => { long first = _breedFirst; _breedFirst = 0; PensionDo("pension", $"accoupler {first} {uid}"); }));
                }
                card.Add(b);
                agrid.Add(card);
            }
            _page.Add(agrid);
        }

        // ─── Cartes ──────────────────────────────────────────────────────────────────────

        private static UnityEngine.Color RarityColor(string rarity)
        {
            string r = AdminItemsUi.Fold(rarity ?? "");
            if (r.StartsWith("leg")) return new UnityEngine.Color(1f, 0.6f, 0.15f, 1f);
            if (r.StartsWith("epi")) return K.Purple;
            if (r.StartsWith("rar")) return K.Blue;
            if (r.StartsWith("peu") || r.StartsWith("unc")) return K.Green;
            return K.Muted;
        }

        private static void ShowCardsKit()
        {
            if (!(_pension is JsonElement root) || !Obj(root, "cards", out var c))
            {
                _page.Add(K.Small("Chargement du classeur…"));
                return;
            }
            int packs = Int(c, "packs");
            var head = K.Card(0f, K.Gold);
            head.Add(K.CardHead(K.Glyph("C", K.Gold), "Cartes de monstres", $"{packs} paquet(s) à ouvrir"));
            var top = K.Grid();
            top.Add(K.Button("Ouvrir un paquet", () => PensionDo("cartes", "ouvrir"), true, packs > 0 ? null : "Aucun paquet."));
            top.Add(K.Button("Tout coller", () => PensionDo("cartes", "coller tout"), false));
            top.Add(K.Button($"Acheter un paquet ({Long(c, "packTokens")} jetons)", () => PensionDo("cartes", "acheter"), false));
            head.Add(top);
            _page.Add(head);

            _page.Add(K.Section("Classeur", "Une famille complète donne son bonus."));
            var fgrid = K.Grid();
            foreach (var f in Arr(c, "families"))
            {
                int glued = Int(f, "glued"), total = Math.Max(1, Int(f, "total"));
                bool complete = glued >= total;
                var card = K.Card(280f, complete ? K.Green : (UnityEngine.Color?)null);
                card.Add(K.CardHead(null, Text(f, "name"), Text(f, "bonus").Length > 0 ? "Bonus : " + Text(f, "bonus") : "",
                    complete ? K.Badge("Actif", K.Green) : null));
                card.Add(K.ProgressBar(glued / (float)total, $"{glued} / {Int(f, "total")}", complete ? K.Green : K.Blue, 16f));
                fgrid.Add(card);
            }
            _page.Add(fgrid);

            _page.Add(K.Section("En main", $"Raffineur : misez de 1 à {Math.Max(1, Int(c, "refinerMax"))} doublons d'une carte pour une carte de la rareté supérieure ; échec = lot perdu."));
            var lgrid = K.Grid();
            foreach (var card in Arr(c, "loose"))
            {
                string id = Int(card, "id").ToString(CultureInfo.InvariantCulture);
                bool already = Bool(card, "glued");
                string rarity = Text(card, "rarity");
                var color = RarityColor(rarity);
                var box = K.Card(250f, color);
                box.Add(K.CardHead(K.Glyph("♠", color, 40f), Text(card, "name"), $"x{Int(card, "count")}" + (already ? " · déjà collée" : ""),
                    rarity.Length > 0 ? K.Badge(rarity, color, false) : null));
                var b = K.Grid();
                if (already) b.Add(K.Button("Vendre un doublon", () => PensionDo("cartes", "vendre " + id), false));
                else b.Add(K.Button("Coller", () => PensionDo("cartes", "coller " + id)));
                int lot = Int(card, "refineLot");
                if (lot > 0)
                {
                    double chance = Dbl(card, "refineChance");
                    b.Add(K.Button($"Raffiner {lot} ({chance.ToString("0.#", CultureInfo.InvariantCulture)} %)", () => PensionDo("cartes", $"raffiner {id} {lot}"), false));
                }
                box.Add(b);
                lgrid.Add(box);
            }
            _page.Add(lgrid);
        }

        // ─── Éveil ───────────────────────────────────────────────────────────────────────

        private static void ShowEveilKit()
        {
            if (!(_eveil is JsonElement root) || Arr(root, "dofus").Count == 0)
            {
                _page.Add(K.Small("Chargement de l'Éveil…"));
                return;
            }
            bool needHardcore = Bool(root, "requireHardcore"), evolved = Bool(root, "evolved"), hardcore = Bool(root, "hardcore");
            var head = K.Card(0f, K.Purple);
            head.Add(K.CardHead(K.Glyph("✧", K.Purple), "Éveil des Dofus — Partie I",
                "Sacrifiez des copies, battez les boss du parcours" + (needHardcore ? " en hardcore" : "") + ", puis le défi final en solo. Récompense : le Dofus stasifié.",
                needHardcore ? K.Badge(hardcore ? "Hardcore activé" : "Hardcore requis", hardcore ? K.Green : K.Red) : null));
            if (needHardcore && !hardcore)
            {
                if (evolved) head.Add(K.Button("Activer le hardcore", () =>
                    Ask("stasis/act", new Dictionary<string, object> { ["tab"] = "paliers", ["command"] = ".hardcore", ["args"] = "on" }, r => { Say("Mode hardcore activé."); AskEveil(""); })));
                else head.Add(K.Small("Le hardcore est réservé aux personnages évolués.", K.Red));
            }
            _page.Add(head);

            var grid = K.Grid();
            foreach (var d in Arr(root, "dofus"))
            {
                string id = Text(d, "id"), stage = Text(d, "stage");
                var color = stage == "done" ? K.Green : stage == "challenge" ? K.Gold : stage == "route" ? K.Blue : (UnityEngine.Color?)null;
                var card = K.Card(440f, color);
                string badge = stage switch { "route" => "Parcours", "challenge" => "Défi final", "done" => "Éveillé", _ => "À commencer" };
                card.Add(K.CardHead(K.ItemIcon(Int(d, "item"), 52f), Text(d, "name"), $"Niveau {Int(d, "level")}",
                    K.Badge(badge, color ?? K.Muted, stage != "none")));
                switch (stage)
                {
                    case "route":
                        int done = Int(d, "done"), total = Math.Max(1, Int(d, "total"));
                        card.Add(K.ProgressBar(done / (float)total, $"Parcours {done} / {Int(d, "total")}", K.Blue));
                        string remaining = Join(d, "remaining");
                        if (remaining.Length > 0) card.Add(K.Fact("Restants", remaining));
                        break;
                    case "challenge":
                        card.Add(K.Fact("Défi", $"{Text(d, "boss")} niveau {Int(d, "bossLevel")}, en solo", K.Gold));
                        card.Add(K.Fact("Bonus", Join(d, "effects"), K.Green));
                        card.Add(K.Button("Lancer le défi", () => AskEveil(id + " defi")));
                        break;
                    case "done":
                        card.Add(K.Small("Dofus éveillé.", K.Green));
                        break;
                    default:
                        int held = Int(d, "held"), copies = Math.Max(1, Int(d, "copies"));
                        card.Add(K.ProgressBar(held / (float)copies, $"Copies en sac : {held} / {Int(d, "copies")}", K.Gold));
                        card.Add(K.Fact("Défi final", Text(d, "boss")));
                        card.Add(K.Fact("Bonus", Join(d, "effects"), K.Green));
                        card.Add(K.Button($"Sacrifier {Int(d, "copies")} copies", () => AskEveil(id + " commencer"), true,
                            held >= copies ? null : $"Il vous manque {copies - held} copie(s)."));
                        break;
                }
                grid.Add(card);
            }
            _page.Add(grid);
        }
    }
}
