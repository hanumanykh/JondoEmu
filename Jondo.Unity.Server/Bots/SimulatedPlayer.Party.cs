using System;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// A bot in a real player's party: it accepts his invitation, follows him from map to map and
    /// goes into his fights with him -- the automatic entry and ready of the party window, which
    /// the server already honours (FightHandler.FollowersInto) --, and plays its turns as in any
    /// fight. While it is in his party it hunts nothing of its own and does not leave the world.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>How long it keeps trying to reach a leader it cannot find a way to before it leaves.</summary>
        private const int LostSteps = 6;

        private int _lostSteps;

        /// <summary>The real player's party it is in, or null.</summary>
        private Parties.Party? PlayerParty
        {
            get
            {
                if (_characterId == 0) return null;
                var party = Parties.Of(_characterId);
                if (party == null || party.LeaderId == _characterId) return null;
                var leader = SessionRegistry.FindByCharacter(party.LeaderId);
                return leader != null && !SimulatedPlayers.IsSimulated(leader.AccountId) ? party : null;
            }
        }

        private bool InParty => PlayerParty != null;

        /// <summary>
        /// An invitation (ijz { f1 me, f2 who, f3 seats, f5 party, f7 name }): from a real player it
        /// says yes after a moment, as somebody reading it; from a bot, or on its way out, no answer.
        /// </summary>
        private async Task InvitedAsync(byte[] payload)
        {
            long host = Var(payload, 2);
            int party = (int)Var(payload, 5);
            var inviter = SessionRegistry.FindByCharacter(host);
            if (party == 0 || inviter == null || SimulatedPlayers.IsSimulated(inviter.AccountId) || _leaving) return;
            await Task.Delay(_dice.Next(1500, 4000));
            if (_closed || Session is not { } session || session.State.IsInFight) return;

            await SendAsync(Op.Ijx, Pb.New().Var(1, party).Build());
            await Task.Delay(500);
            if (!InParty) return;
            // Into his fights with him, and ready when he is.
            await SendAsync(Op.Ilf);
            await SendAsync(Op.Ikr);
            _lostSteps = 0;
            if (_queuedSince != null)
            {
                await SendAsync(Op.Lsi);
                _queuedSince = null;
            }
            await SayAsync(ChatChannels.Party, LineOf("joined", session, leader: inviter.State.CharacterName));
            SimulatedPlayers.Journal($"{_name} joins the party of {inviter.State.CharacterName}.");
        }

        /// <summary>
        /// One thing to do in the party: to its leader's map if it is elsewhere, a few steps if it is
        /// there, and now and then a word. False when it is in no player's party.
        /// </summary>
        private async Task<bool> FollowAsync(GameSession session, CancellationToken stop)
        {
            var party = PlayerParty;
            if (party == null) return false;
            var leader = SessionRegistry.FindByCharacter(party.LeaderId);
            if (leader == null) return false;
            // Into his fights and ready with him, however it came into his party -- an invitation
            // or the dungeon search (DungeonCompanions).
            if (!FightHandler.AutoJoinOn(_characterId) || !FightHandler.AutoReadyOn(_characterId))
                FightHandler.SetAutoOptions(_characterId, join: true, ready: true);
            // His fight's placement takes it in by itself, if it stands on his map.
            if (leader.State.IsInFight) return true;

            long there = leader.State.MapId;
            if (there != session.State.MapId)
            {
                // A dungeon's rooms are joined by its fights and doors, not by borders: in one, or
                // coming out of one, it goes straight to him, as the search's group is brought.
                if (DungeonManager.OfRoom(there) != null || DungeonManager.OfRoom(session.State.MapId) != null)
                {
                    if (await JoinLeaderAsync(session, there)) _lostSteps = 0;
                    else if (++_lostSteps >= LostSteps) await LeavePartyAsync(session, party);
                    return true;
                }
                if (await GoToAsync(session, there, stop)) _lostSteps = 0;
                else if (++_lostSteps >= LostSteps)
                {
                    SimulatedPlayers.Journal($"{_name} cannot find a way to {there}; it leaves the party.");
                    await LeavePartyAsync(session, party);
                }
                return true;
            }

            _lostSteps = 0;
            if (_dice.Next(100) < 4) await SayAsync(ChatChannels.Party, LineOf("party", session));
            else if (_dice.Next(100) < 40) await WanderAsync(session, stop);
            return true;
        }

        /// <summary>
        /// To its leader's map at once, by the server's own teleport -- the map loads on its socket
        /// as any teleport's does (jru, answered by SimulatedPlayer.OnFrameAsync).
        /// </summary>
        private async Task<bool> JoinLeaderAsync(GameSession session, long map)
        {
            if (session.Stream == null || !await session.UnoCadaVez.WaitAsync(TimeSpan.FromSeconds(5))) return false;
            try
            {
                using (SessionContext.Push(session))
                {
                    if (session.State.IsInFight) return false;
                    _arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (await WorldMoveHandler.TeleportAsync(session.Stream, map) == null) return false;
                }
            }
            finally
            {
                session.UnoCadaVez.Release();
            }
            return await WithinAsync(_arrived.Task) == map;
        }

        /// <summary>Says goodbye and leaves the party (inh { f2 party }).</summary>
        private async Task LeavePartyAsync(GameSession session, Parties.Party party)
        {
            await SayAsync(ChatChannels.Party, LineOf("left", session));
            await Task.Delay(800);
            await SendAsync(Op.Inh, Pb.New().Var(2, party.Id).Build());
        }
    }
}
