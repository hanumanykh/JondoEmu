using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The automatic dungeon search (Managers.Matchmaking) for a server lived alone: a real player
    /// who searches and finds nobody is completed with simulated players of his level, who search
    /// the same dungeon, say yes at once and come into it with him. Once the group is made they are
    /// in his party, and SimulatedPlayer.Party has them follow him from room to room and into
    /// every fight.
    /// </summary>
    internal static class DungeonCompanions
    {
        /// <summary>How long a real player searches alone before the bots come: other players first.</summary>
        internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

        /// <summary>How far a companion's level may be from the player's.</summary>
        internal const int LevelSpread = 20;

        /// <summary>Whether a character is a simulated player's.</summary>
        internal static bool IsBot(long characterId)
            => SessionRegistry.FindByCharacter(characterId) is { } s && SimulatedPlayers.IsSimulated(s.AccountId);

        /// <summary>
        /// The real players' searches that have waited long enough get the bots they lack, searching
        /// one dungeon of theirs that the bots' levels fit. Returns how many bots joined.
        /// </summary>
        internal static int Fill(DateTime now)
        {
            int size = Matchmaking.GroupSize;
            var seekers = QueueSnapshot();
            int joined = 0;
            var taken = new HashSet<long>();
            foreach (var seeker in seekers.OrderBy(s => s.Since))
            {
                if (now - seeker.Since < Patience) continue;
                if (seeker.Members.All(IsBot) || seeker.Members.Count >= size) continue;
                int missing = size - seeker.Members.Count - BotsAlreadySearching(seeker);
                if (missing <= 0) continue;

                int level = seeker.Members.Where(m => !IsBot(m))
                    .Select(m => DatabaseManager.GetCharacterById(m)?.Level ?? 1).DefaultIfEmpty(1).Max();
                foreach (int dungeonId in seeker.Dungeons)
                {
                    var dungeon = DungeonManager.Get(dungeonId);
                    if (dungeon == null || dungeon.FirstRoom == 0) continue;
                    var bots = Candidates(level, dungeon.MinLevel, taken).Take(missing).ToList();
                    if (bots.Count < missing) continue;
                    foreach (var bot in bots)
                    {
                        taken.Add(bot.CharacterId);
                        Matchmaking.Join(new Matchmaking.Seeker
                        {
                            LeaderId = bot.CharacterId,
                            Members = new List<long> { bot.CharacterId },
                            Dungeons = new List<int> { dungeonId },
                            // Just after the player, so that the group made is his.
                            Since = seeker.Since.AddMilliseconds(1 + joined),
                        });
                        joined++;
                    }
                    SimulatedPlayers.Journal($"{bots.Count} bot(s) search {dungeon.Name} to complete a player's group: " +
                                             string.Join(", ", bots.Select(b => $"{b.State.CharacterName} ({b.State.CharacterLevel})")) + ".");
                    break;
                }
            }
            return joined;
        }

        /// <summary>The queue as it stands, copied.</summary>
        private static List<Matchmaking.Seeker> QueueSnapshot()
        {
            var all = new List<Matchmaking.Seeker>();
            foreach (var session in SessionRegistry.InWorld())
            {
                var seeker = Matchmaking.SeekerOf(session.CharacterId);
                if (seeker != null && seeker.LeaderId == session.CharacterId && !all.Contains(seeker)) all.Add(seeker);
            }
            return all;
        }

        /// <summary>Bots already searching one of this search's dungeons for it.</summary>
        private static int BotsAlreadySearching(Matchmaking.Seeker seeker)
            => QueueSnapshot().Count(s => s != seeker && s.Members.All(IsBot) && s.Dungeons.Intersect(seeker.Dungeons).Any());

        /// <summary>
        /// The bots in the world free to come: not fighting, not in a party, not searching, not
        /// offered anything, of a level near the player's and at least the dungeon's; the nearest
        /// in level first.
        /// </summary>
        private static IEnumerable<GameSession> Candidates(int level, int minLevel, HashSet<long> taken)
            => SessionRegistry.InWorld()
                .Where(s => SimulatedPlayers.IsSimulated(s.AccountId) && !taken.Contains(s.CharacterId)
                            && !s.State.IsInFight && !Parties.IsInParty(s.CharacterId)
                            && Matchmaking.SeekerOf(s.CharacterId) == null && Matchmaking.MatchOf(s.CharacterId) == null
                            && s.State.CharacterLevel >= minLevel
                            && Math.Abs(s.State.CharacterLevel - level) <= LevelSpread)
                .OrderBy(s => Math.Abs(s.State.CharacterLevel - level));

        /// <summary>The bots of an offer say yes at once. True when everybody has said it now.</summary>
        internal static bool AcceptFor(Matchmaking.Match match, out Matchmaking.Match accepted)
        {
            accepted = match;
            bool all = false;
            foreach (long member in match.Members.Where(IsBot).ToList())
            {
                var (now, everybody) = Matchmaking.Accept(member);
                if (now != null) accepted = now;
                all |= everybody;
            }
            return all;
        }
    }
}
