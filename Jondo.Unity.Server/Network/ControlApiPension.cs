using System;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix window's Pension and Cartes tabs: /api/pension/state (the whole view) and
    /// /api/pension/do { "command": "pension"|"caisse"|"cartes", "args": "..." }, which runs the
    /// chat command's own action on the session's turn and answers the view with its text.
    /// </summary>
    public static partial class ControlApi
    {
        private static Respuesta PensionState(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            using (SessionContext.Push(me)) return Bien(CommandHandler.PensionView(me));
        }

        private static Respuesta PensionDo(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    string args = Texto(cuerpo, "args") ?? "";
                    string text = (Texto(cuerpo, "command") ?? "").ToLowerInvariant() switch
                    {
                        "pension" => CommandHandler.PensionCommandAsync(me, args).GetAwaiter().GetResult(),
                        "caisse" => CommandHandler.CrateCommandAsync(me, args).GetAwaiter().GetResult(),
                        "cartes" => CommandHandler.CardsCommandAsync(me, args).GetAwaiter().GetResult(),
                        _ => "Commande inconnue.",
                    };
                    return Bien(CommandHandler.PensionView(me, text));
                }
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
