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

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Stasis progression from the chat, and the session-side actions the JondoFix window's
    /// control routes (/api/stasis/...) share with it: the dons, the Ascension, the Evolution and
    /// the research.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are in Managers/Stasis (<see cref="Dons"/>, <see cref="Ascension"/>); what is here
    /// is the session around them -- the profile's lock, the save, and what the client must be
    /// told afterwards: the sheet when a don changes a characteristic, the kamas when a reroll is
    /// paid, and for an Ascension or an Evolution the whole level change through
    /// <see cref="SetLevelAsync"/>, the path «.level» already takes (window, experience, sheet,
    /// spells and bar).
    /// </para>
    /// <para>
    /// Every player may type these, whatever his role, so they are served before the role table
    /// (<see cref="TryProgressionAsync"/> is called from TryHandleAsync right after the jail
    /// check) and their texts are French only, as the Stasis rules they copy: they do not enter
    /// the three-language usage catalogue the staff commands are tested against.
    /// </para>
    /// <para>
    /// The callers hold the session's turn and its SessionContext: the chat line is served on it,
    /// and the control route takes it (UnoCadaVez) before calling in.
    /// </para>
    /// </remarks>
    public static partial class CommandHandler
    {
        private static readonly HashSet<string> ProgressionCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dons", ".don", ".ascension", ".evolution", ".recherche",
        };

        private static readonly HashSet<string> ConfirmWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "confirmer", "oui", "ok", "yes", "si", "sí",
        };

        /// <summary>A progression command, served whatever the role. False when the line is not one.</summary>
        private static async Task<bool> TryProgressionAsync(NetworkStream stream, string command, string rest, int channel, long accountId)
        {
            if (!ProgressionCommands.Contains(command)) return false;
            var session = SessionContext.Current;
            if (!session.HasCharacter) return true;
            string text;
            try
            {
                text = command.ToLowerInvariant() switch
                {
                    ".dons" => DonsText(session),
                    ".don" => await DonCommandAsync(session, rest),
                    ".ascension" => ConfirmWords.Contains(rest.Trim())
                        ? AscensionDoneText(await AscendAsync(session))
                        : AscensionPreviewText(session),
                    ".evolution" => ConfirmWords.Contains(rest.Trim())
                        ? EvolutionDoneText(await EvolveAsync(session))
                        : EvolutionPreviewText(session),
                    _ => await ResearchCommandAsync(session, rest),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Progression] {command} failed: {ex}");
                text = "La commande a échoué : " + ex.Message;
            }
            await NotifyAsync(stream, text, channel, accountId);
            return true;
        }

        // ─── The actions, shared with the control routes ────────────────────────────────

        /// <summary>Keeps card <paramref name="index"/> of the oldest offer. "ok", or the refusal's code.</summary>
        internal static async Task<string> PickDonAsync(GameSession session, int index, string? stat)
        {
            var profile = ProgressionStore.Of(session.CharacterId);
            string result;
            ChosenDon? kept;
            lock (profile.Gate)
            {
                result = Dons.Pick(profile, ProgressionContent.Current, index, stat, out kept);
                if (result == "ok")
                {
                    ProgressionStore.Save(profile);
                    Console.WriteLine($"[Progression] {session.State.CharacterName} keeps {kept!.Card} ({Dons.RarityLabel(kept.Rarity)}) at level {kept.Level}.");
                }
            }
            // A don «Pension»: its egg goes to the account's pension.
            if (result == "ok" && kept != null && Dons.PensionChoiceOf(kept.Card) is { } egg)
                GivePensionDonEgg(session, Dons.RealLevel(kept.Level), egg.Kind, egg.Choice);
            if (result == "ok") await RefreshAfterDonAsync(session);
            return result;
        }

        /// <summary>The egg of a don «Pension» picked, into the account's pension (once per character and level).</summary>
        private static void GivePensionDonEgg(GameSession session, int level, string kind, int choice)
        {
            if (session.AccountId <= 0) return;
            var don = Dons.PensionDonAt(level, ProgressionContent.Current);
            var account = Pension.Of(session.AccountId);
            PensionEgg? egg;
            lock (account.Gate) egg = Pension.GiveDonEgg(account, session.CharacterId, level, kind, choice, don?.Rarity ?? Pension.Starter);
            if (egg == null)
            {
                Console.WriteLine($"[Progression] {session.State.CharacterName}: the don «Pension» of level {level} was already given.");
                return;
            }
            Pension.Save(account);
            Console.WriteLine($"[Progression] {session.State.CharacterName} gets egg #{egg.Id} ({Pension.Describe(egg, Pension.Content)}) from the don «Pension» of level {level}.");
        }

        /// <summary>
        /// What the character should have and may lack (Dons.EnsureOffers): the starting offer,
        /// the passives past 50, the dons «Pension» the account's pension has not given. Asked at
        /// every look at the dons -- the window's polls, after each fight and at login, and «.dons».
        /// </summary>
        internal static void EnsureDonOffers(GameSession session)
        {
            if (session.CharacterId <= 0) return;
            var profile = ProgressionStore.Of(session.CharacterId);
            var account = session.AccountId > 0 ? Pension.Of(session.AccountId) : null;
            long character = session.CharacterId;
            Func<int, bool>? given = account == null ? null : level =>
            {
                lock (account.Gate) return Pension.GiftGiven(account, character, level);
            };
            lock (profile.Gate)
            {
                int changed = Dons.EnsureOffers(profile, ProgressionContent.Current, session.State.Breed, session.State.CharacterLevel, given);
                if (changed == 0) return;
                ProgressionStore.Save(profile);
                Console.WriteLine($"[Progression] {session.State.CharacterName}: {changed} offer(s) of dons given or dropped at a look, {profile.Pending.Count} waiting.");
            }
        }

        /// <summary>Rerolls the oldest offer, free or for kamas. "ok", or the refusal's code.</summary>
        internal static async Task<string> RerollDonAsync(GameSession session)
        {
            var profile = ProgressionStore.Of(session.CharacterId);
            var state = session.State;
            string result;
            long paid;
            if (JondoCoin.IsOrbe && session.Stream != null)
            {
                // The Orbes are Jondo Coins: the cost is taken out of the bag, in the session's context.
                using (SessionContext.Push(session))
                {
                    lock (profile.Gate)
                    {
                        long held = OrbesHeld();
                        result = Dons.Reroll(profile, ProgressionContent.Current, state.Breed, ref held, out paid, OrbePrice);
                        if (result == "ok") ProgressionStore.Save(profile);
                    }
                    if (result == "ok" && paid > 0) await PayOrbesAsync(session.Stream, -paid);
                }
                return result;
            }
            lock (profile.Gate)
            {
                long kamas = state.Kamas;
                result = Dons.Reroll(profile, ProgressionContent.Current, state.Breed, ref kamas, out paid);
                if (result == "ok")
                {
                    state.Kamas = kamas;
                    ProgressionStore.Save(profile);
                }
            }
            if (result == "ok" && paid > 0)
            {
                DatabaseManager.SaveCurrentCharacter();
                await session.SendAsync(ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(state.Kamas)));
            }
            return result;
        }

        /// <summary>A refonte: the characteristic card's points moved from one characteristic to another.</summary>
        internal static async Task<string> RepickDonAsync(GameSession session, string from, string to)
        {
            var profile = ProgressionStore.Of(session.CharacterId);
            string result;
            lock (profile.Gate)
            {
                result = Dons.Repick(profile, ProgressionContent.Current, from, to, out _);
                if (result == "ok") ProgressionStore.Save(profile);
            }
            if (result == "ok") await RefreshAfterDonAsync(session);
            return result;
        }

        /// <summary>Buys a research's next rank.</summary>
        internal static async Task<string> BuyResearchAsync(GameSession session, string id)
        {
            var profile = ProgressionStore.Of(session.CharacterId);
            string result;
            lock (profile.Gate)
            {
                result = Ascension.BuyResearch(profile, ProgressionContent.Current, id);
                if (result == "ok") ProgressionStore.Save(profile);
            }
            // A stat research (Fortune, Tempo) changes the sheet at once.
            if (result == "ok") await RefreshAfterDonAsync(session);
            return result;
        }

        /// <summary>The Ascension, state and client. The result's code is "ok" or the refusal's.</summary>
        internal static async Task<(string Result, RebirthOutcome Outcome)> AscendAsync(GameSession session)
        {
            var state = session.State;
            if (state.IsInFight) return ("combat", default);
            var profile = ProgressionStore.Of(session.CharacterId);
            RebirthOutcome outcome;
            lock (profile.Gate)
            {
                string? why = Ascension.CannotAscend(profile, state.CharacterLevel, ProgressionContent.Current);
                if (why != null) return (why, default);
                outcome = Ascension.Ascend(state, profile, ProgressionContent.Current);
                ProgressionStore.Save(profile);
            }
            Console.WriteLine($"[Progression] {state.CharacterName} ascends from {outcome.FromLevel}: +{outcome.Eclats} Éclats, ascension {profile.Ascensions}.");
            await MoveLevelAsync(session, outcome.ToLevel);
            return ("ok", outcome);
        }

        /// <summary>The Evolution, state and client.</summary>
        internal static async Task<(string Result, RebirthOutcome Outcome)> EvolveAsync(GameSession session)
        {
            var state = session.State;
            if (state.IsInFight) return ("combat", default);
            var profile = ProgressionStore.Of(session.CharacterId);
            RebirthOutcome outcome;
            lock (profile.Gate)
            {
                string? why = Ascension.CannotEvolve(profile, state.CharacterLevel, ProgressionContent.Current);
                if (why != null) return (why, default);
                outcome = Ascension.Evolve(state, profile, ProgressionContent.Current);
                ProgressionStore.Save(profile);
            }
            Console.WriteLine($"[Progression] {state.CharacterName} evolves from {outcome.FromLevel} to {outcome.ToLevel}, {outcome.DonsKept} dons kept.");
            await MoveLevelAsync(session, outcome.ToLevel);
            return ("ok", outcome);
        }

        /// <summary>The level moved and the client told, as «.level» does it; the sheet saved with it.</summary>
        private static async Task MoveLevelAsync(GameSession session, int level)
        {
            if (session.Stream != null) await SetLevelAsync(session.Stream, level);
            else
            {
                session.State.CharacterLevel = level;
                if (ExperienceTable.IsLoaded) session.State.Experience = ExperienceTable.LevelFloor(level);
                DatabaseManager.SaveCurrentCharacter();
            }
        }

        private static async Task RefreshAfterDonAsync(GameSession session)
        {
            if (session.Stream == null || session.State.IsInFight) return;
            await RefreshSheetAsync(session);
        }

        // ─── What the window and «.dons» show ───────────────────────────────────────────

        /// <summary>The whole progression of a character, as /api/stasis/state answers it.</summary>
        internal static object ProgressionView(GameSession session)
        {
            var state = session.State;
            var content = ProgressionContent.Current;
            Dons.ItemNameOf = ItemName;
            EnsureDonOffers(session);
            var profile = ProgressionStore.Of(session.CharacterId);
            lock (profile.Gate)
            {
                var offer = profile.Pending.FirstOrDefault();
                bool freeReroll = Dons.RerollsLeft(profile, content) > 0;
                string? cannotAscend = Ascension.CannotAscend(profile, state.CharacterLevel, content);
                string? cannotEvolve = Ascension.CannotEvolve(profile, state.CharacterLevel, content);
                int catchUp = (profile.Ascensions > 0 || profile.Evolved) && state.CharacterLevel < profile.BestLevel
                    ? Ascension.CatchUpMultiplier(profile, content) : 1;
                return new
                {
                    character = state.CharacterName,
                    level = state.CharacterLevel,
                    best = profile.BestLevel,
                    eclats = profile.Eclats,
                    ascensions = profile.Ascensions,
                    evolved = profile.Evolved,
                    catchUp,
                    // The popup opens on an offer only out of a fight.
                    fight = state.IsInFight,
                    discovery = Dons.DiscoveryPercent(profile, content),
                    pending = profile.Pending.Count,
                    freeRerolls = Dons.RerollsLeft(profile, content),
                    repicks = Dons.RepicksLeft(profile, content),
                    kamas = state.Kamas,
                    // The Orbes held -- Jondo Coins in coin mode -- for the window's summary line.
                    coins = OrbesHeld(),
                    coinLabel = OrbeLabel,
                    offer = offer == null ? null : new
                    {
                        level = offer.Level,
                        shownLevel = Dons.RealLevel(offer.Level),
                        kind = offer.Kind,
                        kindLabel = Dons.KindLabel(offer.Kind),
                        rerolled = offer.Rerolled,
                        rerollable = Dons.IsRerollable(offer.Kind),
                        canReroll = !offer.Rerolled && Dons.IsRerollable(offer.Kind),
                        freeReroll,
                        rerollCost = freeReroll ? 0 : OrbePrice(Dons.RerollCost(Dons.RealLevel(offer.Level), content)),
                        cards = offer.Cards.Select((card, i) =>
                        {
                            int rarity = offer.RarityAt(i);
                            bool rated = Dons.HasRarity(offer.Kind, content);
                            var (name, description) = Dons.Describe(card, profile, content, rarity);
                            var icon = Dons.IconOf(card, content);
                            // A spell don card carries its spell and its effect apart, for the
                            // window to draw them as a spell card rather than as a line of text.
                            var spellDon = SpellDons.DefinitionOf(card, content)?.Don;
                            return new
                            {
                                index = i + 1,
                                id = card,
                                name,
                                description,
                                // Commun / Rare / Légendaire; "" for the offers without rarity (spell, passive, Pension, accomplishment).
                                rarity = rated ? Dons.RarityKey(rarity) : "",
                                rarityLabel = rated ? Dons.RarityLabel(rarity) : "",
                                cardKind = Dons.CardKindLabel(card, content),
                                // The icon, resolved by the client from its own data (Dons.IconOf).
                                iconKind = icon.Kind,
                                iconId = icon.Id,
                                iconGlyph = icon.Glyph,
                                characteristic = Dons.IsCharacteristicCard(card, content),
                                spell = spellDon?.Spell ?? 0,
                                spellName = spellDon?.SpellName ?? "",
                                effect = spellDon == null ? "" : SpellDons.EffectText(spellDon, spellDon.Value),
                                rank = spellDon == null ? 0 : Dons.RankOf(profile, spellDon.Id),
                                maxRank = spellDon?.MaxRank ?? 0,
                            };
                        }).ToList(),
                    },
                    spellPoints = new
                    {
                        spent = SpellDons.PointsSpent(profile, content),
                        max = content.SpellDons.MaxPoints,
                        families = SpellDons.PointsPerFamily(profile, content).Count,
                        maxFamilies = SpellDons.FamiliesAllowed(state.CharacterLevel, content),
                        perFamily = SpellDons.PointsPerFamilyAllowed(state.CharacterLevel, content),
                        owned = OwnedSpellDons(profile, content).Select(o => new { spellName = o.Don.SpellName, effect = o.Effect, rank = o.Rank, maxRank = o.Don.MaxRank }).ToList(),
                    },
                    characteristics = content.Dons.CharacteristicCard.Stats
                        .Select(s => new
                        {
                            key = s,
                            label = Dons.StatLabels.GetValueOrDefault(s, s),
                            statId = Dons.StatIds.GetValueOrDefault(s),
                            iconGlyph = Dons.StatGlyph(Dons.StatIds.GetValueOrDefault(s)),
                        }).ToList(),
                    bonuses = Dons.BonusesOf(profile, content).OrderBy(kv => kv.Key)
                        .Select(kv => new { stat = Dons.StatName(kv.Key), value = kv.Value }).ToList(),
                    research = content.Research.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        description = r.Description,
                        rank = profile.RankOf(r.Id),
                        max = r.MaxRank,
                        cost = Ascension.NextCost(profile, r),
                    }).ToList(),
                    ascension = new
                    {
                        can = cannotAscend == null,
                        why = cannotAscend == null ? "" : Dons.RefusalText(cannotAscend),
                        minLevel = content.Ascension.MinLevel,
                        eclats = Ascension.EclatsFor(state.CharacterLevel, profile, content),
                        catchUp = Ascension.CatchUpMultiplier(profile, content),
                    },
                    evolution = new
                    {
                        can = cannotEvolve == null,
                        why = cannotEvolve == null ? "" : Dons.RefusalText(cannotEvolve),
                        level = content.Ascension.EvolutionLevel,
                        cost = content.Ascension.EvolutionEclats,
                        returnLevel = content.Ascension.EvolutionReturnLevel,
                    },
                };
            }
        }

        private static string DonsText(GameSession session)
        {
            var state = session.State;
            var content = ProgressionContent.Current;
            Dons.ItemNameOf = ItemName;
            EnsureDonOffers(session);
            var profile = ProgressionStore.Of(session.CharacterId);
            var text = new StringBuilder();
            lock (profile.Gate)
            {
                text.Append($"Progression : niveau {state.CharacterLevel} (record {profile.BestLevel}), {profile.Eclats} Éclats, " +
                            $"{profile.Ascensions} Ascension(s){(profile.Evolved ? ", évolué" : "")}. ");
                var owned = OwnedSpellDons(profile, content);
                if (owned.Count > 0)
                    text.Append($"Dons de sorts ({SpellDons.PointsSpent(profile, content)}/{content.SpellDons.MaxPoints} points) : " +
                                string.Join(", ", owned.Select(o => $"{o.Don.SpellName} {o.Effect}")) + ". ");
                var offer = profile.Pending.FirstOrDefault();
                if (offer == null)
                {
                    text.Append("Aucune offre de don en attente.");
                    return text.ToString();
                }
                text.Append($"{profile.Pending.Count} offre(s) en attente. {Dons.KindLabel(offer.Kind)} du niveau {Dons.RealLevel(offer.Level)} :");
                bool rated = Dons.HasRarity(offer.Kind, content);
                for (int i = 0; i < offer.Cards.Count; i++)
                {
                    var (name, description) = Dons.Describe(offer.Cards[i], profile, content, offer.RarityAt(i));
                    string rarity = rated ? $" [{Dons.RarityLabel(offer.RarityAt(i))}]" : "";
                    text.Append($"\n{i + 1}. {name}{rarity} — {description}");
                }
                string reroll = offer.Rerolled || !Dons.IsRerollable(offer.Kind) ? "relance indisponible"
                    : Dons.RerollsLeft(profile, content) > 0 ? "relance gratuite disponible"
                    : $"relance : {OrbePrice(Dons.RerollCost(Dons.RealLevel(offer.Level), content))} {OrbeLabel}";
                text.Append($"\n.don <n> [caractéristique] pour choisir, .don relance ({reroll}), .don refonte <de> <vers> ({Dons.RepicksLeft(profile, content)} restante(s)).");
            }
            return text.ToString();
        }

        /// <summary>The spell dons a character has, each with its rank and its whole effect («+10 % de dégâts»).</summary>
        private static List<(SpellDonDefinition Don, int Rank, string Effect)> OwnedSpellDons(ProgressionProfile profile, ProgressionContent content)
            => profile.Dons.Select(d => SpellDons.DefinitionOf(d.Card, content)?.Don).Where(d => d != null).Select(d => d!)
                   .DistinctBy(d => d.Id)
                   .Select(d => { int rank = Dons.RankOf(profile, d.Id); return (d, rank, SpellDons.EffectText(d, d.Value * rank)); })
                   .ToList();

        private static async Task<string> DonCommandAsync(GameSession session, string rest)
        {
            var words = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return DonsText(session);
            string first = words[0].ToLowerInvariant();
            if (first is "relance" or "relancer")
            {
                string r = await RerollDonAsync(session);
                return r == "ok" ? "Offre relancée.\n" + DonsText(session) : Dons.RefusalText(r);
            }
            if (first == "refonte")
            {
                if (words.Length < 3) return "Usage : .don refonte <de> <vers> — par exemple .don refonte force agilite";
                string r = await RepickDonAsync(session, words[1], words[2]);
                return r == "ok" ? $"Refonte faite : vos points de don passent de {words[1]} à {words[2]}." : Dons.RefusalText(r);
            }
            if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return "Usage : .don <n> [caractéristique] | .don relance | .don refonte <de> <vers>";
            string result = await PickDonAsync(session, index, words.Length > 1 ? words[1] : null);
            if (result != "ok") return Dons.RefusalText(result);
            int left = ProgressionStore.Of(session.CharacterId).Pending.Count;
            return left > 0 ? "Don obtenu.\n" + DonsText(session) : "Don obtenu.";
        }

        private static string AscensionPreviewText(GameSession session)
        {
            var content = ProgressionContent.Current;
            var profile = ProgressionStore.Of(session.CharacterId);
            lock (profile.Gate)
            {
                string? why = Ascension.CannotAscend(profile, session.State.CharacterLevel, content);
                if (why != null) return "Ascension impossible : " + Dons.RefusalText(why);
                long eclats = Ascension.EclatsFor(session.State.CharacterLevel, profile, content);
                return $"L'Ascension vous ramène au niveau 1 (expérience, caractéristiques et sorts de niveau 1 ; objets et kamas conservés ; " +
                       $"dons perdus) et vous rapporte {eclats} Éclats. Ensuite, expérience x{Ascension.CatchUpMultiplier(profile, content)} " +
                       $"jusqu'au niveau {Math.Max(profile.BestLevel, session.State.CharacterLevel)}. Tapez .ascension confirmer pour la faire.";
            }
        }

        private static string AscensionDoneText((string Result, RebirthOutcome Outcome) done)
            => done.Result == "ok"
                ? $"Ascension accomplie depuis le niveau {done.Outcome.FromLevel} : +{done.Outcome.Eclats} Éclats."
                : "Ascension impossible : " + Dons.RefusalText(done.Result);

        private static string EvolutionPreviewText(GameSession session)
        {
            var a = ProgressionContent.Current.Ascension;
            var profile = ProgressionStore.Of(session.CharacterId);
            lock (profile.Gate)
            {
                string? why = Ascension.CannotEvolve(profile, session.State.CharacterLevel, ProgressionContent.Current);
                string head = $"L'Évolution (niveau {a.EvolutionLevel}, {a.EvolutionEclats} Éclats, donjons de palier {a.EvolutionLevel}) " +
                              $"vous ramène définitivement au niveau {a.EvolutionReturnLevel}, évolué, en gardant vos dons jusqu'au niveau {a.EvolutionKeepDonsUpTo}. ";
                return why != null ? head + "Impossible pour l'instant : " + Dons.RefusalText(why) : head + "Tapez .evolution confirmer pour la faire.";
            }
        }

        private static string EvolutionDoneText((string Result, RebirthOutcome Outcome) done)
            => done.Result == "ok"
                ? $"Évolution accomplie : vous voici niveau {done.Outcome.ToLevel}, évolué, avec {done.Outcome.DonsKept} dons conservés."
                : "Évolution impossible : " + Dons.RefusalText(done.Result);

        private static async Task<string> ResearchCommandAsync(GameSession session, string rest)
        {
            var content = ProgressionContent.Current;
            var profile = ProgressionStore.Of(session.CharacterId);
            string id = (rest ?? "").Trim();
            if (id.Length > 0)
            {
                string r = await BuyResearchAsync(session, id);
                if (r != "ok") return Dons.RefusalText(r);
            }
            var text = new StringBuilder();
            lock (profile.Gate)
            {
                text.Append(id.Length > 0 ? "Recherche achetée. " : "");
                text.Append($"Recherches ({profile.Eclats} Éclats) :");
                foreach (var r in content.Research)
                {
                    long cost = Ascension.NextCost(profile, r);
                    text.Append($"\n{r.Id} — {r.Name} {profile.RankOf(r.Id)}/{r.MaxRank} : {r.Description}" +
                                (cost >= 0 ? $" ({cost} Éclats)" : " (max)"));
                }
                text.Append("\n.recherche <id> pour acheter le rang suivant.");
            }
            return text.ToString();
        }
    }
}
