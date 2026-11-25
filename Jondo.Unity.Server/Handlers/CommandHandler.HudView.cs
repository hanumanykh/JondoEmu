using System;
using System.Collections.Concurrent;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Managers.Economy;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The two always-visible JondoFix HUD panels (/api/stasis/hud, ProgressionUi.HudPanels.cs):
    /// the zone gauge bar (zone, y/z, target, active multipliers) and «Le Système» (chapter
    /// mission and the tracks). Polled every few seconds out of a fight, so it is kept cheap:
    /// in-memory profiles only, and the view cached per character for <see cref="HudCacheFor"/>,
    /// built again at once when the map or the fight state changed (or when the client asks
    /// «fresh», after a GO or a Récupérer).
    /// </summary>
    public static partial class CommandHandler
    {
        private static readonly TimeSpan HudCacheFor = TimeSpan.FromSeconds(4);

        private sealed record HudCached(DateTime At, long MapId, bool Fight, object View);

        private static readonly ConcurrentDictionary<long, HudCached> _hudCache = new();

        /// <summary>The HUD view; in a fight only <c>{ fight = true }</c>, nothing built.</summary>
        internal static object HudView(GameSession session, bool fresh)
        {
            long me = session.CharacterId;
            long map = session.State.MapId;
            bool fight = session.State.IsInFight;
            if (fight)
            {
                _hudCache.TryRemove(me, out _); // the fight's end changes the gauge and the missions
                return new { fight = true, mapId = map };
            }
            var now = DateTime.UtcNow;
            if (!fresh && _hudCache.TryGetValue(me, out var cached) && cached.MapId == map && !cached.Fight && now - cached.At < HudCacheFor)
                return cached.View;

            object? gauge = null, missions = null;
            try { gauge = HudGauge(session); } catch (Exception ex) { Console.WriteLine($"[Hud] Gauge: {ex.Message}"); }
            try { missions = HudMissions(session); } catch (Exception ex) { Console.WriteLine($"[Hud] Missions: {ex.Message}"); }
            var view = new { fight = false, mapId = map, gauge, missions };
            _hudCache[me] = new HudCached(now, map, false, view);
            return view;
        }

        private static object HudGauge(GameSession session)
        {
            int subArea = Zones.SubAreaOf(session.State.MapId);
            var zone = Zones.Of(subArea);
            if (!Farm.Current.Gauge.Enabled || zone == null || zone.Equipment.Count == 0)
                return new { available = false, name = subArea > 0 ? Tides.SafeName(subArea) : "", level = zone?.Level ?? 0 };

            var profile = FarmProfiles.Of(session.CharacterId);
            var now = Farm.Clock();
            int progress, completions, target;
            lock (profile.Gate)
            {
                // Roll only moves the window past its end, as GaugeView does.
                if (!profile.Gauges.TryGetValue(subArea, out var state)) state = new GaugeState();
                ZoneGauge.Roll(state, zone.Level, now);
                progress = state.Progress;
                completions = state.Completions;
                target = state.Target;
            }
            var g = Farm.Current.Gauge;
            int threshold = ZoneGauge.Threshold(zone.Level, completions);
            bool tide = Tides.IsTideZone(subArea);
            bool buff = ZoneCounter.IsActive(profile, subArea, ZoneCounter.Gauge, now);
            var badges = new System.Collections.Generic.List<object>();
            if (tide) badges.Add(new { label = "Marée", factor = g.TideFactor });
            else if (buff) badges.Add(new { label = "Buff", factor = g.BuffFactor });
            badges.Add(new { label = "Archi", factor = g.ArchmonsterFactor, perFight = true });
            return new
            {
                available = true,
                subArea,
                name = Tides.SafeName(subArea),
                level = zone.Level,
                progress,
                threshold,
                percent = Math.Clamp(progress * 100 / Math.Max(1, threshold), 0, 100),
                full = progress >= threshold,
                target,
                targetName = target != 0 ? NameOfItem(target) : "",
                badges,
            };
        }

        private static object HudMissions(GameSession session)
        {
            var profile = MissionsOf(session);
            var file = Missions.Current;
            lock (profile.Gate)
            {
                object? Of(string slot)
                {
                    if (!profile.Slots.TryGetValue(slot, out var m)) return null;
                    var chapter = file.Chapters.FirstOrDefault(c => c.Id == m.Source);
                    var track = file.Tracks.FirstOrDefault(t => t.Id == m.Source);
                    return new
                    {
                        slot,
                        source = chapter?.Title ?? track?.Title ?? m.Source,
                        title = m.Title,
                        done = m.Kind == Missions.KindDone,
                        progress = m.Progress,
                        need = m.Need,
                        complete = m.Complete,
                        canGo = !m.Complete && m.Kind != Missions.KindDone && (m.Zone > 0 || m.Kind == Missions.KindDungeon),
                    };
                }
                return new
                {
                    chapter = Of(Missions.ChapterSlot),
                    tracks = Enumerable.Range(0, file.TracksShown).Select(i => Of(Missions.TrackSlot(i))).Where(t => t != null).ToList(),
                };
            }
        }
    }
}
