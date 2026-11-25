using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using MelonLoader;

namespace JondoFix
{
    /// <summary>
    /// The Stasis «+25 % de vitesse de déplacement hors combat» (docs/stasis-gameplay-spec.md §0,
    /// §21), drawn by the client: the walk is the client's own animation, the server only
    /// confirms the path (WorldMoveHandler) and never times it, so a faster walk is accepted
    /// as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The protocol has no speed to send for a character on foot. What the client walks at is an
    /// EntitySpeed (Il2CppCore.Rendering.Entity.Physics.MovementSolvers: three floats behind
    /// obfuscated names), handed to its EntityPhysics through a void method of one EntitySpeed
    /// argument. Every such method is patched: the speed handed in is replaced by a copy with its
    /// three floats times <see cref="Factor"/>. The copies made here are remembered so that one
    /// setter calling another never scales twice. Everything is found by type and shape at
    /// runtime, not by the obfuscated names, and anything missing leaves the walk as it is.
    /// </para>
    /// <para>
    /// Out of combat only: every <see cref="PollEvery"/> seconds the server is asked whether the
    /// character is in a fight and what bonus applies (/api/stasis/speed, the session's own
    /// token). No answer, a fight, or no token: no scaling. It scales whatever walks on the
    /// client while the player is out of combat -- the other players (who have it too) and the
    /// roaming monsters and NPCs alike, which is only drawing.
    /// </para>
    /// <para>MelonPreferences «JondoFix_Movement / FasterWalk» (default on) switches it off.</para>
    /// </remarks>
    public static class MovementSpeed
    {
        private const float PollEvery = 2f;

        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonLogger.Instance _log;
        private static Type _speedType;
        private static ConstructorInfo _copy;
        private static List<PropertyInfo> _floats = new List<PropertyInfo>();
        private static readonly HashSet<IntPtr> _ours = new HashSet<IntPtr>();
        private static volatile float _factor = 1f;
        private static float _lastPoll = -100f;
        private static volatile bool _polling;
        private static bool _warned;

        /// <summary>What a speed is multiplied by now: 1 in a fight or without an answer.</summary>
        public static float Factor => _factor;

        private static string Token
            => (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();

        private static bool _installed;

        /// <summary>Once, from the first <see cref="Tick"/>: finds the speed and patches its setters. Never throws.</summary>
        private static void Install(MelonLogger.Instance log)
        {
            _log = log;
            try
            {
                var cat = MelonPreferences.CreateCategory("JondoFix_Movement", "JondoFix movement");
                _enabled = cat.CreateEntry("FasterWalk", true,
                    description: "Stasis: walk faster out of combat by the server's bonus (+25 %).");
                if (!_enabled.Value)
                {
                    log.Msg("[Movement] FasterWalk is off, nothing patched.");
                    return;
                }

                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } }).ToList();
                _speedType = types.FirstOrDefault(t => t.Name == "EntitySpeed" && (t.Namespace ?? "").EndsWith("MovementSolvers", StringComparison.Ordinal));
                var physics = types.FirstOrDefault(t => t.Name == "EntityPhysics" && (t.Namespace ?? "").EndsWith("Physics", StringComparison.Ordinal));
                if (_speedType == null || physics == null)
                {
                    log.Warning("[Movement] EntitySpeed or EntityPhysics not found: the walk keeps its speed.");
                    return;
                }
                _copy = _speedType.GetConstructor(new[] { _speedType });
                _floats = _speedType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(float) && p.CanRead && p.CanWrite).ToList();
                if (_copy == null || _floats.Count == 0)
                {
                    log.Warning("[Movement] EntitySpeed has no copy constructor or no speed floats: the walk keeps its speed.");
                    return;
                }

                var setters = physics.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 1
                                && m.GetParameters()[0].ParameterType == _speedType).ToList();
                if (setters.Count == 0)
                {
                    log.Warning("[Movement] EntityPhysics takes no EntitySpeed: the walk keeps its speed.");
                    return;
                }
                var harmony = new HarmonyLib.Harmony("com.jondo.fix.movement");
                var prefix = new HarmonyMethod(typeof(MovementSpeed).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));
                foreach (var setter in setters) harmony.Patch(setter, prefix: prefix);
                log.Msg($"[Movement] Out-of-combat walk speed hooked on EntityPhysics ({setters.Count} setter(s), {_floats.Count} speed float(s)).");
            }
            catch (Exception ex)
            {
                log.Warning($"[Movement] Not applied: {ex.Message}");
            }
        }

        /// <summary>From OnUpdate (its one line in Class1.cs): installs once, then asks the server now and then whether the bonus applies.</summary>
        public static void Tick(MelonLogger.Instance log)
        {
            if (!_installed) { _installed = true; Install(log); }
            if (_enabled == null || !_enabled.Value || _speedType == null || _polling) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now - _lastPoll < PollEvery) return;
            _lastPoll = now;
            string token = Token;
            if (token.Length == 0) { _factor = 1f; return; }
            _polling = true;
            Task.Run(async () =>
            {
                try
                {
                    var (code, text) = await AdminItemsUi.PostAsync("stasis/speed", new Dictionary<string, object> { ["token"] = token });
                    if (code != 200) { _factor = 1f; return; }
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    bool fight = root.TryGetProperty("fight", out var f) && f.ValueKind == JsonValueKind.True;
                    int percent = root.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                    float factor = fight ? 1f : 1f + Math.Clamp(percent, 0, 100) / 100f;
                    // Logged when it changes, so the bonus can be confirmed in MelonLoader's log.
                    if (factor != _factor) _log?.Msg($"[Movement] Walk factor x{factor:0.00} ({(fight ? "in a fight" : $"+{percent} % out of combat")}).");
                    _factor = factor;
                }
                catch
                {
                    _factor = 1f;
                }
                finally
                {
                    _polling = false;
                }
            });
        }

        /// <summary>The speed handed in, replaced by a scaled copy out of combat.</summary>
        private static void Prefix(object[] __args)
        {
            try
            {
                float factor = _factor;
                if (factor == 1f || __args == null || __args.Length != 1 || __args[0] == null) return;
                if (__args[0] is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase given && _ours.Contains(given.Pointer)) return;
                object copy = _copy.Invoke(new[] { __args[0] });
                foreach (var p in _floats) p.SetValue(copy, (float)p.GetValue(copy) * factor);
                if (copy is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase made)
                {
                    if (_ours.Count > 4096) _ours.Clear();
                    _ours.Add(made.Pointer);
                }
                __args[0] = copy;
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; _log?.Warning($"[Movement] Scaling failed, walk left as is: {ex.Message}"); }
            }
        }
    }
}
