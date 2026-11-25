using System;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The players' side of the Stasis events (Managers.StasisEvents), the Jetons Event shop
    /// (Managers.EventTokens) and the Fissure (Managers.Fissure): «.event», «.jetons» and «.fissure».
    /// </summary>
    /// <remarks>
    /// Commands rather than the spec's Hub NPCs (Gardien de la Fissure, the Hub's event board): the
    /// Hub is a Stasis map the Dofus client does not have. Every player may type them; only opening
    /// or closing an event by hand («.event &lt;nom&gt;», «.event stop») asks for an administrator,
    /// checked here against the account's role as the dispatcher checks it. The texts are French,
    /// the language the owner plays in; the usage lines are in the catalogue's three languages.
    /// </remarks>
    public static partial class CommandHandler
    {
        // ─── .event ─────────────────────────────────────────────────────────────────────────

        private static async Task EventAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            string word = FirstWordOf(rest).ToLowerInvariant();
            long who = accountId > 0 ? accountId : SessionContext.Current.AccountId;
            bool admin = Roles.AlMenos(DatabaseManager.GetAccountRole(who), Roles.Administrador);

            switch (word)
            {
                case "":
                    await NotifyAsync(stream, StasisEvents.Board(GameState.CharacterId), channel, accountId);
                    return;
                case "aller":
                    await EventGoAsync(stream, channel, accountId);
                    return;
                case "stop":
                    if (!admin) { await NotifyAsync(stream, T("command.denied", ".event stop"), channel, accountId); return; }
                    if (StasisEvents.Now == null) { await NotifyAsync(stream, "Aucun événement en cours.", channel, accountId); return; }
                    await StasisEvents.EndAsync();
                    return;
            }

            string? kind = word switch
            {
                "timeattack" or "time" or "ta" => StasisEvents.TimeAttack,
                "poutch" or "puch" => StasisEvents.Poutch,
                "traque" or "grandetraque" => StasisEvents.Traque,
                _ => null,
            };
            if (kind == null) { await NotifyAsync(stream, Usage(".event"), channel, accountId); return; }
            if (!admin) { await NotifyAsync(stream, T("command.denied", ".event " + word), channel, accountId); return; }
            string? refusal = await StasisEvents.BeginAsync(kind);
            if (refusal != null) await NotifyAsync(stream, refusal, channel, accountId);
        }

        /// <summary>«.event aller»: to the Poutch during the contest.</summary>
        private static async Task EventGoAsync(NetworkStream stream, int channel, long accountId)
        {
            var r = StasisEvents.Now;
            if (r == null || r.Def.Kind != StasisEvents.Poutch || r.Def.Map <= 0)
            {
                string why = r?.Def.Kind == StasisEvents.TimeAttack
                    ? "Time Attack : entrez seul dans le donjon de votre choix, le chrono part au premier combat."
                    : r?.Def.Kind == StasisEvents.Traque
                        ? "Grande Traque : suivez les indices et rendez-vous sur la carte des PNJ cachés."
                        : "Aucun Concours Poutch en cours.";
                await NotifyAsync(stream, why, channel, accountId);
                return;
            }
            if (GameState.IsInFight) { await NotifyAsync(stream, "Pas en combat.", channel, accountId); return; }
            if (await WorldMoveHandler.TeleportAsync(stream, r.Def.Map) == null)
                await NotifyAsync(stream, "Le dojo du Poutch est inaccessible pour le moment.", channel, accountId);
        }

        // ─── .jetons ────────────────────────────────────────────────────────────────────────

        private static async Task JetonsAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            long me = GameState.CharacterId;
            var shop = StasisEvents.Current.Shop;
            string word = FirstWordOf(rest).ToLowerInvariant();
            if (word != "acheter")
            {
                string list = string.Join(" ; ", shop.Select(s => $"[{s.Id}] {s.Name} — {s.Price} jetons"));
                await NotifyAsync(stream, $"Jetons Event : {EventTokens.Balance(me)}. Boutique du Gardien de la Fissure : {list}. " +
                                          ".jetons acheter <id> (la rune : .jetons acheter rune <objet ou uid>).", channel, accountId);
                return;
            }

            string args = RestOf(rest).Trim();
            string id = FirstWordOf(args).ToLowerInvariant();
            var article = shop.FirstOrDefault(s => s.Id == id);
            if (article == null) { await NotifyAsync(stream, Usage(".jetons"), channel, accountId); return; }
            if (EventTokens.Balance(me) < article.Price)
            {
                await NotifyAsync(stream, $"{article.Name} coûte {article.Price} jetons ; vous en avez {EventTokens.Balance(me)}.", channel, accountId);
                return;
            }

            if (article.Rune)
            {
                string query = RestOf(args).Trim();
                var item = query.Length == 0 ? null : FindPiece(query);
                if (item == null)
                {
                    await NotifyAsync(stream, "Indiquez l'objet (nom ou uid, porté ou dans le sac) qui reçoit la rune.", channel, accountId);
                    return;
                }
                if (EventTokens.AfterRune(EventTokens.RuneOf(item.Uid)) == null)
                {
                    await NotifyAsync(stream, $"{NameOfItem(item.Template)} porte déjà {EventTokens.RuneCapPerItem} % : la rune serait perdue.", channel, accountId);
                    return;
                }
                if (!EventTokens.TrySpend(me, article.Price)) return;
                int now = EventTokens.ApplyRune(item.Uid) ?? EventTokens.RuneOf(item.Uid);
                ActivityJournal.Current.Write("events.rune", accountId > 0 ? accountId : SessionContext.Current.AccountId, me, new { uid = item.Uid, now });
                await NotifyAsync(stream, $"Rune Do Sort appliquée : {NameOfItem(item.Template)} donne +{now} % de dégâts (porté). " +
                                          $"Jetons restants : {EventTokens.Balance(me)}.", channel, accountId);
                return;
            }

            if (!EventTokens.TrySpend(me, article.Price)) return;
            if (!await WorkshopHandler.GiveAsync(stream, article.Item, article.Quantity))
            {
                EventTokens.Add(me, article.Price);
                await NotifyAsync(stream, "L'objet n'a pas pu être créé ; vos jetons vous sont rendus.", channel, accountId);
                return;
            }
            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("events.shop", accountId > 0 ? accountId : SessionContext.Current.AccountId, me,
                new { article = article.Id, article.Item, article.Quantity, article.Price });
            await NotifyAsync(stream, $"Acheté : {article.Name} pour {article.Price} jetons. Restants : {EventTokens.Balance(me)}.", channel, accountId);
        }

        /// <summary>An equipment piece of the character's, worn or in the bag, by uid or by name.</summary>
        private static Equipment.Item? FindPiece(string query)
        {
            var pieces = Equipment.All.Where(i => i.Quantity > 0 && Forgemagic.TemplateOf(i.Template) is { } t && !Forgemagic.Stacks(t)).ToList();
            if (long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid))
                return pieces.FirstOrDefault(i => i.Uid == uid);
            string wanted = Folded(query);
            return pieces.OrderBy(i => i.Position == Equipment.Bag ? 1 : 0)
                         .FirstOrDefault(i => Folded(NameOfItem(i.Template)) == wanted)
                ?? pieces.OrderBy(i => i.Position == Equipment.Bag ? 1 : 0)
                         .FirstOrDefault(i => Folded(NameOfItem(i.Template)).Contains(wanted));
        }

        // ─── .fissure ───────────────────────────────────────────────────────────────────────

        private static async Task FissureAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            long me = GameState.CharacterId;
            var settings = Fissure.Current;
            string word = FirstWordOf(rest).ToLowerInvariant();

            if (word == "")
            {
                var p = Fissure.ProgressOf(me);
                var exp = Fissure.Of(me);
                string bands = string.Join(" ; ", settings.Bands.Select(b =>
                    $"{b.Id} ({b.Name}, niv. {b.MinLevel}+, clé {b.Key:N0} kamas, {b.Validate} vague(s) pour valider" +
                    (b.Limit > 0 ? $", {b.Limit} max)" : ", sans limite)")));
                int owed = Fissure.DofusOwed(p.Eligible, p.DofusClaimed, settings.Dofus.WavesNeeded);
                await NotifyAsync(stream,
                    $"Fissure : {p.Waves} vagues gagnées (record {p.Best}), {p.Eligible % settings.Dofus.WavesNeeded}/{settings.Dofus.WavesNeeded} vagues éligibles vers un Dofus garanti" +
                    (owed > 0 ? $", {owed} Dofus à réclamer (.fissure dofus <nom>)" : "") + ". " +
                    (exp != null ? $"Expédition {exp.Band.Name} en cours, vague {exp.Wave} (niveau {Fissure.MonsterLevel(exp.Band, exp.Wave)}). " : "") +
                    $"Clés : {bands}. .fissure <clé> pour partir, .fissure rejoindre pour suivre votre groupe, .fissure quitter pour abandonner et sortir.", channel, accountId);
                return;
            }

            if (word == "quitter" || word == "abandon" || word == "sortir")
            {
                var closed = Fissure.Close(me);
                string said = closed == null ? "Aucune expédition en cours."
                    : $"Expédition {closed.Band.Name} abandonnée après {closed.Cleared} vague(s), qui restent acquises.";
                // The arena is a map no door or border leads out of: whoever stands on it is taken
                // back where he came from, or to the Sanctuary when that is not known.
                if (settings.ArenaMap > 0 && GameState.MapId == settings.ArenaMap && !GameState.IsInFight)
                {
                    (long Map, int Cell) back = _fissureOrigins.TryRemove(me, out var o) ? o : (Sanctuary.Current.MapId, Sanctuary.Current.Cell);
                    if (await TeleportHandler.ToMapAsync(stream, back.Map, back.Cell) < 0)
                        said += " La sortie est bloquée : utilisez « .sanctuaire » ou une potion de rappel.";
                }
                await NotifyAsync(stream, said, channel, accountId);
                return;
            }

            if (word == "rejoindre")
            {
                // A party member goes to the expedition his party opened: the arena has no way in.
                var myParty = Parties.Of(me);
                var theirs = myParty == null ? null
                    : Parties.MembersOf(myParty).Select(Fissure.Of).FirstOrDefault(e => e != null);
                if (theirs == null) { await NotifyAsync(stream, "Aucune expédition ouverte dans votre groupe.", channel, accountId); return; }
                if (GameState.IsInFight) { await NotifyAsync(stream, "Pas en combat.", channel, accountId); return; }
                if (GameState.MapId == theirs.ArenaMap) { await NotifyAsync(stream, "Vous êtes déjà dans la Fissure.", channel, accountId); return; }
                long fromMap = GameState.MapId;
                int fromCell = GameState.CellId;
                if (await WorldMoveHandler.TeleportAsync(stream, theirs.ArenaMap) == null)
                    await NotifyAsync(stream, "Impossible de vous emmener dans la Fissure pour le moment.", channel, accountId);
                else
                    _fissureOrigins[me] = (fromMap, fromCell);
                return;
            }

            if (word == "dofus")
            {
                string name = Folded(RestOf(rest));
                var choice = settings.Dofus.Items.FirstOrDefault(d => name.Length > 0 && Folded(d.Name).Contains(name));
                string names = string.Join(", ", settings.Dofus.Items.Select(d => d.Name));
                if (choice.Item == 0) { await NotifyAsync(stream, $"Choisissez votre Dofus : {names}.", channel, accountId); return; }
                if (!Fissure.Claim(me))
                {
                    var p = Fissure.ProgressOf(me);
                    await NotifyAsync(stream, $"Pas encore : {p.Eligible % settings.Dofus.WavesNeeded}/{settings.Dofus.WavesNeeded} vagues éligibles.", channel, accountId);
                    return;
                }
                if (!await WorkshopHandler.GiveAsync(stream, choice.Item, 1))
                {
                    Console.WriteLine($"[Fissure] The Dofus {choice.Item} could not be given to {me}; the claim stands.");
                    await NotifyAsync(stream, "Le Dofus n'a pas pu être créé. Prévenez un administrateur.", channel, accountId);
                    return;
                }
                await RefreshPodsAsync(stream);
                ActivityJournal.Current.Write("fissure.dofus", accountId > 0 ? accountId : SessionContext.Current.AccountId, me, new { choice.Item });
                await StasisEvents.AnnounceAsync($"{GameState.CharacterName} reçoit le Dofus {choice.Name} de la Fissure !");
                return;
            }

            var band = Fissure.BandOf(word);
            if (band == null || !settings.Enabled) { await NotifyAsync(stream, Usage(".fissure"), channel, accountId); return; }
            if (Fissure.Of(me) != null) { await NotifyAsync(stream, "Une expédition est déjà en cours (.fissure quitter pour l'abandonner).", channel, accountId); return; }
            if (GameState.IsInFight) { await NotifyAsync(stream, "Pas en combat.", channel, accountId); return; }
            if (GameState.CharacterLevel < band.MinLevel)
            {
                await NotifyAsync(stream, $"La clé {band.Name} demande le niveau {band.MinLevel}.", channel, accountId);
                return;
            }

            var party = Parties.Of(me);
            int fighters = Math.Clamp(party == null ? 1 : Parties.MembersOf(party).Count, 1, settings.MaxFighters);
            long cost = OrbePrice((long)band.Key * fighters);
            if (OrbesHeld() < cost)
            {
                await NotifyAsync(stream, $"{fighters} clé(s) {band.Name} coûtent {cost:N0} {OrbeLabel} ; vous en avez {OrbesHeld():N0}.", channel, accountId);
                return;
            }
            var opened = Fissure.Open(me, band, fighters);
            if (opened == null) { await NotifyAsync(stream, "La Fissure ne s'ouvre pas (vague impossible à placer).", channel, accountId); return; }
            if (!await PayOrbesAsync(stream, -cost)) { Fissure.Close(me); return; }
            ActivityJournal.Current.Write("fissure.open", accountId > 0 ? accountId : SessionContext.Current.AccountId, me, new { band = band.Id, fighters, cost });
            var bonus = Fissure.FormationFor(fighters, settings.Formations);
            await NotifyAsync(stream, $"Fissure {band.Name} ouverte pour {cost:N0} {OrbeLabel}. Vague 1 (niveau {Fissure.MonsterLevel(band, 1)}) vous attend. " +
                                      $"Bonus de formation ({fighters}) : +{bonus.Ap} PA, +{bonus.Mp} PM, +{bonus.LifePercent} % PV, +{bonus.DamagePercent} % dégâts.",
                              channel, accountId);
            if (GameState.MapId == opened.ArenaMap) return;
            long leftMap = GameState.MapId;
            int leftCell = GameState.CellId;
            if (await WorldMoveHandler.TeleportAsync(stream, opened.ArenaMap) == null)
            {
                Fissure.Close(me);
                await PayOrbesAsync(stream, cost);
                await NotifyAsync(stream, "Impossible de vous emmener dans la Fissure ; l'expédition est annulée et remboursée.", channel, accountId);
                return;
            }
            _fissureOrigins[me] = (leftMap, leftCell);
            if (fighters > 1)
                await NotifyAsync(stream, "Vos compagnons de groupe vous rejoignent avec « .fissure rejoindre ».", channel, accountId);
        }

        /// <summary>
        /// Where each character stood before being taken into the Fissure's arena, for «.fissure
        /// quitter» to take him back: the arena (content/events/fissure.json «arenaMap») is a map no
        /// door or border leads out of. In memory only; without it the way out is the Sanctuary.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long Map, int Cell)> _fissureOrigins = new();

        /// <summary>The first word of a line, «» when there is none.</summary>
        private static string FirstWordOf(string text)
        {
            string t = (text ?? "").Trim();
            int space = t.IndexOf(' ');
            return space < 0 ? t : t[..space];
        }
    }
}
