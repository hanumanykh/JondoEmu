using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Where a «GO» to a subarea lands (mission GO, the tide's «Y aller»): the subarea's zaap map
    /// when it has one, else an outdoor map of that very subarea on the Twelve's world map, the one
    /// nearest the middle of the subarea. Never a dungeon room, an inside (WorldMap -1), another
    /// world's map (Frigost's islands, dimensions...), a map with no walkable floor, nor a map the
    /// map table gives to another subarea -- MapSubareas lists maps by a subarea id the positions
    /// do not agree with (444 «Campos» holds the Orbalantyr's rooms of subarea 20663).
    /// </summary>
    internal static class GoDestination
    {
        /// <summary>The main world map's id (MapInfo.WorldMap).</summary>
        internal const int MainWorld = 1;

        /// <summary>The maps a GO to <paramref name="subArea"/> may land on, best first; empty: nowhere.</summary>
        /// <param name="maps">Every map known (MapManager.Maps.Values).</param>
        /// <param name="zaapMaps">The maps of the activated zaaps.</param>
        /// <param name="isDungeonRoom">Whether a map is a dungeon's room.</param>
        /// <param name="hasFloor">Whether a map has somewhere to stand; null: assume so.</param>
        internal static List<long> Candidates(int subArea, IEnumerable<MapInfo> maps, IEnumerable<long> zaapMaps,
                                              Func<long, bool> isDungeonRoom, Func<long, bool>? hasFloor = null)
        {
            if (subArea <= 0) return new List<long>();
            var own = maps.Where(m => m.SubAreaId == subArea && m.WorldMap == MainWorld && !isDungeonRoom(m.MapId)
                                      && (hasFloor == null || hasFloor(m.MapId)))
                          .ToList();
            var outdoor = own.Where(m => m.Outdoor).ToList();
            if (outdoor.Count == 0) return new List<long>();
            var zaaps = zaapMaps.ToHashSet();
            // A zaap may stand on an inside-flagged map of the subarea (a village square): kept.
            var byZaap = own.Where(m => zaaps.Contains(m.MapId)).Select(m => m.MapId);
            double cx = outdoor.Average(m => m.PosX), cy = outdoor.Average(m => m.PosY);
            var byCentre = outdoor.OrderBy(m => (m.PosX - cx) * (m.PosX - cx) + (m.PosY - cy) * (m.PosY - cy))
                                  .ThenBy(m => m.MapId).Select(m => m.MapId);
            return byZaap.OrderBy(id => id).Concat(byCentre).Distinct().ToList();
        }

        /// <summary>The live world's candidates: MapManager's maps, the zaaps, the dungeons' rooms, the main floor.</summary>
        internal static List<long> Of(int subArea)
            => Candidates(subArea, MapManager.Maps.Values,
                          Interactives.Waypoints.Where(w => w.Activated && w.SubAreaId == subArea).Select(w => w.MapId),
                          map => DungeonManager.OfRoom(map) != null,
                          map => !MapManager.WalkableCells.TryGetValue(map, out var cells) || cells.Count == 0
                                 ? MapManager.GetFightWalkable(map) is { Count: > 0 }
                                 : MapManager.MainFloor(map).Count > 0);

        /// <summary>Whether a GO to the subarea lands somewhere (true when the maps are not loaded, as in tests).</summary>
        internal static bool Reachable(int subArea) => MapManager.Maps.Count == 0 || Of(subArea).Count > 0;
    }
}
