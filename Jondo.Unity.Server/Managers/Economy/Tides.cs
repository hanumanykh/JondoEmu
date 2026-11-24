using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers.Economy
{
    /// <summary>
    /// The Marées de Stasis: one subarea a level band boosted for fifty minutes, ten of rest, then
    /// another subarea (docs/stasis-gameplay-spec.md, §7). Under a tide the experience takes
    /// +250 % on top of the server's rate for whoever is of the band (twenty levels of tolerance
    /// above it), and everybody fighting there takes twice the Orbes (kamas) and twice the gauge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schedule is a pure function of the clock: cycle number = minutes since 2026-01-01 UTC
    /// divided by (on + off), on during the first <see cref="Farm.TideSettings.OnMinutes"/> of
    /// it. Each band's subarea is drawn from the cycle number among the subareas whose level falls
    /// in the band and whose roster has a few monsters (no dungeon, no lone boss), so a restart
    /// keeps the same tide and nothing has to be saved. <see cref="Farm.TideSettings.Global"/>
    /// puts the whole world under the tide instead, for a server too small to find a zone.
    /// </para>
    /// <para>
    /// The spec's «repop immédiat» needs no hook here: on this server a beaten group is replaced
    /// the moment the fight ends (FightHandler, RespawnOneGroup), tide or no tide.
    /// </para>
    /// </remarks>
    public static class Tides
    {
        private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public readonly record struct Phase(long Cycle, bool Active, TimeSpan Left);

        /// <summary>Where the schedule stands at a moment.</summary>
        public static Phase PhaseAt(DateTime utc) => PhaseAt(utc, Farm.Current.Tides);

        internal static Phase PhaseAt(DateTime utc, Farm.TideSettings s)
        {
            int on = Math.Max(1, s.OnMinutes), cycle = on + Math.Max(0, s.OffMinutes);
            double minutes = (utc - Epoch).TotalMinutes;
            long index = (long)Math.Floor(minutes / cycle);
            double into = minutes - index * (double)cycle;
            bool active = s.Enabled && into < on;
            double left = active ? on - into : cycle - into;
            return new Phase(index, active, TimeSpan.FromMinutes(left));
        }

        public static Phase Now => PhaseAt(Farm.Clock());

        /// <summary>The band a level belongs to, null past them all.</summary>
        internal static Farm.TideBand? BandOf(int level)
            => Farm.Current.Tides.Bands.FirstOrDefault(b => level >= b.Min && level <= b.Max);

        private static long _candidatesCycle = -1;
        private static Dictionary<Farm.TideBand, int> _chosen = new();
        private static readonly object Gate = new();

        /// <summary>The subarea each band's tide is in, for a cycle.</summary>
        internal static IReadOnlyDictionary<Farm.TideBand, int> ZonesOf(long cycle)
        {
            lock (Gate)
            {
                if (_candidatesCycle == cycle) return _chosen;
                var s = Farm.Current.Tides;
                var chosen = new Dictionary<Farm.TideBand, int>();
                var dungeons = DungeonManager.All.Values.SelectMany(d => d.Rooms).Select(Zones.SubAreaOf).ToHashSet();
                var candidates = Zones.Rosters.Where(r => r.Value.Count >= s.MinimumRoster && !dungeons.Contains(r.Key))
                                              .Select(r => (Id: r.Key, Level: Zones.LevelOf(r.Value)))
                                              .ToList();
                foreach (var band in s.Bands)
                {
                    var inBand = candidates.Where(c => c.Level >= band.Min && c.Level <= band.Max).OrderBy(c => c.Id).ToList();
                    if (inBand.Count == 0) continue;
                    chosen[band] = inBand[(int)(Mix(cycle, band.Min) % (ulong)inBand.Count)].Id;
                }
                _chosen = chosen;
                _candidatesCycle = cycle;
                return chosen;
            }
        }

        /// <summary>Whether a subarea is under a tide right now (for the Orbes and the gauge: everybody there).</summary>
        public static bool IsTideZone(int subAreaId)
        {
            var phase = Now;
            if (!phase.Active || subAreaId <= 0) return false;
            if (Farm.Current.Tides.Global) return true;
            return ZonesOf(phase.Cycle).Values.Contains(subAreaId);
        }

        /// <summary>
        /// Whether the tide raises this character's experience in this subarea: the zone is under
        /// a tide and his level is in its band, or at most the tolerance above it.
        /// </summary>
        public static bool BoostsExperience(int subAreaId, int level)
        {
            var phase = Now;
            if (!phase.Active || subAreaId <= 0) return false;
            var s = Farm.Current.Tides;
            if (s.Global) return true;
            foreach (var (band, zone) in ZonesOf(phase.Cycle))
                if (zone == subAreaId && level >= band.Min && level <= band.Max + s.LevelTolerance) return true;
            return false;
        }

        private static ulong Mix(long cycle, int salt)
        {
            unchecked
            {
                ulong x = (ulong)cycle * 0x9E3779B97F4A7C15UL ^ (ulong)salt * 0xBF58476D1CE4E5B9UL;
                x ^= x >> 31;
                x *= 0x94D049BB133111EBUL;
                x ^= x >> 29;
                return x;
            }
        }

        // ─── The announcements ──────────────────────────────────────────────────────────────

        private static bool? _wasActive;
        private static long _announcedCycle = -1;

        /// <summary>The French lines a tide's start and end are told in.</summary>
        internal static string Describe(long cycle)
        {
            var s = Farm.Current.Tides;
            if (s.Global) return "le monde entier";
            var zones = ZonesOf(cycle);
            if (zones.Count == 0) return "aucune zone";
            return string.Join(", ", zones.Select(z => $"{SafeName(z.Value)} (niv. {z.Key.Min}-{z.Key.Max})"));
        }

        internal static string SafeName(int subAreaId)
        {
            try { return DatabaseManager.GetSubAreaName(subAreaId) is { Length: > 0 } n ? n : $"sous-zone {subAreaId}"; }
            catch { return $"sous-zone {subAreaId}"; }
        }

        /// <summary>
        /// The clock's tick (Program, every 30 s): when a tide starts or ends, every real player in
        /// the world is told -- not the simulated ones, who read nothing.
        /// </summary>
        public static async Task TickAsync()
        {
            var phase = Now;
            bool changed = _wasActive != phase.Active || (phase.Active && _announcedCycle != phase.Cycle);
            bool first = _wasActive == null;
            _wasActive = phase.Active;
            if (!changed) return;
            string text;
            if (phase.Active)
            {
                _announcedCycle = phase.Cycle;
                text = $"Marée de Stasis ! Pendant {(int)Math.Ceiling(phase.Left.TotalMinutes)} min : {Describe(phase.Cycle)}. " +
                       $"+{Farm.Current.Tides.ExperiencePercent} % d'XP (pour votre tranche), kamas x2, jauge x2.";
            }
            else
            {
                if (first) return;
                text = $"La Marée de Stasis se retire. Prochaine marée dans {(int)Math.Ceiling(phase.Left.TotalMinutes)} min.";
            }
            Console.WriteLine($"[Tides] {text}");
            foreach (var s in SessionRegistry.InWorld().ToList())
            {
                if (SimulatedPlayers.IsSimulated(s.AccountId)) continue;
                try
                {
                    await s.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Tides] The tide's word did not reach {s.State.CharacterName}: {ex.Message}");
                }
            }
        }
    }
}
