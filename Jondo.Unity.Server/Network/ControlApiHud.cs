using System;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// /api/stasis/hud: the JondoFix HUD panels' poll (zone gauge bar, «Le Système»). Read-only and
    /// cached per character (CommandHandler.HudView.cs); it does not wait for the session's turn,
    /// so a busy character never makes the poll queue behind it.
    /// </summary>
    public static partial class ControlApi
    {
        private static Respuesta HudState(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            try
            {
                using (SessionContext.Push(me))
                    return Bien(CommandHandler.HudView(me, (Texto(cuerpo, "fresh") ?? "") == "1"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] stasis/hud failed: {ex.Message}");
                return Mal(500, ex.Message);
            }
        }
    }
}
