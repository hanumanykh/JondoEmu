using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The tiers and dungeons from the chat (Managers/Stasis/Tiers.cs): «.paliers» shows the tier
    /// and its gate, «.donjons» the week's free entries, «.cle» buys a dungeon key for kamas,
    /// «.hardcore» switches an evolved character's dungeons to hardcore.
    /// </summary>
    /// <remarks>
    /// Commands rather than windows because the 3.6 client has no Stasis Hub: its «Aperçu» tab,
    /// where Stasis lists the tier dungeons, and the dungeon sheet where keys are bought from afar
    /// are server-side here. The answers are French, as the Stasis rules; the usage lines are in the
    /// catalogue's three languages because every command must have one (AdminCommandTests).
    /// </remarks>
    public static partial class CommandHandler
    {
        private static async Task TiersCommandAsync(NetworkStream stream, string command, string rest, int channel, long accountId)
        {
            var session = SessionContext.Current;
            if (!session.HasCharacter) return;
            string text = command switch
            {
                ".paliers" => TiersText(session.State),
                ".donjons" => FreeEntriesText(session.State, DateTime.Now),
                ".cle" => await BuyKeyAsync(stream, session, rest),
                _ => HardcoreCommand(session.State, rest),
            };
            await NotifyAsync(stream, text, channel, accountId);
        }

        // ─── .paliers ───────────────────────────────────────────────────────────────────

        private static string TiersText(SessionState state)
        {
            var file = Tiers.Current;
            if (file.Tiers.Count == 0) return "Aucun palier n'est configuré sur ce serveur.";
            bool evolved = Ascension.IsEvolved(state.CharacterId);
            var done = Tiers.DoneDungeons(state.CharacterId);
            var parts = new List<string>();
            int gate = 0;
            foreach (var tier in file.Tiers.OrderBy(t => t.Level))
            {
                var left = Tiers.Remaining(tier, done);
                int count = tier.Dungeons.Count - left.Count;
                string line = $"Palier {tier.Level} : {count}/{tier.Dungeons.Count}";
                if (left.Count > 0 && left.Count <= tier.Dungeons.Count)
                    line += " — restants : " + string.Join(", ", left.Select(Tiers.DungeonName));
                parts.Add(line);
                if (gate == 0 && left.Count > 0 && tier.Level >= file.GateFromLevel && tier.Level < file.Tiers.Max(t => t.Level))
                    gate = tier.Level;
            }
            string head = $"Niveau {state.CharacterLevel}" + (evolved ? " (évolué : seuls les donjons faits en hardcore comptent)" : "") + ". ";
            string gateText = gate != 0
                ? $"Expérience bloquée au niveau {gate + 25} tant que le palier {gate} n'est pas terminé (l'XP au-delà est perdue). "
                : "Aucun blocage d'expérience. ";
            return head + gateText + string.Join(" | ", parts) + ".";
        }

        // ─── .donjons ───────────────────────────────────────────────────────────────────

        private static string FreeEntriesText(SessionState state, DateTime now)
        {
            int level = state.CharacterLevel;
            // The keyed dungeons the character can enter, nearest his level first.
            var dungeons = DungeonManager.All.Values
                .Where(d => d.BaseId == 0 && d.Rooms.Count > 0 && d.Required.Count > 0 && d.MinLevel <= level)
                .OrderBy(d => Math.Abs(Tiers.KeyLevel(d) - level)).ThenBy(d => d.Name)
                .ToList();
            if (dungeons.Count == 0) return "Aucun donjon à clé n'est accessible à votre niveau.";
            var free = dungeons.Where(d => DungeonKeyring.FreeEntryLeft(state.CharacterId, d.Id, now)).ToList();
            var shown = dungeons.Take(12).Select(d => (free.Contains(d) ? "✓ " : "✗ ") + d.Name);
            return $"Entrées gratuites cette semaine : {free.Count}/{dungeons.Count} donjons accessibles " +
                   $"(1 par donjon, utilisée avant les clés, retour le mardi {DungeonKeyring.NextReset(now):dd/MM} à 00h00). " +
                   "Les plus proches de votre niveau : " + string.Join(", ", shown) + ".";
        }

        // ─── .cle ───────────────────────────────────────────────────────────────────────

        private static async Task<string> BuyKeyAsync(NetworkStream stream, GameSession session, string rest)
        {
            if (rest.Trim().Length == 0) return Usage(".cle");
            var dungeon = Tiers.FindDungeon(rest);
            if (dungeon == null) return $"Aucun donjon à clé ne correspond à « {rest.Trim()} ».";
            int key = Tiers.KeyItem(dungeon);
            long price = Tiers.KeyPrice(dungeon);
            if (key == 0 || price < 0) return $"{dungeon.Name} ne demande pas de clé.";
            var state = session.State;
            if (state.Kamas < price)
                return $"La clé de {dungeon.Name} (tranche {Tiers.Current.BracketFor(Tiers.KeyLevel(dungeon))?.Label}) coûte {price:N0} kamas ; vous en avez {state.Kamas:N0}.";
            state.Kamas -= price;
            if (!await Equipment.GiveAsync(stream, key, 1))
            {
                state.Kamas += price;
                return "La clé n'a pas pu être ajoutée à votre inventaire ; rien n'a été payé.";
            }
            DatabaseManager.SaveCurrentCharacter();
            await session.SendAsync(ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(state.Kamas)));
            Console.WriteLine($"[Tiers] {state.CharacterName} buys the key of {dungeon.Name} ({key}) for {price} kamas.");
            return $"Clé de {dungeon.Name} achetée pour {price:N0} kamas.";
        }

        // ─── .hardcore ──────────────────────────────────────────────────────────────────

        private static string HardcoreCommand(SessionState state, string rest)
        {
            string word = rest.Trim().ToLowerInvariant();
            bool evolved = Ascension.IsEvolved(state.CharacterId);
            if (word != "on" && word != "off")
            {
                string now = evolved && Tiers.Backend.HardcoreWanted(state.CharacterId) ? "activé" : "désactivé";
                return Usage(".hardcore") + $" Actuellement : {now}.";
            }
            if (!evolved) return "Le mode hardcore est réservé aux personnages évolués.";
            Tiers.Backend.SetHardcoreWanted(state.CharacterId, word == "on");
            var h = Tiers.Current.Hardcore;
            return word == "on"
                ? $"Mode hardcore activé : dans vos donjons (vous en chef), monstres PV x{h.Life}, dégâts x{h.Damage}, drop x{h.Drop}. Seuls ces donjons valident vos paliers."
                : "Mode hardcore désactivé.";
        }
    }
}
