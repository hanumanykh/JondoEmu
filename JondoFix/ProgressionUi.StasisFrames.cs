using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppCore.DataCenter.Metadata.Effect;
using Il2CppCore.DataCenter.Metadata.Effect.Instance;
using Il2CppCore.UILogic.Components.Slots;
using Il2CppCore.UILogic.Components.Tooltips.Builder;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine.UIElements;
using K = JondoFix.UiKit;

namespace JondoFix
{
    /// <summary>
    /// Stasis tiers made visible: a coloured frame and an «S1».. «S4» corner badge on every item
    /// slot the client draws (Slots.Slot: inventory, equipment, bank, exchange, loot), a coloured
    /// header line in the item tooltip, and a small popup opened from the badge with «Fusionner (x/3)»
    /// and «Raffiner (+N)», which call the Forge Stasis tab's own server actions (stasis/act).
    /// </summary>
    /// <remarks>
    /// The tier is read client-side from the item's effects: the server writes «Stasis S2» in the
    /// 988 line (Stasis.cs, LabelPattern), so no server change. The item wrapper classes are
    /// obfuscated in this client: Il2Cpp.lz is the item interface (dbpz = effects, fgwb = ItemData),
    /// Il2Cpp.me the inventory item; a client update renaming them only loses the frames (logged once).
    /// </remarks>
    public static partial class ProgressionUi
    {
        private const string FrameName = "JondoStasisFrame";
        private const string TooltipLineName = "JondoStasisTooltipLine";
        private const string PopupName = "JondoStasisPopup";
        private static readonly Regex StasisLabel = new Regex(@"Stasis S([1-4])", RegexOptions.Compiled);
        private static readonly HashSet<string> _framesWarned = new HashSet<string>();

        internal static UnityEngine.Color TierColor(int tier) => tier switch
        {
            1 => new UnityEngine.Color(0.35f, 0.85f, 0.35f, 1f),
            2 => new UnityEngine.Color(0.30f, 0.60f, 1f, 1f),
            3 => new UnityEngine.Color(0.72f, 0.42f, 1f, 1f),
            _ => new UnityEngine.Color(1f, 0.66f, 0.15f, 1f),
        };

        private static string TierBonus(int tier) => tier switch { 1 => "+25 %", 2 => "+50 %", 3 => "x2", _ => "x3" };

        private static void WarnFramesOnce(string key, string message)
        {
            if (_framesWarned.Add(key)) MelonLogger.Warning("[JondoFix] Stasis frames: " + message);
        }

        // ─── Install ─────────────────────────────────────────────────────────────────────

        internal static void InstallStasisFrames(MelonLogger.Instance log)
        {
            var harmony = new HarmonyLib.Harmony("com.jondo.fix.stasisframes");
            int ok = 0;
            ok += PatchFrames(harmony, typeof(Slot), "RefreshImmediate", nameof(SlotPostfix), log);
            ok += PatchFrames(harmony, typeof(Slot), "UpdateSlotData", nameof(SlotPostfix), log);
            ok += PatchFrames(harmony, typeof(Slot), "UpdateContent", nameof(SlotPostfix), log);
            ok += PatchFrames(harmony, typeof(ItemTooltipBuilder), "SetupViewWithContent", nameof(TooltipPostfix), log);
            ok += PatchFrames(harmony, typeof(ItemTooltipBuilder), "UpdateContent", nameof(TooltipPostfix), log);
            log.Msg($"[JondoFix] Stasis frames: {ok}/5 hooks on Slots.Slot and ItemTooltipBuilder.");
        }

        private static int PatchFrames(HarmonyLib.Harmony harmony, Type type, string method, string postfix, MelonLogger.Instance log)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) { log.Warning($"[JondoFix] Stasis frames: {type.Name}.{method} not found."); return 0; }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProgressionUi).GetMethod(postfix,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                return 1;
            }
            catch (Exception ex)
            {
                log.Warning($"[JondoFix] Stasis frames: could not patch {type.Name}.{method}: {ex.Message}");
                return 0;
            }
        }

        // ─── Reading the tier ────────────────────────────────────────────────────────────

        /// <summary>The Stasis tier (1..4) of an item wrapper, 0 when none; and its template id.</summary>
        private static int StasisTierOf(Il2CppObjectBase wrapper, out int gid)
        {
            gid = 0;
            if (wrapper == null) return 0;
            Il2CppSystem.Collections.Generic.List<EffectInstanceData> effects = null;
            try
            {
                var item = wrapper.TryCast<global::Il2Cpp.lz>();
                if (item != null) { effects = item.dbpz; gid = item.fgwb?.id ?? 0; }
            }
            catch { }
            if (effects == null)
            {
                try
                {
                    var item = wrapper.TryCast<global::Il2Cpp.me>();
                    if (item != null) { effects = item.dbpz; gid = item.fgwb?.id ?? 0; }
                }
                catch (Exception ex) { WarnFramesOnce("wrapper", "item wrapper not readable: " + ex.Message); }
            }
            if (effects == null) return 0;
            try
            {
                for (int i = 0; i < effects.Count; i++)
                {
                    var effect = effects[i];
                    if (effect == null) continue;
                    var text = effect.TryCast<EffectInstanceString>()?.text;
                    if (string.IsNullOrEmpty(text) || text.IndexOf("Stasis S", StringComparison.Ordinal) < 0) continue;
                    var m = StasisLabel.Match(text);
                    if (m.Success) return m.Groups[1].Value[0] - '0';
                }
            }
            catch (Exception ex) { WarnFramesOnce("effects", "effects not readable: " + ex.Message); }
            return 0;
        }

        // ─── Slots ───────────────────────────────────────────────────────────────────────

        private static void SlotPostfix(Slot __instance)
        {
            try
            {
                if (__instance == null) return;
                int gid = 0, tier = 0;
                try
                {
                    var data = __instance.data;
                    if (data != null) tier = StasisTierOf(data, out gid);
                }
                catch { }
                var old = FindChild(__instance, FrameName);
                if (tier <= 0)
                {
                    old?.RemoveFromHierarchy();
                    return;
                }
                if (old != null && old.tooltip == $"{gid}:{tier}") return;
                old?.RemoveFromHierarchy();
                AddFrame(__instance, gid, tier);
            }
            catch (Exception ex) { WarnFramesOnce("slot", "slot overlay failed: " + ex.Message); }
        }

        private static VisualElement FindChild(VisualElement parent, string name)
        {
            try
            {
                var h = parent.hierarchy;
                for (int i = 0; i < h.childCount; i++)
                {
                    var c = h[i];
                    if (c != null && c.name == name) return c;
                }
                var cc = parent.contentContainer;
                if (cc != null && cc != parent)
                    for (int i = 0; i < cc.childCount; i++)
                    {
                        var c = cc[i];
                        if (c != null && c.name == name) return c;
                    }
            }
            catch { }
            return null;
        }

        private static void AddFrame(Slot slot, int gid, int tier)
        {
            var color = TierColor(tier);
            var frame = new VisualElement();
            frame.name = FrameName;
            frame.tooltip = $"{gid}:{tier}";
            frame.pickingMode = PickingMode.Ignore;
            frame.style.position = new StyleEnum<Position>(Position.Absolute);
            frame.style.left = new StyleLength(0f);
            frame.style.top = new StyleLength(0f);
            frame.style.right = new StyleLength(0f);
            frame.style.bottom = new StyleLength(0f);
            K.Border(frame, 2f, color);
            K.Round(frame, 4f);
            frame.style.backgroundColor = new StyleColor(K.Alpha(color, 0.10f));

            var badge = new VisualElement();
            badge.style.position = new StyleEnum<Position>(Position.Absolute);
            badge.style.left = new StyleLength(0f);
            badge.style.top = new StyleLength(0f);
            badge.style.backgroundColor = new StyleColor(K.Alpha(color, 0.92f));
            badge.style.borderBottomRightRadius = new StyleLength(4f);
            badge.style.paddingLeft = new StyleLength(3f);
            badge.style.paddingRight = new StyleLength(3f);
            badge.pickingMode = PickingMode.Position;
            var label = new Il2CppCore.UILogic.Components.DofusLabel();
            label.text = "S" + tier.ToString(CultureInfo.InvariantCulture);
            label.pickingMode = PickingMode.Ignore;
            label.style.fontSize = new StyleLength(11f);
            label.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            label.style.color = new StyleColor(UnityEngine.Color.white);
            label.style.marginLeft = new StyleLength(0f);
            label.style.marginRight = new StyleLength(0f);
            label.style.marginTop = new StyleLength(0f);
            label.style.marginBottom = new StyleLength(0f);
            badge.Add(label);
            try
            {
                badge.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                    new Action<PointerDownEvent>(e =>
                    {
                        try { e.StopPropagation(); } catch { }
                        try { OpenStasisPopup(badge, gid, tier); }
                        catch (Exception ex) { WarnFramesOnce("popup", "popup failed: " + ex.Message); }
                    })), TrickleDown.NoTrickleDown);
            }
            catch (Exception ex) { WarnFramesOnce("badgeclick", "badge not clickable: " + ex.Message); }
            frame.Add(badge);

            try { slot.hierarchy.Add(frame); }
            catch { slot.Add(frame); }
            try { frame.BringToFront(); } catch { }
        }

        // ─── Tooltip ─────────────────────────────────────────────────────────────────────

        private static void TooltipPostfix(ItemTooltipBuilder __instance)
        {
            try
            {
                if (__instance == null) return;
                int gid = 0, tier = 0;
                try { tier = StasisTierOf(__instance.itemWrapper, out gid); } catch { }
                VisualElement host = null;
                try { host = __instance.tooltipWithoutShadow; } catch { }
                if (host == null) try { host = __instance.root; } catch { }
                if (host == null) return;
                FindChild(host, TooltipLineName)?.RemoveFromHierarchy();
                if (tier <= 0) return;

                var color = TierColor(tier);
                string next = tier >= 4 ? "palier maximum" : $"fusion : 3 → S{tier + 1}";
                var line = new VisualElement();
                line.name = TooltipLineName;
                line.pickingMode = PickingMode.Ignore;
                line.style.backgroundColor = new StyleColor(K.Alpha(color, 0.28f));
                line.style.borderLeftWidth = new StyleFloat(4f);
                line.style.borderLeftColor = new StyleColor(color);
                K.Pad(line, 8f, 3f);
                line.style.marginBottom = new StyleLength(4f);
                var label = K.Text($"Stasis S{tier} — {TierBonus(tier)} ({next})", true, 14f, color);
                label.pickingMode = PickingMode.Ignore;
                line.Add(label);
                host.Insert(0, line);
            }
            catch (Exception ex) { WarnFramesOnce("tooltip", "tooltip line failed: " + ex.Message); }
        }

        // ─── Popup: Fusionner / Raffiner from the inventory ──────────────────────────────

        private static VisualElement _stasisPopup;
        private static string _stasisPopupConfirm = "";

        private static void CloseStasisPopup()
        {
            try { _stasisPopup?.RemoveFromHierarchy(); } catch { }
            _stasisPopup = null;
            _stasisPopupConfirm = "";
        }

        private static void OpenStasisPopup(VisualElement anchor, int gid, int tier)
        {
            CloseStasisPopup();
            if (Token.Length == 0) return;
            var layer = AdminItemsUi.FindLayer();
            if (layer == null) return;

            var color = TierColor(tier);
            var popup = K.Card(320f, color);
            popup.name = PopupName;
            popup.style.position = new StyleEnum<Position>(Position.Absolute);
            popup.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.06f, 0.07f, 0.10f, 0.96f));
            try
            {
                var at = anchor.worldBound;
                var local = layer.WorldToLocal(new UnityEngine.Vector2(at.xMax + 6f, at.yMin));
                float lw = layer.resolvedStyle.width, lh = layer.resolvedStyle.height;
                float x = local.x, y = local.y;
                if (!float.IsNaN(lw) && lw > 0 && x + 330f > lw) x = Math.Max(0f, lw - 330f);
                if (!float.IsNaN(lh) && lh > 0 && y + 220f > lh) y = Math.Max(0f, lh - 220f);
                popup.style.left = new StyleLength(x);
                popup.style.top = new StyleLength(y);
            }
            catch { popup.style.left = new StyleLength(400f); popup.style.top = new StyleLength(200f); }
            try
            {
                popup.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                    new Action<PointerDownEvent>(e => { try { e.StopPropagation(); } catch { } })), TrickleDown.NoTrickleDown);
            }
            catch { }
            layer.Add(popup);
            _stasisPopup = popup;
            DrawStasisPopup(gid, tier, null, "Chargement…");
            AskForgePiece(gid, tier, "");
        }

        private static void AskForgePiece(int gid, int tier, string said)
        {
            Ask("stasis/panel", new Dictionary<string, object> { ["tab"] = "stasis", ["structured"] = "1" }, root =>
            {
                if (_stasisPopup == null) return;
                JsonElement? piece = null;
                string unit = "Jondo Coins";
                if (Obj(root, "view", out var view))
                {
                    unit = Text(view, "coinLabel");
                    foreach (var p in Arr(view, "pieces"))
                        if (Int(p, "gid") == gid && Int(p, "tier") == tier) { piece = p; break; }
                }
                _stasisUnit = unit.Length > 0 ? unit : "Jondo Coins";
                DrawStasisPopup(gid, tier, piece, said);
            }, quiet: true, failed: () => { if (_stasisPopup != null) DrawStasisPopup(gid, tier, null, "Serveur injoignable."); });
        }

        private static string _stasisUnit = "Jondo Coins";

        private static void DrawStasisPopup(int gid, int tier, JsonElement? piece, string note)
        {
            var popup = _stasisPopup;
            if (popup == null) return;
            popup.Clear();
            var color = TierColor(tier);
            string name = "";
            int count = 0;
            if (piece is JsonElement pe) { name = Text(pe, "name"); count = Int(pe, "count"); }
            popup.Add(K.CardHead(K.ItemIcon(gid, 40f), name.Length > 0 ? name : "Objet stasifié",
                $"Stasis S{tier} — {TierBonus(tier)}", K.Badge("S" + tier.ToString(CultureInfo.InvariantCulture), color)));
            if (!string.IsNullOrEmpty(note)) popup.Add(K.Small(note));

            var buttons = K.Grid();
            buttons.style.marginTop = new StyleLength(6f);
            string arg = gid.ToString(CultureInfo.InvariantCulture) + " S" + tier.ToString(CultureInfo.InvariantCulture);
            if (piece is JsonElement p)
            {
                long cost = Long(p, "fuseCost");
                if (cost >= 0)
                {
                    bool confirm = _stasisPopupConfirm == "fuse";
                    string text = confirm
                        ? (Bool(p, "loseExos") ? "Confirmer (exos perdus)" : "Confirmer la fusion")
                        : $"Fusionner ({Math.Min(count, 3)}/3) → {Text(p, "fuseTo")}";
                    buttons.Add(K.Button(text, () =>
                    {
                        if (_stasisPopupConfirm != "fuse") { _stasisPopupConfirm = "fuse"; DrawStasisPopup(gid, tier, piece, $"Coût : {F(cost)} {_stasisUnit}"); return; }
                        StasisPopupAct(".fusion", arg, gid, tier);
                    }, true, Bool(p, "canFuse") ? null : Text(p, "fuseWhy")));
                }
                long buyback = Long(p, "buyback");
                bool sellConfirm = _stasisPopupConfirm == "sell";
                buttons.Add(K.Button(sellConfirm ? "Confirmer la vente" : $"Raffiner (+{F(buyback)} {_stasisUnit})", () =>
                {
                    if (_stasisPopupConfirm != "sell") { _stasisPopupConfirm = "sell"; DrawStasisPopup(gid, tier, piece, "Vendre 1 exemplaire au raffineur ?"); return; }
                    StasisPopupAct(".raffiner", arg, gid, tier);
                }, false));
            }
            else if (note != "Chargement…")
            {
                popup.Add(K.Small("Seuls les objets du sac (non portés) peuvent être fusionnés ou raffinés."));
            }
            buttons.Add(K.Button("Forge Stasis", () => { CloseStasisPopup(); if (_window == null) Open(); ShowTab("stasis"); }, false));
            buttons.Add(K.Button("Fermer", CloseStasisPopup, false));
            popup.Add(buttons);
        }

        private static void StasisPopupAct(string command, string arg, int gid, int tier)
        {
            _stasisPopupConfirm = "";
            Ask("stasis/act", new Dictionary<string, object> { ["tab"] = "stasis", ["command"] = command, ["args"] = arg, ["structured"] = "1" }, root =>
            {
                var said = new List<string>();
                if (root.TryGetProperty("said", out var s) && s.ValueKind == JsonValueKind.Array)
                    foreach (var line in s.EnumerateArray()) said.Add(line.GetString() ?? "");
                string text = Clean(string.Join(" ", said));
                if (_stasisPopup != null) AskForgePiece(gid, tier, text.Length > 0 ? text : "Fait.");
                if (_window != null && _tab == "stasis") { _panel = root; if (_state is JsonElement st) Show(st); }
            }, quiet: true, failed: () => { if (_stasisPopup != null) DrawStasisPopup(gid, tier, null, "Action refusée."); });
        }
    }
}
