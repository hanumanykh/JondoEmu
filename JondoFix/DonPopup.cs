using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The Stasis level-up don popup (docs/stasis-gameplay-spec.md §10): a centered modal
    /// «Choisissez un don — Niveau X» with the offer's three or four cards side by side -- icon,
    /// name, rarity (Commun grey, Rare blue, Légendaire gold), kind and what it gives --, the
    /// characteristic card's stat picker, «Relancer» and «Plus tard». Keys 1-5 pick a card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asks /api/stasis/state (the session's own token, as ProgressionUi does) every
    /// <see cref="PollEvery"/> seconds while closed, and opens by itself on an offer waiting out
    /// of a fight: so after each fight and at login. «Plus tard» closes it until the next fight
    /// ends; meanwhile a small pulsing «Don disponible» button reopens it. After a pick the next
    /// offer waiting is shown at once. Every action's answer is the whole state again.
    /// </para>
    /// <para>
    /// Icons are the client's own: the server sends per card an icon kind and id (Dons.IconOf):
    /// "stat" (a characteristic id: the CharacteristicData's asset in the characteristic icons'
    /// addressables, as the character sheet draws it), "spell" (the spell's iconId), "item" (the
    /// item's iconId), "class" (the breed symbol), and a FigmaIcons glyph as fallback.
    /// </para>
    /// </remarks>
    public static class DonPopup
    {
        private const float PollEvery = 3f;
        private const float CardWidth = 232f;

        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();
        private static WindowFigma _window;
        private static VisualElement _reopen;
        private static DofusLabel _status;
        private static JsonElement? _state;
        private static float _lastPoll = -100f;
        private static bool _polling, _busy;
        private static int _dismissedLevel;
        private static bool _wasInFight;
        private static string _lastStat = "";
        private static string _token;

        /// <summary>True when the popup runs (a player token): ProgressionUi then leaves the offers to it.</summary>
        public static bool HandlesOffers => Token.Length > 0;

        private static string Token
            => _token ??= (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                           ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();

        // ─── Every frame ─────────────────────────────────────────────────────────────────

        /// <summary>From the mod's OnUpdate: answers, keys 1-5, the quiet poll and the pulsing button.</summary>
        public static void Tick()
        {
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Don popup: {ex.Message}"); }
            }
            if (Token.Length == 0) return;

            // A scene change takes the layer and what is on it.
            if (_window != null && _window.parent == null) { _window = null; _status = null; }
            if (_reopen != null && _reopen.parent == null) _reopen = null;

            if (_window != null) Keys();

            float now = UnityEngine.Time.unscaledTime;
            if (_window == null && !_polling && now - _lastPoll >= PollEvery)
            {
                _lastPoll = now;
                _polling = true;
                Ask("stasis/state", new Dictionary<string, object>(), Polled, failed: () => _polling = false);
            }

            if (_reopen != null)
            {
                float pulse = 0.6f + 0.4f * (float)Math.Abs(Math.Sin(now * 3.0));
                _reopen.style.opacity = new StyleFloat(pulse);
            }
        }

        private static void Polled(JsonElement root)
        {
            _polling = false;
            _state = root;
            bool fight = Bool(root, "fight");
            // A fight just ended: what was put off for later is offered again.
            if (_wasInFight && !fight) _dismissedLevel = 0;
            _wasInFight = fight;
            int level = OfferLevel(root);
            if (level == 0)
            {
                HideReopen();
                return;
            }
            if (!fight && level != _dismissedLevel && _window == null) Open(root);
            else if (_window == null) ShowReopen();
        }

        private static int OfferLevel(JsonElement root)
            => root.TryGetProperty("offer", out var offer) && offer.ValueKind == JsonValueKind.Object ? Int(offer, "level") : 0;

        /// <summary>Keys 1 to 5 (the row or the pad) pick that card, unless a text is being typed.</summary>
        private static void Keys()
        {
            int pressed = 0;
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard == null) return;
                if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame) pressed = 1;
                else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame) pressed = 2;
                else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame) pressed = 3;
                else if (keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame) pressed = 4;
                else if (keyboard.digit5Key.wasPressedThisFrame || keyboard.numpad5Key.wasPressedThisFrame) pressed = 5;
            }
            catch
            {
                return;
            }
            if (pressed == 0 || _busy || Typing() || _state is not JsonElement state) return;
            if (!state.TryGetProperty("offer", out var offer) || offer.ValueKind != JsonValueKind.Object) return;
            foreach (var card in offer.GetProperty("cards").EnumerateArray())
            {
                if (Int(card, "index") != pressed) continue;
                if (Bool(card, "characteristic"))
                {
                    string stat = _lastStat.Length > 0 ? _lastStat : FirstStat(state);
                    if (stat.Length > 0) Pick(pressed, stat);
                }
                else Pick(pressed, "");
                return;
            }
        }

        private static string FirstStat(JsonElement state)
        {
            if (!state.TryGetProperty("characteristics", out var stats) || stats.ValueKind != JsonValueKind.Array) return "";
            foreach (var stat in stats.EnumerateArray()) return Text(stat, "key");
            return "";
        }

        /// <summary>Whether a text field of any of the client's documents has the focus (the chat): the digits are then typed, not picked.</summary>
        private static bool Typing()
        {
            try
            {
                var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
                for (int i = 0; i < documents.Length; i++)
                {
                    var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                    var focused = root?.panel?.focusController?.focusedElement?.TryCast<VisualElement>();
                    for (int depth = 0; focused != null && depth < 6; depth++, focused = focused.parent)
                    {
                        if (focused.TryCast<TextField>() != null) return true;
                        string type = focused.GetIl2CppType()?.Name ?? "";
                        if (type.Contains("TextInput") || type.Contains("TextField")) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ─── The window ──────────────────────────────────────────────────────────────────

        /// <summary>Opens on the state known (the Progression window's button), asking it first when none is.</summary>
        public static void Open()
        {
            if (_state is JsonElement state && OfferLevel(state) > 0) Open(state);
            else Ask("stasis/state", new Dictionary<string, object>(), root =>
            {
                _state = root;
                if (OfferLevel(root) > 0) Open(root);
            });
        }

        private static void Open(JsonElement root)
        {
            HideReopen();
            Close(false);
            try
            {
                var layer = AdminItemsUi.FindLayer();
                if (layer == null) return;
                var offer = root.GetProperty("offer");
                int cards = offer.GetProperty("cards").GetArrayLength();
                float width = Math.Max(560f, cards * (CardWidth + 14f) + 60f), height = 520f;

                int shown = offer.TryGetProperty("shownLevel", out _) ? Int(offer, "shownLevel") : Int(offer, "level");
                var window = new WindowFigma(true, $"Choisissez un don — Niveau {shown}", "", true);
                window.showCloseButton = true;
                window.name = "JondoDonPopup";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(width);
                window.style.minHeight = new StyleLength(height);
                float lw = layer.resolvedStyle.width, lh = layer.resolvedStyle.height;
                window.style.left = new StyleLength(float.IsNaN(lw) || lw <= 0 ? 400f : Math.Max(0f, (lw - width) / 2f));
                window.style.top = new StyleLength(float.IsNaN(lh) || lh <= 0 ? 160f : Math.Max(0f, (lh - height) / 2f));

                var body = new VisualElement();
                body.style.paddingLeft = new StyleLength(26f);
                body.style.paddingRight = new StyleLength(26f);
                body.style.paddingTop = new StyleLength(12f);
                body.style.paddingBottom = new StyleLength(14f);
                Fill(body, root, offer);
                UiKit.Unsqueeze(body);
                window.Add(body);

                layer.Add(window);
                window.BringToFront();
                AdminItemsUi.KeepEvents(window);
                var headerClose = window.header?.rightButton;
                if (headerClose != null) headerClose.add_Clicked(AdminItemsUi.Do(Later));
                _window = window;
                _state = root;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Don popup could not be opened: {ex}");
                Close(false);
            }
        }

        private static void Fill(VisualElement body, JsonElement root, JsonElement offer)
        {
            int pending = Int(root, "pending");
            string head = Text(offer, "kindLabel") + (pending > 1 ? $" · {pending} offres en attente" : "");
            var subtitle = AdminItemsUi.Label(head);
            subtitle.style.color = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.7f));
            subtitle.style.marginBottom = new StyleLength(12f);
            body.Add(subtitle);

            var row = AdminItemsUi.Row();
            row.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            row.style.alignItems = new StyleEnum<Align>(Align.Stretch);
            foreach (var card in offer.GetProperty("cards").EnumerateArray()) row.Add(Card(root, card));
            body.Add(row);

            var actions = AdminItemsUi.Row();
            actions.style.marginTop = new StyleLength(14f);
            actions.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            actions.style.alignItems = new StyleEnum<Align>(Align.Center);
            // A class passive offer (level 50): two free rerolls of three new passives, counted down.
            int passiveRerolls = offer.TryGetProperty("passiveRerollsLeft", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : -1;
            if (Bool(offer, "canReroll"))
            {
                long cost = Long(offer, "rerollCost");
                string coin = Text(root, "coinLabel");
                if (coin.Length == 0) coin = "Jondo Coins";
                string label = passiveRerolls >= 0 ? $"Relancer (gratuit, {passiveRerolls} restante{(passiveRerolls > 1 ? "s" : "")})"
                    : cost > 0 ? $"Relancer ({cost.ToString("N0", CultureInfo.InvariantCulture)} {coin})" : "Relancer (gratuit)";
                actions.Add(Button(label, false, Reroll));
            }
            else
            {
                var note = AdminItemsUi.Label(passiveRerolls >= 0 ? "Plus de relance (9 passifs vus)"
                    : Bool(offer, "rerolled") ? "Offre déjà relancée" : "Offre non relançable");
                note.style.color = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.55f));
                note.style.marginRight = new StyleLength(16f);
                actions.Add(note);
            }
            actions.Add(Button("Plus tard", false, Later));
            body.Add(actions);

            var hint = AdminItemsUi.Label("Touches 1 à 5 : choisir directement.");
            hint.style.color = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.45f));
            hint.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            hint.style.marginTop = new StyleLength(4f);
            body.Add(hint);

            _status = AdminItemsUi.Label("");
            _status.style.color = new StyleColor(new UnityEngine.Color(1f, 0.85f, 0.55f, 1f));
            _status.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            body.Add(_status);
        }

        private static UnityEngine.Color RarityColor(string rarity) => rarity switch
        {
            "legendaire" => new UnityEngine.Color(1f, 0.70f, 0.18f, 1f),
            "rare" => new UnityEngine.Color(0.38f, 0.62f, 1f, 1f),
            "commun" => new UnityEngine.Color(0.86f, 0.86f, 0.86f, 1f),
            _ => new UnityEngine.Color(0.70f, 0.66f, 0.55f, 1f),
        };

        private static VisualElement Card(JsonElement root, JsonElement card)
        {
            string rarity = Text(card, "rarity");
            var color = RarityColor(rarity);
            int index = Int(card, "index");

            var box = new VisualElement();
            box.style.width = new StyleLength(CardWidth);
            box.style.minHeight = new StyleLength(300f);
            box.style.marginLeft = new StyleLength(7f);
            box.style.marginRight = new StyleLength(7f);
            box.style.paddingLeft = new StyleLength(12f);
            box.style.paddingRight = new StyleLength(12f);
            box.style.paddingTop = new StyleLength(12f);
            box.style.paddingBottom = new StyleLength(12f);
            box.style.backgroundColor = new StyleColor(new UnityEngine.Color(color.r * 0.18f, color.g * 0.18f, color.b * 0.18f, 0.85f));
            box.style.borderTopWidth = box.style.borderBottomWidth = box.style.borderLeftWidth = box.style.borderRightWidth = new StyleFloat(2f);
            box.style.borderTopColor = box.style.borderBottomColor = box.style.borderLeftColor = box.style.borderRightColor = new StyleColor(color);
            box.style.borderTopLeftRadius = box.style.borderTopRightRadius = new StyleLength(8f);
            box.style.borderBottomLeftRadius = box.style.borderBottomRightRadius = new StyleLength(8f);

            // Head: the key, the icon, the kind and the rarity.
            var head = AdminItemsUi.Row();
            head.style.alignItems = new StyleEnum<Align>(Align.Center);
            head.style.marginBottom = new StyleLength(8f);
            head.Add(Icon(Text(card, "iconKind"), Int(card, "iconId"), Text(card, "iconGlyph"), 52f));
            var tags = new VisualElement();
            tags.style.marginLeft = new StyleLength(10f);
            tags.style.flexShrink = new StyleFloat(1f);
            var kind = AdminItemsUi.Label($"[{index}] {Text(card, "cardKind")}");
            kind.style.fontSize = new StyleLength(14f);
            kind.style.color = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.6f));
            tags.Add(kind);
            string rarityLabel = Text(card, "rarityLabel");
            if (rarityLabel.Length > 0)
            {
                var r = AdminItemsUi.Label(rarityLabel, true);
                r.style.color = new StyleColor(color);
                tags.Add(r);
            }
            head.Add(tags);
            box.Add(head);

            var name = AdminItemsUi.Label(Text(card, "name"), true);
            name.style.color = new StyleColor(rarity.Length > 0 ? color : UnityEngine.Color.white);
            name.style.marginBottom = new StyleLength(6f);
            box.Add(name);

            string effect = Text(card, "effect");
            if (Int(card, "spell") > 0 && effect.Length > 0 && Text(card, "name").IndexOf(effect, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var e = AdminItemsUi.Label(effect, true);
                e.style.color = new StyleColor(new UnityEngine.Color(0.55f, 0.9f, 0.55f, 1f));
                box.Add(e);
            }
            var description = AdminItemsUi.Label(Text(card, "description"));
            description.style.marginTop = new StyleLength(4f);
            description.style.flexGrow = new StyleFloat(1f);
            box.Add(description);

            if (Bool(card, "characteristic"))
            {
                var picker = AdminItemsUi.Row();
                picker.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
                picker.style.marginTop = new StyleLength(8f);
                if (root.TryGetProperty("characteristics", out var stats) && stats.ValueKind == JsonValueKind.Array)
                    foreach (var stat in stats.EnumerateArray())
                    {
                        string key = Text(stat, "key");
                        picker.Add(StatChoice(Text(stat, "label"), Int(stat, "statId"), Text(stat, "iconGlyph"), () =>
                        {
                            _lastStat = key;
                            Pick(index, key);
                        }));
                    }
                box.Add(picker);
            }
            else
            {
                var choose = Button($"Choisir ({index})", true, () => Pick(index, ""));
                choose.style.marginTop = new StyleLength(10f);
                choose.style.alignSelf = new StyleEnum<Align>(Align.Center);
                box.Add(choose);
            }
            return box;
        }

        /// <summary>One characteristic of the picker: its icon over its name, a click keeps it.</summary>
        private static VisualElement StatChoice(string label, int statId, string glyph, Action click)
        {
            var tile = new VisualElement();
            tile.style.width = new StyleLength(62f);
            tile.style.marginRight = new StyleLength(4f);
            tile.style.marginBottom = new StyleLength(4f);
            tile.style.paddingTop = new StyleLength(4f);
            tile.style.paddingBottom = new StyleLength(4f);
            tile.style.alignItems = new StyleEnum<Align>(Align.Center);
            tile.style.backgroundColor = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.10f));
            tile.style.borderTopLeftRadius = tile.style.borderTopRightRadius = new StyleLength(6f);
            tile.style.borderBottomLeftRadius = tile.style.borderBottomRightRadius = new StyleLength(6f);
            tile.Add(Icon("stat", statId, glyph, 28f));
            var text = AdminItemsUi.Label(label);
            text.style.fontSize = new StyleLength(12f);
            text.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            text.pickingMode = PickingMode.Ignore;
            tile.Add(text);
            tile.AddManipulator(new Clickable(AdminItemsUi.Do(click)).Cast<IManipulator>());
            return tile;
        }

        /// <summary>«Plus tard»: closed until the next fight ends; the pulsing button reopens it.</summary>
        private static void Later()
        {
            if (_state is JsonElement state) _dismissedLevel = OfferLevel(state);
            Close(true);
        }

        private static void Close(bool showReopen)
        {
            try { _window?.RemoveFromHierarchy(); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Closing the don popup: {ex.Message}"); }
            _window = null;
            _status = null;
            if (showReopen && _state is JsonElement state && OfferLevel(state) > 0) ShowReopen();
        }

        private static void Say(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        // ─── Actions ─────────────────────────────────────────────────────────────────────

        private static void Pick(int index, string stat)
        {
            if (_busy) return;
            _busy = true;
            Ask("stasis/pick", new Dictionary<string, object> { ["card"] = index, ["stat"] = stat }, Answered,
                failed: () => _busy = false);
        }

        private static void Reroll()
        {
            if (_busy) return;
            _busy = true;
            Ask("stasis/reroll", new Dictionary<string, object>(), Answered, failed: () => _busy = false);
        }

        /// <summary>An action's answer, the whole state: the next offer at once, or closed when none is left.</summary>
        private static void Answered(JsonElement root)
        {
            _busy = false;
            _state = root;
            if (OfferLevel(root) > 0) Open(root);
            else
            {
                Close(false);
                HideReopen();
            }
        }

        // ─── The pulsing «Don disponible» button ─────────────────────────────────────────

        private static void ShowReopen()
        {
            if (_reopen != null || _window != null) return;
            try
            {
                var layer = AdminItemsUi.FindLayer();
                if (layer == null) return;
                var button = new VisualElement();
                button.name = "JondoDonReopen";
                button.style.position = new StyleEnum<Position>(Position.Absolute);
                button.style.right = new StyleLength(36f);
                button.style.top = new StyleLength(150f);
                button.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                button.style.alignItems = new StyleEnum<Align>(Align.Center);
                button.style.paddingLeft = button.style.paddingRight = new StyleLength(14f);
                button.style.paddingTop = button.style.paddingBottom = new StyleLength(8f);
                button.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.80f, 0.58f, 0.14f, 0.95f));
                button.style.borderTopLeftRadius = button.style.borderTopRightRadius = new StyleLength(16f);
                button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = new StyleLength(16f);
                var star = Glyph("starFilled", 20f);
                if (star != null) { star.style.marginRight = new StyleLength(6f); button.Add(star); }
                var label = AdminItemsUi.Label("Don disponible", true);
                label.pickingMode = PickingMode.Ignore;
                label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                button.Add(label);
                button.AddManipulator(new Clickable(AdminItemsUi.Do(() =>
                {
                    _dismissedLevel = 0;
                    if (_state is JsonElement s && OfferLevel(s) > 0) Open(s);
                    else Open();
                })).Cast<IManipulator>());
                layer.Add(button);
                AdminItemsUi.KeepEvents(button);
                _reopen = button;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Don popup: the «Don disponible» button: {ex.Message}");
            }
        }

        private static void HideReopen()
        {
            try { _reopen?.RemoveFromHierarchy(); } catch { }
            _reopen = null;
        }

        // ─── Icons ───────────────────────────────────────────────────────────────────────

        private static bool _iconsBroken;
        private static readonly Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType, Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType> _iconTypes =
            new Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType, Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType>();
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _statIcons, _classSymbols;
        private static bool _kindsSought;

        /// <summary>An icon of the client's: the card's own picture, else the glyph; always a square of <paramref name="size"/>.</summary>
        private static VisualElement Icon(string kind, int id, string glyph, float size)
        {
            var place = new VisualElement();
            place.style.width = place.style.height = new StyleLength(size);
            place.style.flexShrink = new StyleFloat(0f);
            place.style.alignItems = new StyleEnum<Align>(Align.Center);
            place.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            place.pickingMode = PickingMode.Ignore;
            VisualElement picture = null;
            if (!_iconsBroken && id > 0)
            {
                try
                {
                    picture = kind switch
                    {
                        "stat" => StatPicture(id, size),
                        "spell" => Tile(IconsOf(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Spell), SpellIcon(id), size),
                        "item" => Tile(IconsOf(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Item), ItemIcon(id), size),
                        "class" => ClassSymbol(id, size),
                        _ => null,
                    };
                }
                catch (Exception ex)
                {
                    _iconsBroken = true;
                    MelonLogger.Warning($"[JondoFix] Don popup: no pictures, glyphs only: {ex.Message}");
                }
            }
            picture ??= Glyph(glyph, size * 0.75f);
            if (picture != null) place.Add(picture);
            return place;
        }

        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType IconsOf(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType kind)
        {
            if (!_iconTypes.TryGetValue(kind, out var type))
                _iconTypes[kind] = type = Il2CppCore.UILogic.Admin.AdminSelectItemUI.FindIconAddressableEntryType(kind);
            return type;
        }

        private static VisualElement Tile(Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType type, int icon, float size)
        {
            if (type == null || icon <= 0) return null;
            var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
            tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(type, icon);
            tile.pickingMode = PickingMode.Ignore;
            tile.style.width = tile.style.height = new StyleLength(size);
            return tile;
        }

        private static int SpellIcon(int spell)
        {
            try { return Il2CppCore.DataCenter.DataCenterModule.spellsDataRoot?.GetSpellById(spell)?.iconId ?? 0; }
            catch { return 0; }
        }

        private static int ItemIcon(int gid)
        {
            try { return Il2CppCore.DataCenter.DataCenterModule.itemsDataRoot?.GetItemById(gid)?.iconId ?? 0; }
            catch { return 0; }
        }

        /// <summary>
        /// The characteristic's picture as the sheet draws it: its CharacteristicData's asset
        /// (tx_strength, tx_vitality...) in the addressable kind whose path is the characteristic
        /// icons'. Null when either is missing.
        /// </summary>
        private static VisualElement StatPicture(int stat, float size)
        {
            SeekKinds();
            if (_statIcons == null) return null;
            string asset = null;
            try { asset = Il2CppCore.DataCenter.DataCenterModule.characteristicsDataRoot?.GetCharacteristicById(stat)?.asset; }
            catch { }
            if (string.IsNullOrEmpty(asset)) return null;
            var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
            tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_statIcons, asset, false);
            tile.pickingMode = PickingMode.Ignore;
            tile.style.width = tile.style.height = new StyleLength(size);
            return tile;
        }

        private static VisualElement ClassSymbol(int breed, float size)
        {
            SeekKinds();
            if (_classSymbols == null) return null;
            var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
            tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_classSymbols, breed);
            tile.pickingMode = PickingMode.Ignore;
            tile.style.width = tile.style.height = new StyleLength(size);
            return tile;
        }

        /// <summary>
        /// The client lists its addressable kinds as nameless statics of one obfuscated class
        /// (3.6.10's rd): the characteristic icons' and the class symbols' are found by their path.
        /// </summary>
        private static void SeekKinds()
        {
            if (_kindsSought) return;
            _kindsSought = true;
            var seen = new List<string>();
            try
            {
                foreach (var property in typeof(Il2Cpp.rd).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (property.PropertyType != typeof(Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType)) continue;
                    Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType type;
                    try { type = property.GetValue(null) as Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType; }
                    catch { continue; }
                    string prefix = type?.pathPrefix ?? "";
                    seen.Add(prefix);
                    if (prefix.IndexOf("characteristicIcon", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Several sizes may be listed: the larger is kept.
                        if (_statIcons == null || prefix.Contains("2x")) _statIcons = type;
                    }
                    else if (_statIcons == null && prefix.IndexOf("characteristic", StringComparison.OrdinalIgnoreCase) >= 0) _statIcons = type;
                    if (prefix == "classes/symbol_{0}") _classSymbols = type;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Don popup: the client's addressable kinds could not be read: {ex.Message}");
            }
            MelonLogger.Msg($"[JondoFix] Don popup: characteristic icons {(_statIcons?.pathPrefix ?? "none")}, class symbols {(_classSymbols?.pathPrefix ?? "none")} " +
                            $"(of {seen.Count} kinds: {string.Join(" | ", seen)}).");
        }

        /// <summary>One of the client's FigmaIcons glyphs by name; null when it has no such glyph.</summary>
        private static VisualElement Glyph(string name, float size)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || !Enum.TryParse<FigmaIcons>(name, true, out var glyph)) glyph = FigmaIcons.starFilled;
                var icon = new DofusIcon();
                icon.icon = glyph;
                icon.pickingMode = PickingMode.Ignore;
                icon.style.width = icon.style.height = new StyleLength(size);
                icon.style.fontSize = new StyleLength(size);
                return icon;
            }
            catch
            {
                return null;
            }
        }

        // ─── Pieces ──────────────────────────────────────────────────────────────────────

        private static VisualElement Button(string text, bool primary, Action click)
        {
            try
            {
                var button = new DofusButtonCustom(AdminItemsUi.Do(click), text, AdminItemsUi.NoIcon());
                button.mainStyle = primary ? DofusButtonCustom.ComponentStyleEnum.primary : DofusButtonCustom.ComponentStyleEnum.secondary;
                button.style.marginRight = new StyleLength(10f);
                button.style.marginBottom = new StyleLength(6f);
                button.style.flexShrink = new StyleFloat(0f);
                return button;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Don popup: the client's button would not build ({ex.Message}); drawing one.");
                return AdminItemsUi.FilterButton(text, primary, true, click);
            }
        }

        private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        // ─── Talking to the server ───────────────────────────────────────────────────────

        /// <summary>A control request off the frame; the answer (or its refusal, said) back on it.</summary>
        private static void Ask(string route, Dictionary<string, object> body, Action<JsonElement> then, Action failed = null)
        {
            if (Token.Length == 0) return;
            body["token"] = Token;
            Task.Run(async () =>
            {
                var (code, text) = await AdminItemsUi.PostAsync(route, body);
                _toUnity.Enqueue(() =>
                {
                    if (code != 200)
                    {
                        failed?.Invoke();
                        // Out of the world (loading, character choice): what was put off is offered again on return.
                        if (code == 409 && text.Contains("\"world\"")) { _dismissedLevel = 0; HideReopen(); }
                        if (_window != null) Say(Refusal(code, text));
                        return;
                    }
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
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
                    return (error.GetString() ?? "") switch
                    {
                        "world" => "Votre personnage n'est pas en jeu.",
                        "sesion" => "Session non reconnue.",
                        "personaje-ocupado" => "Personnage occupé, réessayez.",
                        var reason => reason,
                    };
            }
            catch { }
            return "Refusé (" + code.ToString(CultureInfo.InvariantCulture) + ").";
        }
    }
}
