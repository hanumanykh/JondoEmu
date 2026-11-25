using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>content/progression/eveil.json: the six Dofus of the Éveil Partie I.</summary>
    internal sealed class EveilFile
    {
        public bool Enabled { get; set; } = true;
        public bool RequireHardcore { get; set; } = true;
        public bool Repeatable { get; set; }
        public string Label { get; set; } = "Stasis Éveil";
        public List<EveilDofus> Dofus { get; set; } = new();

        /// <summary>The Dofus a player means: its id or name, accents and case aside, exact first, then a beginning.</summary>
        internal EveilDofus? Find(string? word)
        {
            string w = Tiers.Fold((word ?? "").Trim());
            if (w.Length == 0) return null;
            return Dofus.FirstOrDefault(d => Tiers.Fold(d.Id) == w || Tiers.Fold(d.Name) == w)
                   ?? Dofus.FirstOrDefault(d => Tiers.Fold(d.Id).StartsWith(w, StringComparison.Ordinal)
                                                || Tiers.Fold(d.Name).StartsWith(w, StringComparison.Ordinal));
        }
    }

    internal sealed class EveilDofus
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Item { get; set; }
        public int Level { get; set; }
        public int Copies { get; set; }
        public List<EveilDungeon> Dungeons { get; set; } = new();
        public EveilBoss Boss { get; set; } = new();
        public List<EveilEffect> Effects { get; set; } = new();
        public List<EveilEffect> Approximated { get; set; } = new();

        internal string DungeonName(int id)
            => Dungeons.FirstOrDefault(d => d.Id == id)?.Name is { Length: > 0 } name ? name : Tiers.DungeonName(id);
    }

    internal sealed class EveilDungeon
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    internal sealed class EveilBoss
    {
        public string Name { get; set; } = "";
        public int Monster { get; set; }
        /// <summary>0-based grade, clamped to what the monster has (MobSpawnManager.ComposeOffMap).</summary>
        public int Grade { get; set; } = 5;
        /// <summary>The level the boss is scaled to; 0 = the Dofus's level.</summary>
        public int Level { get; set; }
    }

    internal sealed class EveilEffect
    {
        public int Effect { get; set; }
        public int Value { get; set; }
        /// <summary>For an approximated line: the spec's conditional effect it stands for.</summary>
        public string? Replaces { get; set; }
    }

    /// <summary>Where a character stands with one Dofus.</summary>
    internal sealed class EveilProgress
    {
        public DateTime StartedAt { get; set; }
        public HashSet<int> Dungeons { get; } = new();
        public bool Completed { get; set; }
    }

    internal enum EveilStage { NotStarted, Route, Challenge, Done }

    /// <summary>Where the Éveil progress of the characters is kept.</summary>
    internal interface IEveilStore
    {
        /// <summary>Every Dofus this character began, by its id.</summary>
        IReadOnlyDictionary<string, EveilProgress> Of(long characterId);
        /// <summary>Starts (or, repeatable, starts again) a Dofus: no dungeon, not completed.</summary>
        void Start(long characterId, string dofus, DateTime at);
        void AddDungeon(long characterId, string dofus, int dungeonId, DateTime at);
        void Complete(long characterId, string dofus);
    }

    /// <summary>
    /// The Éveil des Dofus, Partie I (docs/stasis-gameplay-spec.md §13): sacrifice of copies, a
    /// route of dungeons won in hardcore after the start, a final challenge alone against a boss,
    /// and a stasified Dofus as the reward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ROUTE. Its dungeons are counted where the tier completions are, Tiers.OnFightEnded --
    /// the won boss room of a dungeon, for the master and each of his heroes --, which calls
    /// <see cref="OnDungeonWon"/>. Only a Dofus already started records anything, so a win before
    /// the start never counts, as the spec wants; and with «requireHardcore» only a hardcore fight
    /// (Tiers.IsHardcore) does, which only an evolved character can lead.
    /// </para>
    /// <para>
    /// THE CHALLENGE. «.eveil &lt;dofus&gt; defi» composes the boss off the map
    /// (MobSpawnManager.ComposeOffMap, as the item fights do) and opens a fight closed to anybody
    /// else (solo). The group id is remembered here: when the fight is created
    /// (<see cref="OnFightCreated"/>, from Tiers.OnFightCreated) the boss is scaled to the Dofus
    /// level with Dreams.ScaleTo, and when it ends in its owner's session
    /// (<see cref="FightEndedAsync"/>, from FightHandler beside the Stasis events) a win gives the
    /// Dofus. Only the challenger is rewarded; a defeat costs nothing but the try. The spec's six
    /// final adversaries are not in this client: eveil.json names the stand-ins.
    /// </para>
    /// <para>
    /// THE REWARD. The template's own lines at their best (Forgemagic.Perfect), plus the file's
    /// effects and approximations added on the same lines when they exist, plus a 988 line
    /// reading the label. «Stasis Éveil» does not match Stasis's «Stasis S1..S4», so the Dofus is
    /// never fused, refined, nor counted as an ordinary copy for a later sacrifice. Being the same
    /// template it is not worn beside the ordinary one, which is the spec's «non cumulable».
    /// </para>
    /// </remarks>
    internal static class Eveil
    {
        private static EveilFile? _current;
        private static readonly object _loadLock = new();

        internal static EveilFile Current
        {
            get
            {
                if (_current != null) return _current;
                lock (_loadLock) return _current ??= Load(Jondo.Unity.Launcher.Paths.ContentFile("progression"));
            }
            set => _current = value;
        }

        internal static EveilFile Load(string directory)
        {
            string path = Path.Combine(directory, "eveil.json");
            try
            {
                if (File.Exists(path)) return ProgressionContent.Parse<EveilFile>(File.ReadAllText(path)) ?? new EveilFile();
                Console.WriteLine($"[Eveil] {path} is missing: no Éveil.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Eveil] {path} could not be read: {ex.Message}");
            }
            return new EveilFile { Enabled = false };
        }

        internal static IEveilStore Backend { get; set; } = new SqliteEveilStore();

        // ─── Pure rules ─────────────────────────────────────────────────────────────────

        internal static EveilStage StageOf(EveilDofus dofus, EveilProgress? progress)
        {
            if (progress == null) return EveilStage.NotStarted;
            if (progress.Completed) return EveilStage.Done;
            return dofus.Dungeons.All(d => progress.Dungeons.Contains(d.Id)) ? EveilStage.Challenge : EveilStage.Route;
        }

        internal static List<EveilDungeon> Remaining(EveilDofus dofus, EveilProgress? progress)
            => dofus.Dungeons.Where(d => progress == null || !progress.Dungeons.Contains(d.Id)).ToList();

        /// <summary>Whether a won boss room counts for this Dofus: started, not done, on the route, not yet counted, hardcore if asked.</summary>
        internal static bool Counts(EveilDofus dofus, EveilProgress? progress, int dungeonId, bool hardcore, bool requireHardcore)
            => progress != null && !progress.Completed
               && (hardcore || !requireHardcore)
               && dofus.Dungeons.Any(d => d.Id == dungeonId)
               && !progress.Dungeons.Contains(dungeonId);

        /// <summary>Whether one may begin: not begun (or done and repeatable), the level reached.</summary>
        internal static bool MayStart(EveilDofus dofus, EveilProgress? progress, int level, bool repeatable)
            => level >= dofus.Level && (progress == null || (progress.Completed && repeatable));

        /// <summary>Whether a piece's effects carry the Éveil label (its 988 line).</summary>
        internal static bool IsAwakened(IEnumerable<Equipment.ItemEffect> effects, string label)
            => effects.Any(e => e.Effect == Stasis.LabelEffect && e.Text != null
                                && e.Text.Contains(label, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Which stacks give the copies: (uid, how many) from the given stacks in their order, or
        /// null when they hold fewer than <paramref name="needed"/> -- nothing is taken then.
        /// </summary>
        internal static List<(long Uid, int Take)>? PlanSacrifice(IEnumerable<(long Uid, int Quantity)> stacks, int needed)
        {
            var plan = new List<(long, int)>();
            int left = needed;
            foreach (var (uid, quantity) in stacks)
            {
                if (left <= 0) break;
                int take = Math.Min(Math.Max(0, quantity), left);
                if (take == 0) continue;
                plan.Add((uid, take));
                left -= take;
            }
            return left > 0 ? null : plan;
        }

        /// <summary>
        /// The awakened Dofus's effects: the base lines, each file line added to a plain line of
        /// the same effect (or appended), and the 988 label in place of any other 988.
        /// </summary>
        internal static List<Equipment.ItemEffect> RewardEffects(IEnumerable<Equipment.ItemEffect> baseEffects, EveilDofus dofus, string label)
        {
            var list = baseEffects.Where(e => e.Effect != Stasis.LabelEffect).ToList();
            foreach (var extra in dofus.Effects.Concat(dofus.Approximated))
            {
                if (extra.Effect <= 0 || extra.Value == 0) continue;
                int at = list.FindIndex(e => e.Effect == extra.Effect && e.Text == null && e.DiceNum == 0 && e.DiceSide == 0);
                if (at >= 0) list[at] = new Equipment.ItemEffect(extra.Effect, list[at].Value + extra.Value, 0, 0);
                else list.Add(new Equipment.ItemEffect(extra.Effect, extra.Value, 0, 0));
            }
            list.Add(new Equipment.ItemEffect(Stasis.LabelEffect, 0, 0, 0, label));
            return list;
        }

        /// <summary>The level the boss is fought at.</summary>
        internal static int BossLevel(EveilDofus dofus) => dofus.Boss.Level > 0 ? dofus.Boss.Level : dofus.Level;

        /// <summary>The French names of the effects the file uses, for the texts and the window.</summary>
        internal static string EffectText(EveilEffect e)
        {
            string name = e.Effect switch
            {
                111 => "PA", 128 => "PM", 117 => "PO", 112 => "dommages", 138 => "puissance",
                118 => "force", 119 => "agilité", 123 => "chance", 124 => "sagesse", 125 => "vitalité", 126 => "intelligence",
                160 => "esquive PA", 161 => "esquive PM", 752 => "fuite", 753 => "tacle",
                210 => "% rés. terre", 211 => "% rés. eau", 212 => "% rés. air", 213 => "% rés. feu", 214 => "% rés. neutre",
                422 => "dommages terre", 424 => "dommages feu", 426 => "dommages eau", 428 => "dommages air", 430 => "dommages neutre",
                2803 => "% rés. mêlée", 2812 => "% dommages aux sorts",
                _ => $"effet {e.Effect}",
            };
            string text = $"+{e.Value} {name}";
            return e.Replaces is { Length: > 0 } r ? $"{text} (à la place de « {r} »)" : text;
        }

        // ─── Progress ───────────────────────────────────────────────────────────────────

        internal static EveilProgress? ProgressOf(long characterId, string dofus)
        {
            if (characterId <= 0) return null;
            try { return Backend.Of(characterId).TryGetValue(dofus, out var p) ? p : null; }
            catch (Exception ex) { Console.WriteLine($"[Eveil] Could not read the progress of {characterId}: {ex.Message}"); return null; }
        }

        internal static IReadOnlyDictionary<string, EveilProgress> AllOf(long characterId)
        {
            if (characterId <= 0) return new Dictionary<string, EveilProgress>();
            try { return Backend.Of(characterId); }
            catch (Exception ex) { Console.WriteLine($"[Eveil] Could not read the progress of {characterId}: {ex.Message}"); return new Dictionary<string, EveilProgress>(); }
        }

        /// <summary>
        /// A dungeon's boss beaten (Tiers.OnFightEnded, for the master and each hero): every
        /// started Dofus whose route has it records it. Never throws into the fight's end.
        /// </summary>
        internal static void OnDungeonWon(long characterId, int dungeonId, bool hardcore)
        {
            if (characterId <= 0) return;
            try
            {
                var file = Current;
                if (!file.Enabled) return;
                var all = Backend.Of(characterId);
                foreach (var dofus in file.Dofus)
                {
                    all.TryGetValue(dofus.Id, out var progress);
                    if (!Counts(dofus, progress, dungeonId, hardcore, file.RequireHardcore)) continue;
                    Backend.AddDungeon(characterId, dofus.Id, dungeonId, DateTime.Now);
                    progress!.Dungeons.Add(dungeonId);
                    int done = dofus.Dungeons.Count(d => progress.Dungeons.Contains(d.Id));
                    string line = $"Éveil du Dofus {dofus.Name} : {dofus.DungeonName(dungeonId)} validé ({done}/{dofus.Dungeons.Count}).";
                    if (done == dofus.Dungeons.Count)
                        line += $" Parcours terminé ! Défi final contre {dofus.Boss.Name} : « .eveil {dofus.Id} defi ».";
                    Console.WriteLine($"[Eveil] {characterId}: {dofus.Id} route {done}/{dofus.Dungeons.Count} ({dungeonId}).");
                    _ = StasisEvents.TellAsync(characterId, line);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Eveil] Could not record the dungeon {dungeonId} for {characterId}: {ex.Message}");
            }
        }

        // ─── The challenge ──────────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<long, (long Character, string Dofus)> _challenges = new();

        /// <summary>Remembers the boss group a character is about to fight for a Dofus.</summary>
        internal static void RegisterChallenge(long groupId, long characterId, string dofus) => _challenges[groupId] = (characterId, dofus);

        internal static void ForgetChallenge(long groupId) => _challenges.TryRemove(groupId, out _);

        internal static bool IsChallenge(long groupId) => _challenges.ContainsKey(groupId);

        /// <summary>At a fight's creation (Tiers.OnFightCreated): a challenge's boss is scaled to the Dofus level.</summary>
        internal static void OnFightCreated(FightInstance fight)
        {
            if (fight == null || !_challenges.TryGetValue(fight.DefenderLeaderId, out var c)) return;
            try
            {
                var dofus = Current.Find(c.Dofus);
                if (dofus == null) return;
                int level = BossLevel(dofus);
                foreach (var monster in fight.Rojo.Where(m => m.IsMonster)) Dreams.ScaleTo(monster, level);
                Console.WriteLine($"[Eveil] Challenge of {c.Character} for {dofus.Id}: {fight.Rojo.Count} monster(s) at level {level}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Eveil] The challenge fight could not be prepared: {ex.Message}");
            }
        }

        /// <summary>
        /// A fight's end in a winner's or loser's own session (FightHandler): the challenger's win
        /// gives the awakened Dofus and closes the Éveil; a defeat lets him try again.
        /// </summary>
        internal static async Task FightEndedAsync(GameSession session, FightInstance fight, bool won)
        {
            if (session == null || fight == null || !_challenges.TryGetValue(fight.DefenderLeaderId, out var c)) return;
            if (session.CharacterId != c.Character) return;
            _challenges.TryRemove(fight.DefenderLeaderId, out _);
            try
            {
                var file = Current;
                var dofus = file.Find(c.Dofus);
                if (dofus == null) return;
                if (!won)
                {
                    await StasisEvents.TellAsync(c.Character, $"Défi de l'Éveil perdu contre {dofus.Boss.Name}. Retentez-le quand vous voulez : « .eveil {dofus.Id} defi ».");
                    return;
                }
                if (StageOf(dofus, ProgressOf(c.Character, dofus.Id)) != EveilStage.Challenge) return;
                if (session.Stream == null) return;
                var template = Forgemagic.TemplateOf(dofus.Item);
                var baseEffects = template != null ? Forgemagic.Perfect(template) : new List<Equipment.ItemEffect>();
                var effects = RewardEffects(baseEffects, dofus, file.Label);
                long uid = await Handlers.WorkshopHandler.GiveExactAsync(session.Stream, dofus.Item, effects);
                if (uid == 0)
                {
                    Console.WriteLine($"[Eveil] The awakened {dofus.Id} ({dofus.Item}) could not be created for {c.Character}.");
                    await StasisEvents.TellAsync(c.Character, "Défi remporté, mais le Dofus n'a pas pu être créé (inventaire ?). Le défi reste ouvert.");
                    return;
                }
                Backend.Complete(c.Character, dofus.Id);
                ActivityJournal.Current.Write("stasis.eveil", session.AccountId, c.Character, new { dofus = dofus.Id, item = dofus.Item, uid });
                await StasisEvents.TellAsync(c.Character, $"Éveil réussi ! Vous recevez le Dofus {dofus.Name} stasifié.");
                await StasisEvents.AnnounceAsync($"{session.State.CharacterName} a éveillé le Dofus {dofus.Name} !");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Eveil] The end of the challenge of {c.Character} failed: {ex.Message}");
            }
        }
    }

    /// <summary>The Éveil tables in the world database, created on first use.</summary>
    internal sealed class SqliteEveilStore : IEveilStore
    {
        private static bool _ensured;
        private static readonly object _ensureLock = new();

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            if (_ensured) return connection;
            lock (_ensureLock)
            {
                if (_ensured) return connection;
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterEveil (
                        CharacterId INTEGER NOT NULL,
                        Dofus       TEXT NOT NULL,
                        StartedAt   TEXT NOT NULL,
                        Completed   INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, Dofus)
                    );
                    CREATE TABLE IF NOT EXISTS CharacterEveilDungeons (
                        CharacterId INTEGER NOT NULL,
                        Dofus       TEXT NOT NULL,
                        DungeonId   INTEGER NOT NULL,
                        WonAt       TEXT NOT NULL,
                        PRIMARY KEY (CharacterId, Dofus, DungeonId)
                    );";
                command.ExecuteNonQuery();
                _ensured = true;
            }
            return connection;
        }

        public IReadOnlyDictionary<string, EveilProgress> Of(long characterId)
        {
            var result = new Dictionary<string, EveilProgress>(StringComparer.OrdinalIgnoreCase);
            using var connection = Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Dofus, StartedAt, Completed FROM CharacterEveil WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DateTime.TryParse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.None, out var at);
                    result[reader.GetString(0)] = new EveilProgress { StartedAt = at, Completed = reader.GetInt64(2) != 0 };
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Dofus, DungeonId FROM CharacterEveilDungeons WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (result.TryGetValue(reader.GetString(0), out var p)) p.Dungeons.Add(reader.GetInt32(1));
            }
            return result;
        }

        public void Start(long characterId, string dofus, DateTime at)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM CharacterEveilDungeons WHERE CharacterId = $c AND Dofus = $d;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$d", dofus);
                command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO CharacterEveil (CharacterId, Dofus, StartedAt, Completed) VALUES ($c, $d, $t, 0) " +
                                      "ON CONFLICT (CharacterId, Dofus) DO UPDATE SET StartedAt = $t, Completed = 0;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$d", dofus);
                command.Parameters.AddWithValue("$t", at.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        public void AddDungeon(long characterId, string dofus, int dungeonId, DateTime at)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO CharacterEveilDungeons (CharacterId, Dofus, DungeonId, WonAt) VALUES ($c, $d, $g, $t);";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$d", dofus);
            command.Parameters.AddWithValue("$g", dungeonId);
            command.Parameters.AddWithValue("$t", at.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public void Complete(long characterId, string dofus)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE CharacterEveil SET Completed = 1 WHERE CharacterId = $c AND Dofus = $d;";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$d", dofus);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The Éveil store in memory, for the tests.</summary>
    internal sealed class MemoryEveilStore : IEveilStore
    {
        private readonly ConcurrentDictionary<long, Dictionary<string, EveilProgress>> _rows = new();

        private Dictionary<string, EveilProgress> Rows(long characterId)
            => _rows.GetOrAdd(characterId, _ => new Dictionary<string, EveilProgress>(StringComparer.OrdinalIgnoreCase));

        public IReadOnlyDictionary<string, EveilProgress> Of(long characterId)
        {
            var rows = Rows(characterId);
            lock (rows)
                return rows.ToDictionary(kv => kv.Key, kv =>
                {
                    var copy = new EveilProgress { StartedAt = kv.Value.StartedAt, Completed = kv.Value.Completed };
                    copy.Dungeons.UnionWith(kv.Value.Dungeons);
                    return copy;
                }, StringComparer.OrdinalIgnoreCase);
        }

        public void Start(long characterId, string dofus, DateTime at)
        {
            var rows = Rows(characterId);
            lock (rows) rows[dofus] = new EveilProgress { StartedAt = at };
        }

        public void AddDungeon(long characterId, string dofus, int dungeonId, DateTime at)
        {
            var rows = Rows(characterId);
            lock (rows) if (rows.TryGetValue(dofus, out var p)) p.Dungeons.Add(dungeonId);
        }

        public void Complete(long characterId, string dofus)
        {
            var rows = Rows(characterId);
            lock (rows) if (rows.TryGetValue(dofus, out var p)) p.Completed = true;
        }
    }
}
