using System;

namespace Jondo.Unity.World.Fights
{
    public enum ElementType
    {
        Neutral = 0,
        Earth = 1,
        Fire = 2,
        Water = 3,
        Air = 4
    }

    /// <summary>
    /// A damage blow up to the target's resistances, in the order and with the roundings of the
    /// client's own fight preview (3.6.10.11, read with Ghidra over the REA IL2CPP seeds; the
    /// names are obfuscated, the characteristics' are the client's StatId enum's).
    /// </summary>
    /// <remarks>
    /// <para>hal.bncy adds the element's characteristic, 25 (DamagePercent, power) and 98
    /// (DamagePercentSpell) or 103 (WeaponPower), and multiplies the roll by the sum when it is
    /// above zero (ra.pqh); hal.bncz then adds the flat bonuses -- 16, the element's 88 to 92 and
    /// 86 on a critical --; hal.bnde multiplies by 31 (WeaponDamagePercent) when there is any.
    /// ham.bndh takes the target's element reduction (54 to 58) and, on a critical, its 87
    /// (CriticalDamageReduction) off it, and multiplies what is left by <c>1 - resistance × 0.01</c>
    /// in single precision, never below zero.</para>
    /// <para>Every multiplication rounds DOWN at once (ra.fay, ra.ppy, ra.pqh: a floor of the
    /// product taken as a float), not to the nearest at the end: with 47 % resistance a blow of
    /// 100 leaves 52, as 1 - 0.01f × 47 is a hair under 0.53.</para>
    /// </remarks>
    public static class DamageCalculator
    {
        public static int CalculateDamage(
            int baseDamage,
            ElementType element,
            int statValue,           // E.g. Strength for Earth, Intelligence for Fire
            int power,               // General Power
            int flatElementDamage,   // Flat elemental damage
            int flatDamage,          // General flat damage
            int targetResPct,        // Target resistance %
            int targetFlatRes,       // Target flat resistance: its element reduction, and its 87 on a critical
            bool isCritical = false,
            double critBonusMultiplier = 1.25,
            int percentBonus = 0,         // 98 for a spell's blow, 103 for a weapon's
            int weaponDamagePercent = 0)  // 31
        {
            if (baseDamage <= 0) return 0;

            // Overflow: with Omega levels (up to 2000) and their characteristic points, a stat in the
            // tens of thousands plus a thousand percent of power is reachable, and the old int
            // arithmetic wrapped -- 100 base at +100,000 % is 100,100,000, but a few multipliers
            // further an int goes negative and the Max(0, ...) at the end turned a huge blow into 0.
            // Every intermediate is a long now and the result is clamped into int, which is what
            // the life points and the packets carry. For any value an int held before, the long
            // path gives the same number bit for bit: the float roundings below are the same
            // operations on the same values, only the containers are wider.
            long effectiveBase = isCritical ? ClampToLong(Math.Round(baseDamage * critBonusMultiplier)) : baseDamage;

            // 1. The characteristics, power and the % bonus together, when they add up to more than nothing.
            long percent = (long)statValue + power + percentBonus;
            long damage = percent > 0 ? TimesPercent(effectiveBase, 100 + percent) : effectiveBase;

            // 2. The flat ones on top, and the weapon damage percent on all of it.
            damage += (long)flatElementDamage + flatDamage;
            if (weaponDamagePercent != 0) damage = TimesPercent(damage, 100L + weaponDamagePercent);

            // 3. The target's flat reduction, then its percentage resistance.
            damage -= targetFlatRes;
            float left = Math.Max(0f, 1f - targetResPct * 0.01f);
            damage = ClampToLong(MathF.Floor(damage * left));

            // Nothing at 100 % resistance or with a reduction above the blow: the official game shows «-0»,
            // and the client's preview draws 0 (the range is not floored at one). Never a heal.
            return ClampToInt(Math.Max(0L, damage));
        }

        /// <summary>
        /// A value times a percentage the client's way: <c>floor((float)(value × percent × 0.01))</c>
        /// (ra.fay, ra.pqh), clamped into int instead of wrapping.
        /// </summary>
        /// <remarks>
        /// Casting a float outside int's range to int is not a number you can rely on (it has been
        /// int.MinValue on x64), so a blow multiplied past two billion came back as a large negative
        /// and then as zero. Same float rounding as before for every in-range result.
        /// </remarks>
        public static int TimesPercent(int value, int percent)
            => ClampToInt(TimesPercent((long)value, (long)percent));

        /// <summary>The long form of <see cref="TimesPercent(int, int)"/>, for the wide intermediates.</summary>
        public static long TimesPercent(long value, long percent)
            => ClampToLong(Math.Floor((float)(value * (percent * 0.01))));

        /// <summary>
        /// A value times a percentage rounded to the nearest (banker's, as Math.Round does): the
        /// server's own final multipliers -- portals, the combo, a dream's % damage -- which are
        /// not the client's floored ones. The product is taken in long, where
        /// <c>damage * (100 + combo)</c> in int wrapped past about 21 million damage at +100 %.
        /// </summary>
        public static int RoundedPercent(int value, int percent)
            => ClampToInt(ClampToLong(Math.Round((long)value * percent / 100.0)));

        /// <summary>A long into int, saturating at its ends.</summary>
        public static int ClampToInt(long value)
            => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

        /// <summary>A double into long, saturating at its ends; NaN counts as zero.</summary>
        public static long ClampToLong(double value)
        {
            if (double.IsNaN(value)) return 0;
            // long.MaxValue is not exactly representable; 9.2e18 rounds up to 2^63, so compare with >=.
            if (value >= 9.2233720368547758E18) return long.MaxValue;
            if (value <= -9.2233720368547758E18) return long.MinValue;
            return (long)value;
        }
    }
}
