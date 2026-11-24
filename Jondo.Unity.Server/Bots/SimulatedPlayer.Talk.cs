using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// What a bot says, as a player writes it (ktm), drawn from content/bots/chatter.json. It
    /// speaks only where a real player hears it: two hundred bots talking to each other would
    /// only fill the log.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        private const string ChatterFile = "bots/chatter.json";

        /// <summary>How often one bot may speak on its map, and anybody on one map.</summary>
        private static readonly TimeSpan OwnPause = TimeSpan.FromMinutes(5), MapPause = TimeSpan.FromSeconds(75);

        /// <summary>How often any bot may write on the server-wide channels (trade, recruitment).</summary>
        private static readonly TimeSpan WorldPause = TimeSpan.FromMinutes(4);

        private static readonly ConcurrentDictionary<long, DateTime> _mapSaid = new();
        private static DateTime _worldSaid = DateTime.MinValue;
        private static readonly object WorldLock = new();

        private DateTime _lastSaid = DateTime.MinValue;
        private readonly HashSet<long> _greeted = new();
        private bool _wasInFight;

        private static readonly Lazy<Dictionary<string, string[]>> Lines = new(() =>
        {
            var lines = new Dictionary<string, string[]>();
            string path = Paths.ContentFile(ChatterFile);
            try
            {
                if (!File.Exists(path)) return lines;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.Array) continue;
                    lines[property.Name] = property.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()!).Where(s => s.Length > 0).ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bots] {ChatterFile} could not be read: {ex.Message}");
            }
            return lines;
        });

        /// <summary>The real players -- not bots -- in the world on a map.</summary>
        internal static List<GameSession> PlayersOn(long map)
            => SessionRegistry.OnMap(map).Where(s => s.IsInWorld && !SimulatedPlayers.IsSimulated(s.AccountId)).ToList();

        private static bool AnyPlayerInWorld()
            => SessionRegistry.InWorld().Any(s => !SimulatedPlayers.IsSimulated(s.AccountId));

        /// <summary>A line of a kind, its placeholders filled; null when the file has none.</summary>
        private string? LineOf(string kind, GameSession session, string player = "", string leader = "")
        {
            if (!Lines.Value.TryGetValue(kind, out var list) || list.Length == 0) return null;
            string line = list[_dice.Next(list.Length)];
            if (line.Contains("{zone}"))
            {
                var info = MapManager.GetMapInfo(session.State.MapId);
                string zone = info == null ? "" : DatabaseManager.GetSubAreaName(info.SubAreaId);
                line = line.Replace("{zone}", zone.Length > 0 ? zone : "ce coin");
            }
            return line.Replace("{player}", player)
                       .Replace("{leader}", leader)
                       .Replace("{level}", session.State.CharacterLevel.ToString());
        }

        /// <summary>Writes a line on a channel, as the client does (ktm { f2 text, f3 channel }).</summary>
        private async Task SayAsync(int channel, string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _lastSaid = DateTime.UtcNow;
            await SendAsync(Op.Ktm, Pb.New().Str(2, line).Var(3, channel).Build());
        }

        /// <summary>Whether the map may hear another bot now; if so, it counts from now.</summary>
        private static bool MayTalkOn(long map)
        {
            var now = DateTime.UtcNow;
            if (_mapSaid.TryGetValue(map, out var last) && now - last < MapPause) return false;
            _mapSaid[map] = now;
            return true;
        }

        private static bool MayTalkToTheWorld()
        {
            lock (WorldLock)
            {
                var now = DateTime.UtcNow;
                if (now - _worldSaid < WorldPause) return false;
                _worldSaid = now;
                return true;
            }
        }

        /// <summary>
        /// Between two things to do, now and then something said: a greeting to a real player it
        /// meets, a word on the map, after a fight won, or an offer to the whole server.
        /// </summary>
        private async Task ChatterAsync(GameSession session)
        {
            if (Lines.Value.Count == 0) return;
            long map = session.State.MapId;
            bool fought = _wasInFight;
            _wasInFight = false;
            var players = PlayersOn(map);

            if (players.Count > 0)
            {
                var stranger = players.FirstOrDefault(p => !_greeted.Contains(p.CharacterId));
                if (stranger != null)
                {
                    _greeted.Add(stranger.CharacterId);
                    if (_dice.Next(100) < 35 && MayTalkOn(map))
                    {
                        await SayAsync(ChatChannels.General, LineOf("greet", session, stranger.State.CharacterName));
                        await EmoteAsync(_dice.Next(2) == 0 ? Wave : Hello);
                        return;
                    }
                }
                if (fought && _dice.Next(100) < 30 && MayTalkOn(map))
                {
                    await SayAsync(ChatChannels.General, LineOf("fight", session));
                    await EmoteAsync(_dice.Next(2) == 0 ? Dance : Laugh);
                    return;
                }
                if (DateTime.UtcNow - _lastSaid > OwnPause && _dice.Next(100) < 12 && MayTalkOn(map))
                {
                    await SayAsync(ChatChannels.General, LineOf("map", session));
                    return;
                }
            }

            // The server-wide channels, rarely, and only if somebody real is there to read them.
            if (_dice.Next(100) >= 3 || !AnyPlayerInWorld()) return;
            bool selling = MarketplaceListings.OfAccount(EquipmentHouse, _accountId).Count
                           + MarketplaceListings.OfAccount(ResourceHouse, _accountId).Count > 0;
            string kind = !InParty && _dice.Next(2) == 0 ? "recruit" : selling ? "trade" : "recruit";
            if (kind == "recruit" && InParty) return;
            if (!MayTalkToTheWorld()) return;
            await SayAsync(kind == "trade" ? ChatChannels.Trade : ChatChannels.Recruitment, LineOf(kind, session));
        }
    }
}
