using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The end-of-fight recap JondoFix toasts after a won PvM fight: the Jondo Coins won, the zone
    /// gauge's progress, the missions that moved and the bonuses that were on (Marée, archimonstre,
    /// comptoir buffs). FightHandler <see cref="Begin"/>s it with the coins and bonuses, then
    /// FarmEconomy.AfterWonFightAsync notes the gauge and MissionsAfterFightAsync the missions; the
    /// latest recap of each character is answered in the «alertes» view the HUD already polls
    /// (CommandHandler.PanelViews.cs), keyed by the fight id so it is toasted once. Memory only.
    /// </summary>
    public static class FightRecaps
    {
        public sealed class Recap
        {
            public long FightId { get; init; }
            public DateTime At { get; init; }
            public long Coins { get; init; }
            public string CoinLabel { get; init; } = "";
            public List<string> Bonuses { get; } = new();
            public string Gauge { get; set; } = "";
            public List<string> Missions { get; } = new();
        }

        /// <summary>How long a recap is still answered after its fight: a late poll still sees it, a later login does not.</summary>
        public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(2);

        private static readonly ConcurrentDictionary<long, Recap> _latest = new();

        /// <summary>The recap of a fight just won: its coins and the bonuses it was fought under.</summary>
        public static void Begin(long characterId, FightInstance fight, long coins, string coinLabel)
        {
            if (characterId <= 0) return;
            var recap = new Recap { FightId = fight.FightId, At = DateTime.UtcNow, Coins = coins, CoinLabel = coinLabel };
            try
            {
                var facts = FarmEconomy.FactsOf(fight);
                if (facts.Tide) recap.Bonuses.Add("Marée");
                if (facts.Archmonster) recap.Bonuses.Add("Archimonstre");
                if (Farm.Current.Counter.Enabled)
                {
                    var profile = FarmProfiles.Of(characterId);
                    var now = Farm.Clock();
                    foreach (string buff in new[] { ZoneCounter.Density, ZoneCounter.Study, ZoneCounter.Gauge, ZoneCounter.Recruit })
                        if (ZoneCounter.IsActive(profile, facts.SubAreaId, buff, now)) recap.Bonuses.Add("Comptoir " + ZoneCounter.Label(buff));
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Recap] Bonuses of fight #{fight.FightId}: {ex.Message}"); }
            _latest[characterId] = recap;
        }

        /// <summary>The gauge's line: «+12 (85/300)», or «pleine» / the item given.</summary>
        public static void NoteGauge(long characterId, long fightId, string text)
        {
            if (_latest.TryGetValue(characterId, out var r) && r.FightId == fightId) lock (r) r.Gauge = text;
        }

        public static void NoteMission(long characterId, long fightId, string text)
        {
            if (_latest.TryGetValue(characterId, out var r) && r.FightId == fightId) lock (r) if (r.Missions.Count < 4) r.Missions.Add(text);
        }

        /// <summary>The latest recap still fresh, for the alerts view; null when none.</summary>
        public static object? ViewOf(long characterId)
        {
            if (!_latest.TryGetValue(characterId, out var r) || DateTime.UtcNow - r.At > KeepFor) return null;
            lock (r)
                return new
                {
                    key = $"recap:{r.FightId}",
                    coins = r.Coins,
                    coinLabel = r.CoinLabel,
                    gauge = r.Gauge,
                    missions = r.Missions.ToList(),
                    bonuses = r.Bonuses.ToList(),
                };
        }
    }
}
