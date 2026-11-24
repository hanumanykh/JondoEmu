using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// Where a fight meets the Stasis-like economy: the one place the fight code asks what a won
    /// fight pays in kamas (Orbes), what it adds to the experience rate and to the drop bonus, and
    /// where the zone gauge is filled. The fight code calls four functions and knows nothing of
    /// tides, archmonsters, gauges or the counter.
    /// </summary>
    /// <remarks>
    /// The hooks: FightRewards.PlanRewards (kamas and experience of each winner), FightHandler's
    /// unplanned fallback (kamas), FightHandler.RollLoot (drop bonus) and the end of a won fight in
    /// each winner's own session (the gauge). A fight counts when it pays loot (ReparteBotin) and
    /// is not a dream's -- the Koliseo, challenges and tax collectors never get here.
    /// </remarks>
    public static class FarmEconomy
    {
        /// <summary>What the economy reads of a fight: where it was and what was beaten.</summary>
        public sealed record Facts(int SubAreaId, IReadOnlyList<Orbes.Monster> Monsters, bool Archmonster, bool Tide);

        private static HashSet<int>? _bosses;

        private static bool IsBoss(int monsterId)
        {
            _bosses ??= DungeonManager.IsLoaded ? DungeonManager.All.Values.SelectMany(d => d.Bosses).ToHashSet() : null;
            return _bosses?.Contains(monsterId) ?? false;
        }

        /// <summary>The beaten monsters of a fight -- the group's, not the summons nor the illusions.</summary>
        internal static List<Fighter> Beaten(FightInstance fight)
            => fight.Rojo.Where(m => m.IsMonster && m.BeatenAsGroupMember && !m.EsIlusion).ToList();

        public static Facts FactsOf(FightInstance fight)
        {
            var beaten = Beaten(fight);
            int subArea = Zones.SubAreaOf(fight.RoleplayMapId);
            // An archmonster summoned during the fight does not count (§4): only the group's.
            bool archmonster = beaten.Any(m => !m.EsInvocado && Archimonsters.Is(m.MonsterId));
            return new Facts(subArea, beaten.Select(m => new Orbes.Monster(m.Level, IsBoss(m.MonsterId))).ToList(),
                             archmonster, Tides.IsTideZone(subArea));
        }

        /// <summary>
        /// The kamas (Orbes) one winner takes: his whole share (§2: nothing is cut among the group).
        /// Falls back to the old 10 + 5 x level a monster when the Orbes are switched off.
        /// </summary>
        public static long KamasFor(FightInstance fight, int characterLevel)
        {
            var facts = FactsOf(fight);
            if (!Orbes.Current.Enabled)
                return ServerSettings.WithBonus(facts.Monsters.Sum(m => 10L + m.Level * 5L), ServerSettings.Current.KamasBonusPercent);
            long orbes = Orbes.ForFighter(characterLevel, facts.Monsters, facts.Archmonster, facts.Tide);
            return ServerSettings.WithBonus(orbes, ServerSettings.Current.KamasBonusPercent);
        }

        /// <summary>
        /// What is added to the server's experience rate for this winner: the tide's +250 % when it
        /// is in his band, or else the counter's Étude +50 %.
        /// </summary>
        public static int ExtraExperiencePercent(FightInstance fight, long characterId, int level)
        {
            int subArea = Zones.SubAreaOf(fight.RoleplayMapId);
            if (Tides.BoostsExperience(subArea, level)) return Farm.Current.Tides.ExperiencePercent;
            if (Farm.Current.Counter.Enabled && ZoneCounter.IsActive(FarmProfiles.Of(characterId), subArea, ZoneCounter.Study, Farm.Clock()))
                return Farm.Current.Counter.StudyExperiencePercent;
            return 0;
        }

        /// <summary>What is added to the drop bonus: +400 % with an archmonster in the group, +100 % under Densité.</summary>
        public static int ExtraDropPercent(FightInstance fight, long characterId)
        {
            if (Dreams.IsDreamMap(fight.RoleplayMapId)) return 0;
            int extra = 0;
            if (Beaten(fight).Any(m => !m.EsInvocado && Archimonsters.Is(m.MonsterId))) extra += Farm.Current.Archmonsters.DropPercent;
            int subArea = Zones.SubAreaOf(fight.RoleplayMapId);
            if (Farm.Current.Counter.Enabled && ZoneCounter.IsActive(FarmProfiles.Of(characterId), subArea, ZoneCounter.Density, Farm.Clock()))
                extra += Farm.Current.Counter.DensityDropPercent;
            return extra;
        }

        /// <summary>
        /// A won fight's end, in the winner's own session: his gauge of the subarea takes the
        /// monsters, and pays the target when it is full. Never throws into the fight's end.
        /// </summary>
        public static async Task AfterWonFightAsync(NetworkStream stream, FightInstance fight)
        {
            try
            {
                if (!Farm.Current.Gauge.Enabled || !fight.Reglas.ReparteBotin || fight.Reglas.PagaElKoliseo) return;
                if (Dreams.IsDreamMap(fight.RoleplayMapId)) return;
                var facts = FactsOf(fight);
                var zone = Zones.Of(facts.SubAreaId);
                if (zone == null || zone.Equipment.Count == 0 || facts.Monsters.Count == 0) return;

                long characterId = GameState.CharacterId;
                var profile = FarmProfiles.Of(characterId);
                var now = Farm.Clock();
                bool buff = ZoneCounter.IsActive(profile, zone.SubAreaId, ZoneCounter.Gauge, now);
                int gain = ZoneGauge.Gain(facts.Monsters.Count, facts.Archmonster, facts.Tide, buff);
                int item;
                GaugeState state;
                lock (profile.Gate)
                {
                    if (!profile.Gauges.TryGetValue(zone.SubAreaId, out state!)) profile.Gauges[zone.SubAreaId] = state = new GaugeState();
                    item = ZoneGauge.Add(state, zone.Level, gain, zone.Equipment, now);
                }
                FarmProfiles.Save(profile);
                if (item != 0) await DeliverAsync(stream, item);
                else if (state.Progress >= ZoneGauge.Threshold(zone.Level, state.Completions))
                    await TellAsync(stream, "Votre jauge de zone est pleine : choisissez une cible avec .jauge cible <objet>.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Farm] The gauge of {GameState.CharacterId} failed after fight #{fight.FightId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Gives the guaranteed item, stasified at the character's tier (<see cref="Stasis.DropTier"/>:
        /// S1, or the evolved distribution) at its max roll, and records the tier.
        /// </summary>
        internal static async Task<bool> DeliverAsync(NetworkStream stream, int gid)
        {
            var template = Forgemagic.TemplateOf(gid);
            if (template == null) return false;
            int tier = Stasis.Applies(template) ? Stasis.DropTier(GameState.CharacterId, Random.Shared) : 0;
            var effects = tier != 0 ? Stasis.AtMax(template, tier) : Forgemagic.Perfect(template);
            long uid = await WorkshopHandler.GiveExactAsync(stream, gid, effects);
            if (uid == 0)
            {
                Console.WriteLine($"[Farm] The gauge's item {gid} could not be given to {GameState.CharacterId}.");
                return false;
            }
            if (tier != 0) Stasis.Mark(uid, tier);
            ActivityJournal.Current.Write("farm.gauge", SessionContext.Current.AccountId, GameState.CharacterId, new { gid, tier, uid });
            await TellAsync(stream, $"Jauge de zone pleine : vous recevez {CommandHandler.ItemName(gid)}" +
                                    (tier != 0 ? $" {Stasis.Name(tier)}" : "") + ".");
            return true;
        }

        private static Task TellAsync(NetworkStream stream, string text)
            => NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
    }
}
