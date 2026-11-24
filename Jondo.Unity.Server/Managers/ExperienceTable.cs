using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Character experience table: how much accumulated experience is needed to reach each level.
    /// It comes from the client bundles
    /// (data_assets_characterxpmappingsdataroot.asset.bundle) through extract_character_xp.py.
    ///
    /// The first values line up with what was already hardcoded in the kri -- level 2 = 110 and
    /// level 3 = 650 -- and with the official end-of-fight capture, which for a level 3 character
    /// sends 650 as the floor of the level and 1500 as the threshold for the next one.
    /// </summary>
    public static class ExperienceTable
    {
        private static readonly SortedDictionary<int, long> _floors = new SortedDictionary<int, long>();
        private static int _maxLevel = 1;

        /// <summary>
        /// Whether the table is filled in and safe to read. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST so that it publishes
        /// _floors AND _maxLevel together -- they are read as a pair in <see cref="LevelFloor"/>
        /// and a reader must never see one without the other.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>
        /// Whether there is a table to work with. Loads it first if nobody has yet, so this
        /// answers "is there experience data", never "has somebody remembered to call Initialize".
        /// </summary>
        /// <remarks>
        /// This one carries weight. CommandHandler uses it to decide whether to write
        /// GameState.Experience at all, because with no table LevelFloor returns zero for every
        /// level, and writing that does not floor the character's experience -- it erases it.
        /// </remarks>
        public static bool IsLoaded { get { Ensure(); return _floors.Count > 0; } }

        /// <summary>
        /// The highest level this server hands out: the (extended) table's last level, but never
        /// past <see cref="ServerSettings.MaxCharacterLevel"/>. Read from the settings on every
        /// call rather than baked in at load, so the tests -- and a settings object swapped in
        /// before boot -- see the value they set.
        /// </summary>
        public static int MaxLevel { get { Ensure(); return Math.Min(_maxLevel, ConfiguredCap); } }

        /// <summary>
        /// Furthest the curve is ever extended. The table's last step (1888 to 1889) is about
        /// 9.85e14, so 2000 sits near 2.1e17 and 5000 near 3.2e18 -- still under long.MaxValue
        /// (9.2e18) with room for experience bonuses on top. Nothing needs a level higher.
        /// </summary>
        internal const int HardestLevelCap = 5000;

        /// <summary>The level cap from the settings, never under 200 nor over the hardest cap.</summary>
        private static int ConfiguredCap
            => Math.Clamp(ServerSettings.Current.MaxCharacterLevel, 200, HardestLevelCap);

        /// <summary>
        /// Extends the curve past the client table's last level up to <paramref name="upTo"/>, one
        /// flat step per level equal to the table's own last step.
        /// </summary>
        /// <remarks>
        /// character_xp.json stops at 1889 (200 + Omega 1689) because that is where the client's
        /// bundle stops; a server going to 2000 needs floors for the 111 levels after it. They are
        /// made up here, in code, and not written into the json, so a re-extraction from the
        /// client never loses them and never has to be hand-merged. The Omega part of the official
        /// curve grows about 1% a level near its end (1888 to 1889 costs ~9.85e14), so repeating
        /// its last step keeps the levels just past 1889 costing exactly what 1889 did, without the
        /// compounding that would carry 2000 several times further; 2000 lands near 2.1e17. If the table is too short to have a step (one level or none) or
        /// its last step is not positive, a flat 50,000,000 is used so the floors still grow
        /// strictly -- LevelForXp depends on that.
        ///
        /// Overflow: the step is checked against what is left before long.MaxValue and the
        /// extension stops rather than wrap; at the real table's step it never comes close.
        /// </remarks>
        internal static void ExtendCurve(SortedDictionary<int, long> floors, int upTo)
        {
            if (floors.Count == 0) return;
            int last = floors.Keys.Max();
            if (upTo <= last) return;
            long step = 50_000_000L;
            if (floors.Count >= 2 && floors.TryGetValue(last - 1, out long previous) && floors[last] - previous > 0)
                step = floors[last] - previous;
            long floor = floors[last];
            for (int level = last + 1; level <= upTo; level++)
            {
                if (floor > long.MaxValue - step) break;
                floor += step;
                floors[level] = floor;
            }
        }

        /// <summary>
        /// Reads the table, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, instead of on whoever first asks what a level is worth.
        /// </summary>
        /// <remarks>
        /// CALLING IT AGAIN DOES NOTHING, ON PURPOSE. It used to clear the table and re-read the
        /// file with no lock of any kind, and anybody reading it during that window got a wrong
        /// answer -- measured, every single read: LevelFloor(200) came back 0 instead of
        /// 5,555,424,000. Not an exception, a number; and a zero here travels. It is what
        /// CommandHandler guards against before overwriting a character's experience, and in the
        /// kub it goes out through VarIfNotZero, which drops the field entirely, so the packet
        /// changes shape and not just its contents.
        ///
        /// character_xp.json comes out of the client bundles and does not change while the server
        /// is up, so there was never anything to reload.
        /// </remarks>
        public static void Initialize() => Ensure();

        /// <summary>MaxLevel without the Ensure, for code already inside the load or past it.</summary>
        private static int MaxLevelUnsafe => Math.Min(_maxLevel, ConfiguredCap);

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                }
                finally
                {
                    // In a finally so a missing file still counts as tried: otherwise every
                    // level lookup from here on would go back to the disk and log again.
                    _loaded = true;
                }
            }
        }

        private static void Load()
        {
            string path = Paths.CharacterXpJson;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ExperienceTable] WARNING: {path} not found. " +
                                  "Experience cannot be computed; run extract_character_xp.py.");
                return;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out int level) && prop.Value.TryGetInt64(out long xp))
                    {
                        _floors[level] = xp;
                    }
                }
                int fromFile = _floors.Count > 0 ? _floors.Keys.Max() : 1;
                // Extended only when the settings' cap goes past the client's table (1889); at the
                // default 300 this does nothing. The cap itself is applied on read (MaxLevel).
                ExtendCurve(_floors, ConfiguredCap);
                _maxLevel = _floors.Count > 0 ? _floors.Keys.Max() : 1;
                Console.WriteLine($"[ExperienceTable] Experience table loaded: {_floors.Count} levels " +
                                  $"(file up to {fromFile}, extended to {_maxLevel}, server cap {MaxLevelUnsafe}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExperienceTable] Error reading {path}: {ex.Message}");
            }
        }

        /// <summary>Accumulated experience the given level starts at.</summary>
        /// <remarks>
        /// The whole table's, whatever the server's cap: the cap stops the level a character can
        /// REACH (<see cref="LevelForXp"/>, <see cref="MaxLevel"/>), not the floors of the levels
        /// that exist. A character already past the cap -- an Omega 354 of the captures, or one
        /// made before the cap was lowered -- keeps the floors of his own level in the frames
        /// (jyg's experience lines), which the start-up check compares byte for byte with the
        /// capture: clamped to 300, the server refused to start.
        /// </remarks>
        public static long LevelFloor(int level)
        {
            Ensure();
            if (_floors.Count == 0 || level <= 1) return 0;
            if (_floors.TryGetValue(level, out long xp)) return xp;
            return _floors[Math.Min(level, _maxLevel)];
        }

        /// <summary>Accumulated experience at which the next level is reached.</summary>
        public static long NextLevelFloor(int level)
        {
            Ensure();
            if (_floors.Count == 0) return 0;
            return LevelFloor(Math.Min(level + 1, _maxLevel));
        }

        /// <summary>The level that a given amount of accumulated experience corresponds to.</summary>
        /// <remarks>
        /// Walks the whole table, so it is the read that suffered most from the table being
        /// rebuilt underneath it: enumerating a SortedDictionary somebody else is writing to is
        /// undefined, not merely stale.
        /// </remarks>
        public static int LevelForXp(long experience)
        {
            Ensure();
            if (_floors.Count == 0) return 1;
            // Clamped to the server's cap: experience past the cap's floor keeps accumulating (it is
            // a long, and the kub shows it), but the level stops where the settings say.
            int cap = MaxLevelUnsafe;
            int level = 1;
            foreach (var kv in _floors)
            {
                if (kv.Value > experience || kv.Key > cap) break;
                level = kv.Key;
            }
            return level;
        }
    }
}
