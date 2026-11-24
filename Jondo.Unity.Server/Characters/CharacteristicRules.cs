namespace Jondo.Unity.Server;

/// <summary>Protocol-independent characteristic bases and derived contributions.</summary>
internal static class CharacteristicRules
{
    internal const int BasePods = 1000;
    internal const int PlayerBaseAp = 6;
    internal const int PlayerBaseMp = 3;
    internal static int InnateAp(int level) => PlayerBaseAp + (level >= 100 ? 1 : 0);

    /// <summary>Omega levels continue progression without increasing base life or characteristic capital.</summary>
    internal const int MaxLevelWithStats = Jondo.Unity.World.Fights.Fighter.MaxLevelWithStats;

    internal static int StatLevel(int level) => Math.Min(level, MaxLevelWithStats);
    /// <summary>
    /// Every characteristic point a character of this level has earned: five a level up to 200,
    /// then <see cref="ServerSettings.OmegaPointsPerLevel"/> for each Omega level above it, up to
    /// the server's level cap.
    /// </summary>
    /// <remarks>
    /// This is THE capital: spending checks against it, a reset hands it all back, .level and the
    /// offline level setter write it, and level-ups give the difference between two levels of it
    /// (<see cref="PointsForLevelUp"/>). Keeping one formula is what makes a reset after Omega
    /// levels return exactly what the level-ups gave, whichever way the levels came.
    ///
    /// The Omega share is computed from the settings each call, so with OmegaPointsPerLevel 0 the
    /// result is the old frozen 995 past 200. Level is clamped to the server cap first so
    /// int.MaxValue (or any garbage level) cannot multiply into an overflow.
    /// </remarks>
    internal static int TotalCapitalForLevel(int level)
        => (Math.Max(1, StatLevel(level)) - 1) * 5 + OmegaLevels(level) * OmegaPointsPerLevel;

    /// <summary>Points earned going from one level to a higher one; zero when not going up.</summary>
    /// <remarks>
    /// The one helper the three level-up paths share (fight rewards, heroes, quest and achievement
    /// experience): each used to compute "five a level up to 200" on its own, and three copies of a
    /// rule drift. Defined as a difference of <see cref="TotalCapitalForLevel"/> so the sum of
    /// every level-up always equals the capital of the level reached.
    /// </remarks>
    internal static int PointsForLevelUp(int fromLevel, int toLevel)
        => toLevel <= fromLevel ? 0 : Math.Max(0, TotalCapitalForLevel(toLevel) - TotalCapitalForLevel(fromLevel));

    /// <summary>Omega levels a level counts for: past 200, never past the server's level cap.</summary>
    internal static int OmegaLevels(int level)
    {
        int cap = Math.Clamp(ServerSettings.Current.MaxCharacterLevel, MaxLevelWithStats, ExperienceTable.HardestLevelCap);
        return Math.Clamp(level, MaxLevelWithStats, cap) - MaxLevelWithStats;
    }

    private static int OmegaPointsPerLevel => Math.Clamp(ServerSettings.Current.OmegaPointsPerLevel, 0, 1000);

    /// <summary>
    /// The most one base characteristic can be raised with capital, or int.MaxValue for no cap.
    /// </summary>
    /// <remarks>
    /// Never under 995 + 1: the whole capital of a level-200 character in one characteristic at
    /// one point per point can reach 995, so a cap at or above that changes nothing for a normal
    /// character, whatever the setting says. It only stops thousands of Omega points from piling
    /// into one characteristic and carrying damage formulas toward int's range.
    /// </remarks>
    internal static int MaxBaseCharacteristic
    {
        get
        {
            int cap = ServerSettings.Current.MaxBaseCharacteristic;
            return cap <= 0 ? int.MaxValue : Math.Max(cap, TotalCapitalForLevelWithoutOmega + 1);
        }
    }

    private const int TotalCapitalForLevelWithoutOmega = (MaxLevelWithStats - 1) * 5;
    internal static int BaseLifeForLevel(int level) => 50 + 5 * Math.Max(1, StatLevel(level));

    /// <summary>Allocation cost, widened before multiplication or addition and never narrowed to a budget.</summary>
    internal static long ComputeDistributionCost(int strength, int intelligence, int chance,
        int agility, int vitality, int wisdom, int breed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfNegative(intelligence);
        ArgumentOutOfRangeException.ThrowIfNegative(chance);
        ArgumentOutOfRangeException.ThrowIfNegative(agility);
        ArgumentOutOfRangeException.ThrowIfNegative(vitality);
        ArgumentOutOfRangeException.ThrowIfNegative(wisdom);
        return checked(Managers.BreedStatCost.CostOf(breed, "strength", strength)
            + Managers.BreedStatCost.CostOf(breed, "intelligence", intelligence)
            + Managers.BreedStatCost.CostOf(breed, "chance", chance)
            + Managers.BreedStatCost.CostOf(breed, "agility", agility)
            + Managers.BreedStatCost.CostOf(breed, "vitality", vitality)
            + Managers.BreedStatCost.CostOf(breed, "wisdom", wisdom));
    }

    /// <summary>
    /// Contributions from total primary characteristics, including scrolls, equipment and
    /// alterations: a tenth of wisdom for dodge/withdrawal, a tenth of agility for escape/lock,
    /// five pods per strength, the four elements for initiative and a tenth of chance for prospecting.
    /// The owner sheet includes these in Base; presets expose them in a separate column.
    /// </summary>
    internal static long GivenByCharacteristics(int id, Func<int, long> total)
        => id switch
        {
            Stat.DodgeActionPoints or Stat.DodgeMovementPoints or Stat.WithdrawActionPoints or Stat.WithdrawMovementPoints
                => total(Stat.Wisdom) / 10,
            Stat.Escape or Stat.Lock => total(Stat.Agility) / 10,
            Stat.Pods => checked(5 * total(Stat.Strength)),
            Stat.Initiative => checked(total(Stat.Strength) + total(Stat.Intelligence) + total(Stat.Chance) + total(Stat.Agility)),
            Stat.Prospecting => total(Stat.Chance) / 10,
            _ => 0,
        };
}
