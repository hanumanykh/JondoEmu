using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The players' side of stasification (Managers.Stasis): .stasis explains it, .fusion fuses
    /// three identical pieces of the bag into one of the next tier, .raffiner sells a surplus piece
    /// to the raffineur.
    /// </summary>
    /// <remarks>
    /// Commands and not an NPC's dialogue because the client has no window for a fusion: the
    /// Stasis server's own altar is a server-side script as well. The texts are French, the
    /// language the owner plays in; the usage lines stay in the catalogue's three languages
    /// because every command must have one (AdminCommandTests). Only what is in the bag counts --
    /// a worn piece is never fused nor sold from under its wearer.
    /// </remarks>
    public static partial class CommandHandler
    {
        // ─── .stasis ────────────────────────────────────────────────────────────────────────

        private static async Task StasisAsync(NetworkStream stream, int channel, long accountId)
        {
            var s = Stasis.Current;
            bool evolved = Stasis.IsEvolved(GameState.CharacterId);
            string text =
                "Stasification : tout équipement qui tombe arrive au jet maximum avec un palier. " +
                $"S1 = max x{s.Multipliers.GetValueOrDefault(1)}, S2 = x{s.Multipliers.GetValueOrDefault(2)}, " +
                $"S3 = x{s.Multipliers.GetValueOrDefault(3)}, S4 = x{s.Multipliers.GetValueOrDefault(4)} " +
                "(jamais les PA, PM, portée, critique, invocations ni résistances). " +
                (evolved
                    ? "Personnage évolué : vos butins sont S1 à 66 %, S2 à 30 %, S3 à 3 %, S4 à 1 %. "
                    : "Personnage non évolué : vos butins sont tous S1 ; S3 et S4 ne s'équipent qu'une fois évolué. ") +
                ".fusion <objet> [S1|S2|S3] : 3 objets identiques (même objet, même palier) donnent 1 objet du palier suivant au jet max, contre des kamas (orbes) ; les exos sont perdus de S2 à S3. " +
                ".raffiner <objet> [S1..S4] : le raffineur rachète un objet en trop.";
            await NotifyAsync(stream, text, channel, accountId);
        }

        // ─── .fusion ────────────────────────────────────────────────────────────────────────

        private static async Task FusionAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            var (query, askedTier) = SplitTierWord(rest);
            if (query.Length == 0)
            {
                await NotifyAsync(stream, Usage(".fusion"), channel, accountId);
                return;
            }
            var pieces = StasifiedInBag(query);
            if (pieces.Count == 0)
            {
                await NotifyAsync(stream, $"Aucun objet stasifié « {query} » dans votre sac.", channel, accountId);
                return;
            }

            // Three of one template and one tier: the asked tier, or the lowest that has three.
            var group = pieces.GroupBy(p => (p.Item.Template, p.Tier))
                              .Where(g => g.Key.Tier < Stasis.MaxTier && (askedTier == 0 || g.Key.Tier == askedTier))
                              .Where(g => g.Count() >= Stasis.FusionCount)
                              .OrderBy(g => g.Key.Tier).ThenBy(g => g.Key.Template).FirstOrDefault();
            if (group == null)
            {
                string have = string.Join(", ", pieces.GroupBy(p => p.Tier).OrderBy(g => g.Key)
                                                      .Select(g => $"{g.Count()} {Stasis.Name(g.Key)}"));
                await NotifyAsync(stream, $"Il faut {Stasis.FusionCount} objets identiques du même palier (S1 à S3) ; vous avez : {have}.",
                                  channel, accountId);
                return;
            }

            var (gid, tier) = group.Key;
            var template = Forgemagic.TemplateOf(gid);
            if (template == null) return;
            long cost = OrbePrice(Stasis.FusionCost(template.Level, tier + 1));
            if (OrbesHeld() < cost)
            {
                await NotifyAsync(stream, $"La fusion en {Stasis.Name(tier + 1)} coûte {cost:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.",
                                  channel, accountId);
                return;
            }

            var chosen = group.OrderBy(p => p.Item.Uid).Take(Stasis.FusionCount).ToList();
            var fused = Stasis.Fused(template, tier, chosen.Select(p => (IReadOnlyList<Equipment.ItemEffect>)p.Item.Effects.ToList()));
            if (fused == null) return;

            var session = SessionContext.Current;
            foreach (var piece in chosen)
            {
                if (!await WorkshopHandler.ConsumeAsync(session, piece.Item, 1, inWorkshop: false))
                {
                    await NotifyAsync(stream, "La fusion a échoué : un des objets n'est plus là.", channel, accountId);
                    return;
                }
            }
            long uid = await WorkshopHandler.GiveExactAsync(stream, gid, fused);
            if (uid == 0)
            {
                Console.WriteLine($"[Stasis] The fused {gid} could not be created for {GameState.CharacterId}; its three pieces are gone.");
                await NotifyAsync(stream, "La fusion a échoué : l'objet n'a pas pu être créé.", channel, accountId);
                return;
            }
            Stasis.Mark(uid, tier + 1);
            await PayOrbesAsync(stream, -cost);
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("stasis.fusion", accountId > 0 ? accountId : session.AccountId, GameState.CharacterId,
                new { gid, from = tier, to = tier + 1, cost, uid });
            await NotifyAsync(stream, $"Fusion réussie : {NameOfItem(gid)} {Stasis.Name(tier + 1)} pour {cost:N0} {OrbeLabel}." +
                                      (tier + 1 == 3 ? " Les exos ont été effacés." : ""), channel, accountId);
        }

        // ─── .raffiner ──────────────────────────────────────────────────────────────────────

        private static async Task RaffinerAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            var (query, askedTier) = SplitTierWord(rest);
            if (query.Length == 0)
            {
                await NotifyAsync(stream, Usage(".raffiner"), channel, accountId);
                return;
            }
            // The lowest tier first unless one is named: the surplus is what is sold.
            var piece = StasifiedInBag(query).Where(p => askedTier == 0 || p.Tier == askedTier)
                                             .OrderBy(p => p.Tier).ThenBy(p => p.Item.Uid).FirstOrDefault();
            if (piece.Item == null)
            {
                await NotifyAsync(stream, $"Aucun objet stasifié « {query} »{(askedTier > 0 ? " " + Stasis.Name(askedTier) : "")} dans votre sac.",
                                  channel, accountId);
                return;
            }
            long price = OrbePrice(Stasis.BuybackPrice(ReferencePrices.Of(piece.Item.Template), piece.Tier));
            var session = SessionContext.Current;
            if (!await WorkshopHandler.ConsumeAsync(session, piece.Item, 1, inWorkshop: false)) return;
            await PayOrbesAsync(stream, price);
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("stasis.raffineur", accountId > 0 ? accountId : session.AccountId, GameState.CharacterId,
                new { gid = piece.Item.Template, tier = piece.Tier, price });
            await NotifyAsync(stream, $"Le raffineur rachète {NameOfItem(piece.Item.Template)} {Stasis.Name(piece.Tier)} pour {price:N0} {OrbeLabel}.",
                              channel, accountId);
        }

        // ─── Shared ─────────────────────────────────────────────────────────────────────────

        /// <summary>Kamas given (or taken, negative), saved and shown, as .kamas does.</summary>
        /// <summary>What the Stasis-like costs and gains are counted in: Jondo Coins in coin mode, else kamas.</summary>
        internal static string OrbeLabel => JondoCoin.IsOrbe ? "Jondo Coins" : "kamas";

        /// <summary>A kamas-calibrated cost or gain in the Orbes' currency.</summary>
        internal static long OrbePrice(long kamas) => JondoCoin.IsOrbe ? JondoCoin.FromKamas(kamas) : kamas;

        /// <summary>How many Orbes the character holds: the coins in his bag, or his kamas.</summary>
        internal static long OrbesHeld()
            => JondoCoin.IsOrbe
                ? Equipment.All.Where(i => i.Template == JondoCoin.TemplateId && i.Position == Equipment.Bag).Sum(i => (long)i.Quantity)
                : GameState.Kamas;

        /// <summary>
        /// Pays (positive) or takes (negative) Orbes: coins into or out of the bag, or kamas.
        /// False when there were not enough coins to take.
        /// </summary>
        internal static async Task<bool> PayOrbesAsync(NetworkStream stream, long amount)
        {
            if (!JondoCoin.IsOrbe) { await PayAsync(stream, amount); return true; }
            if (amount == 0) return true;
            if (amount > 0)
                return await WorkshopHandler.GiveAsync(stream, JondoCoin.TemplateId, (int)Math.Min(int.MaxValue, amount));
            long left = -amount;
            if (OrbesHeld() < left) return false;
            var session = SessionContext.Current;
            foreach (var stack in Equipment.All.Where(i => i.Template == JondoCoin.TemplateId && i.Position == Equipment.Bag).ToList())
            {
                int take = (int)Math.Min(stack.Quantity, left);
                if (take <= 0) continue;
                if (!await WorkshopHandler.ConsumeAsync(session, stack, take, inWorkshop: false)) return false;
                left -= take;
                if (left == 0) break;
            }
            return left == 0;
        }

        private static async Task PayAsync(NetworkStream stream, long amount)
        {
            GameState.Kamas = Math.Max(0, GameState.Kamas + amount);
            DatabaseManager.SaveCurrentCharacter();
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
        }

        /// <summary>«Coiffe du Bouftou S2» → («Coiffe du Bouftou», 2); no tier word → 0.</summary>
        internal static (string Query, int Tier) SplitTierWord(string rest)
        {
            string text = (rest ?? "").Trim();
            int space = text.LastIndexOf(' ');
            string last = space < 0 ? text : text[(space + 1)..];
            if (last.Length == 2 && (last[0] == 'S' || last[0] == 's') && last[1] >= '1' && last[1] <= '4')
                return (space < 0 ? "" : text[..space].Trim(), last[1] - '0');
            return (text, 0);
        }

        /// <summary>
        /// The stasified pieces of the bag the player means: by uid or template id when he types a
        /// number, otherwise by name, accents and case aside -- an exact name first, then a part.
        /// </summary>
        private static List<(Equipment.Item Item, int Tier)> StasifiedInBag(string query)
        {
            var bag = Equipment.All.Where(i => i.Position == Equipment.Bag && i.Quantity > 0)
                                   .Select(i => (Item: i, Tier: Stasis.TierOf(i.Uid, i.Effects)))
                                   .Where(p => p.Tier > 0).ToList();
            if (long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            {
                var byUid = bag.FirstOrDefault(p => p.Item.Uid == number);
                int gid = byUid.Item != null ? byUid.Item.Template : (int)number;
                return bag.Where(p => p.Item.Template == gid).ToList();
            }
            string wanted = Folded(query);
            var names = bag.Select(p => p.Item.Template).Distinct().ToDictionary(g => g, g => Folded(NameOfItem(g)));
            var exact = names.Where(kv => kv.Value == wanted).Select(kv => kv.Key).ToHashSet();
            var matched = exact.Count > 0 ? exact : names.Where(kv => kv.Value.Contains(wanted)).Select(kv => kv.Key).ToHashSet();
            return bag.Where(p => matched.Contains(p.Item.Template)).ToList();
        }

        private static string Folded(string text)
        {
            var normalized = (text ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString().Trim();
        }

        /// <summary>An item's name, as the game's Translations table has it; its id when there is none.</summary>
        private static string NameOfItem(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT json_extract(Data, '$.nameId') FROM ItemTemplates WHERE Id = $gid;";
                command.Parameters.AddWithValue("$gid", gid);
                if (command.ExecuteScalar() is not { } key || key is DBNull) return gid.ToString(CultureInfo.InvariantCulture);
                var translation = connection.CreateCommand();
                translation.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                translation.Parameters.AddWithValue("$key", Convert.ToInt64(key).ToString(CultureInfo.InvariantCulture));
                return GameText.Of(Convert.ToInt64(key), translation.ExecuteScalar() as string) is { Length: > 0 } name ? name : gid.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return gid.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
