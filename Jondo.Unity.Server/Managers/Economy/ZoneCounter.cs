using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The zone counter (comptoir de zone, docs/stasis-gameplay-spec.md §2): farming buffs bought
    /// for kamas (the Orbes), thirty real minutes long and only in the subarea they were bought in.
    /// Buying again lengthens it by thirty minutes; the effect never stacks. The clock runs while
    /// the player is away, as on Stasis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <list type="bullet">
    /// <item>Densité: +100 % drop, added to the server's drop bonus (FightHandler.RollLoot).</item>
    /// <item>Étude: +50 % fight experience, added to the rate; refused while a tide raises one's experience.</item>
    /// <item>Jauge: each monster counts twice in the gauge; twice Densité's price, only where there is a gauge, refused under a tide.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Recrue, the ghost ally who drops for the player at 100 prospecting, is not done: it needs a
    /// second loot roll per fight with its own prospecting and a line for it on the end screen --
    /// a fight-loot change for a buff that is, by the spec's own design, a cheaper Densité. Densité
    /// covers its use.
    /// </para>
    /// <para>
    /// Prices go by the zone's level (<see cref="Zones.Zone.Level"/>): Densité 10 x level^1.5 --
    /// 3 500 kamas at 50, 18 000 at 150, 28 000 at 200, a tenth of what half an hour of farm there
    /// brings (<see cref="Orbes"/>) --, Étude three quarters of it, Jauge twice.
    /// </para>
    /// </remarks>
    public static class ZoneCounter
    {
        public const string Density = "densite", Study = "etude", Gauge = "jauge";

        public static readonly IReadOnlyList<string> All = new[] { Density, Study, Gauge };

        /// <summary>«Densité», «étude», «Jauge»… as typed, accents and case aside; null when it is no buff.</summary>
        public static string? Parse(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant()
                                   .Replace('é', 'e').Replace('è', 'e').Replace('ê', 'e');
            return w switch
            {
                "densite" or "density" or "drop" => Density,
                "etude" or "study" or "xp" => Study,
                "jauge" or "gauge" => Gauge,
                _ => null,
            };
        }

        public static string Label(string buff) => buff switch
        {
            Density => "Densité",
            Study => "Étude",
            Gauge => "Jauge",
            _ => buff,
        };

        public static string Effect(string buff) => buff switch
        {
            Density => $"+{Farm.Current.Counter.DensityDropPercent} % de drop",
            Study => $"+{Farm.Current.Counter.StudyExperiencePercent} % d'XP",
            Gauge => "monstres comptés double dans la jauge",
            _ => "",
        };

        /// <summary>What a buff costs in a zone of this level.</summary>
        public static long Price(string buff, int zoneLevel)
        {
            var s = Farm.Current.Counter;
            double density = Math.Max(s.Minimum, s.PerLevel * Math.Pow(Math.Max(1, zoneLevel), s.Exponent));
            double price = buff switch
            {
                Study => density * s.StudyFactor,
                Gauge => density * s.GaugeFactor,
                _ => density,
            };
            return Math.Max(1, (long)Math.Round(price));
        }

        /// <summary>Whether a buff is running for this character in this subarea.</summary>
        public static bool IsActive(FarmProfile profile, int subAreaId, string buff, DateTime now)
            => Remaining(profile, subAreaId, buff, now) > TimeSpan.Zero;

        public static TimeSpan Remaining(FarmProfile profile, int subAreaId, string buff, DateTime now)
        {
            lock (profile.Gate)
            {
                if (!profile.Buffs.TryGetValue(subAreaId, out var buffs) || !buffs.TryGetValue(buff, out var until)) return TimeSpan.Zero;
                return until > now ? until - now : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Starts or lengthens a buff: thirty minutes from now, or from its end when it is still
        /// running. Ended buffs of the profile are swept on the way. Returns the new end.
        /// </summary>
        public static DateTime Extend(FarmProfile profile, int subAreaId, string buff, DateTime now)
        {
            var duration = TimeSpan.FromMinutes(Math.Max(1, Farm.Current.Counter.DurationMinutes));
            lock (profile.Gate)
            {
                foreach (var zone in profile.Buffs.Keys.ToList())
                {
                    foreach (var ended in profile.Buffs[zone].Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                        profile.Buffs[zone].Remove(ended);
                    if (profile.Buffs[zone].Count == 0) profile.Buffs.Remove(zone);
                }
                if (!profile.Buffs.TryGetValue(subAreaId, out var buffs)) profile.Buffs[subAreaId] = buffs = new Dictionary<string, DateTime>();
                var from = buffs.TryGetValue(buff, out var until) && until > now ? until : now;
                return buffs[buff] = from + duration;
            }
        }
    }
}
