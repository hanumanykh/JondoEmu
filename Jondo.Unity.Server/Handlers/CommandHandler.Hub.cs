using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Sanctuary hub from the chat and the F9 window: «.sanctuaire» and «.retour» (Managers.Sanctuary),
    /// «.marchand» (the Stasis Merchant), «.fabriquer» and «.devis» (Managers.Fabrication).
    /// </summary>
    /// <remarks>
    /// Every cost and price is in the Orbes' currency (OrbePrice / OrbesHeld / PayOrbesAsync of
    /// CommandHandler.Stasis.cs: Jondo Coins in coin mode). Each command builds its answer as text
    /// (<see cref="HubAsync"/>) so the chat line and the control route (ControlApiHub.cs) share
    /// every rule. French texts, as the Stasis rules; the usage lines are in the three languages.
    /// </remarks>
    public static partial class CommandHandler
    {
        internal static readonly HashSet<string> HubCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            ".sanctuaire", ".retour", ".marchand", ".fabriquer", ".devis",
        };

        private static async Task HubCommandAsync(NetworkStream stream, string command, string rest, int channel, long accountId)
        {
            var session = SessionContext.Current;
            if (!session.HasCharacter) return;
            await NotifyAsync(stream, await HubAsync(session, command, rest), channel, accountId);
        }

        /// <summary>One hub command's answer; the action done when it is one.</summary>
        internal static async Task<string> HubAsync(GameSession session, string command, string rest)
        {
            try
            {
                return command.ToLowerInvariant() switch
                {
                    ".sanctuaire" => await GoToSanctuaryAsync(session),
                    ".retour" => await BackFromSanctuaryAsync(session),
                    ".marchand" => await MerchantAsync(session, rest),
                    ".fabriquer" => await FabricateAsync(session, rest),
                    _ => QuoteText(session, rest),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hub] {command} failed: {ex}");
                return "La commande a échoué : " + ex.Message;
            }
        }

        // ─── .sanctuaire / .retour ──────────────────────────────────────────────────────

        private static async Task<string> GoToSanctuaryAsync(GameSession session)
        {
            var state = session.State;
            var s = Sanctuary.Current;
            switch (Sanctuary.Refusal(state.IsInFight, state.MapId, m => DungeonManager.OfRoom(m) != null))
            {
                case "combat": return "Impossible d'aller au Sanctuaire en combat.";
                case "deja": return $"Vous êtes déjà au {s.Name}. « .retour » vous ramène d'où vous venez.";
                case "donjon": return "Impossible d'aller au Sanctuaire depuis un donjon.";
            }
            long fromMap = state.MapId;
            int fromCell = state.CellId;
            int landed = await TeleportHandler.ToMapAsync(session, s.MapId, s.Cell);
            if (landed < 0) return "Le Sanctuaire est inaccessible pour le moment.";
            Sanctuary.RecordDeparture(state.CharacterId, fromMap, fromCell);
            return $"Bienvenue au {s.Name} : le Marchand de Stasis, le Raffineur, le comptoir de zone et la banque sont ici. " +
                   "« .retour » vous ramène où vous étiez.";
        }

        private static async Task<string> BackFromSanctuaryAsync(GameSession session)
        {
            var state = session.State;
            if (state.IsInFight) return "Impossible de revenir en combat.";
            if (!Sanctuary.IsSanctuary(state.MapId)) return "« .retour » ne s'utilise qu'au Sanctuaire.";
            var back = Sanctuary.ReturnOf(state.CharacterId);
            if (back == null) return "Aucun point de départ en mémoire : utilisez un zaap ou une potion de rappel.";
            int landed = await TeleportHandler.ToMapAsync(session, back.Value.Map, back.Value.Cell);
            if (landed < 0) return "Votre point de départ est inaccessible ; essayez un zaap.";
            Sanctuary.TakeReturn(state.CharacterId);
            return "Vous voilà revenu à votre point de départ.";
        }

        // ─── .marchand ──────────────────────────────────────────────────────────────────

        private static async Task<string> MerchantAsync(GameSession session, string rest)
        {
            string text = (rest ?? "").Trim();
            int space = text.IndexOf(' ');
            string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
            string tail = space < 0 ? "" : text[(space + 1)..].Trim();
            var state = session.State;

            if (word == "" ) return MerchantHelp(state);
            if (word is "conseil" or "conseille" or "conseillé")
                return RecommendedText(state, tail);
            if (word is "consommables" or "conso")
                return "Consommables : " + string.Join(", ", Sanctuary.Current.Merchant.Consumables
                    .Select(c => $"{NameOfItem(c.Item)} ({OrbePrice(StasisMerchant.PriceOf(c.Item)):N0})")) + $" {OrbeLabel}.";
            if (word is "cle" or "clé")
                return await BuyKeyInOrbesAsync(session, tail);
            if (word == "acheter")
            {
                if (Sanctuary.Current.MerchantOnlyInSanctuary && !Sanctuary.IsSanctuary(state.MapId)) return "Le Marchand de Stasis vous attend au Sanctuaire (« .sanctuaire »). Les clés s'achètent à distance : « .marchand cle <donjon> ».";
                if (state.IsInFight) return "Pas en combat.";
                var (query, quantity) = SplitQuantity(tail);
                if (query.Length == 0) return Usage(".marchand");
                if (query.Equals("conseil", StringComparison.OrdinalIgnoreCase))
                    return await BuyRecommendedAsync(session);
                return await BuyAsync(session, query, quantity);
            }
            if (word == "catalogue" || int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                int level = int.TryParse(word == "catalogue" ? tail : word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : state.CharacterLevel;
                return CatalogueText(level);
            }
            return Usage(".marchand");
        }

        private static string MerchantHelp(SessionState state)
        {
            var (min, max) = StasisMerchant.BandOf(state.CharacterLevel);
            return $"Marchand de Stasis : équipement non stasifié (jets entre moyen et max) au prix de référence, en {OrbeLabel}. " +
                   $".marchand conseil [terre|feu|eau|air] : le stuff conseillé niveau {state.CharacterLevel}. " +
                   $".marchand catalogue [niveau] : la tranche ({min}-{max} pour vous). .marchand consommables. " +
                   ".marchand acheter <objet|id|conseil> [quantité] (au Sanctuaire). .marchand cle <donjon> (partout). " +
                   $"Vous avez {OrbesHeld():N0} {OrbeLabel}.";
        }

        private static List<Outfitter.Piece> RecommendedFor(SessionState state, string element)
            => StasisMerchant.Recommended(state.CharacterLevel, StasisMerchant.ElementOf(state.Breed, element), Outfitter.Craftable.Value);

        private static string RecommendedText(SessionState state, string element)
        {
            var pieces = RecommendedFor(state, element);
            if (pieces.Count == 0) return "Rien à conseiller pour votre niveau.";
            long total = pieces.Sum(p => OrbePrice(StasisMerchant.PriceOf(p.Gid)));
            return $"Conseillé (niveau {state.CharacterLevel}, {StasisMerchant.ElementOf(state.Breed, element)}) : " +
                   string.Join(", ", pieces.Select(p => $"{NameOfItem(p.Gid)} niv. {p.Level} ({OrbePrice(StasisMerchant.PriceOf(p.Gid)):N0})")) +
                   $". Total {total:N0} {OrbeLabel} — « .marchand acheter conseil » prend tout.";
        }

        private static string CatalogueText(int level)
        {
            var (min, max) = StasisMerchant.BandOf(level);
            var pieces = StasisMerchant.Catalogue(Outfitter.Craftable.Value, min, max);
            if (pieces.Count == 0) return $"Rien en stock pour la tranche {min}-{max}.";
            int limit = Math.Max(1, Sanctuary.Current.Merchant.CatalogueLimit);
            var shown = pieces.OrderByDescending(p => p.Level).Take(limit);
            return $"Catalogue {min}-{max} ({pieces.Count} pièces" + (pieces.Count > limit ? $", les {limit} plus hautes" : "") + ") : " +
                   string.Join(", ", shown.Select(p => $"{NameOfItem(p.Gid)} [{p.Gid}] niv. {p.Level} ({OrbePrice(StasisMerchant.PriceOf(p.Gid)):N0})")) +
                   $" {OrbeLabel}.";
        }

        private static async Task<string> BuyAsync(GameSession session, string query, int quantity)
        {
            int gid = FindSoldItem(query);
            if (gid == 0) return $"Le Marchand ne vend pas « {query} ».";
            quantity = Math.Clamp(quantity, 1, StasisMerchant.IsConsumable(gid) ? 100 : 10);
            long unit = OrbePrice(StasisMerchant.PriceOf(gid));
            long cost = unit * quantity;
            if (OrbesHeld() < cost) return $"{quantity} x {NameOfItem(gid)} coûte {cost:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.";
            var stream = session.Stream!;
            if (!await PayOrbesAsync(stream, -cost)) return "Le paiement a échoué ; rien n'a été acheté.";
            int given = await GiveSoldAsync(stream, gid, quantity);
            if (given < quantity) await PayOrbesAsync(stream, unit * (quantity - given));
            if (given == 0) return "L'objet n'a pas pu être ajouté à votre inventaire ; vous êtes remboursé.";
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("stasis.merchant", session.AccountId, session.State.CharacterId, new { gid, quantity = given, cost = unit * given });
            return $"Acheté : {given} x {NameOfItem(gid)} pour {unit * given:N0} {OrbeLabel}.";
        }

        // ─── The Merchant's shop window ─────────────────────────────────────────────────

        /// <summary>Whether this NPC on this map is the Sanctuary's Merchant selling in Jondo Coins.</summary>
        internal static bool IsCoinMerchant(int template, long mapId)
            => JondoCoin.IsOrbe && Sanctuary.RoleOf(template, mapId) == "marchand";

        /// <summary>What the Merchant's window lists for this character: his level band's pieces, then the consumables.</summary>
        internal static List<int> MerchantWindowCatalogue(SessionState state)
        {
            var (min, max) = StasisMerchant.BandOf(state.CharacterLevel);
            var gids = StasisMerchant.Catalogue(Outfitter.Craftable.Value, min, max)
                                     .OrderBy(p => p.Level).Select(p => p.Gid).ToList();
            foreach (var c in Sanctuary.Current.Merchant.Consumables)
                if (c.Item > 0 && !gids.Contains(c.Item)) gids.Add(c.Item);
            return gids;
        }

        /// <summary>The window's prices: each item at the Merchant's price in Jondo Coins, the coin as the currency.</summary>
        internal static TokenShops.Shop MerchantWindowPricing(IEnumerable<int> gids)
        {
            var shop = new TokenShops.Shop { TokenGid = JondoCoin.TemplateId };
            foreach (int gid in gids) shop.Prices[gid] = Math.Max(1, OrbePrice(StasisMerchant.PriceOf(gid)));
            return shop;
        }

        /// <summary>A purchase from the Merchant's window: the same path as «.marchand acheter», told in the chat.</summary>
        internal static async Task MerchantWindowBuyAsync(GameSession session, int gid, int quantity)
        {
            if (session.Stream == null) return;
            if (!MerchantWindowCatalogue(session.State).Contains(gid))
            {
                await NotifyAsync(session.Stream, "Le Marchand ne vend pas cet objet à votre niveau.", 0, 0);
                return;
            }
            string text = await BuyAsync(session, gid.ToString(CultureInfo.InvariantCulture), quantity);
            await NotifyAsync(session.Stream, text, 0, 0);
        }

        private static async Task<string> BuyRecommendedAsync(GameSession session)
        {
            var pieces = RecommendedFor(session.State, "");
            if (pieces.Count == 0) return "Rien à conseiller pour votre niveau.";
            long total = pieces.Sum(p => OrbePrice(StasisMerchant.PriceOf(p.Gid)));
            if (OrbesHeld() < total) return $"Le stuff conseillé coûte {total:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.";
            var stream = session.Stream!;
            if (!await PayOrbesAsync(stream, -total)) return "Le paiement a échoué ; rien n'a été acheté.";
            long refund = 0;
            int bought = 0;
            foreach (var piece in pieces)
            {
                if (await GiveSoldAsync(stream, piece.Gid, 1) == 1) bought++;
                else refund += OrbePrice(StasisMerchant.PriceOf(piece.Gid));
            }
            if (refund > 0) await PayOrbesAsync(stream, refund);
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("stasis.merchant", session.AccountId, session.State.CharacterId, new { recommended = bought, cost = total - refund });
            return $"Stuff conseillé acheté : {bought} pièce(s) pour {total - refund:N0} {OrbeLabel}.";
        }

        private static readonly Random MerchantDice = new();

        /// <summary>Gives what the Merchant sold: a piece rolled between middle and max, one by one; a consumable stacked.</summary>
        private static async Task<int> GiveSoldAsync(NetworkStream stream, int gid, int quantity)
        {
            if (StasisMerchant.IsConsumable(gid))
                return await WorkshopHandler.GiveAsync(stream, gid, quantity) ? quantity : 0;
            var template = Forgemagic.TemplateOf(gid);
            if (template == null) return 0;
            int given = 0;
            for (int i = 0; i < quantity; i++)
            {
                List<Equipment.ItemEffect> effects;
                lock (MerchantDice) effects = StasisMerchant.Roll(template, MerchantDice);
                if (await WorkshopHandler.GiveExactAsync(stream, gid, effects) != 0) given++;
            }
            return given;
        }

        private static async Task<string> BuyKeyInOrbesAsync(GameSession session, string rest)
        {
            if (rest.Length == 0) return Usage(".marchand");
            if (session.State.IsInFight) return "Pas en combat.";
            var dungeon = Tiers.FindDungeon(rest);
            if (dungeon == null) return $"Aucun donjon à clé ne correspond à « {rest} ».";
            int key = Tiers.KeyItem(dungeon);
            long kamas = Tiers.KeyPrice(dungeon);
            if (key == 0 || kamas < 0) return $"{dungeon.Name} ne demande pas de clé.";
            long price = OrbePrice(kamas);
            if (OrbesHeld() < price) return $"La clé de {dungeon.Name} coûte {price:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.";
            var stream = session.Stream!;
            if (!await PayOrbesAsync(stream, -price)) return "Le paiement a échoué.";
            if (!await Equipment.GiveAsync(stream, key, 1))
            {
                await PayOrbesAsync(stream, price);
                return "La clé n'a pas pu être ajoutée à votre inventaire ; vous êtes remboursé.";
            }
            return $"Clé de {dungeon.Name} achetée pour {price:N0} {OrbeLabel}.";
        }

        // ─── .fabriquer ─────────────────────────────────────────────────────────────────

        private static async Task<string> FabricateAsync(GameSession session, string rest)
        {
            if (session.State.IsInFight) return "La fabrication se fait hors combat.";
            var (query, askedTier) = SplitTierWord(rest);
            if (query.Length == 0) return Usage(".fabriquer");
            int gid = FindEquipment(query);
            if (gid == 0) return $"Aucun équipement ne correspond à « {query} ».";
            var recipe = Fabrication.RecipeFor(gid, askedTier >= 2 ? 2 : 1);
            if (recipe == null)
                return askedTier >= 2 ? $"{NameOfItem(gid)} n'a pas de recette légendaire (S2) : niveau {Fabrication.Current.Legendary.FromLevel} minimum."
                                      : $"{NameOfItem(gid)} n'a pas de recette de fabrication Stasis.";
            var quote = QuoteOf(recipe);
            if (!quote.Ready) return "Fabrication impossible. " + QuoteLines(quote);

            var template = Forgemagic.TemplateOf(gid)!;
            var stream = session.Stream!;
            var plain = PlainPieces(gid).OrderBy(i => i.Uid).First();
            if (!await WorkshopHandler.ConsumeAsync(session, plain, 1, inWorkshop: false)) return "L'objet de base n'est plus là.";
            foreach (var (item, need) in recipe.Ingredients)
                if (!await TakeFromBagAsync(session, item, need))
                {
                    Console.WriteLine($"[Fabrication] {item} x{need} vanished while {session.State.CharacterId} made {gid}.");
                    return "Un ingrédient a disparu pendant la fabrication.";
                }
            if (!await PayOrbesAsync(stream, -quote.Cost)) return "Le paiement a échoué.";
            long uid = await WorkshopHandler.GiveExactAsync(stream, gid, Fabrication.Result(template, recipe.Tier));
            if (uid == 0)
            {
                Console.WriteLine($"[Fabrication] {gid} could not be created for {session.State.CharacterId}; its ingredients are gone.");
                return "La fabrication a échoué : l'objet n'a pas pu être créé.";
            }
            Stasis.Mark(uid, recipe.Tier);
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("stasis.fabrication", session.AccountId, session.State.CharacterId, new { gid, tier = recipe.Tier, cost = quote.Cost, uid });
            return $"Fabriqué : {NameOfItem(gid)} {Stasis.Name(recipe.Tier)} au jet parfait, pour {quote.Cost:N0} {OrbeLabel}.";
        }

        // ─── .devis ─────────────────────────────────────────────────────────────────────

        private static string QuoteText(GameSession session, string rest)
        {
            var (query, askedTier) = SplitTierWord(rest);
            if (query.Length == 0) return QuoteListText();
            int gid = FindEquipment(query);
            if (gid == 0) return $"Aucun équipement ne correspond à « {query} ».";
            var recipe = Fabrication.RecipeFor(gid, askedTier >= 2 ? 2 : 1);
            if (recipe == null) return $"{NameOfItem(gid)} n'a pas de recette de fabrication Stasis{(askedTier >= 2 ? " légendaire" : "")}.";
            var quote = QuoteOf(recipe);
            return $"Devis {NameOfItem(gid)} {Stasis.Name(recipe.Tier)} (jet parfait) : " + QuoteLines(quote) +
                   (quote.Ready ? $" Tout y est : « .fabriquer {gid}{(recipe.Tier >= 2 ? " S2" : "")} »." : "");
        }

        private static string QuoteListText()
        {
            var quotes = QuoteList(10);
            if (quotes.Count == 0) return "Aucune recette en vue : il faut l'objet de base non stasifié ou ses ingrédients. « .devis <objet> » pour une recette précise.";
            return "Recettes Stasis (réalisables d'abord) : " + string.Join(" | ", quotes.Select(q =>
                $"{(q.Ready ? "✓" : "✗")} {NameOfItem(q.Recipe.Result)} niv. {q.Recipe.Level} ({q.Cost:N0}" +
                (q.Ready ? ")" : $", {q.MissingKinds} manque(s))"))) + ".";
        }

        /// <summary>The quotes of the recipes in view, the makeable first, then the nearest, then the highest.</summary>
        internal static List<Fabrication.Quote> QuoteList(int count)
        {
            var bag = BagCounts();
            var plain = Equipment.All.Where(i => i.Position == Equipment.Bag && i.Quantity > 0 && Stasis.TierOf(i.Uid, i.Effects) == 0)
                                     .Select(i => i.Template).Distinct().ToList();
            return Fabrication.Candidates(plain, bag.Keys)
                .Select(g => Fabrication.RecipeFor(g)).Where(r => r != null).Select(r => QuoteOf(r!))
                .OrderByDescending(q => q.Ready).ThenBy(q => q.MissingKinds).ThenByDescending(q => q.Recipe.Level)
                .Take(count).ToList();
        }

        private static Fabrication.Quote QuoteOf(Fabrication.Recipe recipe)
            => Fabrication.QuoteFor(recipe, BagCounts(), PlainPieces(recipe.Result).Count, OrbePrice(recipe.Cost), OrbesHeld());

        private static string QuoteLines(Fabrication.Quote quote)
        {
            int gid = quote.Recipe.Result;
            string base_ = quote.BaseOwned
                ? "objet de base non stasifié : ✓"
                : "objet de base non stasifié : ✗" + (StasisMerchant.SellsEquipment(gid)
                    ? $" (au Marchand : {OrbePrice(StasisMerchant.PriceOf(gid)):N0} {OrbeLabel}, « .marchand acheter {gid} »)" : "");
            var lines = quote.Lines.Select(l => $"{NameOfItem(l.Item)} {Math.Min(l.Have, l.Need)}/{l.Need}" + (l.Missing > 0 ? " ✗" : ""));
            return base_ + " ; " + string.Join(", ", lines) + $" ; coût {quote.Cost:N0} {OrbeLabel} (vous : {quote.Orbes:N0}).";
        }

        // ─── The bag ────────────────────────────────────────────────────────────────────

        private static Dictionary<int, long> BagCounts()
            => Equipment.All.Where(i => i.Position == Equipment.Bag && i.Quantity > 0)
                            .GroupBy(i => i.Template).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Quantity));

        private static List<Equipment.Item> PlainPieces(int gid)
            => Equipment.All.Where(i => i.Template == gid && i.Position == Equipment.Bag && i.Quantity > 0
                                        && Stasis.TierOf(i.Uid, i.Effects) == 0).ToList();

        private static async Task<bool> TakeFromBagAsync(GameSession session, int gid, int quantity)
        {
            int left = quantity;
            foreach (var stack in Equipment.All.Where(i => i.Template == gid && i.Position == Equipment.Bag && i.Quantity > 0)
                                               .Where(i => Stasis.TierOf(i.Uid, i.Effects) == 0).ToList())
            {
                int take = Math.Min(stack.Quantity, left);
                if (!await WorkshopHandler.ConsumeAsync(session, stack, take, inWorkshop: false)) return false;
                left -= take;
                if (left == 0) return true;
            }
            return left == 0;
        }

        // ─── Finding items by name ──────────────────────────────────────────────────────

        private static Dictionary<int, string>? _equipmentNames;

        /// <summary>Every equipment template's folded name, read once.</summary>
        private static Dictionary<int, string> EquipmentNames()
        {
            if (_equipmentNames != null) return _equipmentNames;
            var names = new Dictionary<int, string>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT t.Id, t.Type, tr.Text FROM ItemTemplates t JOIN Translations tr " +
                                      "ON tr.Key = CAST(json_extract(t.Data, '$.nameId') AS TEXT);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (!reader.IsDBNull(2) && RandomLoot.IsEquipmentType(reader.GetInt32(1)))
                        names[reader.GetInt32(0)] = Folded(reader.GetString(2));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hub] The equipment names could not be read: {ex.Message}");
            }
            return _equipmentNames = names;
        }

        /// <summary>An equipment template by id or name: an exact name first, then the shortest that contains it.</summary>
        private static int FindEquipment(string query)
        {
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return Forgemagic.TemplateOf(id) != null ? id : 0;
            string wanted = Folded(query);
            var names = EquipmentNames();
            var exact = names.Where(kv => kv.Value == wanted).Select(kv => kv.Key).OrderBy(g => g).ToList();
            if (exact.Count > 0) return exact[0];
            return names.Where(kv => kv.Value.Contains(wanted)).OrderBy(kv => kv.Value.Length).ThenBy(kv => kv.Key)
                        .Select(kv => kv.Key).FirstOrDefault();
        }

        private static int FindSoldItem(string query)
        {
            var consumables = Sanctuary.Current.Merchant.Consumables.Select(c => c.Item).ToList();
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return consumables.Contains(id) || StasisMerchant.SellsEquipment(id) ? id : 0;
            string wanted = Folded(query);
            int consumable = consumables.FirstOrDefault(c => Folded(NameOfItem(c)).Contains(wanted));
            if (consumable != 0) return consumable;
            int gid = FindEquipment(query);
            return gid != 0 && StasisMerchant.SellsEquipment(gid) ? gid : 0;
        }

        /// <summary>«Coiffe 3» → («Coiffe», 3); no number → 1.</summary>
        internal static (string Query, int Quantity) SplitQuantity(string rest)
        {
            string text = (rest ?? "").Trim();
            int space = text.LastIndexOf(' ');
            if (space > 0 && int.TryParse(text[(space + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                return (text[..space].Trim(), n);
            return (text, 1);
        }

        // ─── The Sanctuary's NPCs ───────────────────────────────────────────────────────

        /// <summary>
        /// Talking to one of the Sanctuary's own NPCs: the Merchant, the Raffineur and the counter
        /// answer with their command's text. False for anybody else (the banker opens the bank as
        /// every banker does).
        /// </summary>
        internal static async Task<bool> SanctuaryTalkAsync(GameSession session, int template, long mapId)
        {
            string? role = Sanctuary.RoleOf(template, mapId);
            if (role == null || session.Stream == null) return false;
            string? text = role switch
            {
                "marchand" when IsCoinMerchant(template, mapId) => null,
                "marchand" => MerchantHelp(session.State) + " " + RecommendedText(session.State, ""),
                "raffineur" => RaffineurText(),
                "comptoir" => "Comptoir de zone : 4 buffs de 30 min valables dans la sous-zone d'achat (Densité, Recrue, Étude, Jauge). " +
                              "Achetez-les sur place avec « .comptoir », hors combat. Ici, au Sanctuaire : « .fabriquer » et « .devis » pour la fabrication Stasis.",
                _ => null,
            };
            if (text == null) return false;
            await NotifyAsync(session.Stream, text, 0, 0);
            return true;
        }

        private static string RaffineurText()
        {
            var surplus = Equipment.All.Where(i => i.Position == Equipment.Bag && i.Quantity > 0)
                                       .Select(i => (Item: i, Tier: Stasis.TierOf(i.Uid, i.Effects))).Where(p => p.Tier > 0)
                                       .Take(8).ToList();
            string head = "Le Raffineur rachète vos objets stasifiés en trop contre une petite part du prix du Marchand : « .raffiner <objet> [S1..S4] ». " +
                          "La fusion de 3 objets identiques : « .fusion <objet> ».";
            if (surplus.Count == 0) return head + " Vous n'avez aucun objet stasifié dans le sac.";
            return head + " Dans votre sac : " + string.Join(", ", surplus.Select(p =>
                $"{NameOfItem(p.Item.Template)} {Stasis.Name(p.Tier)} ({OrbePrice(Stasis.BuybackPrice(ReferencePrices.Of(p.Item.Template), p.Tier)):N0})")) +
                $" {OrbeLabel}.";
        }

        // ─── The F9 window ──────────────────────────────────────────────────────────────

        /// <summary>The Sanctuary tab's state: where one is, the Orbes, the Conseillé list and the recipes in view.</summary>
        internal static object HubView(GameSession session, string text)
        {
            var state = session.State;
            bool inside = Sanctuary.IsSanctuary(state.MapId);
            return new
            {
                text,
                currency = OrbeLabel,
                orbes = OrbesHeld(),
                inSanctuary = inside,
                canReturn = inside && Sanctuary.ReturnOf(state.CharacterId) != null,
                sanctuary = Sanctuary.Current.Name,
                recommended = RecommendedFor(state, "").Select(p => new
                {
                    gid = p.Gid, name = NameOfItem(p.Gid), level = p.Level, price = OrbePrice(StasisMerchant.PriceOf(p.Gid)),
                }).ToList(),
                // The Marchand's consumables, for the window's grid (bought with «.marchand acheter <gid>»).
                consumables = Sanctuary.Current.Merchant.Consumables.Select(c => new
                {
                    gid = c.Item, name = NameOfItem(c.Item), price = OrbePrice(StasisMerchant.PriceOf(c.Item)),
                }).ToList(),
                coinItem = JondoCoin.IsOrbe ? JondoCoin.TemplateId : 0,
                recipes = QuoteList(8).Select(q => new
                {
                    gid = q.Recipe.Result, name = NameOfItem(q.Recipe.Result), level = q.Recipe.Level, tier = q.Recipe.Tier,
                    cost = q.Cost, ready = q.Ready, missing = q.MissingKinds,
                }).ToList(),
            };
        }
    }
}
