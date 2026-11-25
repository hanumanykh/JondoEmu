using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the catalogue needs to know of one spell to say which upgrades change something on it:
    /// its highest grade's row of SpellLevels (cost, limits, range, flags), the cap its effects put
    /// on their stacks, and the level it is first learnt at.
    /// </summary>
    internal sealed record SpellFacts(
        int Spell, int Breed, string Name, int Level,
        int ApCost, int CastsPerTurn, int CastsPerTarget, int Cooldown, int InitialCooldown,
        int Critical, int MinRange, int MaxRange, int Flags, int MaxStack);

    /// <summary>
    /// spell_dons_overrides.json: how the Stasis-like spell upgrades are generated from each spell's
    /// real data, and the hand corrections on top (names, ranks, prerequisites, removals, extras).
    /// </summary>
    internal sealed class SpellDonOverridesFile
    {
        /// <summary>Off: only the hand-written dons of spell_dons.json are offered.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// The hand-written dons of spell_dons.json whose kind the generator covers (critique,
        /// portee, relance, pa, lancers) stop being OFFERED on a spell the generator has dons for;
        /// a character who holds one keeps it and its effect.
        /// </summary>
        public bool RetireLegacyOverlaps { get; set; } = true;
        /// <summary>Per kind: value per rank and the level each rank opens at (its count is the max rank).</summary>
        public Dictionary<string, SpellDonKindRule> Kinds { get; set; } = new();
        /// <summary>French names by spell id (the client's data is Spanish).</summary>
        public Dictionary<string, string> SpellNames { get; set; } = new();
        /// <summary>Generated don ids, spell ids ("13106") or "spell:kind" ("13106:adaptabilite") never offered.</summary>
        public List<string> Disable { get; set; } = new();
        /// <summary>Corrections of a generated don, by its id or by "spell:kind".</summary>
        public Dictionary<string, SpellDonPatch> Patches { get; set; } = new();
        /// <summary>Dons added as they are written, by breed id as text.</summary>
        public Dictionary<string, List<SpellDonDefinition>> Extra { get; set; } = new();
    }

    internal sealed class SpellDonKindRule
    {
        public int Value { get; set; } = 1;
        public List<int> RankLevels { get; set; } = new() { 1 };
        /// <summary>
        /// For allonge: the maximum range from which the last rank is offered (short spells stop a
        /// rank earlier). 0: always.
        /// </summary>
        public int LastRankFromRange { get; set; }
    }

    internal sealed class SpellDonPatch
    {
        public int? Value { get; set; }
        public List<int>? RankLevels { get; set; }
        public int? Level { get; set; }
        public List<SpellDonRequirement>? Requires { get; set; }
    }

    /// <summary>To take rank <see cref="AtRank"/> of a don, the same spell needs <see cref="Rank"/> points of <see cref="Kind"/>.</summary>
    internal sealed class SpellDonRequirement
    {
        public int AtRank { get; set; } = 1;
        public string Kind { get; set; } = "";
        public int Rank { get; set; } = 1;
    }

    /// <summary>
    /// The generated catalogue: every class spell (base and variant) gets the upgrades that change
    /// something on it -- no Clairvoyance without a line-of-sight test, no Cadence without a
    /// cooldown -- with the Stasis caps by level, then spell_dons_overrides.json on top.
    /// </summary>
    internal static class SpellDonCatalogue
    {
        internal const string Economie = "economie", Enchainement = "enchainement", Insistance = "insistance",
                              Precision = "precision", Allonge = "allonge", Adaptabilite = "adaptabilite",
                              Clairvoyance = "clairvoyance", Liberte = "liberte", Persistance = "persistance",
                              Cadence = "cadence", Anticipation = "anticipation";

        internal static readonly string[] Kinds =
            { Economie, Enchainement, Insistance, Precision, Allonge, Adaptabilite, Clairvoyance, Liberte, Persistance, Cadence, Anticipation };

        /// <summary>The kinds of spell_dons.json the generator replaces: a legacy don of one of them is not offered beside it.</summary>
        internal static readonly Dictionary<string, string> LegacyEquivalent = new()
        {
            [SpellDons.Critical] = Precision, [SpellDons.Range] = Allonge, [SpellDons.Cooldown] = Cadence,
            [SpellDons.ApCost] = Economie, [SpellDons.CastsPerTurn] = Enchainement,
        };

        /// <summary>The Stasis defaults (play-stasis.net wiki, dons de sorts): ranks open at 10/70/90 as the kind allows.</summary>
        internal static SpellDonKindRule DefaultRule(string kind) => kind switch
        {
            Enchainement => new() { Value = 1, RankLevels = new() { 1, 70 } },
            Insistance => new() { Value = 1, RankLevels = new() { 1, 90 } },
            Precision => new() { Value = 10, RankLevels = new() { 1, 70, 90 } },
            Allonge => new() { Value = 1, RankLevels = new() { 1, 70, 90 }, LastRankFromRange = 5 },
            _ => new() { Value = 1, RankLevels = new() { 1 } },
        };

        internal static SpellDonKindRule RuleOf(string kind, SpellDonOverridesFile o)
            => o.Kinds.TryGetValue(kind, out var r) && r.RankLevels.Count > 0 ? r : DefaultRule(kind);

        /// <summary>Whether this kind changes anything on this spell.</summary>
        internal static bool Applies(string kind, SpellFacts f) => kind switch
        {
            Economie => f.ApCost >= 2,
            Enchainement => f.CastsPerTurn > 0,
            Insistance => f.CastsPerTarget > 0,
            Precision => f.Critical > 0,
            Allonge => f.MaxRange > 0 && f.MaxRange >= f.MinRange,
            Adaptabilite => f.MaxRange > 0 && (f.Flags & SpellLevelFlags.RangeCanBeBoosted) == 0,
            Clairvoyance => f.MaxRange > 0 && (f.Flags & SpellLevelFlags.CastTestLos) != 0,
            Liberte => (f.Flags & (SpellLevelFlags.CastInLine | SpellLevelFlags.CastInDiagonal)) != 0,
            Persistance => f.MaxStack >= 1,
            Cadence => f.Cooldown >= 2,
            Anticipation => f.InitialCooldown >= 1,
            _ => false,
        };

        internal static string IdOf(int breed, int spell, string kind) => $"s{breed}-{spell}-{kind}";

        /// <summary>The upgrades of one spell, overrides applied (names, removals, patches).</summary>
        internal static List<SpellDonDefinition> Generate(SpellFacts f, SpellDonOverridesFile o)
        {
            var list = new List<SpellDonDefinition>();
            if (o.Disable.Contains(f.Spell.ToString())) return list;
            string name = o.SpellNames.TryGetValue(f.Spell.ToString(), out var n) && n.Length > 0 ? n : f.Name;
            if (name.Length == 0) name = $"Sort {f.Spell}";
            foreach (string kind in Kinds)
            {
                if (!Applies(kind, f)) continue;
                string id = IdOf(f.Breed, f.Spell, kind), key = $"{f.Spell}:{kind}";
                if (o.Disable.Contains(id) || o.Disable.Contains(key)) continue;
                var rule = RuleOf(kind, o);
                var levels = rule.RankLevels.ToList();
                if (kind == Allonge && rule.LastRankFromRange > 0 && f.MaxRange < rule.LastRankFromRange && levels.Count > 1)
                    levels.RemoveAt(levels.Count - 1);
                var don = new SpellDonDefinition
                {
                    Id = id, Spell = f.Spell, SpellName = name, Kind = kind, Value = rule.Value,
                    Level = Math.Max(1, f.Level), Generated = true,
                };
                // Insistance's last rank asks for one point of Enchaînement, when the spell has one.
                if (kind == Insistance && levels.Count > 1 && Applies(Enchainement, f) && !o.Disable.Contains($"{f.Spell}:{Enchainement}"))
                    don.Requires.Add(new SpellDonRequirement { AtRank = levels.Count, Kind = Enchainement, Rank = 1 });
                if (o.Patches.TryGetValue(id, out var patch) || o.Patches.TryGetValue(key, out patch))
                {
                    if (patch.Value is { } v) don.Value = v;
                    if (patch.RankLevels is { Count: > 0 } rl) levels = rl.ToList();
                    if (patch.Level is { } l) don.Level = l;
                    if (patch.Requires != null) don.Requires = patch.Requires.ToList();
                }
                // A rank never opens before the spell is learnt.
                don.RankLevels = levels.Select(l => Math.Max(l, don.Level)).ToList();
                don.MaxRank = levels.Count;
                list.Add(don);
            }
            return list;
        }

        /// <summary>Every class's generated dons (plus the overrides' extras), by breed id as text.</summary>
        internal static Dictionary<string, List<SpellDonDefinition>> Build(IEnumerable<SpellFacts> facts, SpellDonOverridesFile o)
        {
            var byBreed = new Dictionary<string, List<SpellDonDefinition>>();
            if (o.Enabled)
                foreach (var f in facts)
                {
                    var dons = Generate(f, o);
                    if (dons.Count == 0) continue;
                    string b = f.Breed.ToString();
                    if (!byBreed.TryGetValue(b, out var l)) byBreed[b] = l = new List<SpellDonDefinition>();
                    l.AddRange(dons);
                }
            foreach (var (b, extra) in o.Extra)
            {
                if (!byBreed.TryGetValue(b, out var l)) byBreed[b] = l = new List<SpellDonDefinition>();
                foreach (var don in extra)
                {
                    if (don.RankLevels.Count == 0) don.RankLevels = Enumerable.Repeat(don.Level, Math.Max(1, don.MaxRank)).ToList();
                    l.RemoveAll(d => string.Equals(d.Id, don.Id, StringComparison.OrdinalIgnoreCase));
                    l.Add(don);
                }
            }
            return byBreed;
        }

        // ─── The spells' real data ──────────────────────────────────────────────────────

        /// <summary>Where the facts come from: the world database. Tests put a list of their own.</summary>
        internal static Func<IReadOnlyList<SpellFacts>> FactsSource = ReadFacts;

        private static IReadOnlyList<SpellFacts> ReadFacts()
        {
            var facts = new List<SpellFacts>();
            try
            {
                if (!SpellTable.IsLoaded) return facts;
                var names = ReadNames();
                foreach (int breed in SpellTable.ClassBreeds)
                    foreach (var pair in SpellTable.PairsOf(breed))
                        foreach (int spell in new[] { pair.Base, pair.Variant })
                        {
                            if (spell <= 0) continue;
                            string name = names.GetValueOrDefault(spell, "");
                            if (name.StartsWith("[!]", StringComparison.Ordinal)) continue;
                            int grade = SpellTable.GradeFor(spell, 200);
                            if (grade <= 0) continue;
                            int learnt = 1;
                            while (learnt < 200 && SpellTable.GradeFor(spell, learnt) == 0) learnt++;
                            var l = Handlers.FightHandler.LimitesDeGrado(spell, grade);
                            if (l.LevelId == 0) continue;
                            int stack = 0;
                            foreach (var e in SpellEffects.De(spell, grade))
                                if (e.Duration != 0 && e.MaxStack >= 1) stack = Math.Max(stack, e.MaxStack);
                            facts.Add(new SpellFacts(spell, breed, name, learnt, l.Cost, l.PorTurno, l.PorObjetivo,
                                l.Intervalo, l.EsperaInicial, l.CriticoPropio, l.AlcanceMinimo, l.AlcanceMaximo, l.Flags, stack));
                        }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellDons] The spell upgrade catalogue could not be generated: {ex.Message}");
            }
            return facts;
        }

        private static Dictionary<int, string> ReadNames()
        {
            var names = new Dictionary<int, string>();
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT s.Id, s.NameId, t.Text FROM Spells s LEFT JOIN Translations t ON t.Key = CAST(s.NameId AS TEXT);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // French from the client's text table first, the database's Spanish as fallback.
                    string fallback = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string name = reader.IsDBNull(1) ? fallback : GameText.Of(reader.GetInt64(1), fallback);
                    if (name.Length > 0) names[reader.GetInt32(0)] = name;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellDons] Could not read the spell names: {ex.Message}");
            }
            return names;
        }

        /// <summary>
        /// The generated catalogue of a content, built once: the French names of the hand-written
        /// file are used for the spells they name, before the overrides' own.
        /// </summary>
        internal static Dictionary<string, List<SpellDonDefinition>> Of(ProgressionContent content)
        {
            var file = content.SpellDons;
            if (file.GeneratedCache != null) return file.GeneratedCache;
            lock (file)
            {
                if (file.GeneratedCache != null) return file.GeneratedCache;
                var o = content.SpellDonOverrides;
                var withNames = new SpellDonOverridesFile
                {
                    Enabled = o.Enabled, RetireLegacyOverlaps = o.RetireLegacyOverlaps, Kinds = o.Kinds,
                    Disable = o.Disable, Patches = o.Patches, Extra = o.Extra,
                    SpellNames = new Dictionary<string, string>(o.SpellNames),
                };
                foreach (var don in file.Classes.Values.SelectMany(l => l))
                    if (don.SpellName.Length > 0) withNames.SpellNames.TryAdd(don.Spell.ToString(), don.SpellName);
                var facts = o.Enabled ? (file.Facts ?? FactsSource)() : Array.Empty<SpellFacts>();
                var built = Build(facts, withNames);
                int count = built.Values.Sum(l => l.Count);
                if (count > 0) Console.WriteLine($"[SpellDons] {count} spell upgrades generated for {built.Count} classes.");
                return file.GeneratedCache = built;
            }
        }
    }
}
