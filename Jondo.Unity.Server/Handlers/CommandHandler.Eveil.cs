using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Éveil des Dofus from the chat and the F9 window (Managers/Stasis/Eveil.cs): «.eveil»
    /// shows the six Dofus, «.eveil &lt;dofus&gt; commencer» sacrifices the copies, «.eveil
    /// &lt;dofus&gt; defi» opens the final challenge once the route is done.
    /// </summary>
    /// <remarks>
    /// One action, <see cref="EveilAsync"/>, answering a French text, for the chat command and
    /// for /api/stasis/eveil alike, as the hub's commands do.
    /// </remarks>
    public static partial class CommandHandler
    {
        private static async Task EveilChatAsync(System.Net.Sockets.NetworkStream stream, string rest, int channel, long accountId)
        {
            var session = SessionContext.Current;
            if (!session.HasCharacter) return;
            await NotifyAsync(stream, await EveilAsync(session, rest), channel, accountId);
        }

        /// <summary>The Éveil command: status, «&lt;dofus&gt; commencer» or «&lt;dofus&gt; defi».</summary>
        internal static async Task<string> EveilAsync(GameSession session, string rest)
        {
            try
            {
                var file = Eveil.Current;
                if (!file.Enabled || file.Dofus.Count == 0) return "L'Éveil des Dofus n'est pas ouvert sur ce serveur.";
                var words = (rest ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return EveilStatusText(session);
                // «.eveil cawotte», «.eveil cawotte commencer», «.eveil glaces defi»; the action may come first too.
                string last = Tiers.Fold(words[^1]);
                bool start = last is "commencer" or "start" or "sacrifier";
                bool challenge = last is "defi" or "defier" or "challenge";
                string name = string.Join(' ', (start || challenge) ? words[..^1] : words);
                var dofus = file.Find(name);
                if (dofus == null)
                    return $"Aucun Dofus d'Éveil « {name} ». Au choix : {string.Join(", ", file.Dofus.Select(d => d.Id))}.";
                if (start) return await StartEveilAsync(session, file, dofus);
                if (challenge) return await ChallengeEveilAsync(session, dofus);
                return DofusLine(session, file, dofus);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Eveil] .eveil failed for {session.CharacterId}: {ex.Message}");
                return "L'Éveil n'a pas pu répondre.";
            }
        }

        // ─── Status ─────────────────────────────────────────────────────────────────────

        private static string EveilStatusText(GameSession session)
        {
            var file = Eveil.Current;
            string head = "Éveil des Dofus (Partie I) : sacrifiez des copies (« .eveil <dofus> commencer »), terminez le parcours de donjons" +
                          (file.RequireHardcore ? " en hardcore (seules les victoires après le début comptent)" : " (victoires après le début)") +
                          ", puis le défi final en solo (« .eveil <dofus> defi »). ";
            return head + string.Join(" | ", file.Dofus.Select(d => DofusLine(session, file, d)));
        }

        private static string DofusLine(GameSession session, EveilFile file, EveilDofus dofus)
        {
            var progress = Eveil.ProgressOf(session.CharacterId, dofus.Id);
            string name = $"{dofus.Name} (niv. {dofus.Level})";
            switch (Eveil.StageOf(dofus, progress))
            {
                case EveilStage.NotStarted:
                    return $"{name} : non commencé, {PlainCopies(dofus, file.Label)}/{dofus.Copies} copies en sac.";
                case EveilStage.Route:
                    var left = Eveil.Remaining(dofus, progress);
                    return $"{name} : parcours {dofus.Dungeons.Count - left.Count}/{dofus.Dungeons.Count}, restants : " +
                           string.Join(", ", left.Select(d => d.Name)) + ".";
                case EveilStage.Challenge:
                    return $"{name} : parcours terminé, défi final contre {dofus.Boss.Name} prêt (« .eveil {dofus.Id} defi »).";
                default:
                    return $"{name} : éveillé ✓" + (file.Repeatable ? $" (recommencer : « .eveil {dofus.Id} commencer »)" : "") + ".";
            }
        }

        // ─── Bag ────────────────────────────────────────────────────────────────────────

        /// <summary>The ordinary copies of a Dofus in the bag: not worn, not awakened.</summary>
        private static List<Equipment.Item> PlainCopyStacks(EveilDofus dofus, string label)
            => Equipment.All.Where(i => i.Template == dofus.Item && i.Position == Equipment.Bag && i.Quantity > 0
                                        && !Eveil.IsAwakened(i.Effects, label))
                            .OrderBy(i => i.Uid).ToList();

        private static int PlainCopies(EveilDofus dofus, string label) => PlainCopyStacks(dofus, label).Sum(i => i.Quantity);

        // ─── commencer ──────────────────────────────────────────────────────────────────

        private static async Task<string> StartEveilAsync(GameSession session, EveilFile file, EveilDofus dofus)
        {
            var state = session.State;
            if (state.IsInFight) return "Pas en combat.";
            var progress = Eveil.ProgressOf(session.CharacterId, dofus.Id);
            if (state.CharacterLevel < dofus.Level)
                return $"L'Éveil du Dofus {dofus.Name} demande le niveau {dofus.Level}.";
            if (!Eveil.MayStart(dofus, progress, state.CharacterLevel, file.Repeatable))
                return Eveil.StageOf(dofus, progress) == EveilStage.Done
                    ? $"Le Dofus {dofus.Name} est déjà éveillé."
                    : $"L'Éveil du Dofus {dofus.Name} est déjà commencé : « .eveil {dofus.Id} » pour le détail.";
            var stacks = PlainCopyStacks(dofus, file.Label);
            var plan = Eveil.PlanSacrifice(stacks.Select(i => (i.Uid, i.Quantity)), dofus.Copies);
            if (plan == null)
                return $"Il faut {dofus.Copies} Dofus {dofus.Name} ordinaires dans le sac (non équipés) ; vous en avez {stacks.Sum(i => i.Quantity)}.";
            foreach (var (uid, take) in plan)
            {
                var item = stacks.First(i => i.Uid == uid);
                if (!await WorkshopHandler.ConsumeAsync(session, item, take, inWorkshop: false))
                {
                    Console.WriteLine($"[Eveil] The sacrifice of {dofus.Id} by {session.CharacterId} stopped halfway ({uid} x{take}).");
                    return "Le sacrifice a échoué : une copie n'est plus là.";
                }
            }
            Eveil.Backend.Start(session.CharacterId, dofus.Id, DateTime.Now);
            if (session.Stream != null) await RefreshPodsAsync(session.Stream);
            ActivityJournal.Current.Write("stasis.eveil.start", session.AccountId, session.CharacterId, new { dofus = dofus.Id, copies = dofus.Copies });
            return $"{dofus.Copies} Dofus {dofus.Name} sacrifiés : le fragment s'éveille. Parcours : " +
                   string.Join(", ", dofus.Dungeons.Select(d => d.Name)) +
                   (file.RequireHardcore ? " — en hardcore (« .hardcore on », personnage évolué)." : ".") +
                   " Seules les victoires à partir de maintenant comptent.";
        }

        // ─── defi ───────────────────────────────────────────────────────────────────────

        private static async Task<string> ChallengeEveilAsync(GameSession session, EveilDofus dofus)
        {
            var state = session.State;
            var progress = Eveil.ProgressOf(session.CharacterId, dofus.Id);
            switch (Eveil.StageOf(dofus, progress))
            {
                case EveilStage.NotStarted: return $"Commencez d'abord l'Éveil : « .eveil {dofus.Id} commencer ».";
                case EveilStage.Done: return $"Le Dofus {dofus.Name} est déjà éveillé.";
                case EveilStage.Route:
                    return $"Le parcours n'est pas terminé : restent {string.Join(", ", Eveil.Remaining(dofus, progress).Select(d => d.Name))}.";
            }
            if (state.IsInFight) return "Vous êtes déjà en combat.";
            if (session.Stream == null) return "Session sans connexion.";
            if (DungeonManager.IsRoom(state.MapId)) return "Le défi ne se lance pas dans une salle de donjon.";
            var group = MobSpawnManager.ComposeOffMap(new[] { (dofus.Boss.Monster, dofus.Boss.Grade) });
            if (group == null || group.Members.Count == 0)
            {
                Console.WriteLine($"[Eveil] The boss {dofus.Boss.Monster} of {dofus.Id} is not a known monster.");
                return $"L'adversaire du défi ({dofus.Boss.Name}) est introuvable sur ce serveur.";
            }
            Eveil.RegisterChallenge(group.MobId, session.CharacterId, dofus.Id);
            await FightHandler.InitiateFightFromMobCollision(session.Stream, group, state.MapId, solo: true);
            if (!state.IsInFight)
            {
                Eveil.ForgetChallenge(group.MobId);
                return "Le défi n'a pas pu commencer ici ; essayez sur une autre carte.";
            }
            Console.WriteLine($"[Eveil] {state.CharacterName} challenges {dofus.Boss.Name} ({dofus.Boss.Monster}) for {dofus.Id}.");
            return $"Défi final de l'Éveil du Dofus {dofus.Name} : {dofus.Boss.Name}, niveau {Eveil.BossLevel(dofus)}, en solo.";
        }

        // ─── The window ─────────────────────────────────────────────────────────────────

        /// <summary>The F9 window's Éveil section: every Dofus, its stage and its progress, and the last answer.</summary>
        internal static object EveilView(GameSession session, string text)
        {
            var file = Eveil.Current;
            long me = session.CharacterId;
            var all = Eveil.AllOf(me);
            return new
            {
                text,
                enabled = file.Enabled,
                requireHardcore = file.RequireHardcore,
                evolved = Ascension.IsEvolved(me),
                hardcore = Tiers.WantsHardcore(me),
                level = session.State.CharacterLevel,
                dofus = file.Dofus.Select(d =>
                {
                    all.TryGetValue(d.Id, out var p);
                    var stage = Eveil.StageOf(d, p);
                    return new
                    {
                        id = d.Id,
                        name = d.Name,
                        item = d.Item,
                        level = d.Level,
                        copies = d.Copies,
                        held = PlainCopies(d, file.Label),
                        stage = stage switch
                        {
                            EveilStage.Route => "route",
                            EveilStage.Challenge => "challenge",
                            EveilStage.Done => "done",
                            _ => "none",
                        },
                        done = d.Dungeons.Count(g => p != null && p.Dungeons.Contains(g.Id)),
                        total = d.Dungeons.Count,
                        remaining = Eveil.Remaining(d, p).Select(g => g.Name).ToList(),
                        boss = d.Boss.Name,
                        bossLevel = Eveil.BossLevel(d),
                        effects = d.Effects.Concat(d.Approximated).Select(Eveil.EffectText).ToList(),
                    };
                }).ToList(),
            };
        }
    }
}
