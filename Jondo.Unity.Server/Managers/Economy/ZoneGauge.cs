using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The zone gauge (docs/stasis-gameplay-spec.md, §3): a bar per character and per subarea that
    /// every monster of a won fight fills; full, it pays one guaranteed item of the zone's list --
    /// the one the player targeted --, stasified at his tier, and starts again from nothing, the
    /// surplus lost. The threshold climbs base → x1.5 → x2 with each item of the window and falls
    /// back to the base every 72 hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the zones with a list have a gauge: a subarea whose monsters drop no equipment has
    /// none. The spec's extra gauge per level band of the big subareas is not done -- one gauge a
    /// subarea, its level the average of its roster.
    /// </para>
    /// <para>
    /// What speeds it: an archmonster in the fight x2, a tide x2 (they multiply), the counter's
    /// Jauge buff x2 outside a tide. Prospecting, Densité, Étude and the challenges do not.
    /// </para>
    /// <para>
    /// A full gauge with no target waits, full, until one is chosen (<c>.jauge cible</c>), which
    /// then pays at once. This class decides; FarmEconomy delivers the item.
    /// </para>
    /// </remarks>
    public static class ZoneGauge
    {
        /// <summary>A zone's base threshold, from its level.</summary>
        public static int BaseThreshold(int zoneLevel)
        {
            var s = Farm.Current.Gauge;
            return Math.Max(1, (int)Math.Round(s.Base + s.PerLevel * Math.Max(1, zoneLevel)));
        }

        /// <summary>The threshold after <paramref name="completions"/> items in the window: base, x1.5, then x2 for good.</summary>
        public static int Threshold(int zoneLevel, int completions)
        {
            var steps = Farm.Current.Gauge.Steps;
            double factor = steps.Count == 0 ? 1 : steps[Math.Clamp(completions, 0, steps.Count - 1)];
            return Math.Max(1, (int)Math.Round(BaseThreshold(zoneLevel) * factor));
        }

        /// <summary>
        /// The window rolled over when its 72 hours are past: the steps fall back to the base and
        /// the progress is kept, but under the new threshold (as the pity scroll does). The target stays.
        /// </summary>
        internal static void Roll(GaugeState state, int zoneLevel, DateTime now)
        {
            var hours = Math.Max(1, Farm.Current.Gauge.ResetHours);
            if (state.WindowStart == default) { state.WindowStart = now; return; }
            if (now - state.WindowStart < TimeSpan.FromHours(hours)) return;
            state.WindowStart = now;
            if (state.Completions == 0) return;
            state.Completions = 0;
            state.Progress = Math.Min(state.Progress, Threshold(zoneLevel, 0) - 1);
        }

        /// <summary>How much one fight adds: its monsters, times the multipliers that apply.</summary>
        public static int Gain(int monsters, bool archmonster, bool tide, bool buff)
        {
            var s = Farm.Current.Gauge;
            double gain = Math.Max(0, monsters);
            if (archmonster) gain *= s.ArchmonsterFactor;
            if (tide) gain *= s.TideFactor;
            else if (buff) gain *= s.BuffFactor;
            return (int)Math.Round(gain);
        }

        /// <summary>
        /// Adds a fight's gain to a gauge. Returns the item to pay when it is full and has a target
        /// (and resets it, the surplus lost), 0 otherwise -- full without a target, it stays full.
        /// </summary>
        public static int Add(GaugeState state, int zoneLevel, int gain, IReadOnlyList<int> list, DateTime now)
        {
            Roll(state, zoneLevel, now);
            int threshold = Threshold(zoneLevel, state.Completions);
            state.Progress = Math.Min(threshold, state.Progress + Math.Max(0, gain));
            return Claim(state, zoneLevel, list);
        }

        /// <summary>A full gauge with a target that is still on the list pays it; 0 otherwise.</summary>
        public static int Claim(GaugeState state, int zoneLevel, IReadOnlyList<int> list)
        {
            int threshold = Threshold(zoneLevel, state.Completions);
            if (state.Progress < threshold) return 0;
            if (state.Target == 0 || !list.Contains(state.Target)) return 0;
            state.Progress = 0;
            state.Completions++;
            return state.Target;
        }
    }
}
