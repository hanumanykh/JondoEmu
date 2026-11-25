using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using Il2CppCore.UILogic.Components;
using MelonLoader;
using UnityEngine.UIElements;
using K = JondoFix.UiKit;

namespace JondoFix
{
    /// <summary>
    /// The two always-visible HUD panels (audit items 3 and 4): the zone gauge bar (zone and level,
    /// y / z with the percent, the target's icon -- a click opens the F9 «Jauge» tab --, the
    /// multipliers that apply) and «Le Système» (the chapter mission and the tracks, each with GO
    /// and Récupérer; a click on the title opens the F9 «Missions» tab).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data: /api/stasis/hud (server: CommandHandler.HudView.cs), cached per character there and
    /// asked every <see cref="HudPollEvery"/> s out of a fight; in a fight the answer is only
    /// «fight» and the panels hide, the poll slowing to <see cref="HudFightPollEvery"/> s so the
    /// fight's end is seen quickly. The server builds the view again at once when the map or the
    /// fight state changed, so a map change and a fight's end show on the next poll; a GO or a
    /// Récupérer asks a «fresh» view at once.
    /// </para>
    /// <para>
    /// Each panel is dragged by its title bar (the «⠿» grip) and collapsed with «–» / «+»; its
    /// place and whether it is collapsed are kept in MelonPreferences «JondoFix_Hud».
    /// </para>
    /// </remarks>
    public static partial class ProgressionUi
    {
        private const float HudPollEvery = 5f, HudFightPollEvery = 3f;

        private sealed class HudPanel
        {
            public string Key;
            public VisualElement Root, Body, CollapseLabelBox;
            public DofusLabel CollapseLabel;
            public MelonPreferences_Entry<float> X, Y;
            public MelonPreferences_Entry<bool> Collapsed;
            public string LastData = "";
        }

        private static HudPanel _gaugePanel, _systemPanel;
        private static MelonPreferences_Category _hudPrefs;
        private static float _lastHudPoll = -100f;
        private static bool _hudPollAsked, _hudFight = true, _hudFresh;
        private static int _hudMap;
        private static JsonElement? _hudData;

        // Dragging: the panel, the pointer and the panel's place when it started.
        private static HudPanel _dragging;
        private static UnityEngine.Vector2 _dragFrom;
        private static float _dragLeft, _dragTop;

        /// <summary>From HudTick: the poll, the drag, the panels kept on the layer and shown or hidden.</summary>
        private static void HudPanelsTick(float now)
        {
            TickDrag();

            float every = _hudFight ? HudFightPollEvery : HudPollEvery;
            if (!_hudPollAsked && (now - _lastHudPoll >= every || _hudFresh))
            {
                _lastHudPoll = now;
                _hudPollAsked = true;
                var body = new Dictionary<string, object>();
                if (_hudFresh) body["fresh"] = "1";
                _hudFresh = false;
                Ask("stasis/hud", body, OnHud, quiet: true, failed: () => { _hudPollAsked = false; _hudFight = true; });
            }
        }

        /// <summary>From HudTick's slower half: both panels on the layer when they should show.</summary>
        private static void HudPanelsPlace()
        {
            bool show = _inWorld && !_hudFight && _hudData.HasValue;
            foreach (var p in new[] { Panel(ref _gaugePanel, "Gauge"), Panel(ref _systemPanel, "System") })
            {
                if (p == null) continue;
                if (!show)
                {
                    p.Root.RemoveFromHierarchy();
                    continue;
                }
                if (p.Root.panel == null)
                {
                    var layer = AdminItemsUi.FindLayer();
                    if (layer == null) return;
                    layer.Add(p.Root);
                }
                p.Root.BringToFront();
            }
        }

        private static void OnHud(JsonElement root)
        {
            _hudPollAsked = false;
            bool wasFight = _hudFight;
            _hudFight = Bool(root, "fight");
            int map = Int(root, "mapId");
            if (wasFight && !_hudFight) _lastAlerts = -100f;   // the fight recap toast, at once
            _hudMap = map;
            if (_hudFight) { HudPanelsPlace(); return; }
            _hudData = root;
            try
            {
                if (Obj(root, "gauge", out var gauge)) Fill(Panel(ref _gaugePanel, "Gauge"), gauge, FillGauge);
                if (Obj(root, "missions", out var missions)) Fill(Panel(ref _systemPanel, "System"), missions, FillSystem);
            }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] HUD panels: {ex.Message}"); }
            HudPanelsPlace();
        }

        private static void Fill(HudPanel p, JsonElement data, Action<VisualElement, JsonElement> draw)
        {
            if (p == null) return;
            string raw = data.GetRawText();
            if (raw == p.LastData) return;
            p.LastData = raw;
            p.Body.Clear();
            draw(p.Body, data);
        }

        // ─── The panels' frame ───────────────────────────────────────────────────────────

        private static void HudPrefs()
        {
            if (_hudPrefs != null) return;
            _hudPrefs = MelonPreferences.CreateCategory("JondoFix_Hud", "JondoFix HUD panels");
        }

        /// <summary>The panel, built the first time (null when the layer is not there yet).</summary>
        private static HudPanel Panel(ref HudPanel field, string key)
        {
            if (field != null) return field;
            try
            {
                HudPrefs();
                bool gauge = key == "Gauge";
                var p = new HudPanel
                {
                    Key = key,
                    X = _hudPrefs.CreateEntry(key + "X", -1f, description: $"{key} panel: left in panel pixels (-1: default place)."),
                    Y = _hudPrefs.CreateEntry(key + "Y", -1f, description: $"{key} panel: top in panel pixels (-1: default place)."),
                    Collapsed = _hudPrefs.CreateEntry(key + "Collapsed", false, description: $"{key} panel collapsed."),
                };
                var root = K.Column();
                root.name = "JondoHud" + key;
                root.style.position = new StyleEnum<Position>(Position.Absolute);
                root.style.width = new StyleLength(gauge ? 400f : 330f);
                root.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.08f, 0.07f, 0.05f, 0.88f));
                K.Border(root, 1f, K.Alpha(K.Gold, 0.8f));
                K.Round(root, 8f);
                K.Pad(root, 8f, 5f);

                var head = K.Row();
                head.style.alignItems = new StyleEnum<Align>(Align.Center);
                var grip = K.Text("⠿", true, 15f, K.Muted);
                grip.style.marginRight = new StyleLength(6f);
                head.Add(grip);
                var title = K.Text(gauge ? "Jauge de zone" : "Le Système", true, 14f, K.Gold);
                title.style.flexGrow = new StyleFloat(1f);
                head.Add(title);
                var collapse = K.Row();
                K.Pad(collapse, 6f, 0f);
                var collapseLabel = K.Text(p.Collapsed.Value ? "+" : "–", true, 16f, K.Gold);
                collapse.Add(collapseLabel);
                head.Add(collapse);
                root.Add(head);

                var body = K.Column();
                body.style.marginTop = new StyleLength(3f);
                body.style.display = new StyleEnum<DisplayStyle>(p.Collapsed.Value ? DisplayStyle.None : DisplayStyle.Flex);
                root.Add(body);

                p.Root = root;
                p.Body = body;
                p.CollapseLabel = collapseLabel;
                p.CollapseLabelBox = collapse;

                try
                {
                    collapse.AddManipulator(new Clickable(AdminItemsUi.Do(() =>
                    {
                        p.Collapsed.Value = !p.Collapsed.Value;
                        p.CollapseLabel.text = p.Collapsed.Value ? "+" : "–";
                        p.Body.style.display = new StyleEnum<DisplayStyle>(p.Collapsed.Value ? DisplayStyle.None : DisplayStyle.Flex);
                        SaveHudPrefs();
                    })).Cast<IManipulator>());
                    // The title opens the tab the panel is about.
                    string tab = gauge ? "jauge" : "missions";
                    title.AddManipulator(new Clickable(AdminItemsUi.Do(() => OpenOn(tab))).Cast<IManipulator>());
                    // The grip (and the empty head around it) drags.
                    grip.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                        new Action<PointerDownEvent>(e =>
                        {
                            try { e.StopPropagation(); } catch { }
                            StartDrag(p);
                        })), TrickleDown.NoTrickleDown);
                }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] HUD {key}: no clicks ({ex.Message})."); }
                AdminItemsUi.KeepEvents(root);

                PlaceHudPanel(p);
                field = p;
                return p;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] HUD {key} panel: {ex.Message}");
                return null;
            }
        }

        /// <summary>Its remembered place, or the default: the gauge at the top centre, the Système at the left edge.</summary>
        private static void PlaceHudPanel(HudPanel p)
        {
            float left = p.X.Value, top = p.Y.Value;
            if (left < 0f || top < 0f)
            {
                float width = 1600f;
                try
                {
                    var layer = AdminItemsUi.FindLayer();
                    float w = layer?.resolvedStyle.width ?? 0f;
                    if (w > 100f && !float.IsNaN(w)) width = w;
                }
                catch { }
                if (p.Key == "Gauge") { left = Math.Max(0f, (width - 400f) / 2f); top = 6f; }
                else { left = 14f; top = 180f; }
            }
            p.Root.style.left = new StyleLength(left);
            p.Root.style.top = new StyleLength(top);
        }

        private static void SaveHudPrefs()
        {
            try { _hudPrefs?.SaveToFile(false); } catch { }
        }

        // ─── Dragging ────────────────────────────────────────────────────────────────────

        private static bool HudPointer(VisualElement on, out UnityEngine.Vector2 at)
        {
            at = default;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                var panel = on?.panel;
                if (mouse == null || panel == null) return false;
                var screen = mouse.position.ReadValue();
                at = RuntimePanelUtils.ScreenToPanel(panel, new UnityEngine.Vector2(screen.x, UnityEngine.Screen.height - screen.y));
                return true;
            }
            catch { return false; }
        }

        private static void StartDrag(HudPanel p)
        {
            if (!HudPointer(p.Root, out _dragFrom)) return;
            _dragLeft = p.Root.resolvedStyle.left;
            _dragTop = p.Root.resolvedStyle.top;
            if (float.IsNaN(_dragLeft)) _dragLeft = 0f;
            if (float.IsNaN(_dragTop)) _dragTop = 0f;
            _dragging = p;
        }

        private static void TickDrag()
        {
            var p = _dragging;
            if (p == null) return;
            bool held;
            try { held = UnityEngine.InputSystem.Mouse.current?.leftButton.isPressed ?? false; }
            catch { held = false; }
            if (!held || !HudPointer(p.Root, out var at))
            {
                _dragging = null;
                p.X.Value = Math.Max(0f, p.Root.resolvedStyle.left);
                p.Y.Value = Math.Max(0f, p.Root.resolvedStyle.top);
                SaveHudPrefs();
                return;
            }
            p.Root.style.left = new StyleLength(Math.Max(0f, _dragLeft + at.x - _dragFrom.x));
            p.Root.style.top = new StyleLength(Math.Max(0f, _dragTop + at.y - _dragFrom.y));
        }

        // ─── The zone gauge ──────────────────────────────────────────────────────────────

        private static void FillGauge(VisualElement body, JsonElement g)
        {
            string name = Text(g, "name");
            int level = Int(g, "level");
            if (!Bool(g, "available"))
            {
                body.Add(K.Small(name.Length > 0 ? $"{name}{(level > 0 ? $" (niv. {level})" : "")} : pas de jauge ici." : "Pas de jauge ici."));
                return;
            }
            var row = K.Row();
            row.style.alignItems = new StyleEnum<Align>(Align.Center);

            int target = Int(g, "target");
            var icon = target != 0 ? K.ItemIcon(target, 38f) : K.Glyph("?", K.Gold, 38f);
            icon.style.marginRight = new StyleLength(8f);
            try { icon.AddManipulator(new Clickable(AdminItemsUi.Do(() => OpenOn("jauge"))).Cast<IManipulator>()); } catch { }
            row.Add(icon);

            var col = K.Fill();
            var line = K.Row();
            line.style.alignItems = new StyleEnum<Align>(Align.Center);
            var zone = K.Text($"{name} · niv. {level}", true, 13f, UnityEngine.Color.white);
            zone.style.flexGrow = new StyleFloat(1f);
            zone.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            zone.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            line.Add(zone);
            foreach (var b in Arr(g, "badges"))
            {
                string label = Text(b, "label");
                double factor = b.TryGetProperty("factor", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 1d;
                bool perFight = Bool(b, "perFight");
                var color = label == "Marée" ? K.Blue : label == "Buff" ? K.Green : K.Purple;
                var badge = K.Badge($"{label} x{factor.ToString("0.#", CultureInfo.InvariantCulture)}", color, !perFight);
                badge.style.marginLeft = new StyleLength(4f);
                badge.style.marginBottom = new StyleLength(0f);
                if (perFight) badge.style.opacity = new StyleFloat(0.6f);
                line.Add(badge);
            }
            col.Add(line);

            long progress = Long(g, "progress"), threshold = Math.Max(1, Long(g, "threshold"));
            bool full = Bool(g, "full");
            string text = $"{F(progress)} / {F(threshold)} ({Int(g, "percent")} %)" + (full ? (target == 0 ? " · choisissez une cible !" : " · pleine !") : "");
            col.Add(K.ProgressBar(progress / (float)threshold, text, full ? K.Gold : K.Green, 16f));
            if (target == 0 && !full) col.Add(K.Small("Aucune cible : cliquez l'icône pour en choisir une."));
            row.Add(col);
            body.Add(row);
        }

        // ─── Le Système ──────────────────────────────────────────────────────────────────

        private static void FillSystem(VisualElement body, JsonElement m)
        {
            if (Obj(m, "chapter", out var chapter)) HudMission(body, chapter, true);
            else body.Add(K.Small("Tous les chapitres sont terminés."));
            foreach (var track in Arr(m, "tracks")) HudMission(body, track, false);
        }

        private static void HudMission(VisualElement body, JsonElement mission, bool chapter)
        {
            var box = K.Column();
            box.style.marginTop = new StyleLength(4f);
            box.style.paddingLeft = new StyleLength(6f);
            box.style.borderLeftWidth = new StyleFloat(3f);
            bool complete = Bool(mission, "complete");
            box.style.borderLeftColor = new StyleColor(complete ? K.Green : chapter ? K.Gold : K.Blue);

            string slot = Text(mission, "slot");
            box.Add(K.Text((chapter ? "Chapitre · " : "Piste · ") + Text(mission, "source"), false, 12f, K.Muted));
            if (Bool(mission, "done"))
            {
                box.Add(K.Small("Terminée : la suite au prochain lot."));
                body.Add(box);
                return;
            }
            var title = K.Text(Text(mission, "title"), true, 13f, UnityEngine.Color.white);
            title.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            box.Add(title);

            var line = K.Row();
            line.style.alignItems = new StyleEnum<Align>(Align.Center);
            long progress = Long(mission, "progress"), need = Math.Max(1, Long(mission, "need"));
            var bar = K.ProgressBar(progress / (float)need, $"{F(progress)} / {F(need)}", complete ? K.Gold : K.Green, 14f);
            bar.style.flexGrow = new StyleFloat(1f);
            line.Add(bar);
            if (complete) line.Add(HudButton("Récupérer", K.Green, () => HudMissionAct("stasis/missions/claim", slot)));
            else if (Bool(mission, "canGo")) line.Add(HudButton("GO", K.Gold, () => HudMissionAct("stasis/missions/go", slot)));
            box.Add(line);
            body.Add(box);
        }

        /// <summary>A small pill-shaped button, lighter than the client's own on a HUD.</summary>
        private static VisualElement HudButton(string text, UnityEngine.Color color, Action click)
        {
            var b = K.Row();
            b.style.flexShrink = new StyleFloat(0f);
            b.style.marginLeft = new StyleLength(6f);
            K.Pad(b, 8f, 2f);
            b.style.backgroundColor = new StyleColor(K.Alpha(color, 0.25f));
            K.Border(b, 1f, color);
            K.Round(b, 6f);
            b.Add(K.Text(text, true, 13f, color));
            try { b.AddManipulator(new Clickable(AdminItemsUi.Do(click)).Cast<IManipulator>()); } catch { }
            return b;
        }

        private static bool _hudActing;

        /// <summary>GO or Récupérer from the HUD; what the server says (or refuses) toasted, the panels asked again.</summary>
        private static void HudMissionAct(string route, string slot)
        {
            if (_hudActing || Token.Length == 0) return;
            _hudActing = true;
            var body = new Dictionary<string, object> { ["slot"] = slot, ["token"] = Token };
            Task.Run(async () =>
            {
                var (code, text) = await AdminItemsUi.PostAsync(route, body);
                _toUnity.Enqueue(() =>
                {
                    _hudActing = false;
                    _hudFresh = true;
                    if (code != 200)
                    {
                        Toast(Refusal(code, text), "missions");
                        return;
                    }
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("missions", out var inner) && inner.ValueKind == JsonValueKind.Object)
                            _missions = inner.Clone();
                        string said = Clean(Text(root, "said"));
                        if (said.Length > 0 && route.EndsWith("claim", StringComparison.Ordinal))
                            Toast(said.Length > 160 ? said.Substring(0, 157) + "..." : said, "missions", null, "✓", K.Green);
                        if (_window != null && _tab == "missions" && _state is JsonElement s) Show(s);
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[JondoFix] HUD mission: {ex.Message}"); }
                });
            });
        }
    }
}
