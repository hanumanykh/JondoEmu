using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The marketplaces of a server lived alone kept full and moving, in the simulated players'
    /// names: lots put on sale until each marketplace holds what a busy one does, at their
    /// reference worth (ReferencePrices) give or take; the bots' lots bought now and then, as other
    /// players would, so that what is on sale changes; and the real players' lots at a fair price
    /// bought within minutes, the kamas paid into their bank as any sale's.
    /// </summary>
    /// <remarks>
    /// The bots walking to the counters (Bots.SimulatedPlayer.Market) sold six lots now and then
    /// and bought little: the marketplaces stayed nearly empty and a player's lot waited for days.
    /// This works on the book itself, a tick a minute. content/market/market.json sets it.
    /// </remarks>
    public static class MarketMaker
    {
        public const string AuthoredFile = "market/market.json";

        internal sealed class Settings
        {
            public bool Enabled { get; set; } = true;
            /// <summary>How many different items each marketplace should have on sale.</summary>
            public int ItemsPerHouse { get; set; } = 500;
            /// <summary>The most lots put on sale in one marketplace on one tick, while it fills.</summary>
            public int ListPerTick { get; set; } = 60;
            /// <summary>A bot's lot is bought, on average, after this many minutes on sale.</summary>
            public int BotLotMinutes { get; set; } = 90;
            /// <summary>A real player's fair lot is bought, on average, after this many minutes.</summary>
            public int PlayerLotMinutes { get; set; } = 4;
            /// <summary>The dearest a player's lot may be, against its worth, and still be bought.</summary>
            public int FairPercent { get; set; } = 130;
            /// <summary>The houses kept: Astrub's resources, consumables and equipment.</summary>
            /// <summary>
            /// The houses kept: resources, consumables, equipment, runes, creatures. A marketplace is
            /// one for the whole world -- Bonta's, Brakmar's and Astrub's counters of a kind open
            /// the same book --, so these fill every city's.
            /// </summary>
            public List<int> Houses { get; set; } = new() { 1261, 1263, 1262, 1264, 1265 };
            /// <summary>Percent of the pieces of equipment put on sale that are exos (an AP, MP or range more).</summary>
            public int ExoPercent { get; set; } = 3;
            /// <summary>Percent that are perfect rolls (every characteristic at its best).</summary>
            public int PerfectPercent { get; set; } = 5;
        }

        private static readonly Random Dice = new();
        private static Settings? _settings;
        private static List<(long Character, long Account)>? _sellers;
        private static readonly Dictionary<int, List<(int Gid, int Type, int Level)>> _catalogue = new();
        private static int _ticking;

        internal static Settings Current => _settings ??= Read(Paths.ContentFile(AuthoredFile));

        internal static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Market] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        /// <summary>One tick: the marketplaces filled, the bots' lots turned over, the players' bought.</summary>
        public static async Task TickAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                var settings = Current;
                if (!settings.Enabled || !ServerSettings.Current.SimulatedPlayers) return;
                var now = DateTime.UtcNow;
                foreach (int id in settings.Houses)
                {
                    if (!Marketplaces.TryGet(id, out var house)) continue;
                    Fill(house, settings, now);
                    await TurnOverAsync(house, settings, now);
                    await BuyFromPlayersAsync(house, settings, now);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Market] The tick broke: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _ticking, 0);
            }
        }

        private static HashSet<long>? _botAccounts;

        private static bool IsBot(long account)
            => Bots.SimulatedPlayers.IsSimulated(account)
               || (_botAccounts ??= Sellers().Select(s => s.Account).ToHashSet()).Contains(account);

        /// <summary>The bots' characters, who the lots are put on sale by.</summary>
        private static List<(long Character, long Account)> Sellers()
        {
            if (_sellers != null) return _sellers;
            var sellers = new List<(long, long)>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, AccountId FROM Characters WHERE Name LIKE $prefix;";
                command.Parameters.AddWithValue("$prefix", Bots.SimulatedPlayers.NamePrefix + "%");
                using var reader = command.ExecuteReader();
                while (reader.Read()) sellers.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Market] The bots could not be read: {ex.Message}");
            }
            return _sellers = sellers;
        }

        /// <summary>
        /// What a marketplace sells, among the items with a worth worth listing: dropped resources
        /// and crafts, of a type the house takes, no higher than its level cap.
        /// </summary>
        private static List<(int Gid, int Type, int Level)> CatalogueOf(Marketplaces.House house)
        {
            if (_catalogue.TryGetValue(house.Id, out var known)) return known;
            var items = new List<(int, int, int)>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Type, json_extract(Data, '$.level') FROM ItemTemplates;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int gid = reader.GetInt32(0), type = reader.GetInt32(1);
                    int level = reader.IsDBNull(2) ? 1 : Convert.ToInt32(reader.GetValue(2));
                    if (!house.Accepts(type) || (house.MaxItemLevel > 0 && level > house.MaxItemLevel)) continue;
                    if (!ReferencePrices.IsTraded(gid)) continue;
                    items.Add((gid, type, level));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Market] The catalogue of marketplace {house.Id} could not be read: {ex.Message}");
            }
            Console.WriteLine($"[Market] Marketplace {house.Id}: {items.Count} items it may sell.");
            return _catalogue[house.Id] = items;
        }

        /// <summary>Lots put on sale until the house has its share of different items.</summary>
        private static void Fill(Marketplaces.House house, Settings settings, DateTime now)
        {
            var sellers = Sellers();
            var catalogue = CatalogueOf(house);
            if (sellers.Count == 0 || catalogue.Count == 0) return;
            var onSale = MarketplaceListings.CheapestLotsBy(house.Id, _ => true, now).Select(l => l.Gid).ToHashSet();
            int wanted = Math.Min(settings.ItemsPerHouse, catalogue.Count) - onSale.Count;
            for (int placed = 0; placed < Math.Min(wanted, settings.ListPerTick); placed++)
            {
                var (gid, type, _) = catalogue[Dice.Next(catalogue.Count)];
                if (onSale.Contains(gid)) continue;
                onSale.Add(gid);
                var template = Forgemagic.TemplateOf(gid);
                // Only what stacks goes in lots of ten or a hundred; equipment is sold one by one,
                // and in the equipment marketplace always, whatever its template says.
                bool stacks = template != null && Forgemagic.Stacks(template) && house.Id != 1262;
                // A resource in a lot or two of its sizes; a piece of equipment, one or two rolls of it.
                var sizes = stacks ? house.Lots.OrderBy(_ => Dice.Next()).Take(Dice.Next(1, 3)).ToList() : new List<int> { 1 };
                int copies = stacks ? 1 : Dice.Next(1, 3);
                foreach (int lot in sizes)
                {
                    for (int copy = 0; copy < copies; copy++)
                    {
                        var (character, account) = sellers[Dice.Next(sellers.Count)];
                        long price = Math.Max(1, ReferencePrices.Of(gid) * lot * Dice.Next(88, 121) / 100);
                        string effects;
                        if (template == null) effects = DatabaseManager.FreshEffectsOf(gid);
                        else if (stacks) effects = Forgemagic.Serialize(Forgemagic.Roll(template, Dice));
                        else
                        {
                            var (rolled, percent) = RollForSale(template, settings);
                            effects = Forgemagic.Serialize(rolled);
                            price = Math.Max(1, price * percent / 100);
                        }
                        MarketplaceListings.Add(house, character, account, gid, type, lot, effects, price, now);
                    }
                }
            }
        }

        /// <summary>The characteristics an exo may carry one more of, and how many times dearer it makes the piece.</summary>
        private static readonly (int Effect, long Factor)[] Exos = { (111, 25), (128, 12), (117, 6) };

        /// <summary>
        /// A piece of equipment as it goes on sale, and its price against the plain item's
        /// reference worth, in percent.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Stasified equipment (Managers.Stasis) goes on sale as it drops: at its max roll and a
        /// tier drawn from content/items/stasis.json's marketTierWeights -- mostly S1, some S2, rare
        /// S3, very rare S4 --, priced by Stasis.PriceFactor (S1 x1.25, then each tier three of the
        /// one below, what fusing up to it costs). Every piece is a max roll then, so the old
        /// «perfect» lot has nothing left to mean and PerfectPercent applies only to what does not
        /// stasify (pets, custom items). The exo stays: one lot in ExoPercent carries an AP, an MP
        /// or a range its template lacks, from six to twenty-five times dearer, as on the official
        /// marketplaces where an exo AP is the dearest thing a piece can carry.
        /// </para>
        /// </remarks>
        internal static (List<Equipment.ItemEffect> Effects, long Percent) RollForSale(Forgemagic.Template template, Settings settings)
        {
            int roll = Dice.Next(100);
            List<Equipment.ItemEffect> effects;
            double percent;
            if (Stasis.Applies(template))
            {
                int tier = Stasis.MarketTier(Dice);
                effects = Stasis.AtMax(template, tier);
                percent = 100 * Stasis.PriceFactor(tier);
            }
            else
            {
                bool perfect = roll < settings.PerfectPercent + settings.ExoPercent && Dice.Next(2) == 0;
                effects = Forgemagic.Roll(template, Dice, perfect ? 100 : 0);
                percent = perfect ? 250 : 100;
            }
            if (roll < settings.ExoPercent && template.Level >= 20)
            {
                var exo = Exos.Where(e => template.MaxOf(e.Effect) == null).OrderBy(_ => Dice.Next()).FirstOrDefault();
                if (exo.Effect != 0)
                {
                    // Before the label, which stays the last line.
                    int at = effects.FindIndex(e => e.Effect == Stasis.LabelEffect);
                    effects.Insert(at < 0 ? effects.Count : at, new Equipment.ItemEffect(exo.Effect, 1, 0, 0));
                    percent *= exo.Factor;
                }
            }
            return (effects, Math.Max(1, (long)Math.Round(percent)));
        }

        /// <summary>
        /// What a lot is worth to a buyer: the reference worth of its item times the lot, and for a
        /// stasified piece its tier's factor (Stasis.PriceFactor) -- the same as the market maker
        /// prices its own, so a player's S3 is not bought as if it were an S1.
        /// </summary>
        internal static long WorthOf(long reference, int lot, int tier)
            => Math.Max(1, Stasis.Worth(reference, tier) * Math.Max(1, lot));

        /// <summary>
        /// The bots' lots bought now and then by other players that are not there: the cheapest of
        /// an offer first, as a buyer takes it, so prices that undercut sell before dear ones.
        /// </summary>
        private static async Task TurnOverAsync(Marketplaces.House house, Settings settings, DateTime now)
        {
            double chance = 1.0 / Math.Max(1, settings.BotLotMinutes);
            var buyer = Sellers().Count > 0 ? Sellers()[Dice.Next(Sellers().Count)].Character : 0;
            foreach (var lot in MarketplaceListings.CheapestLotsBy(house.Id, IsBot, now))
            {
                if (Dice.NextDouble() >= chance) continue;
                var bought = MarketplaceListings.Buy(house, lot.Offer, lot.Lot, lot.Price, buyer, now, out _);
                if (bought != null) await MarketplaceHandler.OfferChangedAsync(house, bought.Value.Offer, added: false);
            }
        }

        /// <summary>
        /// A real player's lot at a fair price bought within minutes -- the cheaper against its
        /// worth, the sooner -- and paid into his bank, as any sale is.
        /// </summary>
        private static async Task BuyFromPlayersAsync(Marketplaces.House house, Settings settings, DateTime now)
        {
            var buyer = Sellers().Count > 0 ? Sellers()[Dice.Next(Sellers().Count)].Character : 0;
            foreach (var lot in MarketplaceListings.CheapestLotsBy(house.Id, account => !IsBot(account), now))
            {
                int tier = Stasis.TierInEffects(MarketplaceListings.EffectsOfOffer(lot.Offer));
                long worth = WorthOf(ReferencePrices.Of(lot.Gid), lot.Lot, tier);
                long percent = lot.Price * 100 / worth;
                if (percent > settings.FairPercent) continue;
                // A fair one in PlayerLotMinutes on average; a bargain twice as fast.
                double perMinute = (percent <= 80 ? 2.0 : 1.0) / Math.Max(1, settings.PlayerLotMinutes);
                if (Dice.NextDouble() >= perMinute) continue;
                var bought = MarketplaceListings.Buy(house, lot.Offer, lot.Lot, lot.Price, buyer, now, out _);
                if (bought == null) continue;
                await MarketplaceHandler.OfferChangedAsync(house, bought.Value.Offer, added: false);
                await MarketplaceHandler.PaySellerAsync(bought.Value.Listing);
                Console.WriteLine($"[Market] A player's lot sold: {lot.Gid} x{lot.Lot} for {lot.Price} ({percent}% of its worth).");
            }
        }
    }
}
