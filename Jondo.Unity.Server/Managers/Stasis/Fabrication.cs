using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Stasis fabrication (spec §6): a piece of equipment made straight at S1 and at its perfect
    /// roll, out of the plain piece (never a stasified one), resources, Orbes and, from a level
    /// band up, a guardian's essence -- a real resource a dungeon boss of that band drops. The
    /// legendary Fissure recipes (high level pieces asked «S2») give S2 for Fissure fragments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recipes are not written one by one: every piece that stasifies and has a game recipe
    /// gets one, its own ingredients scaled by <see cref="Settings.IngredientFactor"/>; a piece no
    /// recipe makes (a drop's) gets the formula, essence only, twice over. content/items/fabrication.json
    /// holds the knobs, the essences by band and hand-written overrides, which win.
    /// </para>
    /// <para>
    /// The costs here are kamas-calibrated, as every Stasis curve; the handler turns them into the
    /// Orbes' currency (CommandHandler.OrbePrice), so the Jondo Coin switch is one place.
    /// </para>
    /// </remarks>
    public static class Fabrication
    {
        public const string AuthoredFile = "items/fabrication.json";

        public sealed class Essence
        {
            public int MinLevel { get; set; }
            public int MaxLevel { get; set; } = 9999;
            public int Item { get; set; }
            public int Quantity { get; set; } = 1;
            public string Why { get; set; } = "";
        }

        public sealed class Ingredient
        {
            public int Item { get; set; }
            public int Quantity { get; set; } = 1;
        }

        public sealed class Override
        {
            public int Result { get; set; }
            public int Tier { get; set; } = 1;
            public List<Ingredient> Ingredients { get; set; } = new();
            public long Cost { get; set; }
            public bool NoEssence { get; set; }
        }

        public sealed class Legendary
        {
            public int FromLevel { get; set; } = 190;
            public int Tier { get; set; } = 2;
            public int Fragment { get; set; } = 17274;
            public int FragmentQuantity { get; set; } = 5;
            public double CostFactor { get; set; } = 4;
        }

        public sealed class Settings
        {
            public bool Enabled { get; set; } = true;
            public double IngredientFactor { get; set; } = 1.0;
            public int FormulaEssenceFactor { get; set; } = 2;
            public long CostPerLevel { get; set; } = 60;
            public long CostMinimum { get; set; } = 500;
            public int EssenceFromLevel { get; set; } = 1;
            public List<Essence> Essences { get; set; } = new();
            public Legendary Legendary { get; set; } = new();
            public List<Override> Overrides { get; set; } = new();
        }

        /// <summary>A recipe: what it makes, at which tier, from what.</summary>
        public sealed record Recipe(int Result, int Level, int Tier, IReadOnlyList<(int Item, int Quantity)> Ingredients,
                                    long Cost, bool Generated);

        /// <summary>One line of a quote: an ingredient, how many it takes, how many are owned.</summary>
        public sealed record QuoteLine(int Item, int Need, long Have)
        {
            public long Missing => Math.Max(0, Need - Have);
        }

        /// <summary>What making a recipe needs against what a character holds.</summary>
        public sealed record Quote(Recipe Recipe, bool BaseOwned, IReadOnlyList<QuoteLine> Lines, long Cost, long Orbes)
        {
            public bool Ready => BaseOwned && Lines.All(l => l.Missing == 0) && Orbes >= Cost;
            public int MissingKinds => (BaseOwned ? 0 : 1) + Lines.Count(l => l.Missing > 0) + (Orbes >= Cost ? 0 : 1);
        }

        /// <summary>Which pieces have a recipe and are sold: those that stasify (a test may widen it).</summary>
        internal static Func<Forgemagic.Template, bool> Eligible { get; set; } = t => Stasis.Applies(t);

        private static Settings? _settings;

        public static Settings Current
        {
            get => _settings ??= Read(Paths.ContentFile(AuthoredFile));
            set => _settings = value;
        }

        public static Settings Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fabrication] {AuthoredFile} could not be read: {ex.Message}");
            }
            return new Settings();
        }

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>The guardian's essence of a level, or null below the first band.</summary>
        public static Essence? EssenceFor(int level)
        {
            var s = Current;
            if (level < s.EssenceFromLevel) return null;
            return s.Essences.FirstOrDefault(e => level >= e.MinLevel && level <= e.MaxLevel && e.Item > 0);
        }

        /// <summary>The service cost of a piece of a level and tier, in kamas (before OrbePrice).</summary>
        public static long CostOf(int level, int tier)
        {
            var s = Current;
            long cost = Math.Max(s.CostMinimum, s.CostPerLevel * Math.Max(1, level));
            if (tier >= 2) cost = (long)Math.Round(cost * Math.Max(1, s.Legendary.CostFactor));
            return cost;
        }

        /// <summary>Whether a piece of this level has its legendary (S2) recipe.</summary>
        public static bool HasLegendary(int level) => Current.Legendary.FromLevel > 0 && level >= Current.Legendary.FromLevel;

        /// <summary>
        /// The recipe of a piece at a tier (1, or the legendary 2), or null when it has none: not
        /// equipment that stasifies, fabrication off, or S2 asked below the legendary level.
        /// </summary>
        public static Recipe? RecipeFor(int gid, int tier = 1)
        {
            var s = Current;
            if (!s.Enabled) return null;
            var template = Forgemagic.TemplateOf(gid);
            if (template == null || !Eligible(template)) return null;
            tier = tier >= 2 ? 2 : 1;
            if (tier == 2 && !HasLegendary(template.Level)) return null;

            var written = s.Overrides.FirstOrDefault(o => o.Result == gid && (o.Tier >= 2 ? 2 : 1) == tier);
            var lines = new List<(int Item, int Quantity)>();
            bool generated = written == null;
            bool essence = true;
            if (written != null)
            {
                lines.AddRange(written.Ingredients.Where(i => i.Item > 0 && i.Quantity > 0).Select(i => (i.Item, i.Quantity)));
                essence = !written.NoEssence;
            }
            else if (RecipeManager.TryGetByResult(gid, out var game))
            {
                double factor = Math.Max(0.01, s.IngredientFactor);
                foreach (var i in game.Ingredients.Where(i => i.ItemId != gid && i.Quantity > 0))
                    lines.Add((i.ItemId, Math.Max(1, (int)Math.Ceiling(i.Quantity * factor))));
            }

            var band = essence ? EssenceFor(template.Level) : null;
            if (band != null)
            {
                int quantity = band.Quantity * (generated && lines.Count == 0 ? Math.Max(1, s.FormulaEssenceFactor) : 1);
                Add(lines, band.Item, quantity);
            }
            if (tier == 2 && s.Legendary.Fragment > 0 && s.Legendary.FragmentQuantity > 0)
                Add(lines, s.Legendary.Fragment, s.Legendary.FragmentQuantity);
            // A piece without a game recipe and below every band would be free: not offered.
            if (lines.Count == 0) return null;

            long cost = written != null && written.Cost > 0 ? written.Cost : CostOf(template.Level, tier);
            return new Recipe(gid, template.Level, tier, lines, cost, generated);
        }

        private static void Add(List<(int Item, int Quantity)> lines, int item, int quantity)
        {
            int at = lines.FindIndex(l => l.Item == item);
            if (at >= 0) lines[at] = (item, lines[at].Quantity + quantity);
            else lines.Add((item, quantity));
        }

        /// <summary>
        /// A quote: the plain piece owned or not, each ingredient against the bag, the cost (already
        /// in the currency <paramref name="orbes"/> counts) against what is held.
        /// </summary>
        public static Quote QuoteFor(Recipe recipe, IReadOnlyDictionary<int, long> bag, int plainPieces, long cost, long orbes)
            => new(recipe, plainPieces > 0,
                   recipe.Ingredients.Select(i => new QuoteLine(i.Item, i.Quantity, bag.TryGetValue(i.Item, out long n) ? n : 0)).ToList(),
                   cost, orbes);

        /// <summary>What the made piece carries: the perfect roll of its template, at the recipe's tier.</summary>
        public static List<Equipment.ItemEffect> Result(Forgemagic.Template template, int tier)
            => Stasis.AtMax(template, tier);

        /// <summary>
        /// Every recipe a holder of these items could look at: the pieces whose plain copy he holds
        /// or whose ingredients he holds some of. Used by «.devis» alone to sort the makeable first.
        /// </summary>
        public static IEnumerable<int> Candidates(IEnumerable<int> plainPieces, IEnumerable<int> heldItems)
        {
            var seen = new HashSet<int>(plainPieces);
            var held = heldItems.ToHashSet();
            foreach (var recipe in RecipeManager.All)
                if (!seen.Contains(recipe.ResultId) && recipe.Ingredients.Count > 0 && recipe.Ingredients.All(i => held.Contains(i.ItemId)))
                    seen.Add(recipe.ResultId);
            return seen;
        }
    }
}
