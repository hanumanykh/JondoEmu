using System;
using System.Collections.Generic;
using System.Text.Json;
using MelonLoader;
using UnityEngine.UIElements;
using K = JondoFix.UiKit;

namespace JondoFix
{
    /// <summary>
    /// The always-visible «Stasis» button at the right edge of the screen, which opens and closes
    /// the F9 window, and the toasts when something wants the player's attention: a full gauge, a
    /// mission ready to claim, a Fissure wave won or a Dofus owed, an event or a tide under way.
    /// </summary>
    /// <remarks>
    /// The alerts are the server's (/api/stasis/panel, tab «alertes»: CommandHandler.PanelViews.cs),
    /// asked every <see cref="AlertEvery"/> seconds while the window is closed; each alert has a
    /// key, and a toast is shown the first time a key is seen. A click on a toast opens the tab it
    /// is about. The button shows only once the server answered, so not on the login screens.
    /// </remarks>
    public static partial class ProgressionUi
    {
        private const float AlertEvery = 12f, ToastFor = 7f, HudCheckEvery = 2f;

        private static VisualElement _hud, _hudCount, _toastBox;
        private static float _lastHudCheck = -100f, _lastAlerts = -100f;
        private static bool _alertsAsked, _inWorld;
        private static int _alertCount;
        private static readonly HashSet<string> _seenAlerts = new HashSet<string>();
        private static readonly List<(VisualElement Toast, float Until)> _toasts = new List<(VisualElement, float)>();

        /// <summary>From the mod's OnUpdate: the button kept on screen, the alerts asked, the old toasts taken away.</summary>
        public static void HudTick()
        {
            if (Token.Length == 0) return;
            float now = UnityEngine.Time.unscaledTime;

            if (!_alertsAsked && _window == null && now - _lastAlerts >= AlertEvery)
            {
                _lastAlerts = now;
                _alertsAsked = true;
                Ask("stasis/panel", new Dictionary<string, object> { ["tab"] = "alertes", ["structured"] = "1" }, OnAlerts,
                    quiet: true, failed: () => { _alertsAsked = false; _inWorld = false; });
            }

            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (_toasts[i].Until > now) continue;
                try { _toasts[i].Toast.RemoveFromHierarchy(); } catch { }
                _toasts.RemoveAt(i);
            }

            if (now - _lastHudCheck < HudCheckEvery) return;
            _lastHudCheck = now;
            try
            {
                if (!_inWorld)
                {
                    _hud?.RemoveFromHierarchy();
                    return;
                }
                if (_hud == null || _hud.panel == null)
                {
                    var layer = AdminItemsUi.FindLayer();
                    if (layer == null) return;
                    if (_hud == null) _hud = BuildHud();
                    layer.Add(_hud);
                }
                _hud.BringToFront();
                if (_toastBox != null && _toastBox.panel != null) _toastBox.BringToFront();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Stasis HUD: {ex.Message}");
                _hud = null;
            }
        }

        private static VisualElement BuildHud()
        {
            var hud = K.Row();
            hud.name = "JondoStasisHud";
            hud.style.position = new StyleEnum<Position>(Position.Absolute);
            hud.style.right = new StyleLength(14f);
            hud.style.top = new StyleLength(240f);
            K.Pad(hud, 10f, 6f);
            hud.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.08f, 0.07f, 0.05f, 0.92f));
            K.Border(hud, 2f, K.Gold);
            K.Round(hud, 10f);
            var icon = K.ItemIcon(K.JondoCoin, 26f);
            icon.style.marginRight = new StyleLength(6f);
            hud.Add(icon);
            var label = K.Text("Stasis", true, 16f, K.Gold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            hud.Add(label);
            _hudCount = K.Row();
            hud.Add(_hudCount);
            ShowHudCount();
            try { hud.AddManipulator(new Clickable(AdminItemsUi.Do(() => { if (_window != null) Close(); else Open(); })).Cast<IManipulator>()); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Stasis HUD: the button takes no click: {ex.Message}"); }
            AdminItemsUi.KeepEvents(hud);
            return hud;
        }

        private static void ShowHudCount()
        {
            if (_hudCount == null) return;
            _hudCount.Clear();
            if (_alertCount <= 0) return;
            var badge = K.Badge(_alertCount.ToString(System.Globalization.CultureInfo.InvariantCulture), K.Red);
            badge.style.marginLeft = new StyleLength(6f);
            badge.style.marginRight = new StyleLength(0f);
            badge.style.marginBottom = new StyleLength(0f);
            _hudCount.Add(badge);
        }

        private static void OnAlerts(JsonElement root)
        {
            _alertsAsked = false;
            _inWorld = true;
            NoteCoins(root);
            if (!Obj(root, "view", out var view)) return;
            NoteCoins(view);
            var alerts = Arr(view, "alerts");
            var now = new HashSet<string>();
            foreach (var alert in alerts)
            {
                string key = Text(alert, "key");
                if (key.Length == 0) continue;
                now.Add(key);
                if (_seenAlerts.Add(key)) Toast(Text(alert, "text"), Text(alert, "tab"));
            }
            // A key gone (claimed, chosen, over) may come back later and be toasted again.
            _seenAlerts.RemoveWhere(k => !now.Contains(k));
            _alertCount = alerts.Count;
            ShowHudCount();
        }

        /// <summary>A toast at the right edge, under the button; a click opens the tab it is about.</summary>
        private static void Toast(string text, string tab)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var layer = AdminItemsUi.FindLayer();
                if (layer == null) return;
                if (_toastBox == null || _toastBox.panel == null)
                {
                    _toastBox = K.Column();
                    _toastBox.name = "JondoStasisToasts";
                    _toastBox.style.position = new StyleEnum<Position>(Position.Absolute);
                    _toastBox.style.right = new StyleLength(14f);
                    _toastBox.style.top = new StyleLength(290f);
                    _toastBox.style.width = new StyleLength(320f);
                    _toastBox.pickingMode = PickingMode.Ignore;
                    layer.Add(_toastBox);
                }
                while (_toasts.Count >= 4)
                {
                    try { _toasts[0].Toast.RemoveFromHierarchy(); } catch { }
                    _toasts.RemoveAt(0);
                }
                var toast = K.Row();
                toast.style.alignItems = new StyleEnum<Align>(Align.Center);
                toast.style.marginBottom = new StyleLength(8f);
                K.Pad(toast, 12f, 10f);
                toast.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.10f, 0.08f, 0.04f, 0.95f));
                K.Border(toast, 1f, K.Gold);
                toast.style.borderLeftWidth = new StyleFloat(4f);
                K.Round(toast, 6f);
                toast.Add(K.Glyph("!", K.Gold, 30f));
                var body = K.Fill();
                body.style.marginLeft = new StyleLength(10f);
                body.Add(K.Text(text, true, 15f, UnityEngine.Color.white));
                body.Add(K.Small("Cliquez pour ouvrir"));
                toast.Add(body);
                string target = string.IsNullOrEmpty(tab) ? "dons" : tab;
                try
                {
                    toast.AddManipulator(new Clickable(AdminItemsUi.Do(() =>
                    {
                        try { toast.RemoveFromHierarchy(); } catch { }
                        OpenOn(target);
                    })).Cast<IManipulator>());
                }
                catch { }
                AdminItemsUi.KeepEvents(toast);
                _toastBox.Add(toast);
                _toastBox.BringToFront();
                _toasts.Add((toast, UnityEngine.Time.unscaledTime + ToastFor));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Stasis toast: {ex.Message}");
            }
        }

        /// <summary>The window opened (if it was not) on a tab, its data asked.</summary>
        private static void OpenOn(string tab)
        {
            _tab = tab;
            if (_window == null) Open();
            ShowTab(tab);
        }
    }
}
