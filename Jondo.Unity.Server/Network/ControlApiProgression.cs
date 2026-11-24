using System;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix «Progression» window's side of the control API: /api/stasis/state, pick,
    /// reroll, repick, research, ascend and evolve -- the dons, the research tree, the Ascension and
    /// the Evolution of the account's character in the world.
    /// </summary>
    /// <remarks>
    /// As the Heroes' and the Cazacabezas's: asked with the session's own token
    /// (JONDO_PLAYER_TOKEN), for the account's own character only, open to every role. Each action
    /// runs on the session's turn (UnoCadaVez, as /api/personaje does) and in its SessionContext,
    /// so the packets it sends cannot cross a reply the client is being given, and every rule is
    /// CommandHandler.Progression.cs's, the same the chat commands use. Every answer is the whole
    /// state again: the window redraws from it and never keeps a copy of its own. The ascend and
    /// evolve routes want "confirm": true -- the window's confirmation is what sends it.
    /// </remarks>
    public static partial class ControlApi
    {
        private static Respuesta ProgressionState(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            using (SessionContext.Push(me)) return Bien(CommandHandler.ProgressionView(me));
        }

        private static Respuesta ProgressionPick(string cuerpo, long account)
            => OnTurn(account, me => CommandHandler.PickDonAsync(me, (int)Numero(cuerpo, "card"), Texto(cuerpo, "stat")));

        private static Respuesta ProgressionReroll(long account)
            => OnTurn(account, CommandHandler.RerollDonAsync);

        private static Respuesta ProgressionRepick(string cuerpo, long account)
            => OnTurn(account, me => CommandHandler.RepickDonAsync(me, Texto(cuerpo, "from"), Texto(cuerpo, "to")));

        private static Respuesta ProgressionResearch(string cuerpo, long account)
            => OnTurn(account, me => CommandHandler.BuyResearchAsync(me, Texto(cuerpo, "id")));

        private static Respuesta ProgressionAscend(string cuerpo, long account)
            => !cuerpo.Contains("\"confirm\":true", StringComparison.Ordinal) && !cuerpo.Contains("\"confirm\": true", StringComparison.Ordinal)
                ? Mal(400, "confirmation")
                : OnTurn(account, async me => (await CommandHandler.AscendAsync(me)).Result);

        private static Respuesta ProgressionEvolve(string cuerpo, long account)
            => !cuerpo.Contains("\"confirm\":true", StringComparison.Ordinal) && !cuerpo.Contains("\"confirm\": true", StringComparison.Ordinal)
                ? Mal(400, "confirmation")
                : OnTurn(account, async me => (await CommandHandler.EvolveAsync(me)).Result);

        /// <summary>
        /// An action on the character's turn and in its context; then the whole state, or a 409
        /// whose error is the refusal's French text (the window shows it as it comes).
        /// </summary>
        private static Respuesta OnTurn(long account, Func<GameSession, Task<string>> action)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    string result = action(me).GetAwaiter().GetResult();
                    if (result != "ok") return Mal(409, Managers.Dons.RefusalText(result));
                    return Bien(CommandHandler.ProgressionView(me));
                }
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
