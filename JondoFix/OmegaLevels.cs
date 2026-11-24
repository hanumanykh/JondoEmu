using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLoader;

namespace JondoFix
{
    /// <summary>
    /// "Levels up to 2000" (client side). The client's XP table ends at 1889; beyond
    /// it the server uses a flat XP step (last step of datos/character_xp.json, kept
    /// constant). Two patches, each behind a MelonPreferences toggle:
    ///  1. XpBeyondTable (default OFF; level cap is now 300, never active below the table end 1889): prefix on the XP gauge's SetXp(long, long, long,
    ///     uint, icon). When the XP is past the table end and the floors received are
    ///     inconsistent (next &lt;= floor, or XP outside [floor, next)), they are
    ///     recomputed with the flat step, up to level 2000.
    ///  2. ShowPlainLevel (default ON): postfix on SetOmega(bool, uint) / SetXp of the
    ///     same gauge. Hides the Omega badge and rewrites "200" in the gauge's level
    ///     label as 200 + Omega (e.g. 1500). Only the XP gauge widget is affected.
    /// The gauge type is located by member signatures at runtime, not by class name.
    /// </summary>
    internal static class OmegaLevels
    {
        public const int TargetMaxLevel = 2000;

        // Fallback values (datos/character_xp.json of the emulator, levels 1888/1889).
        private static int _tableMaxLevel = 1889;
        private static long _tableMaxXp = 99487914159522688L;
        private static long _flatStep = 99487914159522688L - 98502885311957344L;

        private static MelonPreferences_Entry<bool> _xpBeyondTable;
        private static MelonPreferences_Entry<bool> _showPlainLevel;
        private static MelonLogger.Instance _log;

        private static readonly Dictionary<IntPtr, uint> LastOmega = new();
        private static PropertyInfo _bindingProp;
        private static bool _bindingSearched;

        public static void Init(MelonLogger.Instance log, string xpJsonPath)
        {
            _log = log;
            try
            {
                var cat = MelonPreferences.CreateCategory("JondoFix_Levels", "JondoFix levels > 200");
                _xpBeyondTable = cat.CreateEntry("XpBeyondTable", false,
                    description: "Recompute XP gauge floors past the client's XP table (flat step, up to 2000).");
                _showPlainLevel = cat.CreateEntry("ShowPlainLevel", true,
                    description: "XP gauge shows the plain level (e.g. 1500) instead of 200 + Omega.");
            }
            catch (Exception ex)
            {
                log.Warning($"[OmegaLevels] Preferences unavailable, disabled: {ex.Message}");
                return;
            }

            if (!_xpBeyondTable.Value && !_showPlainLevel.Value)
            {
                log.Msg("[OmegaLevels] Both toggles OFF, nothing patched.");
                return;
            }

            LoadXpTable(xpJsonPath);

            try
            {
                var (setXp, setOmega) = FindGauge();
                if (setXp == null)
                {
                    log.Warning("[OmegaLevels] No type with SetXp(Int64,Int64,Int64,UInt32,*) + " +
                                "SetOmega(Boolean,UInt32) found. Levels > 200 patch NOT applied.");
                    return;
                }
                log.Msg($"[OmegaLevels] XP gauge found: {setXp.DeclaringType.FullName} " +
                        $"(SetXp, SetOmega={(setOmega != null)}).");

                var harmony = new HarmonyLib.Harmony("jondo.omegalevels");
                harmony.Patch(setXp,
                    prefix: new HarmonyMethod(typeof(OmegaLevels).GetMethod(nameof(SetXpPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(OmegaLevels).GetMethod(nameof(SetXpPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                if (setOmega != null)
                {
                    harmony.Patch(setOmega,
                        postfix: new HarmonyMethod(typeof(OmegaLevels).GetMethod(nameof(SetOmegaPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                log.Msg($"[OmegaLevels] Patched. XpBeyondTable={_xpBeyondTable.Value}, " +
                        $"ShowPlainLevel={_showPlainLevel.Value}, table end {_tableMaxLevel} " +
                        $"({_tableMaxXp} XP), flat step {_flatStep}.");
            }
            catch (Exception ex)
            {
                log.Warning($"[OmegaLevels] Patch failed, nothing applied: {ex.Message}");
            }
        }

        private static void LoadXpTable(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _log.Msg("[OmegaLevels] character_xp.json not found, using built-in values.");
                    return;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var levels = new SortedDictionary<int, long>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (int.TryParse(p.Name, out int lvl) && p.Value.TryGetInt64(out long xp))
                        levels[lvl] = xp;
                }
                if (levels.Count < 2) return;
                var last = levels.Last();
                var prev = levels.Reverse().Skip(1).First();
                long step = last.Value - prev.Value;
                if (step <= 0) return;
                _tableMaxLevel = last.Key;
                _tableMaxXp = last.Value;
                _flatStep = step;
            }
            catch (Exception ex)
            {
                _log.Warning($"[OmegaLevels] Could not read {path}: {ex.Message}; built-in values kept.");
            }
        }

        private static (MethodInfo setXp, MethodInfo setOmega) FindGauge()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name ?? "";
                if (!name.StartsWith("Il2CppCore", StringComparison.Ordinal)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    MethodInfo setXp = null, setOmega = null;
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var m in methods)
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "SetXp" && ps.Length == 5 && ps[0].ParameterType == typeof(long) &&
                            ps[1].ParameterType == typeof(long) && ps[2].ParameterType == typeof(long) &&
                            ps[3].ParameterType == typeof(uint))
                            setXp = m;
                        else if (m.Name == "SetOmega" && ps.Length == 2 && ps[0].ParameterType == typeof(bool) &&
                                 ps[1].ParameterType == typeof(uint))
                            setOmega = m;
                    }
                    if (setXp != null && setOmega != null) return (setXp, setOmega);
                }
            }
            return (null, null);
        }

        private static long FloorOf(long level) => _tableMaxXp + (level - _tableMaxLevel) * _flatStep;

        // SetXp(current, floor, nextFloor, level, icon)
        private static void SetXpPrefix(ref long __0, ref long __1, ref long __2)
        {
            try
            {
                if (_xpBeyondTable == null || !_xpBeyondTable.Value) return;
                long xp = __0;
                if (xp < _tableMaxXp || _flatStep <= 0) return;
                bool consistent = __2 > __1 && xp >= __1 && xp < __2;
                if (consistent) return;

                long level = _tableMaxLevel + (xp - _tableMaxXp) / _flatStep;
                if (level >= TargetMaxLevel)
                {
                    // Max level: full gauge.
                    __1 = FloorOf(TargetMaxLevel);
                    __2 = Math.Max(__1 + 1, xp);
                    return;
                }
                __1 = FloorOf(level);
                __2 = FloorOf(level + 1);
            }
            catch { }
        }

        private static void SetXpPostfix(object __instance) => ApplyPlainLevel(__instance, null);

        private static void SetOmegaPostfix(object __instance, bool __0, uint __1) =>
            ApplyPlainLevel(__instance, __0 ? __1 : 0u);

        private static void ApplyPlainLevel(object instance, uint? omega)
        {
            try
            {
                if (_showPlainLevel == null || !_showPlainLevel.Value || instance == null) return;
                if (instance is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj) return;
                IntPtr key = obj.Pointer;
                if (omega.HasValue) LastOmega[key] = omega.Value;
                if (!LastOmega.TryGetValue(key, out uint om) || om == 0) return;

                object binding = GetBinding(instance);
                if (binding == null) return;

                if (GetMember(binding, "omega") is UnityEngine.UIElements.VisualElement omegaVe)
                    omegaVe.style.display = new UnityEngine.UIElements.StyleEnum<UnityEngine.UIElements.DisplayStyle>(
                        UnityEngine.UIElements.DisplayStyle.None);

                if (GetMember(binding, "lbl_title") is UnityEngine.UIElements.TextElement title)
                {
                    string text = title.text ?? "";
                    string plain = (200 + om).ToString();
                    int idx = text.LastIndexOf("200", StringComparison.Ordinal);
                    if (idx >= 0 && !text.Contains(plain))
                        title.text = text.Substring(0, idx) + plain + text.Substring(idx + 3);
                }
            }
            catch { }
        }

        private static object GetBinding(object instance)
        {
            if (!_bindingSearched)
            {
                _bindingSearched = true;
                for (var t = instance.GetType(); t != null && _bindingProp == null; t = t.BaseType)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var pt = p.PropertyType;
                        if (pt.GetProperty("lbl_title") != null && pt.GetProperty("omega") != null &&
                            pt.GetProperty("lbl_omegaValue") != null)
                        {
                            _bindingProp = p;
                            break;
                        }
                    }
                }
                _log?.Msg(_bindingProp != null
                    ? $"[OmegaLevels] Gauge binding found: {_bindingProp.PropertyType.FullName} via {_bindingProp.Name}."
                    : "[OmegaLevels] Gauge binding (lbl_title/omega/lbl_omegaValue) not found; ShowPlainLevel inactive.");
            }
            return _bindingProp?.GetValue(instance);
        }

        private static object GetMember(object binding, string name) =>
            binding.GetType().GetProperty(name)?.GetValue(binding);
    }
}
