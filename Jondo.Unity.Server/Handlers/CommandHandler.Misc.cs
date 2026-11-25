using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Managers.Economy;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The smaller Stasis pieces: «.donjon options» (the leader's «salle complète»,
    /// Managers.DungeonRules) and the F9 window's Stasis tabs -- zone gauge, counter, tides,
    /// tiers, events and tokens, Fissure -- which ask the chat commands themselves and show what
    /// they answer (/api/stasis/panel and /api/stasis/act, Network/ControlApiPanel.cs).
    /// </summary>
    /// <remarks>
    /// The tabs do not copy the commands' rules: a tab runs the command on the player's turn with
    /// its notices captured (<see cref="_panelCapture"/>, read by NotifyAsync) instead of sent to
    /// the chat, and adds the buttons that make sense for it (a buff to buy, a target to choose,
    /// a key to take). Every action is one of the commands of <see cref="PanelCommands"/>, typed
    /// as the player would type it, so the role check, the costs in Jondo Coins and the refusals
    /// are the chat's. The texts are French, as the commands'.
    /// </remarks>
    public static partial class CommandHandler
    {
        // ─── .donjon options ────────────────────────────────────────────────────────────────

        private static async Task DungeonOptionsAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            var words = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
            if (words.Length == 0 || words[0] != "options")
            {
                await NotifyAsync(stream, Usage(".donjon"), channel, accountId);
                return;
            }
            long me = GameState.CharacterId;
            if (words.Length >= 3 && words[1] is "salle" or "complete" or "salle-complete")
            {
                bool? on = words[2] switch { "on" or "oui" or "1" => true, "off" or "non" or "0" => false, _ => null };
                if (on == null) { await NotifyAsync(stream, Usage(".donjon"), channel, accountId); return; }
                var party = Parties.Of(me);
                if (party != null && party.LeaderId != me)
                {
                    await NotifyAsync(stream, "Seul le chef du groupe choisit les options de donjon.", channel, accountId);
                    return;
                }
                DungeonRules.SetFullRoom(me, on.Value);
            }
            else if (words.Length > 1)
            {
                await NotifyAsync(stream, Usage(".donjon"), channel, accountId);
                return;
            }
            await NotifyAsync(stream, DungeonOptionsText(me), channel, accountId);
        }

        internal static string DungeonOptionsText(long characterId)
            => $"Options de donjon : salle complète {(DungeonRules.FullRoom(characterId) ? "OUI" : "NON")} " +
               "(oui : chaque salle se bat comme à 4, le boss au grade de 4 personnes, plus dur et plus rentable ; choisi au début du combat). " +
               "Mécaniques de boss : toujours « avec » sur ce serveur. " +
               ".donjon options salle on|off pour changer (chef du groupe).";

        // ─── The F9 window's Stasis tabs ────────────────────────────────────────────────────

        /// <summary>Set while a tab runs a command: NotifyAsync puts its texts here instead of the chat.</summary>
        private static readonly AsyncLocal<List<string>?> _panelCapture = new();

        /// <summary>Each tab and the commands it shows, in order.</summary>
        internal static readonly IReadOnlyDictionary<string, (string Title, string[] Lines)> PanelTabs =
            new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
            {
                ["jauge"] = ("Jauge de zone", new[] { ".jauge" }),
                ["comptoir"] = ("Comptoir de zone", new[] { ".comptoir" }),
                ["maree"] = ("Marées de Stasis", new[] { ".maree" }),
                ["paliers"] = ("Paliers et donjons", new[] { ".paliers", ".donjons", ".donjon options" }),
                ["event"] = ("Événements et jetons", new[] { ".event", ".jetons" }),
                ["fissure"] = ("Fissure", new[] { ".fissure" }),
                // Widgets only (CommandHandler.PanelViews.cs): no chat answer to show.
                ["stasis"] = ("Forge Stasis", Array.Empty<string>()),
                ["alertes"] = ("Alertes", Array.Empty<string>()),
            };

        /// <summary>The commands a tab's button may type.</summary>
        internal static readonly HashSet<string> PanelCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jauge", ".comptoir", ".maree", ".paliers", ".donjons", ".donjon", ".event", ".jetons", ".fissure",
            ".cle", ".hardcore", ".stasis", ".fusion", ".raffiner",
        };

        /// <summary>A chat line run as the session, its notices captured. Null stream: nothing runs.</summary>
        internal static async Task<List<string>> CapturedAsync(GameSession session, string line)
        {
            var captured = new List<string>();
            if (session.Stream == null) return captured;
            var before = _panelCapture.Value;
            _panelCapture.Value = captured;
            try
            {
                await TryHandleAsync(session.Stream, line, 0, session.AccountId);
            }
            catch (Exception ex)
            {
                captured.Add("La commande a échoué : " + ex.Message);
            }
            finally
            {
                _panelCapture.Value = before;
            }
            return captured;
        }

        /// <summary>
        /// A button of a tab: its command, typed. "ok" and what it answered, or a refusal code
        /// when the command is not one of the tabs'.
        /// </summary>
        internal static async Task<(string Result, List<string> Said)> PanelActAsync(GameSession session, string command, string args)
        {
            command = (command ?? "").Trim();
            if (!command.StartsWith(".", StringComparison.Ordinal)) command = "." + command;
            if (!PanelCommands.Contains(command)) return ("commande", new List<string>());
            // «.maree aller <sous-zone>»: the window's «Y aller», which the chat's .maree does not have.
            string[] words = (args ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (command.Equals(".maree", StringComparison.OrdinalIgnoreCase) && words.Length == 2 && words[0] == "aller"
                && int.TryParse(words[1], out int tideZone))
                return ("ok", new List<string> { await TideGoAsync(session, tideZone) });
            string line = (command + " " + (args ?? "").Trim()).Trim();
            return ("ok", await CapturedAsync(session, line));
        }

        /// <summary>What a tab shows: its commands' answers, its buttons, the Jondo Coins held.</summary>
        internal static async Task<object?> PanelViewAsync(GameSession session, string tab, IReadOnlyList<string>? said = null, bool structured = false)
        {
            if (!PanelTabs.TryGetValue(tab ?? "", out var def)) return null;
            var lines = new List<string>();
            // JondoFix sends «structured»: it draws the view and the chat answers are not run.
            if (!structured)
                foreach (string command in def.Lines) lines.AddRange(await CapturedAsync(session, command));
            return new
            {
                tab = tab!.ToLowerInvariant(),
                title = def.Title,
                coins = OrbesHeld(),
                coinLabel = OrbeLabel,
                said = said ?? Array.Empty<string>(),
                lines,
                actions = PanelActions(session, tab!.ToLowerInvariant()),
                // The widgets of the tab (CommandHandler.PanelViews.cs); older clients ignore it.
                view = PanelStructured(session, tab!.ToLowerInvariant()),
            };
        }

        /// <summary>The buttons of a tab: label, command and its words.</summary>
        private static List<object> PanelActions(GameSession session, string tab)
        {
            var actions = new List<object>();
            void Add(string label, string command, string args) => actions.Add(new { label, command, args });
            try
            {
                switch (tab)
                {
                    case "jauge":
                    {
                        var zone = Zones.Of(Zones.SubAreaOf(session.State.MapId));
                        if (zone == null) break;
                        for (int i = 0; i < zone.Equipment.Count && i < 24; i++)
                            Add("Cibler : " + NameOfItem(zone.Equipment[i]), ".jauge", "cible " + (i + 1));
                        break;
                    }
                    case "comptoir":
                    {
                        var zone = Zones.Of(Zones.SubAreaOf(session.State.MapId));
                        if (zone == null) break;
                        foreach (string buff in ZoneCounter.All)
                            Add($"{ZoneCounter.Label(buff)} ({OrbePrice(ZoneCounter.Price(buff, zone.Level)):N0} {OrbeLabel})", ".comptoir", buff);
                        break;
                    }
                    case "paliers":
                        Add(DungeonRules.FullRoom(session.CharacterId) ? "Salle complète : désactiver" : "Salle complète : activer",
                            ".donjon", DungeonRules.FullRoom(session.CharacterId) ? "options salle off" : "options salle on");
                        break;
                    case "event":
                        Add("Aller au Poutch", ".event", "aller");
                        foreach (var article in StasisEvents.Current.Shop.Where(a => !a.Rune))
                            Add($"Acheter {article.Name} ({article.Price} jetons)", ".jetons", "acheter " + article.Id);
                        break;
                    case "fissure":
                        foreach (var band in Fissure.Current.Bands)
                            Add($"Partir : {band.Name}", ".fissure", band.Id);
                        Add("Abandonner l'expédition", ".fissure", "quitter");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Panel] The buttons of «{tab}» could not be built: {ex.Message}");
            }
            return actions;
        }
    }
}
