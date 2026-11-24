using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix F9 window's Stasis tabs (zone gauge, counter, tides, tiers, events and tokens,
    /// Fissure): /api/stasis/panel answers a tab -- what its chat commands say, its buttons and the
    /// Jondo Coins held --, /api/stasis/act presses one of its buttons and answers the tab again
    /// with what the command said (CommandHandler.Misc.cs).
    /// </summary>
    /// <remarks>
    /// As the progression routes: the session's own token, the account's own character, on its
    /// turn and in its SessionContext; a button only types one of CommandHandler.PanelCommands.
    /// </remarks>
    public static partial class ControlApi
    {
        /// <summary>
        /// The Stasis walk bonus out of combat (§0, §21), for JondoFix's MovementSpeed: whether the
        /// character is fighting, and the percent its client walks faster by when not. The server
        /// never times a walk (WorldMoveHandler), so nothing else has to know.
        /// </summary>
        internal const int OutOfFightWalkPercent = 25;

        private static Respuesta WalkSpeed(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            return Bien(new { fight = me.State.IsInFight, percent = OutOfFightWalkPercent });
        }

        private static Respuesta Panel(string cuerpo, long account) => PanelRun(cuerpo, account, act: false);

        private static Respuesta PanelAct(string cuerpo, long account) => PanelRun(cuerpo, account, act: true);

        private static Respuesta PanelRun(string cuerpo, long account, bool act)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            string tab = (Texto(cuerpo, "tab") ?? "").Trim();
            if (!CommandHandler.PanelTabs.ContainsKey(tab)) return Mal(400, "tab");
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    List<string>? said = null;
                    if (act)
                    {
                        var (result, text) = CommandHandler.PanelActAsync(me, Texto(cuerpo, "command") ?? "", Texto(cuerpo, "args") ?? "")
                                                           .GetAwaiter().GetResult();
                        if (result != "ok") return Mal(400, "command");
                        said = text;
                    }
                    return Bien(CommandHandler.PanelViewAsync(me, tab, said).GetAwaiter().GetResult()!);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] stasis/panel failed: {ex.Message}");
                return Mal(500, ex.Message);
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
