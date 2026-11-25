using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Stasis daily events (docs/stasis-gameplay-spec.md §16, content/events/events.json): one a
    /// day, in turn Time Attack, Concours Poutch and Grande Traque, each paying Jetons Event
    /// (<see cref="EventTokens"/>) to its podium -- on a server where the one real player competes
    /// against a seeded field of the bots' results.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ROTATION. The day's event is the rotation's entry at (days since «anchorDate») mod 3
    /// (<see cref="KindOn"/>). It opens at its hour, server local time, once a real player is in the
    /// world -- the rule <see cref="WorldHappenings"/> keeps: the simulated players
    /// (<see cref="Bots.SimulatedPlayers.IsSimulated"/>) neither count as an audience nor as
    /// competitors -- and no later than «latestHour»; an event that did not open by then is skipped
    /// for the day. An administrator opens one at any time with «.event &lt;nom&gt;» (CommandHandler.Events.cs).
    /// One event runs at a time.
    /// </para>
    /// <para>
    /// THE BOTS. The 200 simulated players do not really run dungeons or hit the poutch; what they
    /// put on the board is a field of results drawn from the day and the event
    /// (<see cref="BotEntries"/>), named after them (Jondobot-Aa...), spread over the three
    /// categories. Seeded, so the same day gives the same field after a restart.
    /// </para>
    /// <para>
    /// TIME ATTACK. A solo fight (one person on the blue side) in a room of a dungeon starts that
    /// character's clock for that dungeon when none runs, or again when it is the dungeon's first
    /// room; a won fight with one of the dungeon's bosses in it stops it, and the time is kept if it
    /// beats his best. Read off the fights (<see cref="OnFightCreated"/>, <see cref="FightEndedAsync"/>)
    /// rather than the dungeon's doors, which belong to DungeonHandler. Simplified: the spec's
    /// dungeon brackets by category are not enforced; the category is the character's.
    /// </para>
    /// <para>
    /// POUTCH. A punching bag (<see cref="MobSpawnManager.PunchingBag"/>) is planted on «poutch.map»,
    /// the inner hall of the Pandawushu Dojo (209455110), a room no quest, NPC or monster uses; its
    /// fights follow the kanojedo's training rulebook (<see cref="IsPoutchRoom"/>: no loot, the bag
    /// stays) and are held on its twin board 209585672 (content/fights/arenas.json). A solo
    /// fight there against it scores the damage dealt (FightStatistics.TotalDamage), counted whole
    /// when it took at most «maxTurns» turns and brought back to «maxTurns» turns' worth otherwise
    /// (<see cref="PoutchScore"/>), times the class coefficient of the spec. The fight ends however
    /// the player ends it -- giving up included.
    /// </para>
    /// <para>
    /// GRANDE TRAQUE. «hidden» spots are drawn on outdoor maps of one subarea. No NPC is planted --
    /// placing one would go through the NPC tables the world reads at startup --: standing on a
    /// spot's map is finding it, checked on every tick. One point per spot found first; clues go out
    /// every «clueEveryMinutes» (the zone, then coordinates within a shrinking spread). The bots get
    /// seeded points and the odd «on a piste» line so the chase has company.
    /// </para>
    /// </remarks>
    public static class StasisEvents
    {
        public const string AuthoredFile = "events/events.json";
        public const string TimeAttack = "timeattack", Poutch = "poutch", Traque = "traque";

        /// <summary>One event's settings.</summary>
        public sealed record EventDef(
            string Kind, string Name, int Minutes, int Hour, int WeekendHour,
            IReadOnlyDictionary<string, int[]> Rewards,
            int BotCount, int BotMin, int BotMax,
            IReadOnlyDictionary<string, string> Texts,
            long Map, int MaxTurns, IReadOnlyDictionary<int, double> ClassCoefficients,
            int Hidden, int ClueEveryMinutes, int MinSubareaLevel)
        {
            public string Text(string key, string fallback) => Texts.TryGetValue(key, out var t) ? t : fallback;

            /// <summary>Whether a lower score is a better one: times are.</summary>
            public bool LowerIsBetter => Kind == TimeAttack;
        }

        /// <summary>One article of the Gardien de la Fissure's shop.</summary>
        public sealed record ShopItem(string Id, string Name, int Price, int Item, int Quantity, bool Rune);

        /// <summary>The whole file.</summary>
        public sealed record Settings(bool Enabled, DateOnly Anchor, int LatestHour,
                                      IReadOnlyList<string> Rotation,
                                      IReadOnlyDictionary<string, EventDef> Events,
                                      IReadOnlyList<ShopItem> Shop)
        {
            public static Settings Off { get; } = new(false, new DateOnly(2026, 11, 24), 23,
                new[] { TimeAttack, Poutch, Traque }, new Dictionary<string, EventDef>(), Array.Empty<ShopItem>());
        }

        /// <summary>One line of a ranking: a character (Id &gt; 0) or a bot (Id 0).</summary>
        public sealed record Entry(string Name, long CharacterId, string Category, long Score);

        /// <summary>A hidden spot of the Traque.</summary>
        public sealed class Spot
        {
            public long MapId { get; init; }
            public int X { get; init; }
            public int Y { get; init; }
            public long FoundBy { get; set; }
        }

        /// <summary>The event running now.</summary>
        public sealed class Running
        {
            public EventDef Def { get; init; } = null!;
            public DateTime Until { get; init; }
            public DateOnly Day { get; init; }
            public ConcurrentDictionary<long, Entry> Best { get; } = new();
            public List<Entry> Bots { get; init; } = new();
            /// <summary>Time Attack: the character's clock, by dungeon.</summary>
            public ConcurrentDictionary<long, (int Dungeon, DateTime Since)> Runs { get; } = new();
            public List<Spot> Spots { get; } = new();
            public int Subarea { get; set; }
            public int ClueStage { get; set; }
            public DateTime NextClue { get; set; }
            public long PoutchGroup { get; set; }
        }

        private static Settings _settings = Settings.Off;
        private static Timer? _timer;
        private static int _ticking;
        private static Running? _running;
        private static DateOnly _lastRunDay = DateOnly.MinValue;

        public static Settings Current => _settings;
        public static Running? Now => _running;

        /// <summary>
        /// Whether a map is the Concours Poutch's training room («poutch.map»). Its fights follow the
        /// kanojedo's training rulebook (Kanojedo.IsDojo): the room has no master puch to say so.
        /// </summary>
        public static bool IsPoutchRoom(long mapId)
            => mapId > 0 && _settings.Events.TryGetValue(Poutch, out var def) && def.Map == mapId;

        // ─── Reading ────────────────────────────────────────────────────────────────────────

        public static Settings Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var anchor = DateOnly.TryParse(Str(root, "anchorDate", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
                ? a : new DateOnly(2026, 11, 24);
            var rotation = new List<string>();
            if (root.TryGetProperty("rotation", out var rot) && rot.ValueKind == JsonValueKind.Array)
                foreach (var r in rot.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } k) rotation.Add(k.ToLowerInvariant());
            if (rotation.Count == 0) rotation.AddRange(new[] { TimeAttack, Poutch, Traque });

            var events = new Dictionary<string, EventDef>();
            foreach (var kind in new[] { TimeAttack, Poutch, Traque })
            {
                if (!root.TryGetProperty(kind, out var e) || e.ValueKind != JsonValueKind.Object) continue;
                var rewards = new Dictionary<string, int[]>();
                if (e.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Object)
                    foreach (var p in rw.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Array)
                            rewards[p.Name] = p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
                                                    .Select(v => v.GetInt32()).ToArray();
                var texts = new Dictionary<string, string>();
                foreach (var p in e.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) texts[p.Name] = p.Value.GetString() ?? "";
                var coefs = new Dictionary<int, double>();
                if (e.TryGetProperty("classCoefficients", out var cc) && cc.ValueKind == JsonValueKind.Object)
                    foreach (var p in cc.EnumerateObject())
                        if (int.TryParse(p.Name, out int breed) && p.Value.ValueKind == JsonValueKind.Number)
                            coefs[breed] = p.Value.GetDouble();
                int hour = Math.Clamp(Int(e, "hour", 18), 0, 23);
                events[kind] = new EventDef(kind, Str(e, "name", kind), Math.Max(1, Int(e, "minutes", 60)),
                    hour, Math.Clamp(Int(e, "weekendHour", hour), 0, 23), rewards,
                    Math.Max(0, Int(e, "botCount", 10)), Int(e, "botMin", 0), Math.Max(Int(e, "botMin", 0), Int(e, "botMax", 100)),
                    texts, Long(e, "map", 0), Math.Max(1, Int(e, "maxTurns", 3)), coefs,
                    Math.Max(1, Int(e, "hidden", 5)), Math.Max(1, Int(e, "clueEveryMinutes", 5)), Int(e, "minSubareaLevel", 1));
            }

            var shop = new List<ShopItem>();
            if (root.TryGetProperty("shop", out var sh) && sh.ValueKind == JsonValueKind.Array)
                foreach (var i in sh.EnumerateArray())
                {
                    string id = Str(i, "id", "");
                    int price = Int(i, "price", 0);
                    bool rune = i.TryGetProperty("rune", out var rv) && rv.ValueKind == JsonValueKind.True;
                    int item = Int(i, "item", 0);
                    if (id.Length == 0 || price <= 0 || (!rune && item <= 0)) continue;
                    shop.Add(new ShopItem(id.ToLowerInvariant(), Str(i, "name", id), price, item, Math.Max(1, Int(i, "quantity", 1)), rune));
                }

            return new Settings(!root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                anchor, Math.Clamp(Int(root, "latestHour", 23), 0, 23), rotation, events, shop);
        }

        // ─── Pure rules (tested) ────────────────────────────────────────────────────────────

        /// <summary>The event of a day: the rotation's entry at (days since the anchor) mod its length.</summary>
        public static string KindOn(DateOnly day, Settings settings)
        {
            int n = settings.Rotation.Count;
            if (n == 0) return TimeAttack;
            int days = day.DayNumber - settings.Anchor.DayNumber;
            return settings.Rotation[((days % n) + n) % n];
        }

        /// <summary>The hour an event opens on a day: its weekend hour on Saturday and Sunday.</summary>
        public static int StartHourOf(EventDef def, DateOnly day)
            => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? def.WeekendHour : def.Hour;

        /// <summary>The spec's categories: III evolved, I up to level 200, II above.</summary>
        public static string Category(int level, bool evolved) => evolved ? "III" : level <= 200 ? "I" : "II";

        /// <summary>
        /// A category's ranking, best first: the lowest score first for a time, the highest for
        /// anything else; ties go to the real character, then by name, so it is stable.
        /// </summary>
        public static List<Entry> Rank(IEnumerable<Entry> entries, bool lowerIsBetter)
        {
            var ordered = lowerIsBetter ? entries.OrderBy(e => e.Score) : entries.OrderByDescending(e => e.Score);
            return ordered.ThenByDescending(e => e.CharacterId > 0).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The tokens a 1-based rank pays in a category: nothing past the podium.</summary>
        public static int TokensFor(EventDef def, string category, int rank)
            => rank >= 1 && def.Rewards.TryGetValue(category, out var r) && rank <= r.Length ? r[rank - 1] : 0;

        /// <summary>
        /// The bots' field for an event on a day: <see cref="EventDef.BotCount"/> results between
        /// its bounds, spread over the three categories, the same every time for the same day.
        /// </summary>
        public static List<Entry> BotEntries(EventDef def, DateOnly day)
        {
            int kindIndex = def.Kind == TimeAttack ? 1 : def.Kind == Poutch ? 2 : 3;
            var dice = new Random(day.DayNumber * 7 + kindIndex);
            var list = new List<Entry>();
            var categories = new[] { "I", "II", "III" };
            var used = new HashSet<int>();
            for (int i = 0; i < def.BotCount; i++)
            {
                int number;
                do number = dice.Next(1, Bots.SimulatedPlayers.Most + 1); while (!used.Add(number) && used.Count < Bots.SimulatedPlayers.Most);
                // Higher categories score better: a time shorter, damage greater.
                string category = categories[i % 3];
                double lift = category == "I" ? 0 : category == "II" ? 0.15 : 0.3;
                double quality = Math.Min(1, dice.NextDouble() * (1 + lift));
                double span = def.BotMax - def.BotMin;
                long score = (long)Math.Round(def.LowerIsBetter ? def.BotMax - span * quality : def.BotMin + span * quality);
                list.Add(new Entry(Bots.SimulatedPlayers.NameOf(number), 0, category, Math.Clamp(score, def.BotMin, def.BotMax)));
            }
            return list;
        }

        /// <summary>
        /// A poutch fight's score: the damage whole within <paramref name="maxTurns"/> turns,
        /// otherwise brought back to that many turns' worth, times the class coefficient.
        /// </summary>
        public static long PoutchScore(long damage, int turns, int maxTurns, double coefficient)
        {
            if (damage <= 0) return 0;
            double counted = turns <= maxTurns ? damage : (double)damage * maxTurns / Math.Max(1, turns);
            return (long)Math.Round(counted * (coefficient <= 0 ? 1 : coefficient));
        }

        /// <summary>Keeps the better of two results for one character.</summary>
        public static Entry Better(Entry? old, Entry fresh, bool lowerIsBetter)
            => old == null ? fresh
             : lowerIsBetter ? (fresh.Score < old.Score ? fresh : old)
             : (fresh.Score > old.Score ? fresh : old);

        /// <summary>The spread of the Traque's coordinates clue at a stage: 6, 4, 2, then 1.</summary>
        public static int ClueSpread(int stage) => stage <= 1 ? 6 : stage == 2 ? 4 : stage == 3 ? 2 : 1;

        /// <summary>«m:ss» for a time in seconds.</summary>
        public static string Clock(long seconds) => $"{seconds / 60}:{seconds % 60:00}";

        // ─── The clock ──────────────────────────────────────────────────────────────────────

        public static void Start()
        {
            try { _settings = Load(Paths.ContentFile(AuthoredFile)); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Events] Could not read {AuthoredFile}: {ex.Message}");
                return;
            }
            Console.WriteLine($"[Events] {_settings.Events.Count} event(s), {_settings.Shop.Count} shop article(s), " +
                              $"today: {KindOn(DateOnly.FromDateTime(DateTime.Now), _settings)}.");
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
                try { TickAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"[Events] The clock failed: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _ticking, 0); }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15));
        }

        internal static bool RealPlayerOnline()
            => SessionRegistry.InWorld().Any(s => !Bots.SimulatedPlayers.IsSimulated(s.AccountId));

        private static async Task TickAsync()
        {
            var now = DateTime.Now;
            if (_running is { } r)
            {
                if (r.Def.Kind == Traque) await TraqueTickAsync(r, now);
                if (now >= r.Until) await EndAsync();
                return;
            }
            if (!_settings.Enabled) return;
            var today = DateOnly.FromDateTime(now);
            if (_lastRunDay == today) return;
            string kind = KindOn(today, _settings);
            if (!_settings.Events.TryGetValue(kind, out var def)) return;
            if (now.Hour < StartHourOf(def, today) || now.Hour > _settings.LatestHour) return;
            if (!RealPlayerOnline()) return;
            await BeginAsync(kind);
        }

        /// <summary>Opens an event now. Null when it started; the reason otherwise.</summary>
        public static async Task<string?> BeginAsync(string kind)
        {
            if (_running != null) return $"{_running.Def.Name} est déjà en cours.";
            if (!_settings.Events.TryGetValue(kind, out var def)) return $"Événement inconnu : {kind}.";
            var now = DateTime.Now;
            var day = DateOnly.FromDateTime(now);
            var run = new Running { Def = def, Until = now.AddMinutes(def.Minutes), Day = day, Bots = BotEntries(def, day) };
            _lastRunDay = day;

            string zone = "";
            if (kind == Poutch && def.Map > 0)
            {
                bool already = MobSpawnManager.GetMobsForMap(def.Map).Any(g => g.Members.Any(m => m.Monster?.Id == MobSpawnManager.PunchingBag));
                if (!already)
                    run.PoutchGroup = MobSpawnManager.SpawnNamed(def.Map, MobSpawnManager.PunchingBag, 1)?.MobId ?? 0;
            }
            if (kind == Traque)
            {
                if (!DrawSpots(run, def)) return "Aucune zone trouvée pour cacher les PNJ.";
                zone = SubareaName(run.Subarea);
                run.NextClue = now.AddMinutes(def.ClueEveryMinutes);
            }
            _running = run;
            Console.WriteLine($"[Events] {def.Name} opens until {run.Until:HH:mm}.");
            await AnnounceAsync(def.Text("start", def.Name + " commence !")
                .Replace("{minutes}", def.Minutes.ToString()).Replace("{hidden}", run.Spots.Count.ToString()).Replace("{zone}", zone));
            return null;
        }

        /// <summary>Closes the running event: ranks, pays, cries the podium.</summary>
        public static async Task EndAsync()
        {
            var r = Interlocked.Exchange(ref _running, null);
            if (r == null) return;
            if (r.PoutchGroup != 0 && r.Def.Map > 0) MobSpawnManager.RemoveMobGroup(r.Def.Map, r.PoutchGroup);

            var podium = new List<string>();
            foreach (var category in new[] { "I", "II", "III" })
            {
                var ranked = Rank(r.Bots.Where(b => b.Category == category).Concat(r.Best.Values.Where(e => e.Category == category)),
                                  r.Def.LowerIsBetter);
                if (ranked.Count == 0) continue;
                podium.Add($"[{category}] " + string.Join(", ", ranked.Take(3).Select((e, i) => $"{i + 1}. {e.Name} ({Show(r.Def, e.Score)})")));
                for (int i = 0; i < ranked.Count; i++)
                {
                    var e = ranked[i];
                    if (e.CharacterId <= 0) continue;
                    int tokens = TokensFor(r.Def, category, i + 1);
                    if (tokens > 0) EventTokens.Add(e.CharacterId, tokens);
                    await TellAsync(e.CharacterId,
                        $"{r.Def.Name} : vous finissez {i + 1}e de la catégorie {category} ({Show(r.Def, e.Score)})" +
                        (tokens > 0 ? $" et gagnez {tokens} jetons (solde {EventTokens.Balance(e.CharacterId)})." : "."));
                }
            }
            Console.WriteLine($"[Events] {r.Def.Name} closed.");
            await AnnounceAsync(r.Def.Text("end", "Fin de " + r.Def.Name + ". Podium : {podium}")
                .Replace("{podium}", podium.Count == 0 ? "personne" : string.Join(" | ", podium)));
        }

        /// <summary>A score as the event reads it.</summary>
        public static string Show(EventDef def, long score)
            => def.Kind == TimeAttack ? Clock(score) : def.Kind == Poutch ? $"{score:N0} dégâts" : $"{score} pt";

        /// <summary>The running event's board for one character: his place and the top three per category.</summary>
        public static string Board(long characterId)
        {
            var r = _running;
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (r == null)
            {
                string kind = KindOn(today, _settings);
                string next = _settings.Events.TryGetValue(kind, out var d)
                    ? $"{d.Name} à {StartHourOf(d, today)}h" : kind;
                return $"Aucun événement en cours. Aujourd'hui : {next}" +
                       (_lastRunDay == today ? " (déjà passé)." : ".") +
                       $" Demain : {Name(KindOn(today.AddDays(1), _settings))}. Jetons : {EventTokens.Balance(characterId)}.";
            }
            var left = r.Until - DateTime.Now;
            var lines = new List<string> { $"{r.Def.Name} en cours, encore {Math.Max(0, (int)left.TotalMinutes)} min." };
            foreach (var category in new[] { "I", "II", "III" })
            {
                var ranked = Rank(r.Bots.Where(b => b.Category == category).Concat(r.Best.Values.Where(e => e.Category == category)), r.Def.LowerIsBetter);
                if (ranked.Count == 0) continue;
                string top = string.Join(", ", ranked.Take(3).Select((e, i) => $"{i + 1}. {e.Name} {Show(r.Def, e.Score)}"));
                int mine = ranked.FindIndex(e => e.CharacterId == characterId);
                lines.Add($"[{category}] {top}" + (mine >= 0 ? $" — vous : {mine + 1}e ({Show(r.Def, ranked[mine].Score)})" : ""));
            }
            if (r.Def.Kind == Traque)
                lines.Add($"PNJ restants : {r.Spots.Count(s => s.FoundBy == 0)}/{r.Spots.Count}, zone : {SubareaName(r.Subarea)}.");
            return string.Join(" ", lines);
        }

        private static string Name(string kind) => _settings.Events.TryGetValue(kind, out var d) ? d.Name : kind;

        // ─── Fights ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A fight just built, from FightHandler (the character who started it is the session's):
        /// the runes of his worn pieces, the Time Attack clock, and the Fissure's wave.
        /// </summary>
        internal static void OnFightCreated(long characterId, FightInstance fight)
        {
            try
            {
                var me = fight.Azul.FirstOrDefault(f => f.Id == characterId && !f.IsMonster);
                if (me != null)
                {
                    int rune = EventTokens.RuneBonusOf(Equipment.All.Where(i => i.Position != Equipment.Bag).Select(i => i.Uid));
                    if (rune > 0) me.DamageDealtPercent += rune;
                }

                if (_running is { } r && r.Def.Kind == TimeAttack && Solo(fight)
                    && DungeonManager.OfRoom(fight.RoleplayMapId) is { } dungeon)
                {
                    bool firstRoom = dungeon.Rooms.Count > 0 && dungeon.Rooms[0] == fight.RoleplayMapId;
                    if (firstRoom || !r.Runs.TryGetValue(characterId, out var run) || run.Dungeon != dungeon.Id)
                        r.Runs[characterId] = (dungeon.Id, DateTime.UtcNow);
                }

                Fissure.OnFightCreated(characterId, fight);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Events] A fight's start could not be read: {ex.Message}");
            }
        }

        /// <summary>A fight over, as one of its people lives it (FightHandler's TerminarParaUnoAsync).</summary>
        internal static async Task FightEndedAsync(GameSession session, FightInstance fight, bool won)
        {
            try
            {
                long id = session.CharacterId;
                if (_running is { } r && !Bots.SimulatedPlayers.IsSimulated(session.AccountId))
                {
                    string category = Category(session.State.CharacterLevel, Stasis.IsEvolved(id));
                    if (r.Def.Kind == TimeAttack && won && r.Runs.TryGetValue(id, out var run)
                        && DungeonManager.Get(run.Dungeon) is { } dungeon
                        && fight.Rojo.Any(m => dungeon.Bosses.Contains(m.MonsterId)))
                    {
                        r.Runs.TryRemove(id, out _);
                        long seconds = Math.Max(1, (long)(DateTime.UtcNow - run.Since).TotalSeconds);
                        var fresh = new Entry(session.State.CharacterName, id, category, seconds);
                        var kept = r.Best.AddOrUpdate(id, fresh, (_, old) => Better(old, fresh, true));
                        await TellAsync(id, $"Time Attack : {dungeon.Name} en {Clock(seconds)} (meilleur : {Clock(kept.Score)}).");
                    }
                    else if (r.Def.Kind == Poutch && fight.RoleplayMapId == r.Def.Map && Solo(fight)
                             && fight.Rojo.Any(m => m.MonsterId == MobSpawnManager.PunchingBag))
                    {
                        var stats = fight.StatisticsOf(id);
                        double coef = r.Def.ClassCoefficients.TryGetValue(session.State.Breed, out var c) ? c : 1.0;
                        long score = PoutchScore(stats?.TotalDamage ?? 0, stats?.TurnsPlayed ?? 1, r.Def.MaxTurns, coef);
                        var fresh = new Entry(session.State.CharacterName, id, category, score);
                        var kept = r.Best.AddOrUpdate(id, fresh, (_, old) => Better(old, fresh, false));
                        await TellAsync(id, $"Poutch : {score:N0} points de dégâts (meilleur : {kept.Score:N0}).");
                    }
                }
                await Fissure.FightEndedAsync(session, fight, won);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Events] A fight's end could not be read: {ex.Message}");
            }
        }

        private static bool Solo(FightInstance fight) => fight.Azul.Count(f => !f.IsMonster && !f.EsInvocado) == 1;

        // ─── The Traque ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a map may hide one of the Traque's NPCs: outdoors, on the Twelve's own world map
        /// (worldMap 1) and not a fight board. Without the world map a third of the subareas drawn
        /// were somewhere else -- the dream lands (Valdi's Nightmare, worldMap 19), Frigost's island,
        /// the dimensions, the Koliseo's boards at [-13,-29] --, out of most players' reach and with
        /// [x,y] clues that point at a square of Amakna's world instead.
        /// </summary>
        internal static bool TraqueGround(MapInfo m)
            => m.Outdoor && m.SubAreaId > 0 && m.WorldMap == 1
               && m.Flags != MapManager.ArenaFlags && m.Flags != MapManager.RaidArenaFlags;

        private static bool DrawSpots(Running run, EventDef def)
        {
            var bySubarea = MapManager.Maps.Values
                .Where(m => TraqueGround(m) && Interactives.LevelOfSubArea(m.SubAreaId) >= def.MinSubareaLevel)
                .GroupBy(m => m.SubAreaId)
                .Where(g => g.Count() >= def.Hidden * 2)
                .ToList();
            for (int attempt = 0; attempt < 20 && bySubarea.Count > 0; attempt++)
            {
                var group = bySubarea[Random.Shared.Next(bySubarea.Count)];
                var maps = group.Where(m => MapManager.OnTheWorldMap(m.MapId)).OrderBy(_ => Random.Shared.Next()).Take(def.Hidden).ToList();
                if (maps.Count < def.Hidden) continue;
                run.Subarea = group.Key;
                foreach (var m in maps) run.Spots.Add(new Spot { MapId = m.MapId, X = m.PosX, Y = m.PosY });
                Console.WriteLine($"[Events] Traque in subarea {group.Key}: {string.Join(", ", maps.Select(m => m.MapId))}.");
                return true;
            }
            return false;
        }

        private static async Task TraqueTickAsync(Running r, DateTime now)
        {
            string zone = SubareaName(r.Subarea);
            foreach (var s in SessionRegistry.InWorld())
            {
                if (Bots.SimulatedPlayers.IsSimulated(s.AccountId) || !s.HasCharacter) continue;
                var spot = r.Spots.FirstOrDefault(p => p.FoundBy == 0 && p.MapId == s.MapId);
                if (spot == null) continue;
                spot.FoundBy = s.CharacterId;
                string category = Category(s.State.CharacterLevel, Stasis.IsEvolved(s.CharacterId));
                var fresh = new Entry(s.State.CharacterName, s.CharacterId, category, 1);
                r.Best.AddOrUpdate(s.CharacterId, fresh, (_, old) => old with { Score = old.Score + 1 });
                await AnnounceAsync(r.Def.Text("found", "{name} a trouvé un PNJ caché !")
                    .Replace("{name}", s.State.CharacterName).Replace("{zone}", zone)
                    .Replace("{x}", spot.X.ToString()).Replace("{y}", spot.Y.ToString()));
            }
            if (r.Spots.All(p => p.FoundBy != 0)) { await EndAsync(); return; }
            if (now < r.NextClue) return;
            r.NextClue = now.AddMinutes(r.Def.ClueEveryMinutes);
            r.ClueStage++;
            var left = r.Spots.Where(p => p.FoundBy == 0).ToList();
            var target = left[Random.Shared.Next(left.Count)];
            int spread = ClueSpread(r.ClueStage);
            int x = target.X + Random.Shared.Next(-spread / 2, spread / 2 + 1);
            int y = target.Y + Random.Shared.Next(-spread / 2, spread / 2 + 1);
            await AnnounceAsync(r.Def.Text("clueCoords", "Indice : {zone} [{x},{y}]")
                .Replace("{zone}", zone).Replace("{x}", x.ToString()).Replace("{y}", y.ToString()).Replace("{spread}", spread.ToString()));
            var bot = r.Bots.Count > 0 ? r.Bots[Random.Shared.Next(r.Bots.Count)] : null;
            if (bot != null && Random.Shared.Next(2) == 0)
                await AnnounceAsync(r.Def.Text("botNews", "{name} est sur une piste...").Replace("{name}", bot.Name));
        }

        private static string SubareaName(int subarea)
        {
            string name = DatabaseManager.GetSubAreaName(subarea);
            return name.Length == 0 ? $"#{subarea}" : name;
        }

        // ─── Talking ────────────────────────────────────────────────────────────────────────

        /// <summary>A notice to every real player in the world, as WorldHappenings cries.</summary>
        internal static async Task AnnounceAsync(string text)
        {
            Console.WriteLine($"[Events] {text}");
            foreach (var s in SessionRegistry.InWorld())
            {
                if (Bots.SimulatedPlayers.IsSimulated(s.AccountId)) continue;
                try { await s.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text))); }
                catch (Exception ex) { Console.WriteLine($"[Events] An announcement did not reach {s.State.CharacterName}: {ex.Message}"); }
            }
        }

        /// <summary>A notice to one character, when he is connected.</summary>
        internal static async Task TellAsync(long characterId, string text)
        {
            var s = SessionRegistry.InWorld().FirstOrDefault(x => x.CharacterId == characterId);
            if (s == null) return;
            try { await s.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text))); }
            catch (Exception ex) { Console.WriteLine($"[Events] A notice did not reach {characterId}: {ex.Message}"); }
        }

        // ─── JSON ───────────────────────────────────────────────────────────────────────────

        internal static int Int(JsonElement e, string key, int fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
               && v.TryGetInt32(out int i) ? i : fallback;

        internal static long Long(JsonElement e, string key, long fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
               && v.TryGetInt64(out long i) ? i : fallback;

        internal static string Str(JsonElement e, string key, string fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() ?? fallback : fallback;

        /// <summary>For the tests: settings set by hand, nothing running.</summary>
        internal static void UseForTests(Settings settings)
        {
            _settings = settings;
            _running = null;
            _lastRunDay = DateOnly.MinValue;
        }
    }
}
