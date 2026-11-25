using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Managers.Economy;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The structured side of the F9 window's Stasis tabs (/api/stasis/panel, field «view»): what
    /// each tab draws as widgets -- the zone gauge's bar and its items, the counter's buff cards,
    /// the tide zones, the tier track, the events' schedule and token shop, the Fissure's bands,
    /// the Stasis forge (fusion and raffineur) -- and «alertes», the little poll JondoFix makes
    /// while the window is closed for its toasts. The chat commands stay as they are; the tabs'
    /// buttons still type them (PanelActAsync), so the costs and the refusals are the chat's.
    /// </summary>
    /// <remarks>
    /// Read-only: nothing here pays, gives or saves. The names are the server's French ones
    /// (NameOfItem, Tides.SafeName); the reasons a button is disabled are written for the player.
    /// </remarks>
    public static partial class CommandHandler
    {
        /// <summary>The coin the window shows in its header: the Jondo Coin, or 0 for kamas.</summary>
        private static int CoinItem => JondoCoin.IsOrbe ? JondoCoin.TemplateId : 0;

        /// <summary>A tab's widgets, or null when it has none (an older client draws the lines).</summary>
        internal static object? PanelStructured(GameSession session, string tab)
        {
            try
            {
                return tab switch
                {
                    "jauge" => GaugeView(session),
                    "comptoir" => CounterView(session),
                    "maree" => TideView(session),
                    "paliers" => TiersView(session),
                    "event" => EventsView(session),
                    "fissure" => FissureView(session),
                    "stasis" => ForgeView(session),
                    "alertes" => AlertsView(session),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Panel] The view of «{tab}» could not be built: {ex.Message}");
                return null;
            }
        }

        private static long Seconds(TimeSpan span) => span <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(span.TotalSeconds);

        private static object ZoneOf(int subArea, Zones.Zone zone)
            => new { subArea, name = Tides.SafeName(subArea), level = zone.Level };

        // ─── Jauge ──────────────────────────────────────────────────────────────────────────

        private static object GaugeView(GameSession session)
        {
            int subArea = Zones.SubAreaOf(session.State.MapId);
            var zone = Zones.Of(subArea);
            if (!Farm.Current.Gauge.Enabled || zone == null || zone.Equipment.Count == 0)
                return new { available = false, reason = "Cette zone n'a pas de jauge : aucun équipement n'y tombe à coup sûr. Allez dans une zone de monstres." };

            var profile = FarmProfiles.Of(session.CharacterId);
            var now = Farm.Clock();
            GaugeState state;
            lock (profile.Gate)
            {
                if (!profile.Gauges.TryGetValue(subArea, out state!)) state = new GaugeState();
                ZoneGauge.Roll(state, zone.Level, now);
            }
            var g = Farm.Current.Gauge;
            int threshold = ZoneGauge.Threshold(zone.Level, state.Completions);
            bool tide = Tides.IsTideZone(subArea);
            bool buff = ZoneCounter.IsActive(profile, subArea, ZoneCounter.Gauge, now);
            var reset = state.WindowStart + TimeSpan.FromHours(Math.Max(1, g.ResetHours)) - now;
            return new
            {
                available = true,
                zone = ZoneOf(subArea, zone),
                progress = state.Progress,
                threshold,
                percent = Math.Clamp(state.Progress * 100 / Math.Max(1, threshold), 0, 100),
                full = state.Progress >= threshold,
                completions = state.Completions,
                resetIn = FormatSpan(reset),
                resetSeconds = Seconds(reset),
                target = state.Target,
                targetName = state.Target != 0 ? NameOfItem(state.Target) : "",
                items = zone.Equipment.Take(48).Select((gid, i) => new
                {
                    gid, index = i + 1, name = NameOfItem(gid), target = gid == state.Target,
                }).ToList(),
                multipliers = new[]
                {
                    new { label = "Marée", factor = g.TideFactor, active = tide, note = tide ? "Marée en cours dans cette zone" : "Pas de Marée ici" },
                    new { label = "Archimonstre", factor = g.ArchmonsterFactor, active = false, note = "Par combat contre un archimonstre" },
                    new { label = "Buff Jauge", factor = g.BuffFactor, active = buff && !tide, note = buff ? (tide ? "Sans effet sous la Marée" : "Actif (Comptoir)") : "S'achète au Comptoir" },
                },
            };
        }

        // ─── Comptoir ───────────────────────────────────────────────────────────────────────

        private static object CounterView(GameSession session)
        {
            int subArea = Zones.SubAreaOf(session.State.MapId);
            var zone = Zones.Of(subArea);
            if (!Farm.Current.Counter.Enabled || zone == null)
                return new { available = false, reason = "Pas de comptoir ici : il faut être dans une zone de monstres." };
            var profile = FarmProfiles.Of(session.CharacterId);
            var now = Farm.Clock();
            long held = OrbesHeld();
            int level = session.State.CharacterLevel;
            bool fight = session.State.IsInFight;
            return new
            {
                available = true,
                zone = ZoneOf(subArea, zone),
                durationMinutes = Farm.Current.Counter.DurationMinutes,
                coins = held,
                coinLabel = OrbeLabel,
                coinItem = CoinItem,
                buffs = ZoneCounter.All.Select(b =>
                {
                    var left = ZoneCounter.Remaining(profile, subArea, b, now);
                    long price = OrbePrice(ZoneCounter.Price(b, zone.Level));
                    string why = "";
                    if (fight) why = "Indisponible en combat.";
                    else if (b == ZoneCounter.Study && Tides.BoostsExperience(subArea, level)) why = "Une Marée booste déjà votre XP ici.";
                    else if (b == ZoneCounter.Gauge && Tides.IsTideZone(subArea)) why = "La Marée double déjà la jauge ici.";
                    else if (b == ZoneCounter.Gauge && zone.Equipment.Count == 0) why = "Cette zone n'a pas de jauge.";
                    else if (ZoneCounter.ExclusiveWith(b) is { } other && ZoneCounter.IsActive(profile, subArea, other, now))
                        why = $"Ne se cumule pas avec {ZoneCounter.Label(other)} (actif).";
                    else if (held < price) why = $"Il vous manque {price - held:N0} {OrbeLabel}.";
                    return new
                    {
                        id = b, label = ZoneCounter.Label(b), effect = ZoneCounter.Effect(b), price,
                        active = left > TimeSpan.Zero, remaining = left > TimeSpan.Zero ? FormatSpan(left) : "", remainingSeconds = Seconds(left),
                        canBuy = why.Length == 0, why,
                    };
                }).ToList(),
            };
        }

        // ─── Marées ─────────────────────────────────────────────────────────────────────────

        private static object TideView(GameSession session)
        {
            var phase = Tides.Now;
            var s = Farm.Current.Tides;
            int level = session.State.CharacterLevel;
            int here = Zones.SubAreaOf(session.State.MapId);
            object ZoneList(long cycle, bool current) => Tides.ZonesOf(cycle).OrderBy(z => z.Key.Min).Select(z =>
            {
                bool mine = level >= z.Key.Min && level <= z.Key.Max + s.LevelTolerance;
                string why = !current ? "Cette Marée n'a pas encore commencé."
                    : !phase.Active ? "Pas de Marée en ce moment."
                    : session.State.IsInFight ? "Indisponible en combat."
                    : z.Value == here ? "Vous y êtes déjà." : "";
                return new
                {
                    subArea = z.Value, name = Tides.SafeName(z.Value), minLevel = z.Key.Min, maxLevel = z.Key.Max,
                    forMe = mine, here = z.Value == here,
                    boosts = new[] { $"+{s.ExperiencePercent} % d'XP (niv. {z.Key.Min}-{z.Key.Max + s.LevelTolerance})", "Kamas x2", "Jauge x2" },
                    canGo = why.Length == 0, why,
                };
            }).ToList();
            return new
            {
                enabled = s.Enabled,
                active = phase.Active,
                global = s.Global,
                left = FormatSpan(phase.Left),
                leftSeconds = Seconds(phase.Left),
                experiencePercent = s.ExperiencePercent,
                onMinutes = s.OnMinutes,
                offMinutes = s.OffMinutes,
                zones = phase.Active ? ZoneList(phase.Cycle, true) : ZoneList(phase.Cycle + 1, false),
            };
        }

        /// <summary>«.maree aller N» from the window: to a tide zone, by its zaap first, else one of its maps.</summary>
        internal static async Task<string> TideGoAsync(GameSession session, int subArea)
        {
            var state = session.State;
            if (session.Stream == null) return "Hors ligne.";
            if (state.IsInFight) return "Impossible en combat.";
            if (DungeonManager.OfRoom(state.MapId) != null) return "Impossible depuis un donjon.";
            var phase = Tides.Now;
            if (!phase.Active || !Tides.ZonesOf(phase.Cycle).Values.Contains(subArea)) return "Cette zone n'est pas sous la Marée.";
            var maps = new List<long>();
            maps.AddRange(Interactives.Waypoints.Where(w => w.Activated && w.SubAreaId == subArea).Select(w => w.MapId));
            maps.AddRange(DatabaseManager.MapsOfSubArea(subArea).Take(30));
            foreach (long map in maps.Distinct())
            {
                if (map == state.MapId) return "Vous y êtes.";
                if (await WorldMoveHandler.TeleportAsync(session.Stream, map) != null) return $"En route vers {Tides.SafeName(subArea)}.";
            }
            return "Cette zone est inaccessible pour le moment.";
        }

        // ─── Paliers ────────────────────────────────────────────────────────────────────────

        private static object TiersView(GameSession session)
        {
            var file = Tiers.Current;
            var state = session.State;
            long me = session.CharacterId;
            bool evolved = Ascension.IsEvolved(me);
            var done = Tiers.DoneDungeons(me);
            var now = DateTime.Now;
            int top = file.Tiers.Count == 0 ? 0 : file.Tiers.Max(t => t.Level);
            long held = OrbesHeld();
            int gate = 0;
            var tiers = new List<object>();
            foreach (var tier in file.Tiers.OrderBy(t => t.Level))
            {
                var left = Tiers.Remaining(tier, done);
                bool blocking = gate == 0 && left.Count > 0 && tier.Level >= file.GateFromLevel && tier.Level < top;
                if (blocking) gate = tier.Level;
                tiers.Add(new
                {
                    level = tier.Level,
                    done = tier.Dungeons.Count - left.Count,
                    total = tier.Dungeons.Count,
                    complete = left.Count == 0,
                    reached = state.CharacterLevel >= tier.Level,
                    blocking,
                    dungeons = tier.Dungeons.Select(id =>
                    {
                        var d = DungeonManager.Get(id);
                        int key = d == null ? 0 : Tiers.KeyItem(d);
                        long price = d == null ? -1 : Tiers.KeyPrice(d);
                        return new
                        {
                            id, name = Tiers.DungeonName(id), done = done.Contains(id),
                            minLevel = d?.MinLevel ?? 0,
                            keyItem = key, keyName = key != 0 ? NameOfItem(key) : "", keyPrice = price,
                            canBuyKey = key != 0 && price >= 0 && state.Kamas >= price,
                            keyWhy = key == 0 || price < 0 ? "Pas de clé" : state.Kamas < price ? $"Il vous manque {price - state.Kamas:N0} kamas." : "",
                            keyCoins = key != 0 && price >= 0 && JondoCoin.IsOrbe ? OrbePrice(price) : -1,
                            canBuyKeyCoins = key != 0 && price >= 0 && JondoCoin.IsOrbe && held >= OrbePrice(price),
                            freeEntry = d != null && Tiers.FreeEntryOpens(d, me, now),
                        };
                    }).ToList(),
                });
            }
            var h = file.Hardcore;
            return new
            {
                level = state.CharacterLevel,
                kamas = state.Kamas,
                coins = held, coinLabel = OrbeLabel, coinItem = CoinItem,
                evolved,
                hardcore = evolved && Tiers.WantsHardcore(me),
                canHardcore = evolved,
                hardcoreWhy = evolved ? "" : "Réservé aux personnages évolués.",
                hardcoreText = $"PV x{h.Life}, dégâts x{h.Damage}, drop x{h.Drop} ; seuls ces donjons valident vos paliers une fois évolué.",
                gate,
                gateCap = gate == 0 ? 0 : gate + 25,
                fullRoom = DungeonRules.FullRoom(me),
                freeReset = DungeonKeyring.NextReset(now).ToString("dd/MM"),
                tiers,
                // «Clés de donjon»: every keyed dungeon his level allows, its key bought from
                // anywhere in coins (.marchand cle <id>, BuyKeyInOrbesAsync: the same OrbePrice).
                keys = DungeonManager.All.Values
                    .Where(d => d.MinLevel <= state.CharacterLevel)
                    .Select(d => (d, key: Tiers.KeyItem(d), price: Tiers.KeyPrice(d)))
                    .Where(x => x.key != 0 && x.price >= 0)
                    .GroupBy(x => x.d.Name).Select(g => g.OrderBy(x => x.d.Id).First())
                    .OrderBy(x => x.d.MinLevel).ThenBy(x => x.d.Name)
                    .Select(x => new
                    {
                        id = x.d.Id, name = x.d.Name, minLevel = x.d.MinLevel, keyItem = x.key, keyName = NameOfItem(x.key),
                        price = JondoCoin.IsOrbe ? OrbePrice(x.price) : x.price,
                        canBuy = JondoCoin.IsOrbe ? held >= OrbePrice(x.price) : state.Kamas >= x.price,
                    }).ToList(),
                keysInCoins = JondoCoin.IsOrbe,
            };
        }

        // ─── Événements ─────────────────────────────────────────────────────────────────────

        private static object EventsView(GameSession session)
        {
            long me = session.CharacterId;
            var settings = StasisEvents.Current;
            var running = StasisEvents.Now;
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            long tokens = EventTokens.Balance(me);
            string[] days = { "Dimanche", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi" };

            string Rules(StasisEvents.EventDef d)
            {
                string text = d.Text("start", d.Name).Replace("{minutes}", d.Minutes.ToString());
                // The announcement's commands are the chat's: the window has buttons for them.
                int cut = text.IndexOf(" Classement", StringComparison.Ordinal);
                if (cut > 0) text = text[..cut];
                return text.Replace(" (.event aller)", "");
            }

            var schedule = new List<object>();
            for (int i = 0; i < 7; i++)
            {
                var day = today.AddDays(i);
                string kind = StasisEvents.KindOn(day, settings);
                if (!settings.Events.TryGetValue(kind, out var def)) continue;
                int hour = StasisEvents.StartHourOf(def, day);
                string status = i > 0 ? "À venir"
                    : running != null && running.Def.Kind == kind ? "En cours"
                    : now.Hour < hour ? "Aujourd'hui"
                    : now.Hour > settings.LatestHour ? "Terminé"
                    : "Passé";
                schedule.Add(new
                {
                    day = i == 0 ? "Aujourd'hui" : i == 1 ? "Demain" : days[(int)day.DayOfWeek],
                    date = day.ToString("dd/MM"), kind, name = def.Name, hour, minutes = def.Minutes,
                    rules = Rules(def), status,
                    rewards = def.Rewards.OrderBy(r => r.Key).Select(r => $"Cat. {r.Key} : {string.Join(" / ", r.Value)} jetons").ToList(),
                });
            }

            object? live = null;
            if (running != null)
            {
                var categories = new List<object>();
                foreach (var category in new[] { "I", "II", "III" })
                {
                    var ranked = StasisEvents.Rank(running.Bots.Where(b => b.Category == category)
                        .Concat(running.Best.Values.Where(e => e.Category == category)), running.Def.LowerIsBetter);
                    if (ranked.Count == 0) continue;
                    int mine = ranked.FindIndex(e => e.CharacterId == me);
                    categories.Add(new
                    {
                        category,
                        top = ranked.Take(3).Select((e, i) => new { rank = i + 1, name = e.Name, score = StasisEvents.Show(running.Def, e.Score), me = e.CharacterId == me }).ToList(),
                        myRank = mine + 1,
                        myScore = mine >= 0 ? StasisEvents.Show(running.Def, ranked[mine].Score) : "",
                    });
                }
                live = new
                {
                    kind = running.Def.Kind, name = running.Def.Name, rules = Rules(running.Def),
                    leftMinutes = Math.Max(0, (int)(running.Until - now).TotalMinutes),
                    canGo = running.Def.Kind == StasisEvents.Poutch && running.Def.Map > 0 && !session.State.IsInFight,
                    categories,
                    clues = running.Def.Kind == StasisEvents.Traque ? $"PNJ restants : {running.Spots.Count(s => s.FoundBy == 0)}/{running.Spots.Count}" : "",
                };
            }

            var pieces = Equipment.All.Where(i => i.Quantity > 0 && Forgemagic.TemplateOf(i.Template) is { } t && !Forgemagic.Stacks(t))
                                      .OrderBy(i => i.Position == Equipment.Bag ? 1 : 0).ThenBy(i => i.Position).Take(60).ToList();
            return new
            {
                tokens,
                running = live,
                schedule,
                shop = settings.Shop.Select(a => new
                {
                    id = a.Id, name = a.Name, price = a.Price, item = a.Item, quantity = a.Quantity, rune = a.Rune,
                    canBuy = tokens >= a.Price,
                    why = tokens >= a.Price ? "" : $"Il vous manque {a.Price - tokens} jetons.",
                }).ToList(),
                runeCap = EventTokens.RuneCapPerItem,
                runeTargets = pieces.Select(i => new
                {
                    uid = i.Uid, gid = i.Template, name = NameOfItem(i.Template), worn = i.Position != Equipment.Bag,
                    rune = EventTokens.RuneOf(i.Uid), full = EventTokens.AfterRune(EventTokens.RuneOf(i.Uid)) == null,
                }).ToList(),
            };
        }

        // ─── Fissure ────────────────────────────────────────────────────────────────────────

        private static object FissureView(GameSession session)
        {
            long me = session.CharacterId;
            var settings = Fissure.Current;
            var p = Fissure.ProgressOf(me);
            var exp = Fissure.Of(me);
            var party = Parties.Of(me);
            int fighters = Math.Clamp(party == null ? 1 : Parties.MembersOf(party).Count, 1, Math.Max(1, settings.MaxFighters));
            var partyExp = party == null ? null : Parties.MembersOf(party).Where(m => m != me).Select(Fissure.Of).FirstOrDefault(e => e != null);
            long held = OrbesHeld();
            int needed = Math.Max(1, settings.Dofus.WavesNeeded);
            int owed = Fissure.DofusOwed(p.Eligible, p.DofusClaimed, needed);
            var bonus = Fissure.FormationFor(fighters, settings.Formations);
            return new
            {
                enabled = settings.Enabled,
                coins = held, coinLabel = OrbeLabel, coinItem = CoinItem,
                waves = p.Waves, best = p.Best,
                eligible = p.Eligible % needed, needed, owed,
                dofus = settings.Dofus.Items.Select(d => new { name = d.Name, item = d.Item }).ToList(),
                eligibleBands = settings.Dofus.EligibleBands, eligibleFromWave = settings.Dofus.EligibleFromWave,
                fighters,
                formation = $"+{bonus.Ap} PA, +{bonus.Mp} PM, +{bonus.LifePercent} % PV, +{bonus.DamagePercent} % dégâts",
                expedition = exp == null ? null : new
                {
                    band = exp.Band.Name, wave = exp.Wave, cleared = exp.Cleared, fighters = exp.Fighters,
                    monsterLevel = Fissure.MonsterLevel(exp.Band, exp.Wave),
                    inArena = session.State.MapId == exp.ArenaMap,
                },
                canJoin = exp == null && partyExp != null && session.State.MapId != partyExp.ArenaMap,
                bands = settings.Bands.Select(b =>
                {
                    long cost = OrbePrice((long)b.Key * fighters);
                    string why = !settings.Enabled ? "La Fissure est fermée."
                        : exp != null ? "Une expédition est déjà en cours."
                        : session.State.IsInFight ? "Indisponible en combat."
                        : session.State.CharacterLevel < b.MinLevel ? $"Niveau {b.MinLevel} requis."
                        : held < cost ? $"Il vous manque {cost - held:N0} {OrbeLabel}."
                        : "";
                    return new
                    {
                        id = b.Id, name = b.Name, minLevel = b.MinLevel,
                        keyPrice = cost, keyPriceOne = OrbePrice(b.Key),
                        levels = $"{b.BaseLevel} → {b.MaxLevel}", levelPerWave = b.LevelPerWave,
                        validate = b.Validate, limit = b.Limit,
                        kamasPerWave = b.KamasPerWave, tokensPerWave = b.TokensPerWave, validatedTokens = b.ValidatedTokens,
                        dofusEligible = settings.Dofus.EligibleBands.Count == 0 || settings.Dofus.EligibleBands.Contains(b.Id),
                        resources = b.Resources.Take(8).Select(r => new { item = r.Item, name = NameOfItem(r.Item), quantity = r.Quantity, percent = r.Percent }).ToList(),
                        canOpen = why.Length == 0, why,
                    };
                }).ToList(),
            };
        }

        // ─── Forge Stasis (fusion, raffineur) ───────────────────────────────────────────────

        private static object ForgeView(GameSession session)
        {
            var s = Stasis.Current;
            long held = OrbesHeld();
            bool evolved = Stasis.IsEvolved(session.CharacterId);
            var bag = Equipment.All.Where(i => i.Position == Equipment.Bag && i.Quantity > 0)
                                   .Select(i => (Item: i, Tier: Stasis.TierOf(i.Uid, i.Effects)))
                                   .Where(x => x.Tier > 0).ToList();
            var groups = bag.GroupBy(x => (x.Item.Template, x.Tier)).OrderBy(g => g.Key.Template).ThenBy(g => g.Key.Tier).Take(80).Select(g =>
            {
                var template = Forgemagic.TemplateOf(g.Key.Template);
                int count = g.Sum(x => Math.Max(1, x.Item.Quantity));
                long fuseCost = template == null || g.Key.Tier >= Stasis.MaxTier ? -1 : OrbePrice(Stasis.FusionCost(template.Level, g.Key.Tier + 1));
                string why = g.Key.Tier >= Stasis.MaxTier ? "Palier maximum atteint."
                    : count < Stasis.FusionCount ? $"Il faut {Stasis.FusionCount} exemplaires ({count}/{Stasis.FusionCount})."
                    : session.State.IsInFight ? "Indisponible en combat."
                    : held < fuseCost ? $"Il vous manque {fuseCost - held:N0} {OrbeLabel}."
                    : "";
                return new
                {
                    gid = g.Key.Template, name = NameOfItem(g.Key.Template), level = template?.Level ?? 0,
                    tier = g.Key.Tier, tierName = Stasis.Name(g.Key.Tier), count,
                    fuseCost, canFuse = why.Length == 0, fuseWhy = why,
                    fuseTo = g.Key.Tier < Stasis.MaxTier ? Stasis.Name(g.Key.Tier + 1) : "",
                    loseExos = g.Key.Tier + 1 == 3,
                    buyback = OrbePrice(Stasis.BuybackPrice(ReferencePrices.Of(g.Key.Template), g.Key.Tier)),
                };
            }).ToList();
            return new
            {
                coins = held, coinLabel = OrbeLabel, coinItem = CoinItem,
                evolved,
                fusionCount = Stasis.FusionCount,
                multipliers = Enumerable.Range(1, 4).Select(t => new { tier = Stasis.Name(t), factor = s.Multipliers.GetValueOrDefault(t) }).ToList(),
                rule = evolved
                    ? "Personnage évolué : vos butins sont S1 à 66 %, S2 à 30 %, S3 à 3 %, S4 à 1 %."
                    : "Personnage non évolué : vos butins sont tous S1 ; S3 et S4 ne s'équipent qu'une fois évolué.",
                pieces = groups,
            };
        }

        // ─── Alertes (the toasts) ───────────────────────────────────────────────────────────

        /// <summary>What wants the player's attention: a key and a sentence each; the client toasts the new keys.</summary>
        private static object AlertsView(GameSession session)
        {
            var alerts = new List<object>();
            void Add(string key, string text, string tab) => alerts.Add(new { key, text, tab });
            long me = session.CharacterId;

            int subArea = Zones.SubAreaOf(session.State.MapId);
            var zone = Zones.Of(subArea);
            if (zone != null && zone.Equipment.Count > 0 && Farm.Current.Gauge.Enabled)
            {
                var profile = FarmProfiles.Of(me);
                lock (profile.Gate)
                {
                    if (profile.Gauges.TryGetValue(subArea, out var g) && g.Progress >= ZoneGauge.Threshold(zone.Level, g.Completions))
                        Add($"jauge:{subArea}:{g.Completions}", g.Target == 0 ? "Jauge pleine : choisissez une cible !" : "Jauge pleine !", "jauge");
                }
            }

            try
            {
                var missions = MissionsOf(session);
                lock (missions.Gate)
                    foreach (var (slot, m) in missions.Slots)
                        if (m.Complete && m.Kind != Missions.KindDone)
                            Add($"mission:{slot}:{m.Source}:{m.Step}", $"Mission prête à récupérer : {m.Title}", "missions");
            }
            catch (Exception ex) { Console.WriteLine($"[Panel] Missions for the alerts: {ex.Message}"); }

            var p = Fissure.ProgressOf(me);
            int owed = Fissure.DofusOwed(p.Eligible, p.DofusClaimed, Math.Max(1, Fissure.Current.Dofus.WavesNeeded));
            if (owed > 0) Add($"dofus:{p.DofusClaimed}", "Un Dofus de la Fissure vous attend !", "fissure");
            var exp = Fissure.Of(me);
            if (exp != null && exp.Cleared > 0) Add($"vague:{exp.Band.Id}:{exp.Cleared}", $"Vague {exp.Cleared} de la Fissure gagnée !", "fissure");

            var running = StasisEvents.Now;
            if (running != null) Add($"event:{running.Day}:{running.Def.Kind}", $"{running.Def.Name} en cours !", "event");
            if (Tides.Now.Active) Add($"maree:{Tides.Now.Cycle}", "Une Marée de Stasis est en cours !", "maree");

            // The HUD's «Sanctuaire» / «Retour» button and the end-of-fight recap toast (ProgressionUi.Hud.cs).
            return new
            {
                coins = OrbesHeld(), coinLabel = OrbeLabel, coinItem = CoinItem, alerts,
                inSanctuary = Sanctuary.IsSanctuary(session.State.MapId),
                recap = Managers.Economy.FightRecaps.ViewOf(me),
            };
        }
    }
}
