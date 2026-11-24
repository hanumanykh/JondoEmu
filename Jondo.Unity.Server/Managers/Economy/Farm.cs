using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The Stasis-like farming loop's settings, content/economy/farm.json: the zone gauge, the zone
    /// counter's buffs, the tides and the archmonsters' bonuses. Every number the spec leaves
    /// unpublished (docs/stasis-gameplay-spec.md, "Ce qui manque") is one of these, so the owner
    /// tunes the pace with a line of JSON and not a build.
    /// </summary>
    public static class Farm
    {
        public const string AuthoredFile = "economy/farm.json";

        internal sealed class GaugeSettings
        {
            public bool Enabled { get; set; } = true;
            /// <summary>A zone's base threshold: Base + PerLevel x the zone's level (40 + 0.6 L: 52 at 20, 100 at 100, 160 at 200).</summary>
            public double Base { get; set; } = 40;
            public double PerLevel { get; set; } = 0.6;
            /// <summary>The threshold after 0, 1, 2... guaranteed items in the window: x1, x1.5, then x2 for good.</summary>
            public List<double> Steps { get; set; } = new() { 1.0, 1.5, 2.0 };
            /// <summary>How long a window lasts before the thresholds fall back to the base.</summary>
            public int ResetHours { get; set; } = 72;
            public double ArchmonsterFactor { get; set; } = 2;
            public double TideFactor { get; set; } = 2;
            public double BuffFactor { get; set; } = 2;
        }

        internal sealed class CounterSettings
        {
            public bool Enabled { get; set; } = true;
            public int DurationMinutes { get; set; } = 30;
            /// <summary>Densité's price: PerLevel x the zone's level^Exponent, never under Minimum (28 000 at 200, 3 500 at 50).</summary>
            public double PerLevel { get; set; } = 10;
            public double Exponent { get; set; } = 1.5;
            public long Minimum { get; set; } = 500;
            public double StudyFactor { get; set; } = 0.75;
            public double GaugeFactor { get; set; } = 2;
            public int DensityDropPercent { get; set; } = 100;
            public int StudyExperiencePercent { get; set; } = 50;
        }

        internal sealed class TideBand
        {
            public int Min { get; set; }
            public int Max { get; set; }
        }

        internal sealed class TideSettings
        {
            public bool Enabled { get; set; } = true;
            public int OnMinutes { get; set; } = 50;
            public int OffMinutes { get; set; } = 10;
            /// <summary>Added to the server's experience rate: 200 % + 250 % is the spec's 300 % → 550 %.</summary>
            public int ExperiencePercent { get; set; } = 250;
            /// <summary>Levels above a band that still take its experience («tolérance +20»).</summary>
            public int LevelTolerance { get; set; } = 20;
            /// <summary>The whole world under the tide, not one subarea a band: for a small server.</summary>
            public bool Global { get; set; }
            public int MinimumRoster { get; set; } = 3;
            public List<TideBand> Bands { get; set; } = new()
            {
                new() { Min = 1, Max = 50 }, new() { Min = 51, Max = 100 }, new() { Min = 101, Max = 150 },
                new() { Min = 151, Max = 200 }, new() { Min = 201, Max = 250 }, new() { Min = 251, Max = 300 },
            };
        }

        internal sealed class ArchmonsterSettings
        {
            public int DropPercent { get; set; } = 400;
            public int RespawnMinMinutes { get; set; } = 60;
            public int RespawnMaxMinutes { get; set; } = 180;
        }

        internal sealed class Settings
        {
            public GaugeSettings Gauge { get; set; } = new();
            public CounterSettings Counter { get; set; } = new();
            public TideSettings Tides { get; set; } = new();
            public ArchmonsterSettings Archmonsters { get; set; } = new();
        }

        private static Settings? _settings;

        internal static Settings Current
        {
            get => _settings ??= Read(Paths.ContentFile(AuthoredFile));
            set => _settings = value;
        }

        internal static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Orbes.JsonOptions) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Farm] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        /// <summary>The time, which the tests move instead of waiting hours.</summary>
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    }

    /// <summary>
    /// What the farming loop knows of a subarea: its level, who lives in it, and the equipment its
    /// monsters drop -- the gauge's list of guaranteed items. Read once from the world's tables and
    /// cached; a test writes its own zones with <see cref="Declare"/>.
    /// </summary>
    public static class Zones
    {
        public sealed record Zone(int SubAreaId, int Level, IReadOnlyList<int> Roster, IReadOnlyList<int> Equipment);

        private static readonly ConcurrentDictionary<int, Zone> _zones = new();
        private static Dictionary<int, List<int>>? _rosters;
        private static readonly object Gate = new();

        /// <summary>A zone written by hand, for the tests.</summary>
        internal static void Declare(Zone zone) => _zones[zone.SubAreaId] = zone;

        internal static void Forget()
        {
            _zones.Clear();
            lock (Gate) _rosters = new Dictionary<int, List<int>>();
        }

        /// <summary>Every subarea's roster, Subareas.Monsters, read the way the spawner reads it.</summary>
        internal static IReadOnlyDictionary<int, List<int>> Rosters
        {
            get
            {
                lock (Gate)
                {
                    if (_rosters != null) return _rosters;
                    try
                    {
                        using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                        connection.Open();
                        _rosters = MobSpawnManager.LoadSubareaRosters(connection);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Farm] The subareas could not be read: {ex.Message}");
                        _rosters = new Dictionary<int, List<int>>();
                    }
                    return _rosters;
                }
            }
        }

        /// <summary>The subarea a map belongs to, 0 for none.</summary>
        public static int SubAreaOf(long mapId) => MapManager.GetMapInfo(mapId)?.SubAreaId ?? 0;

        public static Zone? Of(int subAreaId)
        {
            if (subAreaId <= 0) return null;
            if (_zones.TryGetValue(subAreaId, out var known)) return known;
            if (!Rosters.TryGetValue(subAreaId, out var roster) || roster.Count == 0) return null;
            var zone = new Zone(subAreaId, LevelOf(roster), roster, EquipmentOf(roster));
            _zones[subAreaId] = zone;
            return zone;
        }

        /// <summary>A roster's level: the average of its monsters' average grade level (the five grades that roam).</summary>
        internal static int LevelOf(IEnumerable<int> roster)
        {
            var levels = new List<double>();
            foreach (int id in roster)
            {
                var data = MobSpawnManager.GetMonsterData(id);
                if (data == null || data.Grades.Count == 0) continue;
                levels.Add(data.Grades.Take(MobSpawnManager.MobMember.MaxGradesPerMonster).Average(g => g.Level));
            }
            return levels.Count == 0 ? 1 : Math.Max(1, (int)Math.Round(levels.Average()));
        }

        /// <summary>
        /// The equipment a roster drops outside any quest -- what <see cref="Stasis.Applies"/>
        /// would stasify --, the gauge's list.
        /// </summary>
        private static List<int> EquipmentOf(IEnumerable<int> roster)
        {
            var items = new SortedSet<int>();
            try
            {
                foreach (int id in roster)
                    foreach (var drop in DatabaseManager.GetMonsterDrops(id, 0))
                        if (string.IsNullOrWhiteSpace(drop.ReceiverCriterion) && Stasis.Applies(Forgemagic.TemplateOf(drop.ObjectId)))
                            items.Add(drop.ObjectId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Farm] A zone's drops could not be read: {ex.Message}");
            }
            return items.ToList();
        }
    }
}
