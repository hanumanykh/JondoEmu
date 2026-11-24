using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A little life for the NPCs that content/npcs/life.json names: a short walk around their
    /// home cell now and then, and a line on the map's general channel now and then.
    /// </summary>
    /// <remarks>
    /// Every NPC used to stand on its cell forever, which on a solo server makes Astrub feel like
    /// a waxworks. This is the smallest thing that fixes it without touching anything players
    /// rely on:
    ///
    /// - Opt-in by the file. An NPC nobody wrote down never moves and never speaks: merchants,
    ///   bankers and quest givers are clicked by contextual id, which a walk does not change, but
    ///   a player still expects to find them where they always were.
    ///
    /// - The walk is the very message the monster groups already roam with
    ///   (<see cref="MobSpawnManager.RoamAsync"/>): a <see cref="GameMapMovementEvent"/> carrying
    ///   the path and the actor's id. The client animates any actor of the map with it, the NPC
    ///   actor included, since the NPC is one more actor of the jss (see <see cref="Npcs"/>). The
    ///   spawn's cell is updated too, so whoever enters the map afterwards finds the NPC where it
    ///   stopped and not on a cell it left.
    ///
    /// - Only on the main floor of the map (<see cref="MapManager.MainFloor"/>), never onto a cell
    ///   with an interactive element, a player or another NPC, and never further than the entry's
    ///   radius from home. Every other walk heads back home, so the NPC is always near its post.
    ///
    /// - Only while a real player stands on the map: a simulated player (the bots) is nobody's
    ///   audience, and walking for nobody would only cost sends.
    ///
    /// - The line goes as an ordinary chat line (kti) on the general channel, with the NPC's name
    ///   as the speaker -- the name has to come from the file, because the server does not know
    ///   NPC names: they never travel, the client takes them from its own data. The speaker's
    ///   character id is the NPC's contextual id, a negative nobody can whisper to.
    /// </remarks>
    public static class NpcLife
    {
        /// <summary>The authored file, under content/.</summary>
        public const string File = "npcs/life.json";

        /// <summary>One NPC brought to life, as the file writes it.</summary>
        public sealed class Entry
        {
            /// <summary>The NPC template id.</summary>
            public int Npc;

            /// <summary>Only on this map; 0 for every map the template stands on.</summary>
            public long Map;

            /// <summary>How far from home it may walk, in cells. 0 never walks (lines only).</summary>
            public int Radius;

            /// <summary>The name shown as the speaker of its lines.</summary>
            public string Name = "";

            /// <summary>What it says now and then. Empty never speaks.</summary>
            public List<string> Lines = new();
        }

        /// <summary>The largest radius the file may ask for: a patrol, not an expedition.</summary>
        public const int MaxRadius = 4;

        /// <summary>
        /// Reads the file's text. Rows without an npc id are dropped, the radius is held to
        /// 0..<see cref="MaxRadius"/>, blank lines are dropped, and a row that neither walks nor
        /// speaks is dropped as it would do nothing. "_comment" and any other unknown property are
        /// ignored.
        /// </summary>
        public static List<Entry> Parse(string json)
        {
            var entries = new List<Entry>();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (!doc.RootElement.TryGetProperty("npcs", out var list) || list.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var entry = new Entry
                {
                    Npc = Int(row, "npc"),
                    Map = row.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Number ? map.GetInt64() : 0,
                    Radius = Math.Clamp(Int(row, "radius"), 0, MaxRadius),
                    Name = row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "",
                };
                if (row.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    foreach (var line in lines.EnumerateArray())
                        if (line.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(line.GetString()))
                            entry.Lines.Add(line.GetString()!.Trim());

                if (entry.Npc <= 0) continue;
                if (entry.Radius == 0 && entry.Lines.Count == 0) continue;
                entries.Add(entry);
            }
            return entries;
        }

        private static int Int(JsonElement row, string property)
            => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int number) ? number : 0;

        /// <summary>The entry that applies to a placement: its own map's first, else the any-map one.</summary>
        internal static Entry? EntryFor(IReadOnlyList<Entry> entries, long mapId, int npcId)
            => entries.FirstOrDefault(e => e.Npc == npcId && e.Map == mapId)
               ?? entries.FirstOrDefault(e => e.Npc == npcId && e.Map == 0);

        private static List<Entry>? _entries;

        /// <summary>The file's entries, read once; an absent or broken file brings no NPC to life.</summary>
        private static List<Entry> Entries
        {
            get
            {
                if (_entries != null) return _entries;
                var read = new List<Entry>();
                try
                {
                    string path = Paths.ContentFile(File);
                    if (System.IO.File.Exists(path)) read = Parse(System.IO.File.ReadAllText(path));
                    Console.WriteLine($"[NPC life] {read.Count} NPCs brought to life.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NPC life] {File} could not be read: {ex.Message}");
                }
                return _entries = read;
            }
        }

        /// <summary>What is remembered of one living placement, by (map, contextual id).</summary>
        private sealed class State
        {
            public int Home;
            public DateTime NextWalk;
            public DateTime NextLine;
            public int LastLine = -1;
        }

        private static readonly ConcurrentDictionary<(long Map, long Actor), State> _states = new();

        /// <summary>When each map last heard an NPC line: one line per map at most every so often.</summary>
        private static readonly ConcurrentDictionary<long, DateTime> _mapLastLine = new();

        /// <summary>The calm of it: seconds between two walks, and between two lines of one NPC.</summary>
        private const int WalkMinSeconds = 20, WalkMaxSeconds = 60;
        private const int LineMinSeconds = 60, LineMaxSeconds = 180;

        /// <summary>No map hears two NPC lines closer than this, however many NPCs it has.</summary>
        private static readonly TimeSpan MapLineGap = TimeSpan.FromSeconds(40);

        /// <summary>
        /// One beat of the loop, which Program runs every five seconds next to the monsters' roam.
        /// </summary>
        internal static async Task TickAsync()
        {
            var entries = Entries;
            if (entries.Count == 0) return;
            var now = DateTime.UtcNow;

            var occupiedMaps = SessionRegistry.InWorld()
                .Where(SessionRegistry.StandsOnTheMap)
                .GroupBy(session => session.MapId);
            foreach (var map in occupiedMaps)
            {
                // Somebody real has to be watching: bots alone are no audience.
                if (!map.Any(s => !Bots.SimulatedPlayers.IsSimulated(s.AccountId))) continue;
                var audience = map.First().Map;
                if (audience.MapId != map.Key) continue;

                var spawns = Npcs.Of(map.Key);
                if (spawns.Count == 0) continue;
                var players = map.Select(s => s.State.CellId).ToHashSet();

                foreach (var spawn in spawns.ToArray())
                {
                    var entry = EntryFor(entries, map.Key, spawn.NpcId);
                    if (entry == null || !Npcs.VisibleNow(spawn)) continue;

                    var state = _states.GetOrAdd((map.Key, spawn.ContextualId), _ => new State
                    {
                        Home = spawn.Cell,
                        // A first wait, so an NPC does not walk off the instant a player arrives.
                        NextWalk = now.AddSeconds(Random.Shared.Next(WalkMinSeconds, WalkMaxSeconds)),
                        NextLine = now.AddSeconds(Random.Shared.Next(10, LineMinSeconds)),
                    });

                    if (entry.Radius > 0 && now >= state.NextWalk)
                    {
                        state.NextWalk = now.AddSeconds(Random.Shared.Next(WalkMinSeconds, WalkMaxSeconds + 1));
                        var path = PlanWalk(map.Key, spawn, state.Home, entry.Radius, players, spawns);
                        if (path != null)
                        {
                            spawn.Orientation = OrientationOf(path[^2], path[^1]);
                            spawn.Cell = path[^1];
                            await audience.SendAsync(new GameMapMovementEvent(path, spawn.ContextualId, false), fightId: 0);
                        }
                    }

                    if (entry.Lines.Count > 0 && now >= state.NextLine)
                    {
                        state.NextLine = now.AddSeconds(Random.Shared.Next(LineMinSeconds, LineMaxSeconds + 1));
                        if (_mapLastLine.TryGetValue(map.Key, out var last) && now - last < MapLineGap) continue;
                        _mapLastLine[map.Key] = now;

                        int pick = Random.Shared.Next(entry.Lines.Count);
                        if (pick == state.LastLine && entry.Lines.Count > 1) pick = (pick + 1) % entry.Lines.Count;
                        state.LastLine = pick;

                        string name = entry.Name.Length > 0 ? entry.Name : $"PNJ {spawn.NpcId}";
                        byte[] line = ConnectionProtocol.Push(Op.Kti,
                            ConnectionProtocol.BuildChatLine(name, spawn.ContextualId, 0, entry.Lines[pick], 0));
                        await SessionRegistry.BroadcastToMapAsync(map.Key, line, fightId: 0);
                    }
                }
            }
        }

        /// <summary>
        /// The path of the next walk, start cell included, or null to stand still this time.
        /// </summary>
        /// <remarks>
        /// Away from home it goes back home half the time, and always when it already stands at
        /// the edge of its radius; otherwise it picks a free cell 1..4 steps away that is still
        /// within the radius of home. Home is let into the walkable set even when it is not on the
        /// main floor (many NPCs stand where no player steps), so the way back always exists.
        /// </remarks>
        private static List<int>? PlanWalk(long mapId, Npcs.Spawn spawn, int home, int radius,
                                           HashSet<int> players, IReadOnlyList<Npcs.Spawn> spawns)
        {
            var floor = MapManager.MainFloor(mapId);
            if (floor == null || floor.Count == 0) return null;

            var allowed = new HashSet<int>(floor.Where(c => MapGeometry.Distance(home, c) <= radius));
            allowed.ExceptWith(Interactives.ElementsOf(mapId).Where(e => e.Cell != 0).Select(e => e.Cell));
            allowed.ExceptWith(players);
            allowed.ExceptWith(spawns.Where(s => s != spawn).Select(s => s.Cell));
            allowed.Add(home);
            allowed.Add(spawn.Cell);

            int here = spawn.Cell;
            int distance = MapGeometry.Distance(here, home);
            int target;
            if (distance > 0 && (distance >= radius || Random.Shared.Next(2) == 0))
            {
                // Home, unless somebody stands on it: then the free cell nearest to it.
                target = !players.Contains(home) ? home
                    : allowed.Where(c => c != here && !players.Contains(c))
                             .OrderBy(c => MapGeometry.Distance(c, home)).DefaultIfEmpty(-1).First();
            }
            else
            {
                var choices = allowed.Where(c => c != here)
                    .Where(c => MapGeometry.Distance(here, c) is >= 1 and <= 4)
                    .ToList();
                target = choices.Count == 0 ? -1 : choices[Random.Shared.Next(choices.Count)];
            }
            if (target < 0 || target == here) return null;

            var path = MapGeometry.FindShortestPath(here, target, allowed);
            return path != null && path.Count is >= 2 and <= 5 && path[0] == here ? path : null;
        }

        /// <summary>The facing of the last step, as the monster groups take it.</summary>
        private static int OrientationOf(int from, int to)
        {
            var (x, y) = MapGeometry.CellToPoint(from);
            var (toX, toY) = MapGeometry.CellToPoint(to);
            return (toX - x, toY - y) switch
            {
                (1, 0) => 1,
                (0, -1) => 3,
                (-1, 0) => 5,
                _ => 7
            };
        }
    }
}
