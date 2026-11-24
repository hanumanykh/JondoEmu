using System.Collections.Generic;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// Levels past the client's 1889 and up to the server's MaxCharacterLevel, the characteristic
/// points Omega levels give, and the fight arithmetic staying inside int when characteristics
/// reach the sizes a level 2000 can afford.
/// </summary>
/// <remarks>
/// These read ServerSettings.Current, which with no config file in the test run is the defaults
/// (MaxCharacterLevel 300, OmegaPointsPerLevel 1, MaxBaseCharacteristic 3000). The tests that
/// need other values build them through the pure helpers instead of swapping the global, so they
/// can run alongside the rest.
/// </remarks>
public sealed class OmegaProgressionTests
{
    /// <summary>The last levels of the real table, 1885 to 1889, as character_xp.json has them.</summary>
    private static SortedDictionary<int, long> TableTail() => new()
    {
        [1885] = 95_652_431_366_034_276L,
        [1886] = 96_598_131_427_437_018L,
        [1887] = 97_527_609_225_258_992L,
        [1888] = 98_502_885_311_957_344L,
        [1889] = 99_487_914_159_522_688L,
    };

    [Fact]
    public void Curve_extends_to_2000_with_the_tables_last_step_and_stays_monotonic()
    {
        var floors = TableTail();
        long step = floors[1889] - floors[1888];
        ExperienceTable.ExtendCurve(floors, 2000);

        Assert.Equal(2000, floors.Keys.Max());
        long previous = long.MinValue;
        foreach (var kv in floors)
        {
            Assert.True(kv.Value > previous, $"level {kv.Key} does not grow");
            previous = kv.Value;
        }
        Assert.Equal(floors[1889] + step, floors[1890]);
        Assert.Equal(floors[1889] + 111 * step, floors[2000]);
        // Far from long.MaxValue: 2000 is about 2.1e17, and room for a 1000 % bonus on top.
        Assert.True(floors[2000] < long.MaxValue / 10);
    }

    [Fact]
    public void Curve_extends_to_the_hardest_cap_without_overflow()
    {
        var floors = TableTail();
        ExperienceTable.ExtendCurve(floors, ExperienceTable.HardestLevelCap);
        Assert.Equal(ExperienceTable.HardestLevelCap, floors.Keys.Max());
        Assert.True(floors[ExperienceTable.HardestLevelCap] > floors[1889]);
    }

    [Fact]
    public void Short_table_extends_with_a_flat_fifty_million()
    {
        var floors = new SortedDictionary<int, long> { [1] = 0 };
        ExperienceTable.ExtendCurve(floors, 3);
        Assert.Equal(50_000_000L, floors[2]);
        Assert.Equal(100_000_000L, floors[3]);
    }

    [Fact]
    public void Level_ups_add_up_to_the_capital_of_the_level_reached()
    {
        int given = 0;
        for (int level = 1; level < 400; level++)
            given += CharacteristicRules.PointsForLevelUp(level, level + 1);
        Assert.Equal(CharacteristicRules.TotalCapitalForLevel(300), given);
        Assert.Equal(995 + 100, given);
        Assert.Equal(5, CharacteristicRules.PointsForLevelUp(199, 200));
        Assert.Equal(1, CharacteristicRules.PointsForLevelUp(200, 201));
        Assert.Equal(5 + 1, CharacteristicRules.PointsForLevelUp(199, 201));
        Assert.Equal(0, CharacteristicRules.PointsForLevelUp(500, 400));
        // Nothing past the server's cap.
        Assert.Equal(0, CharacteristicRules.PointsForLevelUp(300, 2500));
    }

    [Fact]
    public void Adding_experience_goes_through_one_door_and_pays_the_levels()
    {
        var state = new SessionState { CharacterLevel = 199, Experience = 0 };
        // Not touching the table: an amount that leaves the level alone still adds.
        var none = CharacterProgression.AddExperience(state, 0);
        Assert.False(none.LeveledUp);
        Assert.Equal(0, state.Experience);
        state.Experience = long.MaxValue - 1;
        CharacterProgression.AddExperience(state, 10);
        Assert.Equal(long.MaxValue, state.Experience);
    }

    [Fact]
    public void Base_characteristic_cap_never_bites_below_what_level_200_reaches()
    {
        Assert.True(CharacteristicRules.MaxBaseCharacteristic > 995);
        Assert.Equal(3000, CharacteristicRules.MaxBaseCharacteristic);
    }

    [Theory]
    [InlineData(100_000, 0)]
    [InlineData(100_000, 1_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(2_000_000, 50_000)]
    public void Extreme_characteristics_never_overflow_into_negative_damage(int stat, int power)
    {
        int damage = DamageCalculator.CalculateDamage(100, ElementType.Earth, stat, power,
            flatElementDamage: 5_000, flatDamage: 5_000, targetResPct: 0, targetFlatRes: 0,
            isCritical: true, percentBonus: 1_000, weaponDamagePercent: 1_000);
        Assert.True(damage > 0);
        // And growing with the characteristic until it saturates, never wrapping back down.
        int smaller = DamageCalculator.CalculateDamage(100, ElementType.Earth, stat / 2, power / 2,
            5_000, 5_000, 0, 0, true);
        Assert.True(damage >= smaller);
    }

    [Fact]
    public void Damage_that_used_to_wrap_saturates_at_int_max()
    {
        int damage = DamageCalculator.CalculateDamage(10_000, ElementType.Fire, int.MaxValue / 2, int.MaxValue / 2,
            int.MaxValue, int.MaxValue, targetResPct: -1000, targetFlatRes: int.MinValue,
            weaponDamagePercent: int.MaxValue);
        Assert.Equal(int.MaxValue, damage);
    }

    [Fact]
    public void Ordinary_damage_is_unchanged_by_the_wider_arithmetic()
    {
        // The documented case: 100 at 47 % resistance leaves 52 (1 - 0.01f x 47 is under 0.53).
        Assert.Equal(52, DamageCalculator.CalculateDamage(100, ElementType.Earth, 0, 0, 0, 0, 47, 0));
        // 100 base, 400 strength, 50 power, 20 flat: floor(100 x 5.5) + 20 = 570.
        Assert.Equal(570, DamageCalculator.CalculateDamage(100, ElementType.Earth, 400, 50, 10, 10, 0, 0));
    }

    [Theory]
    [InlineData(1_000_000_000, 1_000, int.MaxValue)]
    [InlineData(100, 150, 150)]
    [InlineData(-5, 200, -10)]
    public void Percent_multipliers_saturate_instead_of_wrapping(int value, int percent, int expected)
    {
        Assert.Equal(expected, DamageCalculator.TimesPercent(value, percent));
        Assert.Equal(expected, DamageCalculator.RoundedPercent(value, percent));
    }
}
