using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Fissure of the Stasis rules (docs/stasis-gameplay-spec.md §12, content/events/fissure.json):
    /// expeditions of waves, each one stronger, paying kamas, resources and Jetons Event per wave,
    /// counted per character towards a guaranteed Dofus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE FIGHT PER WAVE. The Dreams' Fin du rêve and the dimension dungeons bring waves INTO one
    /// fight (FightHandler.BringWaveAsync), which ties the wave to that fight's turn machinery; the
    /// Fissure is built one level up, where nothing of the fight engine has to change: «.fissure
    /// &lt;bande&gt;» buys the band's key and plants wave 1 with <see cref="MobSpawnManager.SpawnComposed"/>
    /// on the arena map, and the character is taken there. When a fight against that group is built
    /// (<see cref="OnFightCreated"/>, matched on the group id the fight carries as its
    /// DefenderLeaderId) its monsters are brought to the wave's level with <see cref="Dreams.ScaleTo"/>
    /// -- the rule the dreams scale by -- and the blue side gets the formation bonus. When it is won
    /// (<see cref="FightEndedAsync"/>) each of its people is paid, and the leader's turn plants the
    /// next wave on the same map, ready when the fight's end brings them back to it. A defeat ends
    /// the expedition; the waves done are kept, as the spec wants for the last survivor.
    /// </para>
    /// <para>
    /// GROUPS. The spec allows 4 fighters, the formation bonus shrinking with each one. The party of
    /// the character who opens the expedition, up to <see cref="Settings.MaxFighters"/>, is the
    /// count the bonus is read for, and the leader pays one key per fighter (the spec's key is per
    /// character). Whoever of them joins the fight is paid for the wave as the leader is.
    /// Simplified: the bonus is given to the fighters present when the fight is built (the leader,
    /// his heroes and companion), not to those who join during placement.
    /// </para>
    /// <para>
    /// THE DOFUS. Every won wave adds to the character's count; the ELIGIBLE ones (spec: waves of 5
    /// and over in Abîme, «dofus.eligibleBands» and «eligibleFromWave») to the count towards the
    /// Dofus: one is owed every <see cref="DofusRules.WavesNeeded"/> of them and claimed with
    /// «.fissure dofus &lt;nom&gt;» among the six of the spec's Éveil table. Kept in the world
    /// database, table <c>FissureProgress</c>. Expeditions in progress are not: a restart ends them.
    /// </para>
    /// </remarks>
    public static class Fissure
    {
        public const string AuthoredFile = "events/fissure.json";
        public const int MostMonsters = 8;

        public sealed record Formation(int Fighters, int Ap, int Mp, int LifePercent, int DamagePercent)
        {
            public static Formation None { get; } = new(0, 0, 0, 0, 0);
        }

        public sealed record Resource(int Item, int Quantity, int Percent);

        public sealed record Band(string Id, string Name, int Key, int MinLevel, int BaseLevel, int LevelPerWave, int MaxLevel,
                                  int BaseMonsters, int WavesPerExtraMonster, int Validate, int Limit,
                                  int KamasPerWave, int TokensPerWave, int ValidatedTokens,
                                  IReadOnlyList<int> Monsters, IReadOnlyList<Resource> Resources);

        public sealed record DofusRules(int WavesNeeded, IReadOnlyList<string> EligibleBands, int EligibleFromWave,
                                        IReadOnlyList<(string Name, int Item)> Items);

        public sealed record Settings(bool Enabled, long ArenaMap, int MaxFighters, IReadOnlyList<Formation> Formations,
                                      IReadOnlyList<Band> Bands, DofusRules Dofus)
        {
            public static Settings Off { get; } = new(false, 0, 4, Array.Empty<Formation>(), Array.Empty<Band>(),
                new DofusRules(1000, Array.Empty<string>(), 1, Array.Empty<(string, int)>()));
        }

        /// <summary>An expedition under way: its leader, its band, the wave planted and the waves won.</summary>
        public sealed class Expedition
        {
            public long Leader { get; init; }
            public Band Band { get; init; } = null!;
            public int Fighters { get; init; } = 1;
            public long ArenaMap { get; init; }
            public int Wave { get; set; } = 1;
            public int Cleared { get; set; }
            public long GroupId { get; set; }
        }

        /// <summary>What a character has done in the Fissure, for ever.</summary>
        public sealed record Progress(long Waves, long Eligible, int DofusClaimed, int Best);

        private static Settings _settings = Settings.Off;
        private static readonly ConcurrentDictionary<long, Expedition> _byLeader = new();
        private static readonly ConcurrentDictionary<long, Progress> _progress = new();
        private static readonly object Gate = new();
        private static bool _loaded;

        public static Settings Current => _settings;

        /// <summary>For the tests: no database.</summary>
        internal static bool InMemoryOnly { get; set; }

        // ─── Reading ────────────────────────────────────────────────────────────────────────

        public static Settings Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var formations = new List<Formation>();
            if (root.TryGetProperty("formation", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var f in fa.EnumerateArray())
                    formations.Add(new Formation(I(f, "fighters", 1), I(f, "ap", 0), I(f, "mp", 0), I(f, "lifePercent", 0), I(f, "damagePercent", 0)));

            var bands = new List<Band>();
            if (root.TryGetProperty("bands", out var ba) && ba.ValueKind == JsonValueKind.Array)
                foreach (var b in ba.EnumerateArray())
                {
                    string id = StasisEvents.Str(b, "id", "");
                    if (id.Length == 0) continue;
                    var monsters = new List<int>();
                    if (b.TryGetProperty("monsters", out var ma) && ma.ValueKind == JsonValueKind.Array)
                        monsters.AddRange(ma.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32()));
                    var resources = new List<Resource>();
                    if (b.TryGetProperty("resources", out var ra) && ra.ValueKind == JsonValueKind.Array)
                        foreach (var r in ra.EnumerateArray())
                            if (I(r, "item", 0) > 0) resources.Add(new Resource(I(r, "item", 0), Math.Max(1, I(r, "quantity", 1)), Math.Clamp(I(r, "percent", 100), 0, 100)));
                    int baseLevel = Math.Max(1, I(b, "baseLevel", 150));
                    bands.Add(new Band(id.ToLowerInvariant(), StasisEvents.Str(b, "name", id), Math.Max(0, I(b, "key", 0)), I(b, "minLevel", 1),
                        baseLevel, Math.Max(0, I(b, "levelPerWave", 5)), Math.Max(baseLevel, I(b, "maxLevel", 300)),
                        Math.Clamp(I(b, "baseMonsters", 2), 1, MostMonsters), Math.Max(1, I(b, "wavesPerExtraMonster", 2)),
                        Math.Max(1, I(b, "validate", 1)), Math.Max(0, I(b, "limit", 0)),
                        Math.Max(0, I(b, "kamasPerWave", 0)), Math.Max(0, I(b, "tokensPerWave", 0)), Math.Max(0, I(b, "validatedTokens", 0)),
                        monsters, resources));
                }

            var dofus = Settings.Off.Dofus;
            if (root.TryGetProperty("dofus", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                var eligible = new List<string>();
                if (d.TryGetProperty("eligibleBands", out var eb) && eb.ValueKind == JsonValueKind.Array)
                    eligible.AddRange(eb.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.ToLowerInvariant()));
                var items = new List<(string, int)>();
                if (d.TryGetProperty("items", out var ia) && ia.ValueKind == JsonValueKind.Array)
                    foreach (var i in ia.EnumerateArray())
                        if (I(i, "item", 0) > 0) items.Add((StasisEvents.Str(i, "name", ""), I(i, "item", 0)));
                dofus = new DofusRules(Math.Max(1, I(d, "wavesNeeded", 1000)), eligible, Math.Max(1, I(d, "eligibleFromWave", 1)), items);
            }

            return new Settings(!root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                StasisEvents.Long(root, "arenaMap", 0), Math.Clamp(I(root, "maxFighters", 4), 1, 8), formations, bands, dofus);
        }

        public static void Start()
        {
            try
            {
                _settings = Load(Paths.ContentFile(AuthoredFile));
                Console.WriteLine($"[Fissure] {_settings.Bands.Count} band(s), arena {_settings.ArenaMap}, Dofus every {_settings.Dofus.WavesNeeded} eligible waves.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fissure] Could not read {AuthoredFile}: {ex.Message}");
            }
        }

        internal static void UseForTests(Settings settings) { _settings = settings; _byLeader.Clear(); }

        // ─── Pure rules (tested) ────────────────────────────────────────────────────────────

        public static Band? BandOf(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant().Replace("î", "i").Replace("ï", "i");
            return _settings.Bands.FirstOrDefault(b => b.Id == w || b.Name.ToLowerInvariant().Replace("î", "i") == w);
        }

        /// <summary>The level wave <paramref name="wave"/> is brought to.</summary>
        public static int MonsterLevel(Band band, int wave)
            => Math.Min(band.MaxLevel, band.BaseLevel + Math.Max(0, wave - 1) * band.LevelPerWave);

        /// <summary>How many monsters wave <paramref name="wave"/> brings: never fewer than the fighters, never more than eight.</summary>
        public static int MonsterCount(Band band, int wave, int fighters)
            => Math.Clamp(band.BaseMonsters + Math.Max(0, wave - 1) / band.WavesPerExtraMonster, Math.Max(1, fighters), MostMonsters);

        /// <summary>The formation bonus for so many fighters: the spec's table, nothing beyond it.</summary>
        public static Formation FormationFor(int fighters, IReadOnlyList<Formation> table)
            => table.FirstOrDefault(f => f.Fighters == fighters) ?? Formation.None;

        /// <summary>The kamas a won wave pays.</summary>
        public static long KamasFor(Band band, int wave) => (long)band.KamasPerWave * Math.Max(1, wave);

        /// <summary>The tokens a won wave pays: the band's per wave, and its validation bonus on the wave that validates it.</summary>
        public static int TokensFor(Band band, int wave) => band.TokensPerWave + (wave == band.Validate ? band.ValidatedTokens : 0);

        /// <summary>Whether a won wave counts towards the Dofus.</summary>
        public static bool Eligible(Band band, int wave, DofusRules rules)
            => (rules.EligibleBands.Count == 0 || rules.EligibleBands.Contains(band.Id)) && wave >= rules.EligibleFromWave;

        /// <summary>The Dofus owed and not yet claimed.</summary>
        public static int DofusOwed(long eligible, int claimed, int needed)
            => (int)Math.Max(0, eligible / Math.Max(1, needed) - claimed);

        /// <summary>Whether the band's key allows one more wave after <paramref name="cleared"/>.</summary>
        public static bool MoreWaves(Band band, int cleared) => band.Limit == 0 || cleared < band.Limit;

        /// <summary>A wave's monsters: drawn from the band's list (seeded per wave), at their best grade for the level.</summary>
        public static List<(int Monster, int Grade)> WaveMembers(Band band, int wave, int fighters, Random dice, Func<int, int, int>? grade = null)
        {
            var list = new List<(int, int)>();
            if (band.Monsters.Count == 0) return list;
            int level = MonsterLevel(band, wave);
            for (int i = 0; i < MonsterCount(band, wave, fighters); i++)
            {
                int m = band.Monsters[dice.Next(band.Monsters.Count)];
                list.Add((m, grade?.Invoke(m, level) ?? 0));
            }
            return list;
        }

        // ─── Expeditions ────────────────────────────────────────────────────────────────────

        public static Expedition? Of(long leader) => _byLeader.TryGetValue(leader, out var e) ? e : null;

        /// <summary>The expedition a fight's group belongs to, if any.</summary>
        public static Expedition? ByGroup(long groupId)
            => groupId == 0 ? null : _byLeader.Values.FirstOrDefault(e => e.GroupId == groupId);

        /// <summary>Opens an expedition (the key is paid by the caller). Null when wave 1 could not be planted.</summary>
        public static Expedition? Open(long leader, Band band, int fighters)
        {
            var exp = new Expedition { Leader = leader, Band = band, Fighters = Math.Clamp(fighters, 1, _settings.MaxFighters), ArenaMap = _settings.ArenaMap };
            if (!Plant(exp)) return null;
            _byLeader[leader] = exp;
            return exp;
        }

        /// <summary>Ends an expedition, taking its planted wave off the map.</summary>
        public static Expedition? Close(long leader)
        {
            if (!_byLeader.TryRemove(leader, out var exp)) return null;
            if (exp.GroupId != 0 && MobSpawnManager.GetMobGroupById(exp.GroupId) != null)
                MobSpawnManager.RemoveMobGroup(exp.ArenaMap, exp.GroupId);
            return exp;
        }

        private static bool Plant(Expedition exp)
        {
            var dice = new Random(HashCode.Combine(exp.Leader, exp.Wave, Environment.TickCount));
            var members = WaveMembers(exp.Band, exp.Wave, exp.Fighters, dice, MobSpawnManager.GradeForLevel);
            if (members.Count == 0) return false;
            var group = MobSpawnManager.SpawnComposed(exp.ArenaMap, members);
            if (group == null) return false;
            exp.GroupId = group.MobId;
            return true;
        }

        // ─── Fights ─────────────────────────────────────────────────────────────────────────

        internal static void OnFightCreated(long characterId, FightInstance fight)
        {
            var exp = ByGroup(fight.DefenderLeaderId);
            if (exp == null) return;
            int level = MonsterLevel(exp.Band, exp.Wave);
            foreach (var monster in fight.Rojo) Dreams.ScaleTo(monster, level);
            var bonus = FormationFor(exp.Fighters, _settings.Formations);
            foreach (var f in fight.Azul.Where(f => !f.EsInvocado))
            {
                f.MaxAP += bonus.Ap; f.CurrentAP += bonus.Ap;
                f.MaxMP += bonus.Mp; f.CurrentMP += bonus.Mp;
                if (bonus.LifePercent > 0)
                {
                    f.MaxHP += f.MaxHP * bonus.LifePercent / 100;
                    f.CurrentHP = f.MaxHP;
                }
                f.DamageDealtPercent += bonus.DamagePercent;
            }
            Console.WriteLine($"[Fissure] Wave {exp.Wave} of {exp.Leader} ({exp.Band.Id}): {fight.Rojo.Count} monster(s) at level {level}, formation of {exp.Fighters}.");
        }

        internal static async Task FightEndedAsync(GameSession session, FightInstance fight, bool won)
        {
            var exp = ByGroup(fight.DefenderLeaderId);
            if (exp == null) return;
            long id = session.CharacterId;
            bool leader = id == exp.Leader;
            int wave = exp.Wave;

            if (!won)
            {
                if (leader)
                {
                    Close(exp.Leader);
                    await StasisEvents.TellAsync(id, $"Fissure : l'expédition {exp.Band.Name} s'arrête à la vague {wave}. {exp.Cleared} vague(s) gagnée(s) restent acquises.");
                }
                return;
            }

            // Every one of its people is paid for the wave.
            long kamas = KamasFor(exp.Band, wave);
            int tokens = TokensFor(exp.Band, wave);
            GameState.Kamas += kamas;
            DatabaseManager.SaveCurrentCharacter();
            await session.SendAsync(ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
            if (tokens > 0) EventTokens.Add(id, tokens);
            var given = new List<string>();
            foreach (var r in exp.Band.Resources)
                if (Random.Shared.Next(100) < r.Percent && session.Stream != null
                    && await Handlers.WorkshopHandler.GiveAsync(session.Stream, r.Item, r.Quantity))
                    given.Add($"{r.Quantity}x #{r.Item}");
            var p = Record(id, wave, Eligible(exp.Band, wave, _settings.Dofus));
            int owed = DofusOwed(p.Eligible, p.DofusClaimed, _settings.Dofus.WavesNeeded);

            string line = $"Fissure {exp.Band.Name}, vague {wave} gagnée : {kamas:N0} kamas, {tokens} jeton(s)" +
                          (given.Count > 0 ? ", " + string.Join(", ", given) : "") +
                          $". Total : {p.Waves} vagues, {p.Eligible % _settings.Dofus.WavesNeeded}/{_settings.Dofus.WavesNeeded} vers le Dofus." +
                          (wave == exp.Band.Validate ? " Expédition validée !" : "") +
                          (owed > 0 ? $" Un Dofus vous attend : .fissure dofus <nom>." : "");

            if (!leader) { await StasisEvents.TellAsync(id, line); return; }

            exp.Cleared = wave;
            if (!MoreWaves(exp.Band, exp.Cleared))
            {
                Close(exp.Leader);
                await StasisEvents.TellAsync(id, line + " La clé est épuisée : fin de l'expédition.");
                return;
            }
            exp.Wave = wave + 1;
            if (!Plant(exp))
            {
                Close(exp.Leader);
                await StasisEvents.TellAsync(id, line + " La vague suivante n'a pas pu apparaître : fin de l'expédition.");
                return;
            }
            await StasisEvents.TellAsync(id, line + $" Vague {exp.Wave} (niveau {MonsterLevel(exp.Band, exp.Wave)}) vous attend.");
        }

        // ─── Progress ───────────────────────────────────────────────────────────────────────

        public static Progress ProgressOf(long characterId)
        {
            EnsureLoaded();
            return _progress.TryGetValue(characterId, out var p) ? p : new Progress(0, 0, 0, 0);
        }

        /// <summary>One won wave in the character's record.</summary>
        public static Progress Record(long characterId, int wave, bool eligible)
        {
            EnsureLoaded();
            lock (Gate)
            {
                var p = ProgressOf(characterId);
                var next = p with { Waves = p.Waves + 1, Eligible = p.Eligible + (eligible ? 1 : 0), Best = Math.Max(p.Best, wave) };
                _progress[characterId] = next;
                Save(characterId, next);
                return next;
            }
        }

        /// <summary>Marks one owed Dofus as claimed; false when none is owed.</summary>
        public static bool Claim(long characterId)
        {
            EnsureLoaded();
            lock (Gate)
            {
                var p = ProgressOf(characterId);
                if (DofusOwed(p.Eligible, p.DofusClaimed, _settings.Dofus.WavesNeeded) <= 0) return false;
                var next = p with { DofusClaimed = p.DofusClaimed + 1 };
                _progress[characterId] = next;
                Save(characterId, next);
                return true;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                if (InMemoryOnly) return;
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    var create = connection.CreateCommand();
                    create.CommandText = "CREATE TABLE IF NOT EXISTS FissureProgress (CharacterId INTEGER PRIMARY KEY, Waves INTEGER NOT NULL, " +
                                         "Eligible INTEGER NOT NULL, DofusClaimed INTEGER NOT NULL, Best INTEGER NOT NULL);";
                    create.ExecuteNonQuery();
                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT CharacterId, Waves, Eligible, DofusClaimed, Best FROM FissureProgress;";
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                        _progress[reader.GetInt64(0)] = new Progress(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fissure] The progress could not be read: {ex.Message}");
                }
            }
        }

        private static void Save(long characterId, Progress p)
        {
            if (InMemoryOnly) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var write = connection.CreateCommand();
                write.CommandText = "INSERT OR REPLACE INTO FissureProgress (CharacterId, Waves, Eligible, DofusClaimed, Best) VALUES ($c, $w, $e, $d, $b);";
                write.Parameters.AddWithValue("$c", characterId);
                write.Parameters.AddWithValue("$w", p.Waves);
                write.Parameters.AddWithValue("$e", p.Eligible);
                write.Parameters.AddWithValue("$d", p.DofusClaimed);
                write.Parameters.AddWithValue("$b", p.Best);
                write.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fissure] The progress of {characterId} could not be saved: {ex.Message}");
            }
        }

        internal static void Forget()
        {
            lock (Gate) { _progress.Clear(); _byLeader.Clear(); _loaded = false; }
        }

        private static int I(JsonElement e, string key, int fallback) => StasisEvents.Int(e, key, fallback);
    }
}
