using System;
using System.Collections.Concurrent;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Stasis dungeon extras (docs/stasis-gameplay-spec.md §1 and §11): the dungeon search's
    /// bonus and the leader's «salle complète» option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE SEARCH BONUS. The spec's table: «Duo (2, +25 %), Équipe (4, +50 %) via file publique ;
    /// groupe préformé +15 %/+30 %». It goes by the group's format, not by the run being finished:
    /// a group the search made (<see cref="Matchmaking.GroupOf"/>) earns it in every fight of ITS
    /// dungeon, on the experience and on the drop alike. Two or three is the Duo format, four the
    /// Équipe; a lone player matched with nobody gets nothing. The companions DungeonCompanions
    /// fills a public queue with count as people of the group, as they do on Stasis. The percent
    /// is ADDED to the rate, as the tides are (300 % → 325 %), not multiplied on top of it.
    /// The spec's «bonus d'Orbes pour donjon complet» has no published amount and is not done.
    /// </para>
    /// <para>
    /// SALLE COMPLÈTE. A dungeon room already fights a player alone with its first four monsters
    /// (MobSpawnManager.MembersFor: clamp(fighters, 4, 8)), which is what the spec calls a full
    /// room (≤ 4). What still follows the number of people is the grade of a boss that grows with
    /// the party (MonsterPartyGrades). With the option on, the room is fought as if four were in
    /// it: the boss at its four-people grade, harder and paying more. The leader's choice counts
    /// for everybody in his party, and is read when the fight starts and when it regrows.
    /// Kept in memory, per character; a restart puts it back to off.
    /// </para>
    /// <para>
    /// MÉCANIQUES SANS is not done: the bosses' mechanics are scripted one by one in the fight
    /// engine (Kontatrás's bell, the portals, the invulnerability states lifted by a fallen
    /// add...) with no flag that switches them off together, and removing the invulnerability
    /// states alone would leave fights that cannot be ended the way they were written.
    /// </para>
    /// </remarks>
    public static class DungeonRules
    {
        /// <summary>The spec's search bonuses: public queue Duo/Équipe, and a preformed group's.</summary>
        public const int DuoPercent = 25, TeamPercent = 50, PreformedDuoPercent = 15, PreformedTeamPercent = 30;

        /// <summary>The bonus a search group of this size earns: 0 alone, Duo for 2 or 3, Équipe for 4.</summary>
        public static int SearchBonusPercent(int size, bool preformed)
            => size >= Matchmaking.GameGroupSize ? (preformed ? PreformedTeamPercent : TeamPercent)
             : size >= 2 ? (preformed ? PreformedDuoPercent : DuoPercent)
             : 0;

        /// <summary>
        /// What the dungeon search adds to a winner's experience and drop in this fight: the bonus
        /// of his search group when the fight is in a room of that group's dungeon, else 0.
        /// </summary>
        public static int SearchBonusPercent(FightInstance fight, long characterId)
        {
            try
            {
                var party = Parties.Of(characterId);
                if (party == null || Matchmaking.GroupOf(party.Id) is not { } group) return 0;
                var dungeon = DungeonManager.Get(group.DungeonId);
                if (dungeon == null || !dungeon.Rooms.Contains(fight.RoleplayMapId)) return 0;
                return SearchBonusPercent(group.Size, group.Preformed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dungeons] The search bonus of {characterId} could not be read: {ex.Message}");
                return 0;
            }
        }

        // ─── Salle complète ─────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<long, bool> _fullRoom = new();

        public static bool FullRoom(long characterId) => _fullRoom.TryGetValue(characterId, out bool on) && on;

        public static void SetFullRoom(long characterId, bool on)
        {
            if (on) _fullRoom[characterId] = true;
            else _fullRoom.TryRemove(characterId, out _);
        }

        internal static void Clear() => _fullRoom.Clear();

        /// <summary>
        /// How many people a dungeon room is fought for: the people there, or at least
        /// <see cref="MobSpawnManager.DungeonMinimum"/> when the leader of the first player's
        /// party (or that player, alone) asked for the full room.
        /// </summary>
        public static int FightersFor(FightInstance fight, int people)
        {
            var first = fight.Azul.FirstOrDefault(f => !f.IsMonster && !f.EsInvocado);
            if (first == null) return people;
            long leader = Parties.Of(first.Id)?.LeaderId ?? first.Id;
            return FightersFor(people, FullRoom(leader));
        }

        public static int FightersFor(int people, bool fullRoom)
            => fullRoom ? Math.Max(people, MobSpawnManager.DungeonMinimum) : people;
    }
}
