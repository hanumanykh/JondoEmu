using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// Where a bot lives: the hunting ground of its level, as a player goes where the monsters
    /// are worth it. They all lived in Astrub, a level-180 bot walking among the Tofus, and the
    /// rest of the world was empty.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>An area a bot may live in: its level and the maps it comes in on.</summary>
        internal sealed record Ground(int Area, int Level, List<long> Maps);

        /// <summary>The fewest maps of an area that make it a ground: a hamlet is not one.</summary>
        private const int GroundMaps = 12;

        /// <summary>
        /// The grounds of the world, worked out once: the areas with a zaap -- a bot finds its way
        /// to the marketplaces and the workshops from there -- whose outdoor maps have monsters,
        /// a way out and floor to stand on. Its level is the median of its maps' monsters.
        /// </summary>
        internal static readonly Lazy<List<Ground>> Grounds = new(() =>
        {
            var monsters = MobSpawnManager.AllMonsters.ToDictionary(m => m.Id);
            var zaapAreas = Interactives.Waypoints
                .Select(w => AreaOf(w.MapId)).Where(a => a >= 0).ToHashSet();
            var levels = new Dictionary<int, List<int>>();
            var maps = new Dictionary<int, List<long>>();
            foreach (var info in MapManager.Maps.Values)
            {
                if (!info.Outdoor || MapManager.IsArena(info.MapId)) continue;
                int area = Interactives.AreaOfSubArea(info.SubAreaId);
                if (area < 0 || !zaapAreas.Contains(area)) continue;
                var roster = MobSpawnManager.RoamingRoster(MobSpawnManager.RosterOfMap(info.MapId), monsters);
                if (roster.Count == 0) continue;
                // A monster's middle grade stands for it.
                int level = (int)roster.Select(id => monsters[id].Grades)
                                       .Where(g => g.Count > 0)
                                       .Select(g => g[g.Count / 2].Level)
                                       .DefaultIfEmpty(0).Average();
                if (level <= 0) continue;
                (levels.TryGetValue(area, out var l) ? l : levels[area] = new List<int>()).Add(level);
                (maps.TryGetValue(area, out var m) ? m : maps[area] = new List<long>()).Add(info.MapId);
            }

            var grounds = new List<Ground>();
            foreach (var (area, list) in levels)
            {
                // Only maps a bot can stand on and leave; looked at last, it is the costly part.
                var standing = maps[area]
                    .Where(id => MobSpawnManager.GetInnerWalkableCells(id).Count > 0
                                 && Exits.GetOrAdd(id, WaysOut).Count > 0)
                    .ToList();
                if (standing.Count < GroundMaps) continue;
                list.Sort();
                grounds.Add(new Ground(area, list[list.Count / 2], standing));
            }
            Console.WriteLine($"[Bots] {grounds.Count} hunting grounds: " +
                              string.Join(", ", grounds.OrderBy(g => g.Level).Select(g => $"area {g.Area} level {g.Level}")));
            return grounds;
        });

        /// <summary>
        /// The ground of a level: one of the three whose level is nearest below it -- or the
        /// lowest, for a beginner --, drawn, so that bots of one level do not all crowd one area.
        /// </summary>
        internal static Ground? GroundFor(int level, Random dice)
        {
            var grounds = Grounds.Value;
            if (grounds.Count == 0) return null;
            var fitting = grounds.Where(g => g.Level <= level + 5)
                                 .OrderBy(g => Math.Abs(g.Level - level))
                                 .Take(3).ToList();
            if (fitting.Count == 0) fitting = grounds.OrderBy(g => g.Level).Take(1).ToList();
            return fitting[dice.Next(fitting.Count)];
        }

        /// <summary>
        /// Before it comes in, out of the war: the ground of its level as its range, and a map of
        /// it to come in on unless it is there already. A bot that grew past its ground moves on
        /// the next time it comes in, as a player changes zones.
        /// </summary>
        private void ChooseGround(int level)
        {
            // A bot has been around: it knows every zaap, or it could not come back from a far
            // ground to the marketplaces and workshops of Astrub when the server asks for the
            // zaaps to be discovered (ServerSettings.RequireZaapDiscovery).
            if (ServerSettings.Current.RequireZaapDiscovery)
                foreach (var waypoint in Interactives.Waypoints.Where(w => w.Activated))
                    ZaapDiscoveries.Discover(_characterId, waypoint.MapId);

            if (_atWar) return;
            Ground? ground;
            try { ground = GroundFor(level, _dice); }
            catch (Exception ex)
            {
                SimulatedPlayers.Journal($"{_name} could not choose a ground: {ex.Message}");
                return;
            }
            if (ground == null) return;
            _range = ground.Area;
            long map = DatabaseManager.MapOfOffline(_characterId);
            if (AreaOf(map) == _range) return;
            long to = ground.Maps[_dice.Next(ground.Maps.Count)];
            var cells = MobSpawnManager.GetInnerWalkableCells(to);
            DatabaseManager.SetPlaceOffline(_characterId, to, cells[_dice.Next(cells.Count)]);
            SimulatedPlayers.Journal($"{_name} (level {level}) goes to hunt in area {ground.Area} (level {ground.Level}).");
        }
    }
}
