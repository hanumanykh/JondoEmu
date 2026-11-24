using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The Orbes of Stasis, which on this server are the kamas: what a won fight against monsters
    /// pays each of its players. The Stasis server has no kamas and pays every fight in its own
    /// currency; the owner chose to keep the word and the coin the client already draws, and to
    /// make kamas behave as Orbes do -- so the marketplaces, the market maker and its reference
    /// prices (<see cref="ReferencePrices"/>) go on as they were, priced in what is now the Orbe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the spec says (docs/stasis-gameplay-spec.md, §2): every PvM fight pays, the higher the
    /// monsters the more; each participant takes a whole share, nothing is cut among the group;
    /// a boss pays well more; an archmonster in the fight multiplies the whole fight by five and a
    /// tide by two; a character well above the monsters earns less. None of the amounts is
    /// published, so the curve below is ours, calibrated on the reference prices.
    /// </para>
    /// <para>
    /// The curve, per monster beaten: <c>Flat + PerLevel x level^Exponent</c>, a boss
    /// <see cref="Settings.BossFactor"/> times that. The level a monster counts at is capped at the
    /// character's level plus <see cref="Settings.CarryTolerance"/>: a level-20 character carried
    /// into a level-200 fight is paid as for level-40 monsters, which is what "lower-level
    /// characters less" means here -- carrying is not a mint. A character more than
    /// <see cref="Settings.OverLevelTolerance"/> above the strongest monster loses
    /// <see cref="Settings.OverLevelPenaltyPerLevel"/> of his pay per level beyond it, never under
    /// <see cref="Settings.OverLevelFloor"/>.
    /// </para>
    /// <para>
    /// The calibration, with the defaults, for a solo character against a group of four monsters
    /// of his level, at the forty fights an hour a practised player keeps up (90 s a fight):
    /// <code>
    ///   level  50   ~ 245 per monster   ~ 39 000 kamas an hour
    ///   level 150   ~1 225 per monster  ~196 000 kamas an hour
    ///   level 200   ~1 880 per monster  ~300 000 kamas an hour
    /// </code>
    /// So a level-200 boss resource the reference prices put at ~139 000 is half an hour of farm,
    /// a Torkelonia piece at ~3 000 000 ten hours -- five under a tide, two with an archmonster in
    /// the group now and then --, which is the pace of a Stasis-like world where an item bought is
    /// never worth one farmed. The quests' and achievements' kamas are the game's own amounts and
    /// stay small beside this.
    /// </para>
    /// </remarks>
    public static class Orbes
    {
        public const string AuthoredFile = "economy/orbes.json";

        internal sealed class Settings
        {
            public bool Enabled { get; set; } = true;
            public double Flat { get; set; } = 10;
            public double PerLevel { get; set; } = 0.66;
            public double Exponent { get; set; } = 1.5;
            public double BossFactor { get; set; } = 5;
            public double ArchmonsterFactor { get; set; } = 5;
            public double TideFactor { get; set; } = 2;
            public int CarryTolerance { get; set; } = 20;
            public int OverLevelTolerance { get; set; } = 30;
            public double OverLevelPenaltyPerLevel { get; set; } = 0.01;
            public double OverLevelFloor { get; set; } = 0.25;
        }

        private static Settings? _settings;

        internal static Settings Current
        {
            get => _settings ??= Read(Paths.ContentFile(AuthoredFile));
            set => _settings = value;
        }

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        internal static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orbes] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        /// <summary>One beaten monster as the formula sees it.</summary>
        public readonly record struct Monster(int Level, bool Boss);

        /// <summary>What one monster pays at the level it counts at.</summary>
        internal static double PerMonster(int level, bool boss, Settings s)
        {
            double worth = s.Flat + s.PerLevel * Math.Pow(Math.Max(1, level), s.Exponent);
            return boss ? worth * s.BossFactor : worth;
        }

        /// <summary>
        /// The kamas (Orbes) one character takes out of a won fight: his whole share, not a part
        /// of the group's. Zero when there was nothing to beat.
        /// </summary>
        public static long ForFighter(int characterLevel, IReadOnlyCollection<Monster> monsters,
                                      bool archmonster, bool tide)
            => ForFighter(characterLevel, monsters, archmonster, tide, Current);

        internal static long ForFighter(int characterLevel, IReadOnlyCollection<Monster> monsters,
                                        bool archmonster, bool tide, Settings s)
        {
            if (monsters.Count == 0) return 0;
            int level = Math.Max(1, characterLevel);
            int cap = level + Math.Max(0, s.CarryTolerance);
            double sum = monsters.Sum(m => PerMonster(Math.Min(m.Level, cap), m.Boss, s));

            int strongest = monsters.Max(m => m.Level);
            int over = level - strongest - Math.Max(0, s.OverLevelTolerance);
            if (over > 0) sum *= Math.Max(s.OverLevelFloor, 1 - over * s.OverLevelPenaltyPerLevel);

            if (archmonster) sum *= s.ArchmonsterFactor;
            if (tide) sum *= s.TideFactor;
            return Math.Max(0, (long)Math.Round(sum));
        }

        /// <summary>
        /// The kamas an hour of farm brings at a level, solo against groups of
        /// <paramref name="groupSize"/> monsters of his level at <paramref name="fightsPerHour"/>:
        /// the calibration of the remarks, worked out rather than written down.
        /// </summary>
        internal static long PerHour(int level, int groupSize = 4, int fightsPerHour = 40, Settings? s = null)
        {
            var monsters = Enumerable.Repeat(new Monster(level, false), groupSize).ToList();
            return ForFighter(level, monsters, false, false, s ?? Current) * fightsPerHour;
        }
    }
}
