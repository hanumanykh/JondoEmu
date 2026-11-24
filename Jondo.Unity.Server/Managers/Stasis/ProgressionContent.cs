using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The authored side of the level-up dons, the Ascension and the research tree: the three files
    /// of content/progression (dons.json, research.json, ascension.json), read once into plain
    /// objects.
    /// </summary>
    /// <remarks>
    /// Every number a designer would want to move -- how many cards an offer has, which levels give
    /// which offer, what a card gives, what a research rank costs, how many Éclats a level is worth --
    /// lives in those files, not here, so balancing the Stasis rules on this server is an edit of
    /// JSON and a restart. The code only knows the SHAPE: an offer kind, a card with effects, a
    /// research kind. <see cref="Current"/> can be swapped (tests parse the real files from the
    /// checkout and install them), and a file that is missing or broken leaves the built-in empty
    /// defaults: no offers and no research rather than a server that does not start.
    /// </remarks>
    internal sealed class ProgressionContent
    {
        internal DonsFile Dons { get; init; } = new();
        internal List<ResearchDefinition> Research { get; init; } = new();
        internal AscensionFile Ascension { get; init; } = new();

        private static ProgressionContent? _current;
        private static readonly object _loadLock = new();

        /// <summary>What is in force: the files read on first use, or what a test installed.</summary>
        internal static ProgressionContent Current
        {
            get
            {
                if (_current != null) return _current;
                lock (_loadLock) return _current ??= Load(Jondo.Unity.Launcher.Paths.ContentFile("progression"));
            }
            set => _current = value;
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>The three files of a directory; any that cannot be read is left at its default, and said.</summary>
        internal static ProgressionContent Load(string directory)
        {
            T Read<T>(string name, T fallback)
            {
                string path = Path.Combine(directory, name);
                try
                {
                    if (File.Exists(path)) return Parse<T>(File.ReadAllText(path)) ?? fallback;
                    Console.WriteLine($"[Progression] {path} is missing: its part of the dons/ascension rules is off.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Progression] {path} could not be read: {ex.Message}");
                }
                return fallback;
            }

            return new ProgressionContent
            {
                Dons = Read("dons.json", new DonsFile()),
                Research = Read("research.json", new ResearchFile()).Research,
                Ascension = Read("ascension.json", new AscensionFile()),
            };
        }

        internal static T? Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        /// <summary>A research by its id, or null.</summary>
        internal ResearchDefinition? ResearchOf(string id)
            => Research.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Every card the files define, by id: level, adventure, passives, accomplishments and the spell card.</summary>
        internal DonCard? CardOf(string id)
        {
            if (string.Equals(id, Dons.SpellCard.Id, StringComparison.OrdinalIgnoreCase)) return Dons.SpellCard;
            return Dons.Level.Concat(Dons.Adventure).Concat(Dons.Accomplishments)
                       .Concat(Dons.Passives.Values.SelectMany(p => p))
                       .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class DonsFile
    {
        public int LastDonLevel { get; set; } = 200;
        public int CardsPerOffer { get; set; } = 3;
        public int AdventureEvery { get; set; } = 5;
        public List<int> SpellLevels { get; set; } = new();
        public int PassiveLevel { get; set; } = 50;
        public List<int> AccomplishmentLevels { get; set; } = new();
        public long RerollKamasPerLevel { get; set; } = 1000;
        public CharacteristicCard CharacteristicCard { get; set; } = new();
        public DonCard SpellCard { get; set; } = new() { Id = "sort" };
        public List<DonCard> Level { get; set; } = new();
        public List<DonCard> Adventure { get; set; } = new();
        /// <summary>By breed id as text, and "default" for a breed without its own.</summary>
        public Dictionary<string, List<DonCard>> Passives { get; set; } = new();
        public List<DonCard> Accomplishments { get; set; } = new();
    }

    internal sealed class CharacteristicCard
    {
        public string Id { get; set; } = "carac";
        public string Name { get; set; } = "Caractéristique au choix";
        public string Description { get; set; } = "+{0} dans la caractéristique de votre choix.";
        public int Amount { get; set; } = 3;
        public double FirstChance { get; set; } = 0.9;
        public List<string> Stats { get; set; } = new();
    }

    internal sealed class DonCard
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>For adventure dons: how many times it can be taken. 1 for the others.</summary>
        public int MaxRank { get; set; } = 1;
        public List<DonEffect> Effects { get; set; } = new();
    }

    internal sealed class DonEffect
    {
        public string Stat { get; set; } = "";
        public long Value { get; set; }
    }

    internal sealed class ResearchFile
    {
        public List<ResearchDefinition> Research { get; set; } = new();
    }

    internal sealed class ResearchDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Stat { get; set; } = "";
        public int PerRank { get; set; }
        public List<long> Costs { get; set; } = new();
        [JsonIgnore] public int MaxRank => Costs.Count;
    }

    internal sealed class AscensionFile
    {
        public int MinLevel { get; set; } = 50;
        public double EclatsBase { get; set; } = 10;
        public double EclatsReferenceLevel { get; set; } = 50;
        public double EclatsExponent { get; set; } = 2;
        public int CatchUpBase { get; set; } = 2;
        public int EvolutionLevel { get; set; } = 300;
        public long EvolutionEclats { get; set; } = 1000;
        public int EvolutionReturnLevel { get; set; } = 200;
        public int EvolutionKeepDonsUpTo { get; set; } = 200;
    }
}
