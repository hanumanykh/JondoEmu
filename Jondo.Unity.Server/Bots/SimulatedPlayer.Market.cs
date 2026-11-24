using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The marketplaces as the bots keep them alive: now and then a round of the counters, where it
    /// buys what real players sell at a fair price -- a player's lot that never sells is a dead
    /// market -- and puts on sale what it no longer needs, priced as people do: near what the
    /// thing goes for, under the cheapest offer when that one is fair.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>The most lots it buys on one round, and the dearest it pays against the worth.</summary>
        private const int BuysPerRound = 4;
        private const int FairPercent = 125;

        private DateTime _nextMarketAt = DateTime.UtcNow + TimeSpan.FromMinutes(5 + new Random().Next(25));

        private static readonly int[] Houses = { ResourceHouse, ConsumableHouse, EquipmentHouse };

        /// <summary>A round of the counters when it is time; false when it is not, or there was nothing to do.</summary>
        private async Task<bool> MarketRoundAsync(GameSession session, CancellationToken stop)
        {
            if (DateTime.UtcNow < _nextMarketAt) return false;
            _nextMarketAt = DateTime.UtcNow + TimeSpan.FromMinutes(_dice.Next(25, 60));
            long origin = session.State.MapId;
            long kamas = session.State.Kamas;
            int bought = 0, listed = 0;
            try
            {
                foreach (int house in Houses.OrderBy(_ => _dice.Next()))
                {
                    if (bought >= BuysPerRound) break;
                    var wanted = BargainsIn(house, session).Take(BuysPerRound - bought).ToList();
                    if (wanted.Count == 0) continue;
                    await AtCounterAsync(session, house, async () =>
                    {
                        foreach (var lot in wanted)
                        {
                            await SendAsync(Op.Kbm, Pb.New().Var(1, lot.Offer).Var(2, lot.Price).Var(3, lot.Lot).Build());
                            await Task.Delay(600, stop);
                            bought++;
                        }
                    }, stop);
                }
                listed = await SellAsync(session, PlanFor(session), stop);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SimulatedPlayers.Journal($"{_name}'s market round broke: {ex.Message}");
            }

            if (session.State.MapId != origin && !session.State.IsInFight)
            {
                if (!await GoToAsync(session, origin, stop)) await GoToAsync(session, m => AreaOf(m) == _range, stop);
            }
            if (bought + listed == 0) return false;
            SimulatedPlayers.Journal($"{_name} goes to market: {bought} lot(s) bought from players, {listed} put on sale; " +
                                     $"kamas {kamas} -> {session.State.Kamas}.");
            return true;
        }

        /// <summary>
        /// What real players sell in a marketplace that it would buy: no dearer than a quarter
        /// over its worth, within its means, the best bargains first, and some by chance.
        /// </summary>
        private IEnumerable<(int Offer, int Gid, int Lot, long Price)> BargainsIn(int house, GameSession session)
        {
            long budget = Math.Max(0, Budget(session) / 2);
            return MarketplaceListings.CheapestLotsBy(house, account => account != _accountId && !SimulatedPlayers.IsSimulated(account),
                                                      DateTime.UtcNow)
                .Select(l => (Lot: l, Worth: Math.Max(1, ValueOf(l.Gid) * l.Lot)))
                .Where(x => x.Lot.Price <= budget && x.Lot.Price * 100 <= x.Worth * FairPercent)
                // The cheaper against its worth, the likelier it is wanted; a little luck either way.
                .Where(x => _dice.Next(100) < Math.Clamp(150 - (int)(x.Lot.Price * 100 / x.Worth), 20, 95))
                .OrderBy(x => x.Lot.Price * 100 / x.Worth)
                .Select(x => x.Lot);
        }

        /// <summary>
        /// The price it asks for a lot: its worth, give or take a fifth; under the cheapest offer of
        /// that size when the cheapest is fair, as a seller who wants it gone. Never under seven
        /// tenths of the worth, nor over half again.
        /// </summary>
        private long AskFor(Marketplaces.House house, int gid, int lot)
        {
            long worth = Math.Max(1, ValueOf(gid) * lot);
            long price = worth * _dice.Next(85, 121) / 100;
            int index = house.LotIndex(lot);
            if (index >= 0)
            {
                var lowest = MarketplaceListings.LowestPrices(house, gid);
                if (index < lowest.Count && lowest[index] > 0 && lowest[index] * 100 <= worth * 140)
                    price = lowest[index] * _dice.Next(94, 100) / 100;
            }
            return Math.Clamp(price, Math.Max(1, worth * 7 / 10), Math.Max(1, worth * 3 / 2));
        }
    }
}
