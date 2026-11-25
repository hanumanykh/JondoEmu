using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Système's missions from the chat and the session-side actions the JondoFix window's
    /// «Missions» tab shares with it (/api/stasis/missions, /go, /claim): «.missions» shows the
    /// chapter mission and the tracks, «.go [piste]» takes the player to the objective,
    /// «.recuperer [piste]» claims a finished mission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are in Managers/Stasis/Missions.cs; what is here is the session around them: the
    /// profile's lock and save, and what the client must be told -- the Orbes paid
    /// (<see cref="PayOrbesAsync"/>, Jondo Coins or kamas as the server is set), the experience
    /// through CharacterProgression.AddExperience with the level-up window and the spells as a
    /// fight's end sends them, the sheet, and for GO the map change of
    /// <see cref="WorldMoveHandler.TeleportAsync"/>.
    /// </para>
    /// <para>
    /// GO is refused in a fight and inside a dungeon: a teleport out of a dungeon room would skip
    /// the dungeon's own exit, and out of a fight would leave the fight without its end. It lands
    /// on the zone's zaap map when the subarea has one, else on the first map of the subarea
    /// that can take the character; for a dungeon, on the map of its door.
    /// </para>
    /// <para>
    /// The callers hold the session's turn and its SessionContext, as for the dons.
    /// </para>
    /// </remarks>
    public static partial class CommandHandler
    {
        private static async Task MissionsCommandAsync(NetworkStream stream, string command, string rest, int channel, long accountId)
        {
            var session = SessionContext.Current;
            if (!session.HasCharacter) return;
            string text = command.ToLowerInvariant() switch
            {
                ".missions" => MissionsText(session),
                ".go" => GoText(await MissionGoAsync(session, SlotFromWord(rest))),
                _ => await RecoverTextAsync(session, rest),
            };
            await NotifyAsync(stream, text, channel, accountId);
        }

        /// <summary>«», «chapitre» → the chapter; «1», «piste 1», «piste1» → the first track.</summary>
        internal static string SlotFromWord(string? rest)
        {
            string word = (rest ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (word.Length == 0 || word.StartsWith("chap", StringComparison.Ordinal)) return Missions.ChapterSlot;
            if (word.StartsWith("piste", StringComparison.Ordinal)) word = word[5..];
            return int.TryParse(word, out int n) && n >= 1 ? Missions.TrackSlot(n - 1) : word;
        }

        // ─── The profile, filled and read again ─────────────────────────────────────────

        private static Func<int, bool> AchievementsOf(GameSession session)
        {
            var log = session.State.Achievements;
            return id => log != null && log.Has(id);
        }

        /// <summary>The profile with its empty slots filled and its live objectives read again; saved when it moved.</summary>
        private static MissionProfile MissionsOf(GameSession session)
        {
            var profile = MissionStore.Of(session.CharacterId);
            var has = AchievementsOf(session);
            bool changed;
            lock (profile.Gate)
            {
                int level = session.State.CharacterLevel;
                changed = Missions.Fill(profile, level, has);
                changed |= Missions.Refresh(profile, level, has).Count > 0;
            }
            if (changed) MissionStore.Save(profile);
            return profile;
        }

        // ─── The actions, shared with the control routes ────────────────────────────────

        /// <summary>
        /// «Récupérer»: the mission of <paramref name="slot"/>, or every finished one when null.
        /// "ok" with the lines said, or the refusal's code.
        /// </summary>
        internal static async Task<(string Result, List<string> Lines)> ClaimMissionAsync(GameSession session, string? slot)
        {
            var lines = new List<string>();
            var stream = session.Stream;
            if (stream == null) return ("hors-ligne", lines);
            var profile = MissionsOf(session);
            var has = AchievementsOf(session);
            double factor = MissionExperienceFactor(session.CharacterId);

            List<string> slots;
            lock (profile.Gate)
                slots = slot != null ? new List<string> { slot }
                    : profile.Slots.Values.Where(m => m.Complete).Select(m => m.Slot).OrderBy(s => s == Missions.ChapterSlot ? 0 : 1).ThenBy(s => s).ToList();
            if (slots.Count == 0) return ("rien", lines);

            long kamas = 0, experience = 0;
            foreach (string s in slots)
            {
                string result;
                MissionClaim? claim;
                lock (profile.Gate) result = Missions.Claim(profile, s, session.State.CharacterLevel, has, factor, out claim);
                if (result != "ok")
                {
                    if (slot != null) return (result, lines);
                    continue;
                }
                kamas += claim!.Kamas;
                experience += claim.Experience;
                lines.Add($"Mission récupérée : {claim.Title}.");
                if (claim.Finished.Length > 0)
                    lines.Add(s == Missions.ChapterSlot ? $"Chapitre terminé : « {claim.Finished} » — bonus de fin de chapitre !" : $"Piste terminée : « {claim.Finished} » — bonus d'Orbes !");
            }
            MissionStore.Save(profile);

            long orbes = OrbePrice(kamas);
            if (orbes > 0 && !await PayOrbesAsync(stream, orbes))
            {
                Console.WriteLine($"[Missions] {session.State.CharacterName}: {orbes} {OrbeLabel} could not be paid.");
                lines.Add(RefusalOfMissions("paiement"));
            }
            if (experience > 0) await GiveMissionExperienceAsync(session, experience);
            lines.Add($"Récompense : {orbes:N0} {OrbeLabel}, {experience:N0} points d'expérience.");
            Console.WriteLine($"[Missions] {session.State.CharacterName} claims {slots.Count} mission(s): {orbes} {OrbeLabel}, {experience} xp.");
            return ("ok", lines);
        }

        /// <summary>Mémoire de la Route (research kind «missionXp», a percent per rank): the missions' experience multiplied.</summary>
        private static double MissionExperienceFactor(long characterId)
        {
            var profile = ProgressionStore.Of(characterId);
            lock (profile.Gate) return 1.0 + Ascension.ResearchSum(profile, ProgressionContent.Current, "missionXp") / 100.0;
        }

        /// <summary>The experience through the one door, and the client told as a fight's end tells it.</summary>
        private static async Task GiveMissionExperienceAsync(GameSession session, long experience)
        {
            var gain = CharacterProgression.AddExperience(session.State, experience);
            DatabaseManager.SaveCurrentCharacter();
            if (session.State.IsInFight) return;
            if (gain.LeveledUp)
            {
                await session.SendAsync(ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(gain.NewLevel)));
                await SpellHandler.SendSpellsAndBarAsync(session);
            }
            await RefreshSheetAsync(session);
        }

        /// <summary>GO: to the objective of <paramref name="slot"/>. "ok", or the refusal's code.</summary>
        internal static async Task<string> MissionGoAsync(GameSession session, string slot)
        {
            var state = session.State;
            var stream = session.Stream;
            if (stream == null) return "hors-ligne";
            if (state.IsInFight) return "combat";
            if (DungeonManager.OfRoom(state.MapId) != null) return "donjon";
            var profile = MissionsOf(session);
            ActiveMission? mission;
            lock (profile.Gate) profile.Slots.TryGetValue(slot, out mission);
            if (mission == null || mission.Kind == Missions.KindDone) return "mission-inconnue";

            var maps = new List<long>();
            if (mission.Kind == Missions.KindDungeon && DungeonManager.Get((int)mission.Target) is { } dungeon && dungeon.EntranceMapId > 0)
                maps.Add(dungeon.EntranceMapId);
            if (mission.Zone > 0) maps.AddRange(GoDestination.Of(mission.Zone).Take(30));
            if (maps.Count == 0) return "nulle-part";
            foreach (long map in maps.Distinct())
            {
                if (map == state.MapId) return "ok";
                if (await WorldMoveHandler.TeleportAsync(stream, map) != null)
                {
                    Console.WriteLine($"[Missions] GO: {state.CharacterName} to {map} for «{mission.Title}».");
                    return "ok";
                }
            }
            return "teleport";
        }

        // ─── The fight's hook ───────────────────────────────────────────────────────────

        /// <summary>
        /// A won fight's end, in the winner's own session (FightHandler, right after the zone
        /// gauge): the fight in its subarea, each monster beaten, the Orbes earned -- the Jondo
        /// Coins of the loot in coin mode, the kamas otherwise -- and the dungeon finished when it
        /// was its boss room (the rule Tiers.OnFightEnded uses). Never throws into the fight's end.
        /// </summary>
        internal static async Task MissionsAfterFightAsync(GameSession session, FightInstance fight, long kamas, IReadOnlyDictionary<int, int> loot)
        {
            try
            {
                if (!session.HasCharacter || !fight.Reglas.ReparteBotin || fight.Reglas.PagaElKoliseo) return;
                if (Dreams.IsDreamMap(fight.RoleplayMapId)) return;
                var profile = MissionsOf(session);
                var done = new List<ActiveMission>();
                int subArea = Managers.Economy.Zones.SubAreaOf(fight.RoleplayMapId);
                long orbes = JondoCoin.IsOrbe ? (loot.TryGetValue(JondoCoin.TemplateId, out int coins) ? coins : 0) : kamas;
                int dungeonDone = 0;
                var dungeon = DungeonManager.OfRoom(fight.RoleplayMapId);
                if (dungeon != null)
                {
                    var credited = DungeonManager.ChosenFrom(dungeon) ?? dungeon;
                    if (credited != dungeon || DungeonManager.NextRoom(dungeon, fight.RoleplayMapId) == 0 || fight.RoleplayMapId == dungeon.LastRoom)
                        dungeonDone = credited.Id;
                }
                Dictionary<string, long> before;
                lock (profile.Gate)
                {
                    before = profile.Slots.ToDictionary(kv => kv.Key, kv => kv.Value.Progress, StringComparer.OrdinalIgnoreCase);
                    done.AddRange(Missions.Record(profile, Missions.KindFights, subArea, 1));
                    foreach (var group in Managers.Economy.FarmEconomy.Beaten(fight).GroupBy(m => m.MonsterId))
                        done.AddRange(Missions.Record(profile, Missions.KindKills, group.Key, group.Count()));
                    done.AddRange(Missions.Record(profile, Missions.KindOrbes, 0, orbes));
                    if (dungeonDone != 0) done.AddRange(Missions.Record(profile, Missions.KindDungeon, dungeonDone, 1));
                }
                MissionStore.Save(profile);
                // The end-of-fight recap (Managers.Economy.FightRecaps): the missions this fight moved.
                lock (profile.Gate)
                    foreach (var (slot, m) in profile.Slots)
                        if (m.Kind != Missions.KindDone && m.Progress != (before.TryGetValue(slot, out long was) ? was : -1))
                            Managers.Economy.FightRecaps.NoteMission(session.CharacterId, fight.FightId,
                                $"{m.Title} : {Math.Min(m.Progress, m.Need):N0}/{m.Need:N0}" + (m.Complete ? " ✓" : ""));
                foreach (var mission in done.Distinct())
                    await session.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(
                        $"Mission accomplie : {mission.Title}. Récupérez-la avec .recuperer ou dans la fenêtre Progression (F9).")));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] The missions of {session.CharacterId} failed after fight #{fight.FightId}: {ex.Message}");
            }
        }

        /// <summary>
        /// For the systems that count something of their own -- the Pension's eggs («hatch») --:
        /// <paramref name="amount"/> of <paramref name="kind"/> on <paramref name="key"/>, saved,
        /// and the player told of what it completed.
        /// </summary>
        internal static async Task RecordMissionAsync(GameSession session, string kind, long key, long amount)
        {
            if (!session.HasCharacter) return;
            var profile = MissionsOf(session);
            List<ActiveMission> done;
            lock (profile.Gate) done = Missions.Record(profile, kind, key, amount);
            MissionStore.Save(profile);
            foreach (var mission in done)
                await session.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice($"Mission accomplie : {mission.Title}.")));
        }

        // ─── What the window and «.missions» show ───────────────────────────────────────

        /// <summary>The Système, as /api/stasis/missions answers it.</summary>
        internal static object MissionsView(GameSession session)
        {
            var profile = MissionsOf(session);
            var file = Missions.Current;
            lock (profile.Gate)
            {
                object? Of(string slot)
                {
                    if (!profile.Slots.TryGetValue(slot, out var m)) return null;
                    var chapter = file.Chapters.FirstOrDefault(c => c.Id == m.Source);
                    var track = file.Tracks.FirstOrDefault(t => t.Id == m.Source);
                    return new
                    {
                        slot,
                        source = chapter?.Title ?? track?.Title ?? m.Source,
                        description = chapter?.Description ?? "",
                        step = m.Step < 0 ? 0 : m.Step + 1,
                        steps = chapter?.Missions.Count ?? track?.Missions.Count ?? 0,
                        last = chapter != null ? m.Step == chapter.Missions.Count - 1 : track != null && m.Step == track.Missions.Count - 1,
                        done = m.Kind == Missions.KindDone,
                        title = m.Title,
                        kind = m.Kind,
                        progress = m.Progress,
                        need = m.Need,
                        complete = m.Complete,
                        canGo = !m.Complete && m.Kind != Missions.KindDone && (m.Zone > 0 || m.Kind == Missions.KindDungeon),
                    };
                }
                return new
                {
                    level = session.State.CharacterLevel,
                    orbes = OrbesHeld(),
                    orbeLabel = OrbeLabel,
                    chaptersDone = profile.ChaptersDone,
                    chapters = file.Chapters.Count,
                    trackEvery = file.TrackEvery,
                    nextBatch = (session.State.CharacterLevel / Math.Max(1, file.TrackEvery) + 1) * Math.Max(1, file.TrackEvery),
                    chapter = Of(Missions.ChapterSlot),
                    tracks = Enumerable.Range(0, file.TracksShown).Select(i => Of(Missions.TrackSlot(i))).Where(t => t != null).ToList(),
                };
            }
        }

        private static string MissionsText(GameSession session)
        {
            var profile = MissionsOf(session);
            var file = Missions.Current;
            var text = new StringBuilder("Le Système —");
            lock (profile.Gate)
            {
                string Line(ActiveMission m) => m.Kind == Missions.KindDone
                    ? $"« {m.Title} » terminée (prochain lot au niveau {(session.State.CharacterLevel / Math.Max(1, file.TrackEvery) + 1) * Math.Max(1, file.TrackEvery)})"
                    : $"{m.Title} : {m.Progress:N0}/{m.Need:N0}" + (m.Complete ? " ✓ à récupérer" : "");
                if (profile.Slots.TryGetValue(Missions.ChapterSlot, out var c))
                {
                    var chapter = file.Chapters.FirstOrDefault(x => x.Id == c.Source);
                    text.Append($"\nChapitre « {chapter?.Title ?? c.Source} »" + (c.Step >= 0 && chapter != null ? $" ({c.Step + 1}/{chapter.Missions.Count})" : "") + " : " + Line(c));
                }
                else text.Append("\nTous les chapitres sont terminés.");
                for (int i = 0; i < file.TracksShown; i++)
                    if (profile.Slots.TryGetValue(Missions.TrackSlot(i), out var t))
                        text.Append($"\nPiste {i + 1} : " + Line(t));
            }
            text.Append("\n.go [piste] pour y aller, .recuperer [piste] pour réclamer. Fenêtre Progression (F9), onglet Missions.");
            return text.ToString();
        }

        private static string GoText(string result) => result == "ok" ? "En route vers l'objectif !" : RefusalOfMissions(result);

        private static async Task<string> RecoverTextAsync(GameSession session, string rest)
        {
            string? slot = (rest ?? "").Trim().Length == 0 ? null : SlotFromWord(rest);
            var (result, lines) = await ClaimMissionAsync(session, slot);
            return result == "ok" ? string.Join("\n", lines) : RefusalOfMissions(result);
        }

        internal static string RefusalOfMissions(string code) => Missions.RefusalText(code);
    }
}
