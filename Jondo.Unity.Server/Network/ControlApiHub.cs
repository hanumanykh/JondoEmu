using System;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix F9 window's «Sanctuaire» tab: /api/stasis/hub runs one hub command
    /// (.sanctuaire, .retour, .marchand, .fabriquer, .devis) for the account's character and
    /// answers its text with the tab's state (CommandHandler.HubView).
    /// </summary>
    /// <remarks>
    /// As the progression routes: the session's own token, the account's own character, on its
    /// turn and in its SessionContext, and only the hub's commands -- the body names one of them
    /// and its arguments, never anything else of the chat's.
    /// </remarks>
    public static partial class ControlApi
    {
        private static Respuesta Hub(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            string command = (Texto(cuerpo, "command") ?? "").Trim();
            string args = (Texto(cuerpo, "args") ?? "").Trim();
            if (command.Length > 0 && !CommandHandler.HubCommands.Contains(command)) return Mal(400, "command");
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    string text = command.Length == 0 ? "" : CommandHandler.HubAsync(me, command, args).GetAwaiter().GetResult();
                    return Bien(CommandHandler.HubView(me, text));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] stasis/hub failed: {ex.Message}");
                return Mal(500, ex.Message);
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
