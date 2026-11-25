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
        /// <summary>The spell dons of every class (spell_dons.json), see <see cref="SpellDons"/>.</summary>
        internal SpellDonsFile SpellDons { get; init; } = new();
        /// <summary>How the spell upgrades are generated from the spells' data, and the hand corrections (spell_dons_overrides.json).</summary>
        internal SpellDonOverridesFile SpellDonOverrides { get; init; } = new();

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
                SpellDons = Read("spell_dons.json", new SpellDonsFile()),
                SpellDonOverrides = Read("spell_dons_overrides.json", new SpellDonOverridesFile()),
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
        /// <summary>The level of the starting offer (given to a new character, and to an older one lacking it); 0: none.</summary>
        public int StartingLevel { get; set; } = 1;
        /// <summary>How many of the last dons kept count as "taken recently" for the last card of an offer.</summary>
        public int RecentWindow { get; set; } = 10;
        /// <summary>The card rarities (Commun/Rare/Légendaire): chances, the research that raises them, the pity.</summary>
        public RarityRules Rarity { get; set; } = new();
        /// <summary>The dons «Pension»: a pet or mount egg chosen at some levels, given to the account's pension.</summary>
        public List<PensionDon> Pension { get; set; } = new();
        public CharacteristicCard CharacteristicCard { get; set; } = new();
        public DonCard SpellCard { get; set; } = new() { Id = "sort" };
        public List<DonCard> Level { get; set; } = new();
        public List<DonCard> Adventure { get; set; } = new();
        /// <summary>By breed id as text, and "default" for a breed without its own.</summary>
        public Dictionary<string, List<DonCard>> Passives { get; set; } = new();
        public List<DonCard> Accomplishments { get; set; } = new();
    }

    /// <summary>
    /// spell_dons.json: the Stasis "points de sort", cut down to a selection -- a handful of dons per
    /// class, each on one real spell of that class, each rank one point. The caps are the Stasis
    /// ones: so many points in all, so many spell families (a spell and its variant share their
    /// points), and per family no more than the level allows.
    /// </summary>
    internal sealed class SpellDonsFile
    {
        public int MaxPoints { get; set; } = 14;
        public int MaxFamilies { get; set; } = 5;
        /// <summary>The levels at which a new family opens: one family per level reached.</summary>
        public List<int> FamilySlotLevels { get; set; } = new();
        /// <summary>From which level (as text) a family may hold so many points: the highest level reached wins.</summary>
        public Dictionary<string, int> PointsPerFamilyByLevel { get; set; } = new();
        /// <summary>By breed id as text.</summary>
        public Dictionary<string, List<SpellDonDefinition>> Classes { get; set; } = new();
        /// <summary>The generated catalogue, built on first use (SpellDonCatalogue.Of).</summary>
        [JsonIgnore] internal Dictionary<string, List<SpellDonDefinition>>? GeneratedCache { get; set; }
        /// <summary>Where the generator reads the spells from; null: the world database. Tests set their own.</summary>
        [JsonIgnore] internal Func<IReadOnlyList<SpellFacts>>? Facts { get; set; }
    }

    internal sealed class SpellDonDefinition
    {
        public string Id { get; set; } = "";
        public int Spell { get; set; }
        /// <summary>The spell's French name, for the texts (the client's own data is Spanish).</summary>
        public string SpellName { get; set; } = "";
        /// <summary>degats, soins, critique, portee, relance, pa or lancers (<see cref="SpellDons"/>).</summary>
        public string Kind { get; set; } = "";
        /// <summary>Per rank: a percent for degats/soins/critique, one for the others.</summary>
        public int Value { get; set; }
        public int MaxRank { get; set; } = 1;
        /// <summary>The level the spell is first learnt at: no offer of it before.</summary>
        public int Level { get; set; } = 1;
        /// <summary>The level each rank opens at (rank 1 first); empty: every rank at <see cref="Level"/>.</summary>
        public List<int> RankLevels { get; set; } = new();
        /// <summary>The other upgrades of the same spell a rank asks for (Insistance 2 needs Enchaînement 1).</summary>
        public List<SpellDonRequirement> Requires { get; set; } = new();
        /// <summary>Made by SpellDonCatalogue from the spell's data rather than written in spell_dons.json.</summary>
        [JsonIgnore] public bool Generated { get; set; }
        /// <summary>The spell's data the generated don was made from (the texts say what changes); null for a written one.</summary>
        [JsonIgnore] public SpellFacts? Facts { get; set; }
    }

    /// <summary>
    /// Per card, a rarity is drawn: Légendaire at <see cref="Legendaire"/> percent, Rare at
    /// <see cref="Rare"/>, Commun otherwise. The research named <see cref="Research"/> (Fortune)
    /// replaces both by its rank's values. After <see cref="PityAfter"/> offers in a row without a
    /// Légendaire, the next has one. Only the offer kinds of <see cref="Kinds"/> draw rarities.
    /// </summary>
    internal sealed class RarityRules
    {
        public double Commun { get; set; } = 75;
        public double Rare { get; set; } = 20;
        public double Legendaire { get; set; } = 5;
        public int PityAfter { get; set; } = 5;
        public string Research { get; set; } = "fortune";
        public List<double> RareByRank { get; set; } = new();
        public List<double> LegendaireByRank { get; set; } = new();
        public List<string> Kinds { get; set; } = new() { "depart", "niveau", "aventure" };
    }

    /// <summary>A don «Pension»: at <see cref="Level"/>, one egg chosen among <see cref="Choices"/>.</summary>
    internal sealed class PensionDon
    {
        public int Level { get; set; }
        /// <summary>"familier" (choices are pet item ids) or "monture" (choices are Breeding species ids).</summary>
        public string Kind { get; set; } = "monture";
        /// <summary>For a pet egg: its rarity in the pension (Starter, Commun...).</summary>
        public string Rarity { get; set; } = "Starter";
        public List<int> Choices { get; set; } = new();
    }

    internal sealed class CharacteristicCard
    {
        public string Id { get; set; } = "carac";
        public string Name { get; set; } = "Caractéristique au choix";
        public string Description { get; set; } = "+{0} dans la caractéristique de votre choix.";
        public int Amount { get; set; } = 3;
        /// <summary>What a Rare / a Légendaire characteristic card gives (a research of kind caracAmount would add to each); null: <see cref="Amount"/>.</summary>
        public int? AmountRare { get; set; }
        public int? AmountLegendaire { get; set; }
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
        /// <summary>No longer offered, still counted for the characters who have it (an older file's card).</summary>
        public bool Retired { get; set; }
        /// <summary>An icon of the client's (a FigmaIcons name) for a card whose effects say nothing drawable.</summary>
        public string Icon { get; set; } = "";
        public List<DonEffect> Effects { get; set; } = new();
    }

    internal sealed class DonEffect
    {
        public string Stat { get; set; } = "";
        /// <summary>The Commun value.</summary>
        public long Value { get; set; }
        /// <summary>The Rare and Légendaire values; null: the rarity below's.</summary>
        public long? Rare { get; set; }
        public long? Legendaire { get; set; }

        /// <summary>The value at a rarity (0 Commun, 1 Rare, 2 Légendaire).</summary>
        public long ValueAt(int rarity) => rarity switch
        {
            >= 2 => Legendaire ?? Rare ?? Value,
            1 => Rare ?? Value,
            _ => Value,
        };
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
        /// <summary>The value at each rank (cumulative), when it is not rank x <see cref="PerRank"/>.</summary>
        public List<double> Values { get; set; } = new();
        /// <summary>Préparation / Savoir / Puissance: the tab's columns.</summary>
        public string Branch { get; set; } = "";
        /// <summary>The short glyph the window draws the card with.</summary>
        public string Icon { get; set; } = "";
        /// <summary>One rank's effect, {v} standing for <see cref="ValueAt"/>.</summary>
        public string Effect { get; set; } = "";
        /// <summary>Not refundable (Métamorphose, Polyvalence, Évolution).</summary>
        public bool Permanent { get; set; }
        public List<long> Costs { get; set; } = new();
        [JsonIgnore] public int MaxRank => Costs.Count;

        /// <summary>What the research gives at a rank: values[rank-1], or rank x perRank; 0 at rank 0.</summary>
        public double ValueAt(int rank)
        {
            if (rank <= 0) return 0;
            if (Values.Count > 0) return Values[Math.Min(rank, Values.Count) - 1];
            return (double)Math.Min(rank, Math.Max(1, MaxRank)) * PerRank;
        }
    }

    internal sealed class AscensionFile
    {
        public int MinLevel { get; set; } = 50;
        public double EclatsBase { get; set; } = 10;
        public double EclatsReferenceLevel { get; set; } = 50;
        public double EclatsExponent { get; set; } = 2;
        /// <summary>Level left -> Éclats (the wiki's table), linear between rows; empty: the formula above.</summary>
        public Dictionary<int, double> EclatsByLevel { get; set; } = new();
        public int CatchUpBase { get; set; } = 2;
        public double CatchUpMax { get; set; } = 9;
        public int EvolutionLevel { get; set; } = 300;
        public long EvolutionEclats { get; set; } = 1000;
        public int EvolutionReturnLevel { get; set; } = 200;
        public int EvolutionKeepDonsUpTo { get; set; } = 200;
    }
}
