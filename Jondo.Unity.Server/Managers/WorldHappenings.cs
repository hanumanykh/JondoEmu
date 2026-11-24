using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The world's own little happenings, for a server where the only real player may be alone:
    /// a roaming boss that turns up somewhere and is cried across the server, and, every hour, a
    /// word about a zone where the fights pay more (content/world/events.json).
    /// </summary>
    /// <remarks>
    /// ROAMING BOSSES. Every <see cref="Settings.BossMinMinutes"/> to <see cref="Settings.BossMaxMinutes"/>
    /// minutes one boss of the authored list is planted, with its escort, on a random outdoor map of
    /// the world map whose subarea's level is within <see cref="Settings.LevelSpread"/> of the boss's,
    /// on a cell of the map's main floor (<see cref="MobSpawnManager.GetInnerWalkableCells"/> crossed
    /// with <see cref="MapManager.MainFloor"/>), through <see cref="MobSpawnManager.SpawnComposed"/>:
    /// it takes one of the negative identifiers the quest groups take, so that it neither clashes with
    /// the world's groups nor survives a respawn. Only one boss walks at a time.
    ///
    /// Its end is read off the map rather than hooked into the fight: the only place a group leaves
    /// its map after a fight is FightHandler's ending, and only when the fight was WON
    /// (MobSpawnManager.RemoveMobGroup). So a boss whose group has vanished before its time is up was
    /// beaten, and is cried as such; one still standing at the end of
    /// <see cref="Settings.BossLifetimeMinutes"/> is taken off with <see cref="MobSpawnManager.RemoveMobGroup"/>
    /// -- not RemoveByHand, which would leave the map marked as emptied by an administrator, without
    /// its own groups until the server stops.
    ///
    /// BONUS ZONES. content/anomalies/bonus_zones.json is not a bonus mechanism: it is only the list
    /// of the subareas the world map's anomalies panel shows (<see cref="AnomalyZones"/>), every one
    /// of them with a bonus of 0. The bonus that is really paid -- +<see cref="Handlers.AnomalyHandler.AnomalyZoneBonus"/>%
    /// experience and loot -- belongs to the subareas of the open anomalies (<see cref="Anomalies"/>,
    /// <see cref="Handlers.AnomalyHandler.ZoneBonusAt"/>), and those are all open all the time, each on
    /// its own countdown. Making the bonus itself rotate would mean rewriting how anomalies open and
    /// close, which the panel, the zaaps and the fights all read. So the rotation here is only of the
    /// zone put forward: every <see cref="Settings.ZoneEveryMinutes"/> minutes one anomaly zone, taken
    /// in turn from a shuffled list, is announced with its true bonus.
    ///
    /// Nothing is announced, and no boss is planted, while no real player is in the world: the
    /// simulated players (<see cref="Bots.SimulatedPlayers.IsSimulated"/>) do not count.
    /// </remarks>
    public static class WorldHappenings
    {
        public const string AuthoredFile = "world/events.json";

        /// <summary>One boss of the list: who leads, at which grade, who comes with it, what it is called.</summary>
        public sealed record Boss(int Monster, int Grade, int Level, string Name,
                                  IReadOnlyList<(int Monster, int Grade)> Escort,
                                  string? Sighted, string? Beaten, string? Gone);

        /// <summary>The whole file, read.</summary>
        public sealed record Settings(
            bool Enabled,
            bool BossesEnabled, int BossMinMinutes, int BossMaxMinutes, int BossLifetimeMinutes,
            int LevelSpread, int FirstBossMinutes,
            string SightedText, string BeatenText, string GoneText,
            IReadOnlyList<Boss> Bosses,
            bool ZonesEnabled, int ZoneEveryMinutes, string ZoneText)
        {
            /// <summary>What is used when the file is missing or unreadable: nothing happens.</summary>
            public static Settings Off { get; } = new(false, false, 45, 90, 30, 15, 5,
                "", "", "", Array.Empty<Boss>(), false, 60, "");
        }

        /// <summary>The boss walking now, if any.</summary>
        private sealed record Walking(Boss Boss, long MapId, long GroupId, string Where, DateTime Until);

        private static Settings _settings = Settings.Off;
        private static Timer? _timer;
        private static int _ticking;
        private static Walking? _walking;
        private static DateTime _nextBoss, _nextZone;
        private static List<int> _zoneTurn = new();
        private static bool _zonesAnnouncedOnce;

        /// <summary>
        /// Reads events.json. Absent or malformed keys take their defaults; a boss without a monster
        /// or a name is dropped, since it could be neither planted nor cried.
        /// </summary>
        public static Settings Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var bosses = Obj(root, "bosses");
            var zones = Obj(root, "zones");

            var list = new List<Boss>();
            if (bosses is { } b && b.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    int monster = Int(e, "monster", 0);
                    string name = Str(e, "name", "");
                    if (monster <= 0 || name.Length == 0) continue;
                    var escort = new List<(int, int)>();
                    if (e.TryGetProperty("escort", out var esc) && esc.ValueKind == JsonValueKind.Array)
                        foreach (var m in esc.EnumerateArray())
                        {
                            int id = Int(m, "monster", 0);
                            if (id > 0) escort.Add((id, Int(m, "grade", 0)));
                        }
                    list.Add(new Boss(monster, Int(e, "grade", 0), Int(e, "level", 1), name, escort,
                        StrOrNull(e, "sighted"), StrOrNull(e, "beaten"), StrOrNull(e, "gone")));
                }
            }

            int min = Math.Max(1, Int(bosses, "minMinutes", 45));
            int max = Math.Max(min, Int(bosses, "maxMinutes", 90));
            return new Settings(
                Bool(root, "enabled", true),
                Bool(bosses, "enabled", true), min, max,
                Math.Max(1, Int(bosses, "lifetimeMinutes", 30)),
                Math.Max(0, Int(bosses, "levelSpread", 15)),
                Math.Max(0, Int(bosses, "firstAfterMinutes", 5)),
                Str(bosses, "sighted", "Un {name} a été aperçu près de {zone} !"),
                Str(bosses, "beaten", "Le {name} a été vaincu !"),
                Str(bosses, "gone", "Le {name} a quitté les environs de {zone}."),
                list,
                Bool(zones, "enabled", true),
                Math.Max(1, Int(zones, "everyMinutes", 60)),
                Str(zones, "text", "Zone à l'honneur : {zone} (+{bonus}% d'expérience et de butin) !"));
        }

        /// <summary>A template filled: {name}, {zone}, {bonus}.</summary>
        public static string Fill(string template, string name, string zone, int bonus = 0)
            => template.Replace("{name}", name).Replace("{zone}", zone).Replace("{bonus}", bonus.ToString());

        /// <summary>Reads the file and starts the half-minute clock. Called once at startup.</summary>
        public static void Start()
        {
            try { _settings = Load(Paths.ContentFile(AuthoredFile)); }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorldHappenings] Could not read {AuthoredFile}: {ex.Message}");
                return;
            }
            if (!_settings.Enabled) return;

            var now = DateTime.UtcNow;
            _nextBoss = now.AddMinutes(_settings.FirstBossMinutes);
            _nextZone = now;
            Console.WriteLine($"[WorldHappenings] {_settings.Bosses.Count} roaming boss(es), zones {(_settings.ZonesEnabled ? "on" : "off")}.");
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
                try { TickAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"[WorldHappenings] The clock failed: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _ticking, 0); }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static bool RealPlayerOnline()
            => SessionRegistry.InWorld().Any(s => !Bots.SimulatedPlayers.IsSimulated(s.AccountId));

        private static async Task TickAsync()
        {
            var now = DateTime.UtcNow;

            // The boss walking: beaten (its group gone before its time) or gone (its time up).
            if (_walking is { } w)
            {
                if (MobSpawnManager.GetMobGroupById(w.GroupId) == null)
                {
                    _walking = null;
                    await AnnounceAsync(Fill(w.Boss.Beaten ?? _settings.BeatenText, w.Boss.Name, w.Where));
                }
                else if (now >= w.Until)
                {
                    MobSpawnManager.RemoveMobGroup(w.MapId, w.GroupId);
                    _walking = null;
                    await AnnounceAsync(Fill(w.Boss.Gone ?? _settings.GoneText, w.Boss.Name, w.Where));
                }
            }

            bool someone = RealPlayerOnline();

            if (_settings.BossesEnabled && _walking == null && now >= _nextBoss && _settings.Bosses.Count > 0)
            {
                _nextBoss = now.AddMinutes(Random.Shared.Next(_settings.BossMinMinutes, _settings.BossMaxMinutes + 1));
                if (someone) await SpawnBossAsync(now);
            }

            if (_settings.ZonesEnabled && now >= _nextZone)
            {
                // Held, not skipped, until someone is there to read it: the first real player in
                // hears the zone of the hour on his first tick.
                if (someone)
                {
                    _nextZone = now.AddMinutes(_settings.ZoneEveryMinutes);
                    await AnnounceZoneAsync();
                }
            }
        }

        private static async Task SpawnBossAsync(DateTime now)
        {
            var boss = _settings.Bosses[Random.Shared.Next(_settings.Bosses.Count)];
            for (int attempt = 0; attempt < 20; attempt++)
            {
                long mapId = ChooseMap(boss.Level, _settings.LevelSpread);
                if (mapId == 0) break;

                var cells = MobSpawnManager.GetInnerWalkableCells(mapId);
                var floor = MapManager.MainFloor(mapId);
                var free = cells.Where(c => floor.Contains(c) && MobSpawnManager.GetMobAtCell(mapId, c) == null).ToList();
                if (free.Count == 0) continue;

                var members = new List<(int, int)> { (boss.Monster, boss.Grade) };
                members.AddRange(boss.Escort);
                var group = MobSpawnManager.SpawnComposed(mapId, members, free[Random.Shared.Next(free.Count)]);
                if (group == null) continue;

                var info = MapManager.GetMapInfo(mapId);
                string zone = DatabaseManager.GetSubAreaName(info?.SubAreaId ?? 0);
                string where = info == null ? zone : $"{zone} [{info.PosX},{info.PosY}]";
                _walking = new Walking(boss, mapId, group.MobId, where, now.AddMinutes(_settings.BossLifetimeMinutes));
                Console.WriteLine($"[WorldHappenings] {boss.Name} (#{boss.Monster}) planted on map {mapId}, group {group.MobId}.");
                await AnnounceAsync(Fill(boss.Sighted ?? _settings.SightedText, boss.Name, where));
                return;
            }
            Console.WriteLine($"[WorldHappenings] No map found for {boss.Name} (level {boss.Level}).");
        }

        /// <summary>
        /// A random outdoor map on the world map, not vetoed for monsters, whose subarea's level is
        /// within the spread of the boss's. 0 when there is none.
        /// </summary>
        private static long ChooseMap(int level, int spread)
        {
            var candidates = MapManager.Maps.Values
                .Where(m => m.Outdoor && !MobSpawnManager.IsVetoed(m.MapId)
                            && Math.Abs(Interactives.LevelOfSubArea(m.SubAreaId) - level) <= spread
                            && Interactives.LevelOfSubArea(m.SubAreaId) > 0)
                .Select(m => m.MapId)
                .ToList();
            for (int i = 0; i < 30 && candidates.Count > 0; i++)
            {
                long pick = candidates[Random.Shared.Next(candidates.Count)];
                if (MapManager.OnTheWorldMap(pick)) return pick;
            }
            return 0;
        }

        private static async Task AnnounceZoneAsync()
        {
            if (_zoneTurn.Count == 0)
            {
                _zoneTurn = Anomalies.All.Select(a => a.SubAreaId).Distinct()
                    .OrderBy(_ => Random.Shared.Next()).ToList();
                if (_zoneTurn.Count == 0)
                {
                    if (!_zonesAnnouncedOnce) Console.WriteLine("[WorldHappenings] No anomaly zone to put forward.");
                    _zonesAnnouncedOnce = true;
                    return;
                }
            }
            int zone = _zoneTurn[0];
            _zoneTurn.RemoveAt(0);
            _zonesAnnouncedOnce = true;
            string name = DatabaseManager.GetSubAreaName(zone);
            if (name.Length == 0) name = $"#{zone}";
            await AnnounceAsync(Fill(_settings.ZoneText, "", name, Handlers.AnomalyHandler.AnomalyZoneBonus));
        }

        /// <summary>
        /// A line to every real player in the world, the same notice the administrators' .announce
        /// sends (CommandHandler.AnnounceToAllAsync), but with the text already written: it is French
        /// in the file, not a key of CommandTexts.
        /// </summary>
        private static async Task AnnounceAsync(string text)
        {
            Console.WriteLine($"[WorldHappenings] {text}");
            foreach (var s in SessionRegistry.InWorld())
            {
                if (Bots.SimulatedPlayers.IsSimulated(s.AccountId)) continue;
                try
                {
                    await s.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WorldHappenings] An announcement did not reach {s.State.CharacterName}: {ex.Message}");
                }
            }
        }

        private static JsonElement? Obj(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        private static int Int(JsonElement? e, string key, int fallback)
            => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

        private static bool Bool(JsonElement? e, string key, bool fallback)
            => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
               && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

        private static string Str(JsonElement? e, string key, string fallback)
            => StrOrNull(e, key) ?? fallback;

        private static string? StrOrNull(JsonElement? e, string key)
            => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
