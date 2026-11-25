using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    // ═══ The authored side: content/progression/missions.json ═══════════════════════════════

    /// <summary>
    /// missions.json: the chapters of the Système, the side tracks, and the numbers a designer
    /// moves -- how often a new batch of tracks comes, what the rewards are worth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file does not name zones, monsters or dungeons: a mission says WHAT to do ("win 4
    /// fights in a zone", "beat 12 of one of its monsters", "finish a dungeon") and the level band
    /// of its chapter; WHICH zone, monster or dungeon is drawn from the game's own data when the
    /// mission becomes active (<see cref="Missions.Resolve"/>: the subareas by the level of their
    /// roster, the dungeons by their level, the exploration achievements by the level of their
    /// subareas), near the character's level, and then stored with the mission so it never moves
    /// under the player. Hand-writing 300 levels of zone names would rot with every data import;
    /// a template with «{zone}» does not.
    /// </para>
    /// <para>
    /// The Orbes amounts are written in kamas, the unit every other Stasis file of this server is
    /// calibrated in, and turned into the Orbes' currency (Jondo Coins) when paid or counted --
    /// CommandHandler.OrbePrice --, so switching the Orbes between kamas and coins needs no edit here.
    /// </para>
    /// </remarks>
    internal sealed class MissionsFile
    {
        /// <summary>A new batch of tracks every so many levels (Stasis: 10).</summary>
        public int TrackEvery { get; set; } = 10;
        /// <summary>How many tracks are shown at once (Stasis: 2).</summary>
        public int TracksShown { get; set; } = 2;
        /// <summary>A track's completion bonus, in kamas per character level (paid as Orbes).</summary>
        public long TrackBonusKamasPerLevel { get; set; } = 150;
        public List<ChapterDefinition> Chapters { get; set; } = new();
        public List<TrackDefinition> Tracks { get; set; } = new();
    }

    /// <summary>A chapter: a level band and its missions, done one after the other.</summary>
    internal sealed class ChapterDefinition
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int MinLevel { get; set; } = 1;
        public int MaxLevel { get; set; } = 20;
        /// <summary>Each mission's Orbes, in kamas.</summary>
        public long RewardKamas { get; set; }
        /// <summary>Each mission's experience, as a share of what the character's level needs to the next.</summary>
        public double RewardXpShare { get; set; } = 0.1;
        /// <summary>The bonus of the chapter's last mission, on top of its own reward.</summary>
        public long FinalKamas { get; set; }
        public double FinalXpShare { get; set; } = 0.5;
        public List<MissionTemplate> Missions { get; set; } = new();
    }

    /// <summary>A side track: a few missions and an Orbes bonus when the last is claimed.</summary>
    internal sealed class TrackDefinition
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int MinLevel { get; set; } = 1;
        public int MaxLevel { get; set; } = 300;
        /// <summary>Each mission's experience, as a share of the level's span.</summary>
        public double XpShare { get; set; } = 0.08;
        public List<MissionTemplate> Missions { get; set; } = new();
    }

    /// <summary>One mission as authored: its kind and numbers; the target is drawn when it starts.</summary>
    internal sealed class MissionTemplate
    {
        /// <summary>fights, kills, orbes, level, dungeon, achievement, hatch (<see cref="Missions.Kinds"/>).</summary>
        public string Kind { get; set; } = "";
        public long Count { get; set; } = 1;
        /// <summary>A fixed target (subarea, monster, dungeon, achievement); 0 draws one from the game's data.</summary>
        public long Target { get; set; }
        /// <summary>For «level»: the level to reach; 0 means the band's top.</summary>
        public int Level { get; set; }
        /// <summary>The French title, with {count} {zone} {monster} {dungeon} {achievement} {level} {orbes}; empty takes the kind's.</summary>
        public string Title { get; set; } = "";
    }

    // ═══ The character's side ═══════════════════════════════════════════════════════════════

    /// <summary>A mission under way, as stored: what it asks, how far it is, where GO goes.</summary>
    internal sealed class ActiveMission
    {
        /// <summary><see cref="Missions.ChapterSlot"/> or a track slot (piste1, piste2).</summary>
        public string Slot { get; set; } = "";
        /// <summary>The chapter's or the track's id.</summary>
        public string Source { get; set; } = "";
        /// <summary>The template's index; -1 for the level gate in front of a chapter.</summary>
        public int Step { get; set; }
        /// <summary>For tracks: the batch (level / TrackEvery) it was handed in.</summary>
        public int Batch { get; set; }
        /// <summary>The kind; <see cref="Missions.KindDone"/> for a track finished in its batch.</summary>
        public string Kind { get; set; } = "";
        public long Target { get; set; }
        public long Need { get; set; }
        public long Progress { get; set; }
        public string Title { get; set; } = "";
        /// <summary>The subarea GO takes the player to; 0 for none (the dungeon's door is the target's).</summary>
        public int Zone { get; set; }

        public bool Complete => Kind != Missions.KindDone && Progress >= Need;
    }

    /// <summary>The Système's state for one character: which chapter, and the missions in their slots.</summary>
    internal sealed class MissionProfile
    {
        public long CharacterId { get; init; }
        /// <summary>The chapter's index in missions.json; past the last, every chapter is done.</summary>
        public int Chapter { get; set; }
        public int ChaptersDone { get; set; }
        public Dictionary<string, ActiveMission> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal object Gate { get; } = new();
    }

    /// <summary>What a claim pays and what it moved, for the caller to pay and to say.</summary>
    internal sealed class MissionClaim
    {
        public string Slot { get; init; } = "";
        public string Title { get; init; } = "";
        /// <summary>Orbes, in kamas (the caller turns them into coins).</summary>
        public long Kamas { get; set; }
        public long Experience { get; set; }
        /// <summary>The chapter or track this claim finished, or "".</summary>
        public string Finished { get; set; } = "";
    }

    /// <summary>
    /// What the missions read of the game: the zones and their levels, names, dungeons, the
    /// exploration achievements and the experience a level spans. The live one reads the world's
    /// tables (<see cref="GameMissionWorld"/>); the tests give their own.
    /// </summary>
    internal interface IMissionWorld
    {
        /// <summary>Every subarea with monsters, and its level.</summary>
        IReadOnlyList<(int SubArea, int Level)> Zones { get; }
        bool HasZaap(int subArea);
        string ZoneName(int subArea);
        IReadOnlyList<int> Roster(int subArea);
        string MonsterName(int monster);
        /// <summary>The dungeons a player can walk into, with their level.</summary>
        IReadOnlyList<(int Id, string Name, int Level)> Dungeons { get; }
        /// <summary>The achievements made only of exploring subareas: those a mission can ask for.</summary>
        IReadOnlyList<(int Id, string Name, IReadOnlyList<int> SubAreas)> ExplorationAchievements { get; }
        /// <summary>The experience between a level and the next.</summary>
        long ExperienceSpan(int level);
        /// <summary>A kamas amount in the Orbes' currency (CommandHandler.OrbePrice).</summary>
        long Orbes(long kamas);
        string OrbeLabel { get; }
    }

    /// <summary>
    /// A kind of objective: whether it can be handed out on this server now, and how its target is
    /// drawn. Registered in <see cref="Missions.Kinds"/>, so a system built later -- the Pension's
    /// eggs -- adds or switches on its kind without touching the rest.
    /// </summary>
    internal sealed class ObjectiveKind
    {
        public string Id { get; init; } = "";
        /// <summary>False skips every mission of the kind (a chapter goes on with its next one).</summary>
        public Func<bool> Available { get; init; } = () => true;
        public string DefaultTitle { get; init; } = "";
        /// <summary>Fills Target, Need, Zone and the title's words; false when nothing fits (skipped).</summary>
        public Func<MissionTemplate, Missions.Draw, ActiveMission, Dictionary<string, string>, bool> Resolve { get; init; } = (_, _, _, _) => true;
    }

    // ═══ The rules ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The «Système» missions of the Stasis rules (spec §10, §22): one chapter mission and two
    /// side tracks shown at once, progressing by themselves, claimed with «Récupérer», and a GO
    /// that takes the player to the objective.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CHAPTERS follow each other by level band (1-20, 20-40 ... 180-200, then the tiers to 300).
    /// One mission is active at a time; claiming it pays its Orbes and experience and starts the
    /// next; the last of a chapter adds the chapter's bonus. A character who outlevels a chapter
    /// is moved to the band of his level when a new chapter would start rather than sent back to
    /// fight level-5 monsters at 60; one who is under a chapter's band first gets a level mission.
    /// </para>
    /// <para>
    /// TRACKS come in batches, a new one every <see cref="MissionsFile.TrackEvery"/> levels: two
    /// tracks of 3 to 5 missions each, experience per mission and an Orbes bonus when the track is
    /// done. A track still unclaimed when the next batch comes is replaced, unless its current
    /// mission is complete and waiting for «Récupérer» -- what was earned is never taken away.
    /// </para>
    /// <para>
    /// PROGRESS: what can only be counted when it happens (fights won in a zone, monsters beaten,
    /// Orbes earned, dungeons finished, eggs hatched) is fed by <see cref="Record"/> from the
    /// game's own hooks -- one call after a won fight, one from the Pension when it exists --;
    /// what is a state (a level reached, an achievement earned) is read live by <see cref="Refresh"/>
    /// whenever the missions are looked at, so nothing is missed if a hook is not on its path.
    /// </para>
    /// <para>
    /// The rules here touch only the <see cref="MissionProfile"/>: paying, telling the client and
    /// teleporting are CommandHandler.Missions.cs's, the same split as Dons/Ascension and
    /// CommandHandler.Progression.cs. An Ascension resets the route (<see cref="ResetRoute"/>), as
    /// on Stasis, where «missions de route» are part of what an Ascension wipes.
    /// </para>
    /// </remarks>
    internal static class Missions
    {
        public const string ChapterSlot = "chapitre";
        public const string KindDone = "done";
        public const string KindFights = "fights", KindKills = "kills", KindOrbes = "orbes", KindLevel = "level",
                            KindDungeon = "dungeon", KindAchievement = "achievement", KindHatch = "hatch";

        /// <summary>The track slots, in order: piste1, piste2 ... as many as TracksShown.</summary>
        internal static string TrackSlot(int index) => "piste" + (index + 1);

        /// <summary>
        /// Whether eggs can hatch on this server: the Pension's side sets it when it exists. Until
        /// then the «hatch» missions are skipped and the chapters go on without them.
        /// </summary>
        public static Func<bool> HatchAvailable { get; set; } = () => false;

        /// <summary>The draw a mission's target comes from: the level, the band, a stable seed, the world.</summary>
        internal sealed record Draw(int Level, int BandMin, int BandMax, int Seed, IMissionWorld World, Func<int, bool> HasAchievement);

        /// <summary>The objective kinds by id; add one with <see cref="RegisterKind"/>.</summary>
        internal static readonly ConcurrentDictionary<string, ObjectiveKind> Kinds = new(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterKind(ObjectiveKind kind) => Kinds[kind.Id] = kind;

        static Missions()
        {
            RegisterKind(new ObjectiveKind
            {
                Id = KindFights, DefaultTitle = "Remporter {count} combats : {zone}",
                Resolve = (t, d, m, words) =>
                {
                    int zone = t.Target > 0 ? (int)t.Target : PickZone(d);
                    if (zone == 0) return false;
                    m.Target = zone; m.Zone = zone; m.Need = Math.Max(1, t.Count);
                    words["zone"] = d.World.ZoneName(zone);
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindKills, DefaultTitle = "Vaincre {count} × {monster} ({zone})",
                Resolve = (t, d, m, words) =>
                {
                    int zone = PickZone(d);
                    int monster = (int)t.Target;
                    if (monster == 0)
                    {
                        if (zone == 0) return false;
                        var roster = d.World.Roster(zone).Where(id => d.World.MonsterName(id).Length > 0).ToList();
                        if (roster.Count == 0) return false;
                        monster = roster[Pick(d.Seed, 7, roster.Count)];
                    }
                    m.Target = monster; m.Zone = zone; m.Need = Math.Max(1, t.Count);
                    words["monster"] = d.World.MonsterName(monster);
                    words["zone"] = zone == 0 ? "" : d.World.ZoneName(zone);
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindOrbes, DefaultTitle = "Gagner {count} {orbes} en combat",
                Resolve = (t, d, m, words) =>
                {
                    m.Need = Math.Max(1, d.World.Orbes(t.Count));
                    m.Zone = PickZone(d);
                    words["count"] = m.Need.ToString("N0");
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindLevel, DefaultTitle = "Atteindre le niveau {level}",
                Resolve = (t, d, m, words) =>
                {
                    int level = t.Level > 0 ? t.Level : d.BandMax;
                    // Already there: a free reward is no mission.
                    if (level <= d.Level) return false;
                    m.Need = level; m.Zone = PickZone(d);
                    words["level"] = level.ToString();
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindDungeon, DefaultTitle = "Terminer le donjon : {dungeon}",
                Resolve = (t, d, m, words) =>
                {
                    var dungeons = d.World.Dungeons;
                    (int Id, string Name, int Level) chosen = default;
                    if (t.Target > 0) chosen = dungeons.FirstOrDefault(x => x.Id == t.Target);
                    else
                    {
                        int aim = Math.Clamp(d.Level, d.BandMin, d.BandMax);
                        var near = dungeons.Where(x => x.Level >= d.BandMin && x.Level <= d.BandMax && x.Level <= aim + 15)
                                           .OrderBy(x => Math.Abs(x.Level - aim)).ThenBy(x => x.Id).Take(4).ToList();
                        if (near.Count > 0) chosen = near[Pick(d.Seed, 11, near.Count)];
                    }
                    if (chosen.Id == 0) return false;
                    m.Target = chosen.Id; m.Need = Math.Max(1, t.Count);
                    words["dungeon"] = chosen.Name;
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindAchievement, DefaultTitle = "Accomplir le succès « {achievement} »",
                Resolve = (t, d, m, words) =>
                {
                    var all = d.World.ExplorationAchievements;
                    (int Id, string Name, IReadOnlyList<int> SubAreas) chosen = default;
                    if (t.Target > 0) chosen = all.FirstOrDefault(a => a.Id == t.Target);
                    else
                    {
                        var levels = d.World.Zones.ToDictionary(z => z.SubArea, z => z.Level);
                        var fitting = all.Where(a => a.SubAreas.Count > 0 && !d.HasAchievement(a.Id)
                                                     && a.SubAreas.All(s => levels.TryGetValue(s, out int l) && l >= d.BandMin && l <= d.BandMax))
                                         .OrderBy(a => a.Id).ToList();
                        if (fitting.Count > 0) chosen = fitting[Pick(d.Seed, 13, fitting.Count)];
                    }
                    if (chosen.Id == 0 || d.HasAchievement(chosen.Id)) return false;
                    m.Target = chosen.Id; m.Need = 1; m.Zone = chosen.SubAreas.FirstOrDefault();
                    words["achievement"] = chosen.Name;
                    return true;
                },
            });
            RegisterKind(new ObjectiveKind
            {
                Id = KindHatch, DefaultTitle = "Faire éclore {count} œuf(s) à la Pension",
                Available = () => HatchAvailable(),
                Resolve = (t, d, m, words) => { m.Need = Math.Max(1, t.Count); return true; },
            });
        }

        // ─── Content ────────────────────────────────────────────────────────────────────

        private static MissionsFile? _current;
        private static readonly object _loadLock = new();

        /// <summary>What is in force: missions.json read on first use, or what a test installed.</summary>
        internal static MissionsFile Current
        {
            get
            {
                if (_current != null) return _current;
                lock (_loadLock) return _current ??= Load(Jondo.Unity.Launcher.Paths.ContentFile("progression"));
            }
            set => _current = value;
        }

        internal static MissionsFile Load(string directory)
        {
            string path = Path.Combine(directory, "missions.json");
            try
            {
                if (File.Exists(path)) return ProgressionContent.Parse<MissionsFile>(File.ReadAllText(path)) ?? new MissionsFile();
                Console.WriteLine($"[Missions] {path} is missing: the Système has no missions.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] {path} could not be read: {ex.Message}");
            }
            return new MissionsFile();
        }

        /// <summary>The live world; a test swaps it.</summary>
        internal static IMissionWorld World { get; set; } = new GameMissionWorld();

        // ─── Drawing targets ────────────────────────────────────────────────────────────

        /// <summary>A stable pick: the same character, mission and salt always land on the same index.</summary>
        internal static int Pick(int seed, int salt, int count)
            => count <= 1 ? 0 : (int)((uint)HashCode.Combine(seed, salt) % (uint)count);

        /// <summary>
        /// A zone of the band near the character's level: among the six closest, those with a zaap
        /// first (GO lands on it), then a stable pick.
        /// </summary>
        internal static int PickZone(Draw d)
        {
            int aim = Math.Clamp(d.Level, d.BandMin, d.BandMax);
            var near = d.World.Zones.Where(z => z.Level >= d.BandMin - 2 && z.Level <= d.BandMax && d.World.ZoneName(z.SubArea).Length > 0)
                                    .OrderBy(z => Math.Abs(z.Level - aim) / 5).ThenBy(z => d.World.HasZaap(z.SubArea) ? 0 : 1)
                                    .ThenBy(z => z.SubArea).Take(6).ToList();
            return near.Count == 0 ? 0 : near[Pick(d.Seed, 3, near.Count)].SubArea;
        }

        /// <summary>
        /// A template made into a mission, or null when its kind is off or nothing in the world fits
        /// -- the caller then goes on to the next template.
        /// </summary>
        internal static ActiveMission? Resolve(MissionTemplate template, Draw draw)
        {
            if (!Kinds.TryGetValue(template.Kind, out var kind) || !kind.Available()) return null;
            var mission = new ActiveMission { Kind = kind.Id };
            var words = new Dictionary<string, string>
            {
                ["count"] = template.Count.ToString("N0"),
                ["orbes"] = draw.World.OrbeLabel,
                ["level"] = (template.Level > 0 ? template.Level : draw.BandMax).ToString(),
                ["zone"] = "", ["monster"] = "", ["dungeon"] = "", ["achievement"] = "",
            };
            if (!kind.Resolve(template, draw, mission, words)) return null;
            string title = template.Title.Length > 0 ? template.Title : kind.DefaultTitle;
            foreach (var (key, value) in words) title = title.Replace("{" + key + "}", value, StringComparison.Ordinal);
            mission.Title = title.Replace(" ()", "", StringComparison.Ordinal).Trim();
            return mission;
        }

        // ─── Filling the slots ──────────────────────────────────────────────────────────

        /// <summary>
        /// Every empty slot given its mission: the chapter's next one, and the tracks of the
        /// character's batch. Returns whether anything changed (and so wants a save).
        /// </summary>
        internal static bool Fill(MissionProfile profile, int level, Func<int, bool> hasAchievement, IMissionWorld? world = null)
        {
            world ??= World;
            var file = Current;
            bool changed = false;
            // A chapter where nothing resolves (no zone of its band in this world, eggs off) is
            // passed rather than left as a hole the Système never fills.
            for (int guard = 0; !profile.Slots.ContainsKey(ChapterSlot) && profile.Chapter < file.Chapters.Count && guard <= file.Chapters.Count; guard++)
            {
                if (!StartChapterStep(profile, level, 0, hasAchievement, world)) profile.Chapter++;
                changed = true;
            }

            int batch = Math.Max(0, level / Math.Max(1, file.TrackEvery));
            for (int i = 0; i < file.TracksShown; i++)
            {
                string slot = TrackSlot(i);
                profile.Slots.TryGetValue(slot, out var current);
                bool stale = current == null || (current.Batch < batch && !current.Complete);
                if (!stale) continue;
                var taken = profile.Slots.Values.Where(m => m.Slot != slot && m.Slot != ChapterSlot).Select(m => m.Source).ToHashSet();
                // An empty slot with no track to give stays empty without counting as a change.
                if (profile.Slots.Remove(slot)) changed = true;
                var track = PickTrack(profile.CharacterId, level, batch, i, taken);
                if (track == null) continue;
                var first = StartTrackStep(track, slot, batch, 0, profile.CharacterId, level, hasAchievement, world);
                if (first != null) { profile.Slots[slot] = first; changed = true; }
            }
            return changed;
        }

        /// <summary>The track a slot gets in a batch: one fitting the level, not shown already, picked stably.</summary>
        internal static TrackDefinition? PickTrack(long characterId, int level, int batch, int index, ISet<string> taken)
        {
            var fitting = Current.Tracks.Where(t => t.MinLevel <= level && level <= t.MaxLevel && t.Missions.Count > 0 && !taken.Contains(t.Id))
                                 .OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            if (fitting.Count == 0) return null;
            return fitting[Pick(HashCode.Combine(characterId, batch), 17 + index, fitting.Count)];
        }

        private static ActiveMission? StartTrackStep(TrackDefinition track, string slot, int batch, int step, long characterId, int level,
                                                     Func<int, bool> hasAchievement, IMissionWorld world)
        {
            int band = Math.Max(1, Current.TrackEvery);
            int bandMin = Math.Max(track.MinLevel, level - band / 2);
            int bandMax = Math.Min(track.MaxLevel, Math.Max(bandMin, level + band / 2));
            for (; step < track.Missions.Count; step++)
            {
                var draw = new Draw(level, bandMin, bandMax, HashCode.Combine(characterId, track.Id, batch, step), world, hasAchievement);
                var mission = Resolve(track.Missions[step], draw);
                if (mission == null) continue;
                mission.Slot = slot; mission.Source = track.Id; mission.Step = step; mission.Batch = batch;
                return mission;
            }
            return null;
        }

        /// <summary>
        /// The chapter mission from <paramref name="step"/> on: the first that resolves. Past the
        /// chapter's last, the chapter is done and the next one starts. False when nothing changed.
        /// </summary>
        private static bool StartChapterStep(MissionProfile profile, int level, int step, Func<int, bool> hasAchievement, IMissionWorld world)
        {
            var chapters = Current.Chapters;
            // A fresh chapter the character has outgrown: on to the one of his level.
            if (step == 0)
                while (profile.Chapter < chapters.Count - 1 && chapters[profile.Chapter].MaxLevel < level) profile.Chapter++;
            if (profile.Chapter >= chapters.Count) return false;
            var chapter = chapters[profile.Chapter];

            if (step == 0 && level < chapter.MinLevel)
            {
                profile.Slots[ChapterSlot] = new ActiveMission
                {
                    Slot = ChapterSlot, Source = chapter.Id, Step = -1, Kind = KindLevel, Need = chapter.MinLevel,
                    Title = $"Atteindre le niveau {chapter.MinLevel} pour ouvrir « {chapter.Title} »",
                    Zone = PickZone(new Draw(level, Math.Max(1, level - 5), level + 5, HashCode.Combine(profile.CharacterId, chapter.Id), world, hasAchievement)),
                };
                return true;
            }
            for (int s = Math.Max(0, step); s < chapter.Missions.Count; s++)
            {
                var draw = new Draw(level, chapter.MinLevel, chapter.MaxLevel, HashCode.Combine(profile.CharacterId, chapter.Id, s), world, hasAchievement);
                var mission = Resolve(chapter.Missions[s], draw);
                if (mission == null) continue;
                mission.Slot = ChapterSlot; mission.Source = chapter.Id; mission.Step = s;
                profile.Slots[ChapterSlot] = mission;
                return true;
            }
            return false;
        }

        // ─── Progress ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Something counted happened: <paramref name="amount"/> of <paramref name="kind"/> on
        /// <paramref name="key"/> (the subarea of a fight, the monster beaten, the dungeon finished,
        /// 0 for Orbes and eggs). Returns the missions it completed just now.
        /// </summary>
        internal static List<ActiveMission> Record(MissionProfile profile, string kind, long key, long amount)
        {
            var done = new List<ActiveMission>();
            if (amount <= 0) return done;
            foreach (var mission in profile.Slots.Values)
            {
                if (!string.Equals(mission.Kind, kind, StringComparison.OrdinalIgnoreCase) || mission.Complete) continue;
                if (mission.Kind is KindLevel or KindAchievement) continue;
                if (mission.Target != 0 && mission.Target != key) continue;
                mission.Progress = Math.Min(mission.Need, mission.Progress + amount);
                if (mission.Complete) done.Add(mission);
            }
            return done;
        }

        /// <summary>The live objectives read again: the level reached, the achievements earned. Returns those it completed.</summary>
        internal static List<ActiveMission> Refresh(MissionProfile profile, int level, Func<int, bool> hasAchievement)
        {
            var done = new List<ActiveMission>();
            foreach (var mission in profile.Slots.Values)
            {
                bool was = mission.Complete;
                if (mission.Kind == KindLevel) mission.Progress = Math.Min(mission.Need, level);
                else if (mission.Kind == KindAchievement) mission.Progress = hasAchievement((int)mission.Target) ? 1 : 0;
                else continue;
                if (!was && mission.Complete) done.Add(mission);
            }
            return done;
        }

        // ─── Claiming ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// «Récupérer» on a slot: the reward worked out and the next mission started. "ok" with the
        /// claim, or the refusal's code (mission-inconnue, mission-en-cours). Paying is the caller's.
        /// </summary>
        internal static string Claim(MissionProfile profile, string slot, int level, Func<int, bool> hasAchievement,
                                     double experienceFactor, out MissionClaim? claim, IMissionWorld? world = null)
        {
            world ??= World;
            claim = null;
            Refresh(profile, level, hasAchievement);
            if (!profile.Slots.TryGetValue(slot, out var mission) || mission.Kind == KindDone) return "mission-inconnue";
            if (!mission.Complete) return "mission-en-cours";
            var file = Current;
            long span = Math.Max(1, world.ExperienceSpan(level));
            long Xp(double share) => (long)Math.Round(span * share * experienceFactor);

            claim = new MissionClaim { Slot = slot, Title = mission.Title };
            if (slot == ChapterSlot)
            {
                var chapter = file.Chapters.FirstOrDefault(c => c.Id == mission.Source);
                profile.Slots.Remove(ChapterSlot);
                if (chapter == null) return "ok";
                if (mission.Step >= 0)
                {
                    claim.Kamas = chapter.RewardKamas;
                    claim.Experience = Xp(chapter.RewardXpShare);
                }
                bool next = mission.Step >= 0
                    ? StartChapterStep(profile, level, mission.Step + 1, hasAchievement, world) && profile.Slots[ChapterSlot].Source == chapter.Id
                    : StartChapterStep(profile, level, 0, hasAchievement, world);
                if (mission.Step >= 0 && !next)
                {
                    // The chapter's last mission: its bonus, and the next chapter.
                    claim.Kamas += chapter.FinalKamas;
                    claim.Experience += Xp(chapter.FinalXpShare);
                    claim.Finished = chapter.Title;
                    profile.ChaptersDone++;
                    profile.Chapter = Math.Max(profile.Chapter, file.Chapters.IndexOf(chapter) + 1);
                    profile.Slots.Remove(ChapterSlot);
                    StartChapterStep(profile, level, 0, hasAchievement, world);
                }
                return "ok";
            }

            var track = file.Tracks.FirstOrDefault(t => t.Id == mission.Source);
            profile.Slots.Remove(slot);
            if (track == null) return "ok";
            claim.Experience = Xp(track.XpShare);
            var following = StartTrackStep(track, slot, mission.Batch, mission.Step + 1, profile.CharacterId, level, hasAchievement, world);
            if (following != null) profile.Slots[slot] = following;
            else
            {
                claim.Kamas = file.TrackBonusKamasPerLevel * Math.Max(1, level);
                claim.Finished = track.Title;
                // Finished in its batch: the slot stays empty until the next batch.
                profile.Slots[slot] = new ActiveMission { Slot = slot, Source = track.Id, Kind = KindDone, Batch = mission.Batch, Title = track.Title };
            }
            return "ok";
        }

        /// <summary>The French text of a refusal code of the missions.</summary>
        internal static string RefusalText(string code) => code switch
        {
            "mission-inconnue" => "Aucune mission dans cet emplacement.",
            "mission-en-cours" => "Cette mission n'est pas encore terminée.",
            "rien" => "Aucune mission terminée à récupérer.",
            "combat" => "Impossible en combat.",
            "donjon" => "Impossible dans un donjon : sortez-en d'abord.",
            "nulle-part" => "Cette mission n'a pas de lieu où vous emmener.",
            "teleport" => "La téléportation a échoué : aucune carte sûre pour vous accueillir.",
            "hors-ligne" => "Votre personnage n'est pas en jeu.",
            "paiement" => "La récompense n'a pas pu être versée.",
            _ => code,
        };

        // ─── Hooks ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// From CharacterProgression.AddExperience when levels are gained: only a character whose
        /// missions are already in memory is looked at (no database read on that hot, test-run
        /// path); a new batch of tracks is handed there, the rest waits for the next look.
        /// </summary>
        internal static void OnLevelsGained(long characterId, int newLevel)
        {
            var profile = MissionStore.Peek(characterId);
            if (profile == null) return;
            try
            {
                bool changed;
                lock (profile.Gate) changed = Fill(profile, newLevel, _ => false) | Refresh(profile, newLevel, _ => false).Count > 0;
                if (changed) MissionStore.Save(profile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] Levels of {characterId} not followed: {ex.Message}");
            }
        }

        /// <summary>From the Ascension: the route starts again from the first chapter, tracks dropped.</summary>
        internal static void ResetRoute(long characterId)
        {
            if (characterId <= 0) return;
            try
            {
                var profile = MissionStore.Of(characterId);
                lock (profile.Gate)
                {
                    profile.Chapter = 0;
                    profile.Slots.Clear();
                }
                MissionStore.Save(profile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] The route of {characterId} could not be reset: {ex.Message}");
            }
        }
    }

    // ═══ The live world ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The world's tables as the missions read them: the farming loop's rosters and zone levels
    /// (Economy.Zones), the zaaps (Interactives.Waypoints), the dungeons (DungeonManager), the
    /// achievements' exploration objectives. Built lazily and cached: the whole subarea list is
    /// read once, without the drop tables Zones.Of would also read.
    /// </summary>
    internal sealed class GameMissionWorld : IMissionWorld
    {
        private IReadOnlyList<(int, int)>? _zones;
        private HashSet<int>? _zaaps;
        private IReadOnlyList<(int, string, IReadOnlyList<int>)>? _explorations;
        private readonly ConcurrentDictionary<int, string> _zoneNames = new();
        private readonly ConcurrentDictionary<int, string> _monsterNames = new();
        private readonly object _gate = new();

        /// <summary>A monster a farm mission may send the player after: not a boss, mini-boss nor quest monster.</summary>
        private static bool IsHuntable(int monsterId)
        {
            long flags = MonsterFlags.Of(monsterId);
            if (flags < 0) return true;
            const long boss = 4, miniBoss = 8, quest = 16;
            return (flags & (boss | miniBoss | quest)) == 0;
        }

        public IReadOnlyList<(int SubArea, int Level)> Zones
        {
            get
            {
                lock (_gate)
                {
                    if (_zones != null) return _zones;
                    var list = new List<(int, int)>();
                    // Hunting zones only: a subarea whose monsters include a quest monster or a
                    // boss (Rocher des Sacrifiés and the like) is a quest or story place, not a farm.
                    foreach (var (subArea, roster) in Economy.Zones.Rosters)
                        if (roster.Count > 0 && roster.All(IsHuntable)) list.Add((subArea, Economy.Zones.LevelOf(roster)));
                    return _zones = list;
                }
            }
        }

        public bool HasZaap(int subArea)
        {
            lock (_gate) _zaaps ??= Interactives.Waypoints.Where(w => w.Activated).Select(w => w.SubAreaId).ToHashSet();
            return _zaaps.Contains(subArea);
        }

        public string ZoneName(int subArea) => _zoneNames.GetOrAdd(subArea, id => DatabaseManager.GetSubAreaName(id) ?? "");

        public IReadOnlyList<int> Roster(int subArea)
            => Economy.Zones.Rosters.TryGetValue(subArea, out var roster) ? roster : Array.Empty<int>();

        public string MonsterName(int monster) => _monsterNames.GetOrAdd(monster, id =>
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT t.Text FROM Monsters m JOIN Translations t ON t.Key = m.NameId WHERE m.Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteScalar() as string ?? "";
            }
            catch
            {
                return "";
            }
        });

        public IReadOnlyList<(int Id, string Name, int Level)> Dungeons
            => DungeonManager.All.Values
                   .Where(d => d.BaseId == 0 && d.Rooms.Count > 0 && d.EntranceMapId > 0 && d.Name.Length > 0)
                   .Select(d => (d.Id, d.Name, d.OptimalLevel > 0 ? d.OptimalLevel : d.MinLevel))
                   .ToList();

        public IReadOnlyList<(int Id, string Name, IReadOnlyList<int> SubAreas)> ExplorationAchievements
        {
            get
            {
                lock (_gate)
                {
                    if (_explorations != null) return _explorations;
                    var list = new List<(int, string, IReadOnlyList<int>)>();
                    var book = Achievements.Book;
                    if (book != null)
                        foreach (var a in book.All())
                        {
                            if (a.Objectives.Count == 0 || a.Name.Length == 0) continue;
                            if (!a.Objectives.All(o => o.Link?.Kind == Jondo.Unity.World.Achievements.AchievementLink.Explore && o.Link.Subarea > 0)) continue;
                            list.Add((a.Id, a.Name, a.Objectives.Select(o => o.Link!.Subarea).Distinct().ToList()));
                        }
                    // Not cached while the book is not read yet (start-up order).
                    if (book != null) _explorations = list;
                    return list;
                }
            }
        }

        public long ExperienceSpan(int level)
            => ExperienceTable.IsLoaded ? Math.Max(1, ExperienceTable.NextLevelFloor(level) - ExperienceTable.LevelFloor(level)) : 1000L * level;

        public long Orbes(long kamas) => Handlers.CommandHandler.OrbePrice(kamas);

        public string OrbeLabel => Handlers.CommandHandler.OrbeLabel;
    }

    // ═══ Storage ════════════════════════════════════════════════════════════════════════════

    internal interface IMissionStore
    {
        MissionProfile Load(long characterId);
        void Save(MissionProfile profile);
    }

    /// <summary>
    /// The mission profiles, cached, over an <see cref="IMissionStore"/>: world.db live, memory in
    /// the tests. Unless set, it follows the progression store -- a suite that put
    /// <see cref="ProgressionStore"/> in memory (an Ascension in a test resets the route) does not
    /// get a world.db written next to its binaries.
    /// </summary>
    internal static class MissionStore
    {
        private static IMissionStore? _backend;
        private static readonly ConcurrentDictionary<long, MissionProfile> _cache = new();

        internal static IMissionStore Backend
        {
            get => _backend ??= ProgressionStore.Backend is MemoryProgressionStore ? new MemoryMissionStore() : new SqliteMissionStore();
            set
            {
                _backend = value;
                _cache.Clear();
            }
        }

        internal static MissionProfile Of(long characterId) => _cache.GetOrAdd(characterId, id => Backend.Load(id));
        internal static MissionProfile? Peek(long characterId) => _cache.TryGetValue(characterId, out var p) ? p : null;
        internal static void Save(MissionProfile profile) => Backend.Save(profile);
    }

    internal sealed class MemoryMissionStore : IMissionStore
    {
        public int Saves { get; private set; }
        public MissionProfile Load(long characterId) => new() { CharacterId = characterId };
        public void Save(MissionProfile profile) => Saves++;
    }

    /// <summary>
    /// world.db's side: StasisMissionProfiles (the chapter reached) and StasisMissions (one row per
    /// slot, the mission as it was drawn and how far it is). A save rewrites the character's rows
    /// in one transaction, as the progression store does: three rows, no diffing to get wrong.
    /// </summary>
    internal sealed class SqliteMissionStore : IMissionStore
    {
        private static bool _tableReady;
        private static readonly object _tableLock = new();

        public MissionProfile Load(long characterId)
        {
            var profile = new MissionProfile { CharacterId = characterId };
            try
            {
                EnsureTables();
                using var connection = Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Chapter, ChaptersDone FROM StasisMissionProfiles WHERE CharacterId = $c;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        profile.Chapter = reader.GetInt32(0);
                        profile.ChaptersDone = reader.GetInt32(1);
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Slot, Source, Step, Batch, Kind, Target, Need, Progress, Title, Zone " +
                                          "FROM StasisMissions WHERE CharacterId = $c;";
                    command.Parameters.AddWithValue("$c", characterId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var m = new ActiveMission
                        {
                            Slot = reader.GetString(0), Source = reader.GetString(1), Step = reader.GetInt32(2), Batch = reader.GetInt32(3),
                            Kind = reader.GetString(4), Target = reader.GetInt64(5), Need = reader.GetInt64(6), Progress = reader.GetInt64(7),
                            Title = reader.GetString(8), Zone = reader.GetInt32(9),
                        };
                        profile.Slots[m.Slot] = m;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] Could not read the missions of {characterId}: {ex.Message}");
            }
            return profile;
        }

        public void Save(MissionProfile p)
        {
            try
            {
                EnsureTables();
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                void Run(string sql, params (string Name, object Value)[] values)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
                    command.ExecuteNonQuery();
                }

                List<ActiveMission> slots;
                int chapter, done;
                lock (p.Gate)
                {
                    slots = p.Slots.Values.ToList();
                    chapter = p.Chapter;
                    done = p.ChaptersDone;
                }
                Run("INSERT INTO StasisMissionProfiles (CharacterId, Chapter, ChaptersDone) VALUES ($c, $h, $d) " +
                    "ON CONFLICT(CharacterId) DO UPDATE SET Chapter = excluded.Chapter, ChaptersDone = excluded.ChaptersDone;",
                    ("$c", p.CharacterId), ("$h", chapter), ("$d", done));
                Run("DELETE FROM StasisMissions WHERE CharacterId = $c;", ("$c", p.CharacterId));
                foreach (var m in slots)
                    Run("INSERT INTO StasisMissions (CharacterId, Slot, Source, Step, Batch, Kind, Target, Need, Progress, Title, Zone) " +
                        "VALUES ($c, $s, $o, $t, $b, $k, $g, $n, $p, $x, $z);",
                        ("$c", p.CharacterId), ("$s", m.Slot), ("$o", m.Source), ("$t", m.Step), ("$b", m.Batch), ("$k", m.Kind),
                        ("$g", m.Target), ("$n", m.Need), ("$p", m.Progress), ("$x", m.Title), ("$z", m.Zone));
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Missions] Could not store the missions of {p.CharacterId}: {ex.Message}");
            }
        }

        private static void EnsureTables()
        {
            if (_tableReady) return;
            lock (_tableLock)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS StasisMissionProfiles (
                        CharacterId  INTEGER PRIMARY KEY,
                        Chapter      INTEGER NOT NULL DEFAULT 0,
                        ChaptersDone INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS StasisMissions (
                        CharacterId INTEGER NOT NULL,
                        Slot        TEXT    NOT NULL,
                        Source      TEXT    NOT NULL,
                        Step        INTEGER NOT NULL DEFAULT 0,
                        Batch       INTEGER NOT NULL DEFAULT 0,
                        Kind        TEXT    NOT NULL,
                        Target      INTEGER NOT NULL DEFAULT 0,
                        Need        INTEGER NOT NULL DEFAULT 1,
                        Progress    INTEGER NOT NULL DEFAULT 0,
                        Title       TEXT    NOT NULL DEFAULT '',
                        Zone        INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, Slot));";
                create.ExecuteNonQuery();
                _tableReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }
    }
}
