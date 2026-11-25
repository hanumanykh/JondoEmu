using System;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix F9 window's «Éveil» tab: /api/stasis/eveil { "args": "" | "&lt;dofus&gt; commencer"
    /// | "&lt;dofus&gt; defi" } runs the .eveil command for the account's character and answers its
    /// text with the tab's state (CommandHandler.EveilView).
    /// </summary>
    /// <remarks>
    /// As the hub's route (ControlApiHub.cs): the session's own token, the account's own
    /// character, on its turn (UnoCadaVez) and in its SessionContext, since the sacrifice takes
    /// items from the bag and the challenge opens a fight; and only the Éveil's command.
    /// </remarks>
    public static partial class ControlApi
    {
        private static Respuesta EveilRoute(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            string args = (Texto(cuerpo, "args") ?? "").Trim();
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    string text = args.Length == 0 ? "" : CommandHandler.EveilAsync(me, args).GetAwaiter().GetResult();
                    return Bien(CommandHandler.EveilView(me, text));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] stasis/eveil failed: {ex.Message}");
                return Mal(500, ex.Message);
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
