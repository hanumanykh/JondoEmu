namespace Jondo.Unity.Server;

/// <summary>What adding experience to a character changed: the levels either side and the points given.</summary>
internal readonly record struct ExperienceGain(long Added, int OldLevel, int NewLevel, int PointsGiven)
{
    public bool LeveledUp => NewLevel > OldLevel;
}

/// <summary>
/// THE place experience is added to a character: fight rewards, heroes, quests and achievements
/// all come through <see cref="AddExperience"/>.
/// </summary>
/// <remarks>
/// Each of the three paths used to add to Experience, look the level up and hand out "five points
/// a level up to 200" on its own. With Omega levels (201 to the server's MaxCharacterLevel) giving
/// OmegaPointsPerLevel each, three copies would drift, and later rules -- experience blocked at a
/// tier until its dungeons are done, an Ascension reset -- need one door to stand at. This is that
/// door: wrap or extend it, not the callers. It only touches state; the packets (level-up window,
/// sheet, spells) stay with the callers, which know their stream and their order.
///
/// <see cref="ExperienceTable.LevelForXp"/> stays a pure lookup (experience to level, clamped to
/// the server cap); any gating belongs here, before the experience is added.
/// </remarks>
internal static class CharacterProgression
{
    /// <summary>
    /// Adds <paramref name="experience"/> (already bonused and limited by the caller) to the
    /// character, moves the level to what the total is worth and gives the characteristic points
    /// of every level gained (<see cref="CharacteristicRules.PointsForLevelUp"/>). Non-positive
    /// amounts change nothing. Never lowers the level.
    /// </summary>
    internal static ExperienceGain AddExperience(SessionState state, long experience)
    {
        int oldLevel = state.CharacterLevel;
        if (experience <= 0) return new(0, oldLevel, oldLevel, 0);

        // Saturating: a bonus on a huge gain must not wrap the total negative.
        state.Experience = experience > long.MaxValue - state.Experience ? long.MaxValue : state.Experience + experience;

        int newLevel = ExperienceTable.LevelForXp(state.Experience);
        if (newLevel <= oldLevel) return new(experience, oldLevel, oldLevel, 0);

        int points = CharacteristicRules.PointsForLevelUp(oldLevel, newLevel);
        if (points > 0) state.CharacterRemainingPoints += points;
        state.CharacterLevel = newLevel;
        return new(experience, oldLevel, newLevel, points);
    }
}
