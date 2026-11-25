using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppCore.DataCenter.Metadata.Item;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The small set of widgets the F9 «Progression» window is drawn with, the same on every tab:
    /// cards, progress bars, badges, item icons from the client's own tiles, buttons that say why
    /// they are disabled, sections and wrapping grids. Built from the client's components
    /// (DofusLabel in its text classes, DofusButtonCustom, Slots.Tile) as AdminItemsUi is.
    /// </summary>
    /// <remarks>
    /// Every text wraps and grows with its lines (height auto, flexShrink 0: AdminItemsUi.Label),
    /// so nothing is drawn over the next row; the pages scroll.
    /// </remarks>
    internal static class UiKit
    {
        // ─── Colours ─────────────────────────────────────────────────────────────────────

        internal static readonly UnityEngine.Color Gold = new UnityEngine.Color(0.92f, 0.72f, 0.28f, 1f);
        internal static readonly UnityEngine.Color GoldSoft = new UnityEngine.Color(0.92f, 0.72f, 0.28f, 0.35f);
        internal static readonly UnityEngine.Color Green = new UnityEngine.Color(0.45f, 0.85f, 0.45f, 1f);
        internal static readonly UnityEngine.Color Blue = new UnityEngine.Color(0.40f, 0.65f, 1f, 1f);
        internal static readonly UnityEngine.Color Red = new UnityEngine.Color(0.95f, 0.45f, 0.40f, 1f);
        internal static readonly UnityEngine.Color Purple = new UnityEngine.Color(0.72f, 0.50f, 1f, 1f);
        internal static readonly UnityEngine.Color Muted = new UnityEngine.Color(1f, 1f, 1f, 0.62f);
        internal static readonly UnityEngine.Color CardBack = new UnityEngine.Color(0.06f, 0.07f, 0.10f, 0.72f);
        internal static readonly UnityEngine.Color CardBorder = new UnityEngine.Color(1f, 1f, 1f, 0.10f);
        internal static readonly UnityEngine.Color BarBack = new UnityEngine.Color(0f, 0f, 0f, 0.5f);

        /// <summary>The Jondo Coin's item, shown beside every price in coins.</summary>
        internal const int JondoCoin = 20440;

        internal static UnityEngine.Color Alpha(UnityEngine.Color c, float a) => new UnityEngine.Color(c.r, c.g, c.b, a);

        // ─── Layout ──────────────────────────────────────────────────────────────────────

        internal static VisualElement Row(float gap = 0f)
        {
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            row.style.flexShrink = new StyleFloat(0f);
            return row;
        }

        internal static VisualElement Column()
        {
            var column = new VisualElement();
            column.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
            column.style.flexShrink = new StyleFloat(0f);
            return column;
        }

        /// <summary>A row whose children wrap onto the next line: cards, icons, buttons.</summary>
        internal static VisualElement Grid()
        {
            var grid = Row();
            grid.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            grid.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
            return grid;
        }

        /// <summary>A column that takes what is left of a row, and lets its texts wrap in it.</summary>
        internal static VisualElement Fill()
        {
            var fill = Column();
            fill.style.flexGrow = new StyleFloat(1f);
            fill.style.flexShrink = new StyleFloat(1f);
            fill.style.flexBasis = new StyleLength(0f);
            fill.style.minWidth = new StyleLength(0f);
            return fill;
        }

        internal static void Round(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = new StyleLength(radius);
            e.style.borderTopRightRadius = new StyleLength(radius);
            e.style.borderBottomLeftRadius = new StyleLength(radius);
            e.style.borderBottomRightRadius = new StyleLength(radius);
        }

        internal static void Border(VisualElement e, float width, UnityEngine.Color color)
        {
            e.style.borderTopWidth = new StyleFloat(width);
            e.style.borderBottomWidth = new StyleFloat(width);
            e.style.borderLeftWidth = new StyleFloat(width);
            e.style.borderRightWidth = new StyleFloat(width);
            e.style.borderTopColor = new StyleColor(color);
            e.style.borderBottomColor = new StyleColor(color);
            e.style.borderLeftColor = new StyleColor(color);
            e.style.borderRightColor = new StyleColor(color);
        }

        internal static void Pad(VisualElement e, float x, float y)
        {
            e.style.paddingLeft = new StyleLength(x);
            e.style.paddingRight = new StyleLength(x);
            e.style.paddingTop = new StyleLength(y);
            e.style.paddingBottom = new StyleLength(y);
        }

        // ─── Texts ───────────────────────────────────────────────────────────────────────

        /// <summary>A wrapping text; <paramref name="size"/> 0 keeps the class's size.</summary>
        internal static DofusLabel Text(string text, bool bold = false, float size = 0f, UnityEngine.Color? color = null)
        {
            var label = AdminItemsUi.Label(text ?? "", bold);
            if (size > 0f) label.style.fontSize = new StyleLength(size);
            if (color is UnityEngine.Color c) label.style.color = new StyleColor(c);
            return label;
        }

        internal static DofusLabel Small(string text, UnityEngine.Color? color = null) => Text(text, false, 14f, color ?? Muted);

        /// <summary>A section: its title in the client's large title class, an optional line under it.</summary>
        internal static VisualElement Section(string title, string subtitle = null)
        {
            var box = Column();
            box.style.marginTop = new StyleLength(14f);
            box.style.marginBottom = new StyleLength(8f);
            var label = new DofusLabel();
            label.text = title ?? "";
            label.AddToClassList("title_large");
            label.AddToClassList("textColor_white_white100");
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            label.style.height = new StyleLength(StyleKeyword.Auto);
            label.style.flexShrink = new StyleFloat(0f);
            box.Add(label);
            var rule = new VisualElement();
            rule.style.height = new StyleLength(2f);
            rule.style.marginTop = new StyleLength(4f);
            rule.style.backgroundColor = new StyleColor(GoldSoft);
            box.Add(rule);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = Small(subtitle);
                sub.style.marginTop = new StyleLength(6f);
                box.Add(sub);
            }
            return box;
        }

        // ─── Cards ───────────────────────────────────────────────────────────────────────

        /// <summary>A card: a dark tinted box with a thin frame; width 0 fills the line.</summary>
        internal static VisualElement Card(float width = 0f, UnityEngine.Color? accent = null)
        {
            var card = Column();
            if (width > 0f)
            {
                card.style.width = new StyleLength(width);
                card.style.marginRight = new StyleLength(12f);
            }
            card.style.marginBottom = new StyleLength(12f);
            Pad(card, 14f, 12f);
            card.style.backgroundColor = new StyleColor(CardBack);
            Border(card, 1f, accent is UnityEngine.Color a ? Alpha(a, 0.75f) : CardBorder);
            if (accent is UnityEngine.Color left)
            {
                card.style.borderLeftWidth = new StyleFloat(4f);
                card.style.borderLeftColor = new StyleColor(left);
            }
            Round(card, 6f);
            return card;
        }

        /// <summary>A card's head: an icon (or none), the title and a line under it, badges on the right.</summary>
        internal static VisualElement CardHead(VisualElement icon, string title, string subtitle, params VisualElement[] right)
        {
            var row = Row();
            row.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
            row.style.marginBottom = new StyleLength(6f);
            if (icon != null)
            {
                icon.style.marginRight = new StyleLength(10f);
                row.Add(icon);
            }
            var text = Fill();
            text.Add(Text(title, true));
            if (!string.IsNullOrEmpty(subtitle)) text.Add(Small(subtitle));
            row.Add(text);
            foreach (var r in right)
                if (r != null)
                {
                    r.style.marginLeft = new StyleLength(6f);
                    row.Add(r);
                }
            return row;
        }

        // ─── Progress, badges ────────────────────────────────────────────────────────────

        /// <summary>A bar of <paramref name="ratio"/> (0..1) with its text written over it.</summary>
        internal static VisualElement ProgressBar(float ratio, string text, UnityEngine.Color? fill = null, float height = 20f)
        {
            ratio = Math.Clamp(float.IsNaN(ratio) ? 0f : ratio, 0f, 1f);
            var bar = new VisualElement();
            bar.style.height = new StyleLength(height);
            bar.style.flexShrink = new StyleFloat(0f);
            bar.style.marginTop = new StyleLength(4f);
            bar.style.marginBottom = new StyleLength(4f);
            bar.style.backgroundColor = new StyleColor(BarBack);
            bar.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            Round(bar, 4f);
            Border(bar, 1f, CardBorder);
            var inside = new VisualElement();
            inside.style.position = new StyleEnum<Position>(Position.Absolute);
            inside.style.left = new StyleLength(0f);
            inside.style.top = new StyleLength(0f);
            inside.style.bottom = new StyleLength(0f);
            inside.style.width = new StyleLength(new Length(ratio * 100f, LengthUnit.Percent));
            inside.style.backgroundColor = new StyleColor(fill ?? Green);
            bar.Add(inside);
            if (!string.IsNullOrEmpty(text))
            {
                var label = Text(text, true, 13f, UnityEngine.Color.white);
                label.style.position = new StyleEnum<Position>(Position.Absolute);
                label.style.left = new StyleLength(0f);
                label.style.right = new StyleLength(0f);
                label.style.top = new StyleLength(0f);
                label.style.bottom = new StyleLength(0f);
                label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                label.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
                bar.Add(label);
            }
            return bar;
        }

        /// <summary>A small pill: a multiplier, a state, a rarity.</summary>
        internal static VisualElement Badge(string text, UnityEngine.Color color, bool strong = true)
        {
            var badge = Row();
            badge.style.flexShrink = new StyleFloat(0f);
            badge.style.marginRight = new StyleLength(6f);
            badge.style.marginBottom = new StyleLength(4f);
            Pad(badge, 8f, 2f);
            badge.style.backgroundColor = new StyleColor(Alpha(color, strong ? 0.30f : 0.12f));
            Border(badge, 1f, Alpha(color, strong ? 0.9f : 0.35f));
            Round(badge, 10f);
            var label = Text(text, strong, 13f, strong ? UnityEngine.Color.white : Muted);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            badge.Add(label);
            return badge;
        }

        /// <summary>A price: the coin's icon (Jondo Coins) and the number, or the number and its unit.</summary>
        internal static VisualElement Price(long amount, string unit, int coinItem = JondoCoin)
        {
            var row = Row();
            row.style.flexShrink = new StyleFloat(0f);
            bool coins = (unit ?? "").IndexOf("Jondo", StringComparison.OrdinalIgnoreCase) >= 0 && coinItem > 0;
            if (coins)
            {
                var icon = ItemIcon(coinItem, 20f);
                icon.style.marginRight = new StyleLength(4f);
                row.Add(icon);
            }
            var label = Text(Number(amount) + (coins ? "" : " " + unit), true, 15f, Gold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            row.Add(label);
            return row;
        }

        // The client runs .NET in globalization-invariant mode: no fr-FR culture, so the French
        // thousands space is put in by hand.
        internal static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");

        // ─── Icons ───────────────────────────────────────────────────────────────────────

        private static bool _icons = true;
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _iconType;
        private static readonly Dictionary<int, int> _iconIds = new Dictionary<int, int>();

        /// <summary>The client's icon of an item, by its id; 0 when the client does not know it.</summary>
        private static int IconIdOf(int gid)
        {
            if (gid <= 0) return 0;
            if (_iconIds.TryGetValue(gid, out int known)) return known;
            int icon = 0;
            try { icon = ItemData.GetItemById(gid)?.iconId ?? 0; } catch { }
            _iconIds[gid] = icon;
            return icon;
        }

        /// <summary>
        /// An item's picture in a framed square, from the client's own tile (where the admin picker
        /// keeps item icons); an empty square when there is none, so the rows line up.
        /// </summary>
        internal static VisualElement ItemIcon(int gid, float size = 44f, string glyph = null, UnityEngine.Color? tint = null)
        {
            var place = new VisualElement();
            place.style.width = new StyleLength(size);
            place.style.height = new StyleLength(size);
            place.style.minWidth = new StyleLength(size);
            place.style.minHeight = new StyleLength(size);
            place.style.flexShrink = new StyleFloat(0f);
            place.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            place.style.alignItems = new StyleEnum<Align>(Align.Center);
            place.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            place.pickingMode = PickingMode.Ignore;
            if (size >= 28f)
            {
                place.style.backgroundColor = new StyleColor(tint is UnityEngine.Color t ? Alpha(t, 0.25f) : new UnityEngine.Color(0f, 0f, 0f, 0.35f));
                Border(place, 1f, tint is UnityEngine.Color b ? Alpha(b, 0.8f) : GoldSoft);
                Round(place, 4f);
            }
            int icon = IconIdOf(gid);
            if (_icons && icon > 0)
            {
                try
                {
                    if (_iconType == null)
                        _iconType = Il2CppCore.UILogic.Admin.AdminSelectItemUI.FindIconAddressableEntryType(
                            Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Item);
                    var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
                    tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_iconType, icon);
                    tile.pickingMode = PickingMode.Ignore;
                    float inner = size >= 28f ? size - 4f : size;
                    tile.style.width = new StyleLength(inner);
                    tile.style.height = new StyleLength(inner);
                    place.Add(tile);
                    return place;
                }
                catch (Exception ex)
                {
                    _icons = false;
                    MelonLogger.Warning($"[JondoFix] UiKit: no item icons, the client's tile would not take one: {ex.Message}");
                }
            }
            if (!string.IsNullOrEmpty(glyph))
            {
                var letter = Text(glyph, true, Math.Max(12f, size * 0.42f), tint ?? Gold);
                letter.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
                letter.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                place.Add(letter);
            }
            return place;
        }

        /// <summary>A square with a letter or a symbol, for what has no item: a buff, a band.</summary>
        internal static VisualElement Glyph(string glyph, UnityEngine.Color tint, float size = 44f) => ItemIcon(0, size, glyph, tint);

        // ─── Buttons ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A button of the client's; disabled when <paramref name="why"/> is given, with the reason
        /// written under it in small red text (a disabled button always says why).
        /// </summary>
        internal static VisualElement Button(string text, Action click, bool primary = true, string why = null)
        {
            bool disabled = !string.IsNullOrEmpty(why);
            VisualElement button;
            try
            {
                var b = new DofusButtonCustom(AdminItemsUi.Do(() => { if (!disabled) click?.Invoke(); }), text, AdminItemsUi.NoIcon());
                b.mainStyle = primary ? DofusButtonCustom.ComponentStyleEnum.primary : DofusButtonCustom.ComponentStyleEnum.secondary;
                button = b;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] UiKit: the client's button would not build ({ex.Message}); drawing one.");
                button = AdminItemsUi.FilterButton(text, primary, true, () => { if (!disabled) click?.Invoke(); });
            }
            button.style.flexShrink = new StyleFloat(0f);
            if (!disabled)
            {
                button.style.marginRight = new StyleLength(8f);
                button.style.marginBottom = new StyleLength(6f);
                return button;
            }
            try { button.SetEnabled(false); } catch { }
            button.style.opacity = new StyleFloat(0.55f);
            var box = Column();
            box.style.marginRight = new StyleLength(8f);
            box.style.marginBottom = new StyleLength(6f);
            box.style.maxWidth = new StyleLength(260f);
            box.Add(button);
            var reason = Text(why, false, 13f, Red);
            reason.style.marginTop = new StyleLength(2f);
            box.Add(reason);
            return box;
        }

        /// <summary>A button with an item's icon before it, for a buy or a pick.</summary>
        internal static VisualElement IconButton(int gid, string text, Action click, bool primary = true, string why = null)
        {
            var row = Row();
            row.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
            var icon = ItemIcon(gid, 32f);
            icon.style.marginRight = new StyleLength(6f);
            icon.style.marginTop = new StyleLength(2f);
            row.Add(icon);
            row.Add(Button(text, click, primary, why));
            return row;
        }

        /// <summary>
        /// A clickable item tile for a grid: the icon, its name under it, a line (a price, a count),
        /// gold-framed when picked. Clicking it calls <paramref name="click"/> (none: not clickable).
        /// </summary>
        internal static VisualElement ItemTile(int gid, string name, string line, bool picked, Action click, float width = 110f)
        {
            var tile = Column();
            tile.style.width = new StyleLength(width);
            tile.style.alignItems = new StyleEnum<Align>(Align.Center);
            tile.style.marginRight = new StyleLength(8f);
            tile.style.marginBottom = new StyleLength(8f);
            Pad(tile, 6f, 8f);
            tile.style.backgroundColor = new StyleColor(picked ? Alpha(Gold, 0.22f) : CardBack);
            Border(tile, picked ? 2f : 1f, picked ? Gold : CardBorder);
            Round(tile, 6f);
            tile.Add(ItemIcon(gid, 48f));
            var label = Text(name, picked, 13f, UnityEngine.Color.white);
            label.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.UpperCenter);
            label.style.marginTop = new StyleLength(4f);
            tile.Add(label);
            if (!string.IsNullOrEmpty(line))
            {
                var sub = Small(line);
                sub.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.UpperCenter);
                tile.Add(sub);
            }
            if (click != null)
            {
                try { tile.AddManipulator(new Clickable(AdminItemsUi.Do(click)).Cast<IManipulator>()); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] UiKit: a tile takes no click: {ex.Message}"); }
            }
            return tile;
        }

        /// <summary>A line of label and value, for a card's facts.</summary>
        internal static VisualElement Fact(string label, string value, UnityEngine.Color? color = null)
        {
            var row = Row();
            row.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
            row.style.marginBottom = new StyleLength(2f);
            var l = Small(label + " :");
            l.style.marginRight = new StyleLength(6f);
            row.Add(l);
            var v = Text(value, true, 14f, color ?? UnityEngine.Color.white);
            v.style.flexShrink = new StyleFloat(1f);
            row.Add(v);
            return row;
        }
    
        /// <summary>
        /// Makes a drawn page lay out like a document: nothing in its columns is squeezed below its
        /// own height (the cause of texts drawn over each other), and every text wraps and grows
        /// with its lines. Run once on the page after each draw.
        /// </summary>
        internal static void Unsqueeze(VisualElement root, int depth = 0)
        {
            if (root == null || depth > 40) return;
            try
            {
                int count = root.childCount;
                for (int i = 0; i < count; i++)
                {
                    var child = root.ElementAt(i);
                    if (child == null) continue;
                    child.style.flexShrink = new StyleFloat(0f);
                    if (child.TryCast<TextElement>() != null)
                    {
                        child.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
                        child.style.height = new StyleLength(StyleKeyword.Auto);
                        child.style.minHeight = new StyleLength(StyleKeyword.Auto);
                    }
                    Unsqueeze(child, depth + 1);
                }
            }
            catch (Exception ex)
            {
                if (depth == 0) MelonLogger.Warning($"[JondoFix] UiKit.Unsqueeze: {ex.Message}");
            }
        }
}
}
