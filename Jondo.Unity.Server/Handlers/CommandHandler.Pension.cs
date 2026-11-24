using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Pension (Managers/Stasis/Pension) and the monster cards (MonsterCards) from the chat
    /// -- .pension, .caisse, .cartes -- and the same actions for the JondoFix window's control
    /// routes (/api/pension/state, /api/pension/do).
    /// </summary>
    /// <remarks>
    /// Each action takes the session and returns its French text; the chat shows it, the window
    /// shows it under the state it redraws. Costs follow the Stasis rule of this server: Orbes are
    /// Jondo Coins (OrbePrice, PayOrbesAsync); what Stasis sells for ogrines costs event tokens.
    /// The won-fight hook (<see cref="AfterWonFightPensionAsync"/>) gives the study room its
    /// share, hatches nothing by itself, and drops crates and card packs at the files' rates.
    /// </remarks>
    public static partial class CommandHandler
    {
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ─── Chat ───────────────────────────────────────────────────────────────────────────

        private static async Task PensionChatAsync(NetworkStream stream, string rest, int channel, long accountId)
            => await NotifyAsync(stream, await PensionCommandAsync(SessionContext.Current, rest), channel, accountId);

        private static async Task CrateChatAsync(NetworkStream stream, string rest, int channel, long accountId)
            => await NotifyAsync(stream, await CrateCommandAsync(SessionContext.Current, rest), channel, accountId);

        private static async Task CardsChatAsync(NetworkStream stream, string rest, int channel, long accountId)
            => await NotifyAsync(stream, await CardsCommandAsync(SessionContext.Current, rest), channel, accountId);

        private static (string Verb, string Args) SplitVerb(string rest)
        {
            string text = (rest ?? "").Trim();
            int space = text.IndexOf(' ');
            return space < 0 ? (Folded(text), "") : (Folded(text[..space]), text[(space + 1)..].Trim());
        }

        // ─── .pension ───────────────────────────────────────────────────────────────────────

        internal static async Task<string> PensionCommandAsync(GameSession session, string rest)
        {
            if (!session.HasCharacter || session.AccountId <= 0) return "Aucun personnage en jeu.";
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            var (verb, args) = SplitVerb(rest);
            string result;
            switch (verb)
            {
                case "":
                case "voir":
                    return await PensionTextAsync(session);
                case "incuber":
                    lock (account.Gate) result = Pension.Incubate(account, content, EggNumber(args), Now());
                    break;
                case "annuler":
                case "sortir":
                    lock (account.Gate) result = Pension.Withdraw(account, EggNumber(args));
                    break;
                case "accelerer":
                {
                    var egg = Pension.EggOf(account, EggNumber(args));
                    if (egg == null || egg.Slot == 0) return Pension.RefusalText(egg == null ? "oeuf-inconnu" : "pas-incube");
                    if (!EventTokens.TrySpend(session.CharacterId, content.SpeedUp.Tokens)) return Pension.RefusalText("jetons");
                    lock (account.Gate) result = Pension.SpeedUp(account, content, egg.Id, Now());
                    if (result != "ok") EventTokens.Add(session.CharacterId, content.SpeedUp.Tokens);
                    break;
                }
                case "place":
                    if (account.BoughtSlots >= content.Incubation.BuyableSlots) return Pension.RefusalText("places-max");
                    if (!EventTokens.TrySpend(session.CharacterId, content.Incubation.SlotTokens)) return Pension.RefusalText("jetons");
                    lock (account.Gate) account.BoughtSlots++;
                    result = "ok";
                    break;
                case "eclore":
                {
                    string hatched = await HatchReadyAsync(session);
                    return hatched.Length > 0 ? hatched : "Aucun œuf prêt à éclore.";
                }
                case "etude":
                {
                    if (args.Length == 0) return await PensionTextAsync(session);
                    var animal = AnimalOf(args);
                    if (animal == null) return Pension.RefusalText("animal-inconnu");
                    lock (account.Gate)
                    {
                        if (account.Study.Contains(animal.Uid)) result = "deja-etude";
                        else if (account.Study.Count >= content.Study.Places) result = "etude-pleine";
                        else { account.Study.Add(animal.Uid); result = "ok"; }
                    }
                    break;
                }
                case "retirer":
                {
                    var animal = AnimalOf(args);
                    long uid = animal?.Uid ?? (long.TryParse(args, out long n) ? n : 0);
                    lock (account.Gate) result = account.Study.Remove(uid) ? "ok" : "pas-etude";
                    break;
                }
                case "accoupler":
                    result = await BreedAsync(session, args);
                    if (result.StartsWith("Œuf", StringComparison.Ordinal)) return result;
                    break;
                default:
                    return T(Uso[".pension"]);
            }
            if (result != "ok") return Pension.RefusalText(result);
            Pension.Save(account);
            return "C'est fait. " + await PensionTextAsync(session);
        }

        private static int EggNumber(string args)
            => int.TryParse(args.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;

        /// <summary>The pets and mounts of the character's inventory, worn or in the bag.</summary>
        private static List<Equipment.Item> Animals()
            => Equipment.All.Where(i => i.Quantity > 0 && (Pets.IsPet(i.Template) || Rides.IsRide(i.Template))).OrderBy(i => i.Uid).ToList();

        private static Equipment.Item? AnimalOf(string query)
        {
            var animals = Animals();
            if (long.TryParse(query, out long uid)) return animals.FirstOrDefault(a => a.Uid == uid);
            string wanted = Folded(query);
            if (wanted.Length == 0) return null;
            return animals.FirstOrDefault(a => Folded(ItemName(a.Template)) == wanted)
                ?? animals.FirstOrDefault(a => Folded(ItemName(a.Template)).Contains(wanted));
        }

        private static int AnimalLevel(Equipment.Item item)
            => Rides.IsRide(item.Template) ? Rides.LevelOf(item.Effects) : Pets.LevelOf(item.Effects);

        private static async Task<string> BreedAsync(GameSession session, string args)
        {
            var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2) return T(Uso[".pension"]);
            var a = AnimalOf(words[0]);
            var b = AnimalOf(words[1]);
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            var ra = a == null ? null : Breeding.RideByItem(a.Template);
            var rb = b == null ? null : Breeding.RideByItem(b.Template);
            string check;
            lock (account.Gate) check = Pension.CanBreed(account, content, (a?.Uid ?? 0, ra), (b?.Uid ?? -1, rb));
            if (check != "ok") return check;
            long cost = OrbePrice(Pension.BreedCost(ra!.Generation, content));
            if (OrbesHeld() < cost) return $"L'accouplement coûte {cost:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.";
            if (session.Stream != null && !await PayOrbesAsync(session.Stream, -cost)) return "orbes";
            PensionEgg egg;
            lock (account.Gate) egg = Pension.Breed(account, content, (a!.Uid, ra), (b!.Uid, rb!));
            Pension.Save(account);
            ActivityJournal.Current.Write("pension.breed", session.AccountId, session.CharacterId,
                new { a = a.Uid, b = b.Uid, species = ra.Species, generation = egg.Generation, cost });
            return $"Œuf #{egg.Id} obtenu : {Pension.Describe(egg, content)} (pour {cost:N0} {OrbeLabel}). Incubez-le avec .pension incuber {egg.Id}.";
        }

        /// <summary>The ready eggs hatched into real items; the text of what came out, or "".</summary>
        internal static async Task<string> HatchReadyAsync(GameSession session)
        {
            if (session.Stream == null) return "";
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            List<PensionEgg> ready;
            lock (account.Gate) ready = Pension.Ready(account, Now());
            var lines = new List<string>();
            foreach (var egg in ready)
            {
                int gid;
                List<Equipment.ItemEffect> effects;
                string what;
                if (egg.Kind == Pension.KindMount)
                {
                    var ride = Pension.DrawRide(egg.Species, egg.Generation, Random.Shared);
                    if (ride == null) continue;
                    gid = ride.Item;
                    effects = Rides.Fresh(gid, Random.Shared);
                    what = $"{ItemName(gid)} (gén. {egg.Generation})";
                }
                else
                {
                    string rarity = Pension.HatchRarity(egg.Rarity, content, Random.Shared);
                    gid = Pension.DrawPet(rarity, content, Random.Shared);
                    if (gid == 0) continue;
                    effects = Pets.Fresh(gid);
                    what = $"{ItemName(gid)} ({Pension.RarityLabel(rarity)}" + (rarity != egg.Rarity ? ", rareté augmentée !" : "") + ")";
                }
                long uid = await WorkshopHandler.GiveExactAsync(session.Stream, gid, effects);
                if (uid == 0) continue;
                lock (account.Gate) account.Eggs.Remove(egg);
                Pension.Save(account);
                ActivityJournal.Current.Write("pension.hatch", session.AccountId, session.CharacterId, new { egg = egg.Id, gid, uid });
                Pension.RaiseHatched(session.CharacterId);
                lines.Add($"L'œuf #{egg.Id} éclot : {what}.");
            }
            return string.Join(" ", lines);
        }

        /// <summary>The account's level-100 place and the dons «Pension» the character has earned.</summary>
        private static string GrantPensionGifts(GameSession session)
        {
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            List<PensionEgg> gifts;
            bool changed = false;
            lock (account.Gate)
            {
                if (!account.Level100 && session.State.CharacterLevel >= content.Incubation.Level100)
                {
                    account.Level100 = true;
                    changed = true;
                }
                gifts = Pension.GiftsDue(account, content, session.CharacterId, session.State.CharacterLevel, Random.Shared);
            }
            if (gifts.Count > 0 || changed) Pension.Save(account);
            return gifts.Count == 0 ? "" : "Don Pension : " + string.Join(", ", gifts.Select(g => $"#{g.Id} {Pension.Describe(g, content)}")) + ". ";
        }

        private static async Task<string> PensionTextAsync(GameSession session)
        {
            string gifts = GrantPensionGifts(session);
            string hatched = await HatchReadyAsync(session);
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            var sb = new StringBuilder(gifts);
            if (hatched.Length > 0) sb.Append(hatched).Append(' ');
            long now = Now();
            lock (account.Gate)
            {
                int slots = Pension.Slots(account, content);
                sb.Append($"Pension — incubation {account.Eggs.Count(e => e.Slot != 0)}/{slots} places. ");
                foreach (var egg in account.Eggs.OrderBy(e => e.Id))
                {
                    sb.Append($"#{egg.Id} {Pension.Describe(egg, content)}{(egg.Gift ? " (don)" : "")}");
                    if (egg.Slot != 0) sb.Append($" [place {egg.Slot}, {Remaining(egg.ReadyAt - now)}]");
                    sb.Append("; ");
                }
                if (account.Eggs.Count == 0) sb.Append("Aucun œuf. ");
                sb.Append($"Caisses : {account.Crates}. Salle d'étude {account.Study.Count}/{content.Study.Places} : ");
                var animals = Animals();
                sb.Append(account.Study.Count == 0 ? "vide" : string.Join(", ", account.Study.Select(uid =>
                    animals.FirstOrDefault(a => a.Uid == uid) is { } item ? $"{ItemName(item.Template)} niv. {AnimalLevel(item)} ({uid})" : $"{uid} (absent)")));
                sb.Append('.');
            }
            return sb.ToString();
        }

        private static string Remaining(long seconds)
            => seconds <= 0 ? "prêt" : seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60} min" : $"{Math.Max(1, (seconds + 59) / 60)} min";

        // ─── .caisse ────────────────────────────────────────────────────────────────────────

        internal static async Task<string> CrateCommandAsync(GameSession session, string rest)
        {
            if (!session.HasCharacter || session.AccountId <= 0) return "Aucun personnage en jeu.";
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            var (verb, _) = SplitVerb(rest);
            if (verb == "acheter")
            {
                long price = OrbePrice(content.Crate.PriceOrbes);
                if (OrbesHeld() < price) return $"Une caisse d'œufs coûte {price:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.";
                if (session.Stream == null || !await PayOrbesAsync(session.Stream, -price)) return Pension.RefusalText("orbes");
                lock (account.Gate) account.Crates++;
            }
            else if (verb.Length > 0 && verb != "ouvrir") return T(Uso[".caisse"]);
            PensionEgg egg;
            lock (account.Gate)
            {
                if (account.Crates <= 0) return Pension.RefusalText("aucune-caisse") + $" (.caisse acheter : {OrbePrice(content.Crate.PriceOrbes):N0} {OrbeLabel})";
                account.Crates--;
                egg = Pension.FromCrate(content, Pension.SpeciesIds(content), Random.Shared);
                egg.Id = account.NextEggId();
                account.Eggs.Add(egg);
            }
            Pension.Save(account);
            return $"La caisse contient : #{egg.Id} {Pension.Describe(egg, content)}. Incubez-le avec .pension incuber {egg.Id}.";
        }

        // ─── .cartes ────────────────────────────────────────────────────────────────────────

        internal static async Task<string> CardsCommandAsync(GameSession session, string rest)
        {
            if (!session.HasCharacter || session.AccountId <= 0) return "Aucun personnage en jeu.";
            var content = MonsterCards.Content;
            var binder = MonsterCards.Of(session.AccountId);
            var (verb, args) = SplitVerb(rest);
            switch (verb)
            {
                case "":
                case "classeur":
                    return BinderText(binder, args);
                case "ouvrir":
                {
                    List<int> drawn;
                    lock (binder.Gate)
                    {
                        if (binder.Packs.Count == 0) return MonsterCards.RefusalText("aucun-paquet");
                        int level = binder.Packs[0];
                        binder.Packs.RemoveAt(0);
                        drawn = MonsterCards.DrawPack(level, MonsterCards.Families, content, Random.Shared);
                        MonsterCards.AddLoose(binder, drawn);
                    }
                    MonsterCards.Save(binder);
                    return "Paquet ouvert : " + string.Join(", ", drawn.Select(id => MonsterCards.CardOf(id)?.Name ?? id.ToString())) +
                           $". Collez-les avec .cartes coller <carte> ou .cartes coller tout. Paquets restants : {binder.Packs.Count}.";
                }
                case "coller":
                {
                    var glued = new List<string>();
                    var completed = new List<string>();
                    lock (binder.Gate)
                    {
                        IEnumerable<int> ids = Folded(args) == "tout"
                            ? binder.Loose.Keys.Where(id => !binder.Glued.Contains(id)).ToList()
                            : CardIdOf(args, binder) is int one ? new[] { one } : Array.Empty<int>();
                        string last = "carte-absente";
                        foreach (int id in ids)
                        {
                            last = MonsterCards.Glue(binder, id);
                            if (last != "ok") continue;
                            var card = MonsterCards.CardOf(id);
                            glued.Add(card?.Name ?? id.ToString());
                            if (card != null && MonsterCards.FamilyOf(card.Family) is { } family && MonsterCards.IsComplete(binder, family))
                                completed.Add(family.Name);
                        }
                        if (glued.Count == 0) return MonsterCards.RefusalText(last);
                    }
                    MonsterCards.Save(binder);
                    string text = "Collée(s) : " + string.Join(", ", glued) + ".";
                    if (completed.Count > 0)
                    {
                        text += " Famille complète : " + string.Join(", ", completed) + " ! Le bonus s'applique à tous vos personnages.";
                        await CharacteristicsMessages.Live.SendAsync(session);
                    }
                    return text;
                }
                case "vendre":
                {
                    var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    int count = 1;
                    if (words.Count > 1 && int.TryParse(words[^1], out int n)) { count = Math.Max(1, n); words.RemoveAt(words.Count - 1); }
                    long price;
                    string name;
                    lock (binder.Gate)
                    {
                        if (CardIdOf(string.Join(' ', words), binder) is not int id || MonsterCards.CardOf(id) is not { } card)
                            return MonsterCards.RefusalText("carte-inconnue");
                        count = Math.Min(count, MonsterCards.Duplicates(binder, id));
                        if (count <= 0) return MonsterCards.RefusalText("aucun-doublon");
                        MonsterCards.Take(binder, id, count);
                        price = OrbePrice(MonsterCards.SellPrice(card, content)) * count;
                        name = card.Name;
                    }
                    MonsterCards.Save(binder);
                    if (session.Stream != null) await PayOrbesAsync(session.Stream, price);
                    return $"{count} doublon(s) de {name} vendu(s) pour {price:N0} {OrbeLabel}.";
                }
                case "acheter":
                    if (!EventTokens.TrySpend(session.CharacterId, content.PackTokens)) return MonsterCards.RefusalText("jetons");
                    lock (binder.Gate) binder.Packs.Add(session.State.CharacterLevel);
                    MonsterCards.Save(binder);
                    return $"Paquet acheté ({content.PackTokens} jetons Event). Ouvrez-le avec .cartes ouvrir.";
                default:
                    return T(Uso[".cartes"]);
            }
        }

        /// <summary>A card by monster id or name: those in hand first, then the catalogue.</summary>
        private static int? CardIdOf(string query, CardAccount binder)
        {
            string wanted = Folded(query);
            if (wanted.Length == 0) return null;
            if (int.TryParse(wanted, out int id)) return id;
            var cards = MonsterCards.Families.SelectMany(f => f.Monsters).ToList();
            var held = cards.Where(c => binder.Loose.ContainsKey(c.Id)).ToList();
            foreach (var pool in new[] { held, cards })
            {
                var hit = pool.FirstOrDefault(c => Folded(c.Name) == wanted) ?? pool.FirstOrDefault(c => Folded(c.Name).Contains(wanted));
                if (hit != null) return hit.Id;
            }
            return null;
        }

        private static string BinderText(CardAccount binder, string filter)
        {
            var content = MonsterCards.Content;
            var sb = new StringBuilder();
            lock (binder.Gate)
            {
                var families = MonsterCards.Families;
                int complete = families.Count(f => MonsterCards.IsComplete(binder, f));
                sb.Append($"Classeur : {binder.Glued.Count} carte(s) collée(s), {complete}/{families.Count} famille(s) complète(s), " +
                          $"{binder.Packs.Count} paquet(s), {binder.Loose.Values.Sum()} carte(s) en main. ");
                string wanted = Folded(filter);
                var shown = families.Where(f => wanted.Length > 0 ? Folded(f.Name).Contains(wanted)
                                                                  : f.Monsters.Any(m => binder.Glued.Contains(m.Id) || binder.Loose.ContainsKey(m.Id)));
                foreach (var family in shown.Take(15))
                {
                    var (g, t) = MonsterCards.Completion(binder, family);
                    var bonus = MonsterCards.BonusOf(family, content);
                    sb.Append($"{family.Name} {g}/{t} ({g * 100 / Math.Max(1, t)} %)" + (bonus != null ? $" bonus +{bonus.Value} {bonus.Stat}" : ""));
                    if (wanted.Length > 0)
                        sb.Append(" : " + string.Join(", ", family.Monsters.Select(m => (binder.Glued.Contains(m.Id) ? "✔" : binder.Loose.ContainsKey(m.Id) ? "+" : "·") + m.Name)));
                    sb.Append("; ");
                }
                if (binder.Loose.Count > 0)
                    sb.Append("En main : " + string.Join(", ", binder.Loose.Select(kv => $"{MonsterCards.CardOf(kv.Key)?.Name ?? kv.Key.ToString()} x{kv.Value}")) + ".");
            }
            return sb.ToString();
        }

        // ─── The won fight ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// After a won fight (FightHandler, next to the zone gauge): the study room's share of the
        /// experience, the worn animal's bonus, the dons «Pension», and the crate and pack drops.
        /// </summary>
        internal static async Task AfterWonFightPensionAsync(NetworkStream stream, FightInstance fight, long experience)
        {
            try
            {
                var session = SessionContext.Current;
                if (!session.HasCharacter || session.AccountId <= 0) return;
                var content = Pension.Content;
                var account = Pension.Of(session.AccountId);
                var notes = new List<string>();
                string gifts = GrantPensionGifts(session);
                if (gifts.Length > 0) notes.Add(gifts.Trim());

                if (experience > 0)
                {
                    List<long> studied;
                    lock (account.Gate) studied = account.Study.ToList();
                    int level = session.State.CharacterLevel;
                    foreach (var animal in Animals())
                    {
                        bool mount = Rides.IsRide(animal.Template);
                        long gain = 0;
                        if (studied.Contains(animal.Uid)) gain += Pension.StudyExperience(experience, AnimalLevel(animal), level, mount, content);
                        if (animal.Position == Mounts.Slot) gain += Pension.EquippedExperience(experience, mount, content);
                        if (gain > 0) await GiveAnimalExperienceAsync(stream, session, animal, gain);
                    }
                }

                if (Random.Shared.NextDouble() < content.Crate.DropChance)
                {
                    lock (account.Gate) account.Crates++;
                    Pension.Save(account);
                    notes.Add("Vous trouvez une caisse d'œufs ! Ouvrez-la avec .caisse.");
                }
                var cards = MonsterCards.Content;
                if (Random.Shared.NextDouble() < cards.PackDropChance)
                {
                    var beaten = Managers.Economy.FarmEconomy.Beaten(fight);
                    int packLevel = beaten.Count > 0 ? (int)beaten.Average(m => m.Level) : session.State.CharacterLevel;
                    var binder = MonsterCards.Of(session.AccountId);
                    lock (binder.Gate) binder.Packs.Add(packLevel);
                    MonsterCards.Save(binder);
                    notes.Add("Vous trouvez un paquet de cartes de monstres ! Ouvrez-le avec .cartes ouvrir.");
                }
                foreach (string note in notes)
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(note)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pension] After fight #{fight.FightId}: {ex.Message}");
            }
        }

        /// <summary>Experience into a pet or a mount item: its level and characteristics follow, the client is told.</summary>
        private static async Task GiveAnimalExperienceAsync(NetworkStream stream, GameSession session, Equipment.Item animal, long gain)
        {
            List<Equipment.ItemEffect> effects;
            int before, after;
            if (Rides.IsRide(animal.Template))
            {
                var normal = Rides.Normalized(animal.Template, animal.Effects, Random.Shared);
                before = Rides.LevelOf(normal);
                effects = Rides.WithExperience(animal.Template, normal, gain);
                after = Rides.LevelOf(effects);
            }
            else
            {
                if (LegendaryPets.IsLegendary(animal)) return;
                effects = Pets.Fed(animal.Template, animal.Effects, gain, out before, out after);
            }
            if (!DatabaseManager.UpdateCharacterItem(session.CharacterId, animal.Uid, animal.Quantity, Forgemagic.Serialize(effects))) return;
            animal.Effects.Clear();
            animal.Effects.AddRange(effects);
            bool worn = Equipment.IsWorn(animal.Position);
            if (worn) Equipment.RememberWorn(animal.Uid, animal.Position, animal.Effects);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Iuj, AlterationProtocol.BuildSlotItemChanged(animal)));
            if (worn && after != before) await CharacteristicsMessages.Live.SendAsync(session);
        }

        // ─── The window's view ──────────────────────────────────────────────────────────────

        /// <summary>The pension and the binder as the JondoFix window draws them.</summary>
        internal static object PensionView(GameSession session, string message = "")
        {
            var content = Pension.Content;
            var account = Pension.Of(session.AccountId);
            var binder = MonsterCards.Of(session.AccountId);
            long now = Now();
            var animals = Animals();
            object pension, cards;
            lock (account.Gate)
            {
                pension = new
                {
                    slots = Pension.Slots(account, content),
                    crates = account.Crates,
                    cratePrice = OrbePrice(content.Crate.PriceOrbes),
                    currency = OrbeLabel,
                    canBuySlot = account.BoughtSlots < content.Incubation.BuyableSlots,
                    slotTokens = content.Incubation.SlotTokens,
                    speedUpTokens = content.SpeedUp.Tokens,
                    tokens = EventTokens.Balance(session.CharacterId),
                    studyPlaces = content.Study.Places,
                    eggs = account.Eggs.OrderBy(e => e.Id).Select(e => new
                    {
                        id = e.Id, name = Pension.Describe(e, content), gift = e.Gift, slot = e.Slot,
                        left = e.Slot == 0 ? "" : Remaining(e.ReadyAt - now), ready = e.Slot != 0 && e.ReadyAt <= now,
                    }).ToList(),
                    animals = animals.Select(a => new
                    {
                        uid = a.Uid, name = ItemName(a.Template), level = AnimalLevel(a), mount = Rides.IsRide(a.Template),
                        generation = Breeding.RideByItem(a.Template)?.Generation ?? 0,
                        litters = Breeding.RideByItem(a.Template) is { } r ? Pension.LittersLeft(account, a.Uid, r.Generation, content) : 0,
                        studied = account.Study.Contains(a.Uid), worn = a.Position == Mounts.Slot,
                    }).ToList(),
                };
            }
            lock (binder.Gate)
            {
                cards = new
                {
                    packs = binder.Packs.Count,
                    packTokens = MonsterCards.Content.PackTokens,
                    families = MonsterCards.Families
                        .Where(f => f.Monsters.Any(m => binder.Glued.Contains(m.Id) || binder.Loose.ContainsKey(m.Id)))
                        .Select(f =>
                        {
                            var (g, t) = MonsterCards.Completion(binder, f);
                            var bonus = MonsterCards.BonusOf(f, MonsterCards.Content);
                            return new { name = f.Name, glued = g, total = t, bonus = bonus == null ? "" : $"+{bonus.Value} {bonus.Stat}" };
                        }).ToList(),
                    loose = binder.Loose.Select(kv => new
                    {
                        id = kv.Key, name = MonsterCards.CardOf(kv.Key)?.Name ?? kv.Key.ToString(), count = kv.Value,
                        glued = binder.Glued.Contains(kv.Key),
                    }).ToList(),
                };
            }
            return new { message, pension, cards };
        }
    }
}
