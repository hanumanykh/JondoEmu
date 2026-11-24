using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Managers.Economy;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The players' side of the Stasis-like farming loop (Managers.Economy): .jauge shows the zone
    /// gauge and chooses its target, .comptoir lists and buys the zone counter's buffs, .maree tells
    /// where the tide is.
    /// </summary>
    /// <remarks>
    /// Commands because the 3.6 client has no gauge bar, no Butins window and no counter: Stasis
    /// draws its own. The texts are French, the language the owner plays in; the usage lines stay
    /// in the catalogue's three languages (AdminCommandTests). Everything goes by the subarea the
    /// character stands in, as the spec's counter and gauge do.
    /// </remarks>
    public static partial class CommandHandler
    {
        /// <summary>An item's name for the economy's messages.</summary>
        internal static string ItemName(int gid) => NameOfItem(gid);

        // ─── .jauge ─────────────────────────────────────────────────────────────────────────

        private static async Task GaugeAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            int subArea = Zones.SubAreaOf(SessionContext.State.MapId);
            var zone = Zones.Of(subArea);
            if (!Farm.Current.Gauge.Enabled || zone == null || zone.Equipment.Count == 0)
            {
                await NotifyAsync(stream, "Cette zone n'a pas de jauge (aucun équipement garanti).", channel, accountId);
                return;
            }
            var profile = FarmProfiles.Of(GameState.CharacterId);
            var now = Farm.Clock();
            GaugeState state;
            lock (profile.Gate)
            {
                if (!profile.Gauges.TryGetValue(subArea, out state!)) profile.Gauges[subArea] = state = new GaugeState();
                ZoneGauge.Roll(state, zone.Level, now);
            }

            string text = (rest ?? "").Trim();
            string[] words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && (words[0].Equals("cible", StringComparison.OrdinalIgnoreCase)
                                     || words[0].Equals("target", StringComparison.OrdinalIgnoreCase)))
            {
                await GaugeTargetAsync(stream, zone, profile, state, words.Length > 1 ? words[1] : "", channel, accountId);
                return;
            }

            int threshold = ZoneGauge.Threshold(zone.Level, state.Completions);
            var reset = state.WindowStart + TimeSpan.FromHours(Farm.Current.Gauge.ResetHours) - now;
            var lines = zone.Equipment.Select((gid, i) => $"{i + 1}. {NameOfItem(gid)} ({gid})" + (gid == state.Target ? " ◄" : ""));
            await NotifyAsync(stream,
                $"Jauge de {Tides.SafeName(subArea)} (niv. {zone.Level}) : {state.Progress}/{threshold}" +
                $" — objets garantis ce cycle : {state.Completions}, retour au seuil de base dans {FormatSpan(reset)}. " +
                (state.Target != 0 ? $"Cible : {NameOfItem(state.Target)}." : "Aucune cible : .jauge cible <objet ou numéro>.") +
                (Tides.IsTideZone(subArea) ? " Marée en cours : jauge x2." : ""), channel, accountId);
            await NotifyAsync(stream, "Objets garantis : " + string.Join(" · ", lines), channel, accountId);
        }

        private static async Task GaugeTargetAsync(NetworkStream stream, Zones.Zone zone, FarmProfile profile, GaugeState state,
                                                   string query, int channel, long accountId)
        {
            query = query.Trim();
            if (query.Length == 0)
            {
                await NotifyAsync(stream, Usage(".jauge"), channel, accountId);
                return;
            }
            int chosen = 0;
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                chosen = zone.Equipment.Contains(number) ? number
                       : number >= 1 && number <= zone.Equipment.Count ? zone.Equipment[number - 1] : 0;
            if (chosen == 0)
            {
                string wanted = Folded(query);
                var names = zone.Equipment.ToDictionary(g => g, g => Folded(NameOfItem(g)));
                chosen = names.FirstOrDefault(kv => kv.Value == wanted).Key;
                if (chosen == 0) chosen = names.FirstOrDefault(kv => kv.Value.Contains(wanted)).Key;
            }
            if (chosen == 0)
            {
                await NotifyAsync(stream, $"« {query} » n'est pas dans la liste de cette zone (.jauge pour la voir).", channel, accountId);
                return;
            }
            int pay;
            lock (profile.Gate)
            {
                state.Target = chosen;
                pay = ZoneGauge.Claim(state, zone.Level, zone.Equipment);
            }
            FarmProfiles.Save(profile);
            await NotifyAsync(stream, $"Cible de la jauge : {NameOfItem(chosen)}.", channel, accountId);
            if (pay != 0)
            {
                await FarmEconomy.DeliverAsync(stream, pay);
                await RefreshPodsAsync(stream);
            }
        }

        // ─── .comptoir ──────────────────────────────────────────────────────────────────────

        private static async Task CounterAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            int subArea = Zones.SubAreaOf(SessionContext.State.MapId);
            var zone = Zones.Of(subArea);
            if (!Farm.Current.Counter.Enabled || zone == null)
            {
                await NotifyAsync(stream, "Pas de comptoir ici : il faut être dans une zone de monstres.", channel, accountId);
                return;
            }
            var profile = FarmProfiles.Of(GameState.CharacterId);
            var now = Farm.Clock();
            string word = (rest ?? "").Trim();
            if (word.Length == 0)
            {
                var offers = ZoneCounter.All.Select(b =>
                {
                    var left = ZoneCounter.Remaining(profile, subArea, b, now);
                    return $"{ZoneCounter.Label(b)} ({ZoneCounter.Effect(b)}) : {ZoneCounter.Price(b, zone.Level):N0} kamas" +
                           (left > TimeSpan.Zero ? $" — actif encore {FormatSpan(left)}" : "");
                });
                await NotifyAsync(stream, $"Comptoir de {Tides.SafeName(subArea)} (niv. {zone.Level}), {Farm.Current.Counter.DurationMinutes} min dans cette zone : " +
                                          string.Join(" · ", offers) + ". Acheter : .comptoir <buff>.", channel, accountId);
                return;
            }
            string? buff = ZoneCounter.Parse(word);
            if (buff == null)
            {
                await NotifyAsync(stream, Usage(".comptoir"), channel, accountId);
                return;
            }
            if (SessionContext.State.IsInFight)
            {
                await NotifyAsync(stream, "Le comptoir ne vend rien en combat.", channel, accountId);
                return;
            }
            int level = SessionContext.State.CharacterLevel;
            if (buff == ZoneCounter.Study && Tides.BoostsExperience(subArea, level))
            {
                await NotifyAsync(stream, "Une Marée booste déjà votre XP ici : Étude est refusée.", channel, accountId);
                return;
            }
            if (buff == ZoneCounter.Gauge && Tides.IsTideZone(subArea))
            {
                await NotifyAsync(stream, "La Marée double déjà la jauge ici : le buff Jauge est refusé.", channel, accountId);
                return;
            }
            if (buff == ZoneCounter.Gauge && zone.Equipment.Count == 0)
            {
                await NotifyAsync(stream, "Cette zone n'a pas de jauge.", channel, accountId);
                return;
            }
            long price = OrbePrice(ZoneCounter.Price(buff, zone.Level));
            if (OrbesHeld() < price)
            {
                await NotifyAsync(stream, $"{ZoneCounter.Label(buff)} coûte {price:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.", channel, accountId);
                return;
            }
            if (!await PayOrbesAsync(stream, -price)) return;
            var until = ZoneCounter.Extend(profile, subArea, buff, now);
            FarmProfiles.Save(profile);
            ActivityJournal.Current.Write("farm.counter", accountId > 0 ? accountId : SessionContext.Current.AccountId, GameState.CharacterId,
                new { buff, subArea, price });
            await NotifyAsync(stream, $"{ZoneCounter.Label(buff)} ({ZoneCounter.Effect(buff)}) pour {price:N0} {OrbeLabel} : actif dans " +
                                      $"{Tides.SafeName(subArea)} encore {FormatSpan(until - now)}.", channel, accountId);
        }

        // ─── .maree ─────────────────────────────────────────────────────────────────────────

        private static async Task TideAsync(NetworkStream stream, int channel, long accountId)
        {
            var phase = Tides.Now;
            string text = phase.Active
                ? $"Marée en cours ({FormatSpan(phase.Left)} restantes) : {Tides.Describe(phase.Cycle)}. +{Farm.Current.Tides.ExperiencePercent} % d'XP pour la tranche, kamas x2, jauge x2."
                : $"Pas de Marée en ce moment ; la prochaine dans {FormatSpan(phase.Left)} : {Tides.Describe(phase.Cycle + 1)}.";
            await NotifyAsync(stream, text, channel, accountId);
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes:D2}"
                : $"{Math.Max(0, (int)Math.Ceiling(span.TotalMinutes))} min";
        }
    }
}
