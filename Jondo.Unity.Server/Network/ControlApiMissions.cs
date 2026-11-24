using System;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The JondoFix «Progression» window's «Missions» tab: /api/stasis/missions (the Système as
    /// it stands), /api/stasis/missions/go (GO) and /api/stasis/missions/claim (Récupérer).
    /// </summary>
    /// <remarks>
    /// As the dons' routes (ControlApiProgression.cs): the session's own token, the account's own
    /// character, every role; the actions run on the session's turn (UnoCadaVez) and in its
    /// SessionContext, because GO sends a map change and Récupérer the Orbes and the experience,
    /// and those must not cross a reply the client is being given. Every answer is the whole
    /// Système again, plus for a claim the lines to show; a refusal is a 409 whose error is the
    /// French text, which the window shows as it comes.
    /// </remarks>
    public static partial class ControlApi
    {
        private static Respuesta MissionsState(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            using (SessionContext.Push(me)) return Bien(CommandHandler.MissionsView(me));
        }

        private static Respuesta MissionsGo(string cuerpo, long account)
            => OnMissionTurn(account, me =>
            {
                string result = CommandHandler.MissionGoAsync(me, CommandHandler.SlotFromWord(Texto(cuerpo, "slot"))).GetAwaiter().GetResult();
                return (result, result == "ok" ? "En route vers l'objectif !" : "");
            });

        private static Respuesta MissionsClaim(string cuerpo, long account)
            => OnMissionTurn(account, me =>
            {
                string slot = Texto(cuerpo, "slot");
                var (result, lines) = CommandHandler.ClaimMissionAsync(me, slot.Length == 0 ? null : CommandHandler.SlotFromWord(slot)).GetAwaiter().GetResult();
                return (result, string.Join("\n", lines));
            });

        private static Respuesta OnMissionTurn(long account, Func<GameSession, (string Result, string Said)> action)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            if (!me.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(me))
                {
                    var (result, said) = action(me);
                    if (result != "ok") return Mal(409, CommandHandler.RefusalOfMissions(result));
                    return Bien(new { said, missions = CommandHandler.MissionsView(me) });
                }
            }
            finally
            {
                me.UnoCadaVez.Release();
            }
        }
    }
}
