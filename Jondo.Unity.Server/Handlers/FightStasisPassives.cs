using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using static Jondo.Unity.Server.Network.NetworkEnvelope;
using static Jondo.Protocol.NetworkMessage;
using Passive = Jondo.Unity.Server.Managers.StasisPassives;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The Stasis class passives' hooks in the fight (Managers/Stasis/StasisPassives.cs holds the
    /// rules): armed on the player at the fight's start -- PvM only --, fed by the turn starts and
    /// ends, the blows, the heals, the kills, the casts' outcomes, the walks, the summons and the
    /// deaths, and what they grant told to the board here.
    /// </summary>
    public static partial class FightHandler
    {
        /// <summary>
        /// The character's chosen passive (dons, level 50) armed on his fighter. Nothing in PvP:
        /// on Stasis the passives are «PvM uniquement».
        /// </summary>
        internal static void StasisPassiveArm(FightInstance fight, Fighter fighter, long characterId)
        {
            try
            {
                if (fight == null || fighter == null) return;
                if (fight.EsPvp || characterId <= 0)
                {
                    Passive.Arm(fighter, Passive.Kind.None);
                    return;
                }
                var content = ProgressionContent.Current;
                var profile = ProgressionStore.Of(characterId);
                string? card;
                lock (profile.Gate) card = Passive.ChosenCard(profile, content);
                var kind = card == null ? Passive.Kind.None : Passive.KindOf(card, content);
                Passive.Arm(fighter, kind);
                if (kind != Passive.Kind.None)
                    Program.LogDebug($"[StasisPassive] {fighter.Id} fights with the {kind} passive ({card}).");
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[StasisPassive] Could not arm the passive of {characterId}: {ex.Message}");
            }
        }

        private static List<Passive.State> PassiveHolders(FightInstance fight)
            => Passive.Holders(fight.Todos).ToList();

        /// <summary>Any fighter's turn start: grants run out, and a holder's turn-start rule.</summary>
        internal static async Task StasisPassiveTurnStartAsync(NetworkStream stream, FightInstance fight, Fighter fighter)
        {
            var holders = PassiveHolders(fight);
            if (holders.Count == 0 || fighter == null) return;
            var actions = Passive.TurnStart(holders, fighter, fight.RoundNumber, () => SurroundingsOf(fight, fighter));
            await StasisPassiveActAsync(stream, fight, fighter, actions, sequence: true);
        }

        private static Passive.Surroundings SurroundingsOf(FightInstance fight, Fighter me)
        {
            var enemies = fight.Enemigos(me.Id).Where(e => e != null && e.IsAlive).ToList();
            int inSight = 0, outOfSight = 0, adjacent = 0, atRange = 0, nearest = 0;
            foreach (var e in enemies)
            {
                int d = MapGeometry.Distance(me.CellId, e.CellId);
                if (nearest == 0 || d < nearest) nearest = d;
                if (d <= 1) adjacent++; else atRange++;
                bool seen = !Managers.MonsterTactics.IsInvisible(e)
                            && MapGeometry.HasLineOfSight(me.CellId, e.CellId, cell => BlocksSight(fight, cell));
                if (seen) inSight++; else outOfSight++;
            }
            return new Passive.Surroundings
            {
                EnemiesInSight = inSight, EnemiesOutOfSight = outOfSight,
                EnemiesAdjacent = adjacent, EnemiesAtRange = atRange, NearestEnemy = nearest,
            };
        }

        /// <summary>A holder's turn end (Féca, Pandawa, Enutrof).</summary>
        internal static async Task StasisPassiveTurnEndAsync(NetworkStream stream, FightInstance fight, Fighter ending)
        {
            var mine = Passive.Of(ending);
            if (mine == null) return;
            var actions = Passive.TurnEnd(mine, fight.RoundNumber, fight.Aliados(ending.Id));
            await StasisPassiveActAsync(stream, fight, ending, actions, sequence: true);
        }

        /// <summary>
        /// A blow worked out (before the shield): the passives' % on it, and the stacks it feeds.
        /// The damage to deal.
        /// </summary>
        internal static int StasisPassiveDamage(FightInstance fight, Fighter caster, Fighter target, int damage, int element)
        {
            if (damage <= 0 || caster == null || target == null) return damage;
            var holders = PassiveHolders(fight);
            if (holders.Count == 0) return damage;
            int distance = MapGeometry.Distance(caster.CellId, target.CellId);
            int percent = Passive.DamagePercent(holders, caster, target, distance, element);
            int dealt = Passive.Apply(damage, percent);
            if (percent != 0)
                Program.LogDebug($"[StasisPassive] {caster.Id} on {target.Id}: {percent:+#;-#;0}% from the passives, {damage} becomes {dealt}.");
            if (dealt > 0) Passive.BlowLanded(holders, caster, target, distance, element, fight.RoundNumber);
            return dealt;
        }

        /// <summary>A heal of <paramref name="caster"/>: the passives' % on it.</summary>
        internal static int StasisPassiveHeal(FightInstance fight, Fighter caster, int points)
        {
            if (points <= 0 || caster == null || fight == null) return points;
            var holders = PassiveHolders(fight);
            return holders.Count == 0 ? points : Passive.Apply(points, Passive.HealPercent(holders, caster));
        }

        /// <summary>
        /// A blow about to take <paramref name="target"/>'s last life: true when a Sacrieur's passive
        /// saves him -- the blow is then taken back whole.
        /// </summary>
        internal static async Task<bool> StasisPassiveSavesAsync(NetworkStream stream, FightInstance fight, Fighter caster, Fighter target)
        {
            var holders = PassiveHolders(fight);
            if (holders.Count == 0) return false;
            var actions = Passive.MortalBlow(holders, target, fight.RoundNumber);
            if (actions.Count == 0) return false;
            Program.LogDebug($"[StasisPassive] {target.Id} survives a mortal blow of {caster?.Id} (Sacrieur).");
            await StasisPassiveActAsync(stream, fight, target, actions, sequence: false);
            return true;
        }

        /// <summary>An enemy finished (Sram).</summary>
        internal static async Task StasisPassiveKillAsync(NetworkStream stream, FightInstance fight, Fighter killer, Fighter dead)
        {
            var holders = PassiveHolders(fight);
            if (holders.Count == 0 || killer == null) return;
            await StasisPassiveActAsync(stream, fight, killer, Passive.Killed(holders, killer, dead, fight.RoundNumber), sequence: false);
        }

        /// <summary>A fighter died (Forgelance's and Osamodas' summons; what was put on the dead).</summary>
        internal static void StasisPassiveDeath(FightInstance fight, Fighter dead)
        {
            if (dead == null) return;
            var holders = PassiveHolders(fight);
            if (holders.Count > 0) Passive.Died(holders, dead, fight.RoundNumber);
        }

        /// <summary>Steps walked (Enutrof).</summary>
        internal static void StasisPassiveWalked(FightInstance fight, Fighter mover, int steps)
        {
            if (steps <= 0 || mover == null) return;
            var holders = PassiveHolders(fight);
            if (holders.Count > 0) Passive.Walked(holders, mover, steps, fight.RoundNumber);
        }

        /// <summary>A critical cast (Ecaflip).</summary>
        internal static void StasisPassiveCritical(FightInstance fight, Fighter caster)
        {
            var holders = PassiveHolders(fight);
            if (holders.Count > 0) Passive.Critical(holders, caster, fight.RoundNumber);
        }

        /// <summary>A creature summoned (Osamodas, Forgelance).</summary>
        internal static void StasisPassiveSummoned(FightInstance fight, Fighter summoner, Fighter summon)
        {
            if (summoner == null || summon == null) return;
            var holders = PassiveHolders(fight);
            if (holders.Count == 0) return;
            var around = fight.Enemigos(summoner.Id).Where(e => e != null && e.IsAlive && MapGeometry.Distance(e.CellId, summon.CellId) <= 1).ToList();
            Passive.Summoned(holders, summoner, around, fight.RoundNumber);
        }

        /// <summary>The effects whose outcome moves its bearer by a push, a pull or a swap (Steamer).</summary>
        private static readonly HashSet<int> PassivePushes = new()
        {
            Jondo.Unity.World.Combat.EffectSupport.Push, Jondo.Unity.World.Combat.EffectSupport.Pull,
            Jondo.Unity.World.Combat.EffectSupport.PushWithoutDamage, Jondo.Unity.World.Combat.EffectSupport.PushToTargetCell,
            Jondo.Unity.World.Combat.EffectSupport.PullToTargetCell, 1021, 8, 1023,
        };

        /// <summary>The effects that teleport or swap their bearer (Eliotrope).</summary>
        private static readonly HashSet<int> PassiveTeleports = new() { 4, 8, 1023, 1101, 1104, 1105, 1106 };

        /// <summary>The AP-removal effects whose dodge travels without a characteristic (as the dodge frame reads them).</summary>
        private static readonly HashSet<int> PassiveApRemovals = new() { 1079, 101, 84, 440 };

        /// <summary>One outcome of a cast, before the board is told it: removals, pushes, teleports, heals.</summary>
        internal static async Task StasisPassiveOutcomeAsync(NetworkStream stream, FightInstance fight, Fighter quienLanza, Managers.Outcome c)
        {
            if (c == null || c.Sobre == null || c.Efecto == null) return;
            var holders = PassiveHolders(fight);
            if (holders.Count == 0) return;
            var caster = c.Caster ?? quienLanza;
            if (caster == null) return;
            int round = fight.RoundNumber;
            var actions = new List<Passive.PassiveAction>();

            // A removal of AP or MP tried on an enemy, landed or dodged.
            bool lost = c.Cuanto < 0 && (c.Caracteristica == ActionPointsCharacteristic || c.Caracteristica == MovementPointsCharacteristic);
            if ((lost || c.PuntosEsquivados > 0) && c.Sobre.TeamId != caster.TeamId)
            {
                bool ap = c.Caracteristica == ActionPointsCharacteristic
                          || (c.Caracteristica == 0 && PassiveApRemovals.Contains(c.Efecto.EffectId));
                actions.AddRange(Passive.RemovalAttempt(holders, caster, ap, lost ? -c.Cuanto : 0, c.Sobre, round));
            }

            if (c.Mueve && PassivePushes.Contains(c.Efecto.EffectId) && c.Sobre != caster)
                Passive.Displaced(holders, caster, round);
            if (c.Mueve && PassiveTeleports.Contains(c.Efecto.EffectId) && c.Sobre.TeamId == caster.TeamId)
                actions.AddRange(Passive.AllyTeleported(holders, caster, c.Sobre, round));

            if (c.Cura > 0) Passive.HealGiven(holders, caster, round);

            await StasisPassiveActAsync(stream, fight, caster, actions, sequence: false);
        }

        /// <summary>
        /// What the passives grant, told to the board: AP and MP given, shields with their panel
        /// row, a heal, invisibility, invulnerability. Inside a sequence of its own when asked
        /// (turn start and end); a cast's outcomes are already inside the cast's.
        /// </summary>
        private static async Task StasisPassiveActAsync(NetworkStream stream, FightInstance fight, Fighter author,
                                                        List<Passive.PassiveAction> actions, bool sequence)
        {
            if (actions == null || actions.Count == 0) return;
            async Task Work()
            {
                foreach (var a in actions)
                {
                    var who = a.Who;
                    if (who == null || !who.IsAlive && a.Kind != Passive.ActionKind.HealTo) continue;
                    switch (a.Kind)
                    {
                        case Passive.ActionKind.GiveAp:
                            who.CurrentAP += a.Amount;
                            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe, Network.FightProtocol.BuildPointsGiven(who.Id, 111, who.Id, a.Amount)));
                            break;
                        case Passive.ActionKind.GiveMp:
                            who.CurrentMP += a.Amount;
                            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe, Network.FightProtocol.BuildPointsGiven(who.Id, 128, who.Id, a.Amount)));
                            break;
                        case Passive.ActionKind.Shield:
                            await PassiveShieldAsync(fight, author, who, a.Amount, a.UntilRound);
                            break;
                        case Passive.ActionKind.HealTo:
                        {
                            int healed = Math.Max(0, a.Amount - who.CurrentHP);
                            if (healed <= 0) break;
                            who.CurrentHP += healed;
                            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe, Network.FightProtocol.BuildHeal(author.Id, healed, who.Id)));
                            await RefrescarLaVidaAsync(stream, fight, who, author);
                            break;
                        }
                        case Passive.ActionKind.Invulnerable:
                            await GiveStateAsync(fight, who, InvulnerableState, 1);
                            break;
                        case Passive.ActionKind.Invisible:
                            await PassiveInvisibleAsync(fight, who, a.UntilRound);
                            break;
                    }
                }
            }
            if (sequence) await InASequenceAsync(fight, author.Id, Work);
            else await Work();
        }

        /// <summary>
        /// A shield of the passives: points on the fighter and a 1040 row on his panel, as a spell's
        /// (EffectEngine's ShieldPanelEffect). The Eliotrope's replaces the last one it gave.
        /// </summary>
        private static async Task PassiveShieldAsync(FightInstance fight, Fighter author, Fighter who, int amount, int untilRound)
        {
            const int StasisPassiveShieldSpell = 0;
            if (Passive.Of(author)?.Kind == Passive.Kind.Eliotrope)
                foreach (var old in who.Buffs.Puestos.Where(b => b.EffectId == Fighter.ShieldRow && b.Quien == author.Id && b.HechizoOrigen == StasisPassiveShieldSpell).ToList())
                    if (who.Buffs.QuitarFila(old))
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya, Network.FightProtocol.BuildBuffGone(who.Id, old.Numero)));
            who.Escudar(amount, untilRound);
            var row = who.Buffs.Poner(new Buff
            {
                EffectId = Fighter.ShieldRow, Cuanto = amount, MaxStacks = -1, HechizoOrigen = StasisPassiveShieldSpell, NivelOrigen = 1,
                Quien = author.Id, Disparador = Managers.EffectEngine.AlLanzar, CaducaEnRonda = untilRound, EmpiezaEnRonda = fight.RoundNumber,
            }, fight.SiguienteEmbrujo);
            var (categoria, boost) = DatabaseManager.EffectFamily(row.EffectId);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm, Network.FightProtocol.BuildBuff(
                who.Id, author.Id, row.Numero, row.EffectId, row.EffectUid, amount, 0, 0, 0, row.Disparador, untilRound, 0,
                Network.FightProtocol.FamiliaDelEmbrujo(row.EffectId, categoria, boost), 1, false)));
            Program.LogDebug($"[StasisPassive] {who.Id} shielded for {amount} until round {untilRound} by {author.Id}.");
        }

        /// <summary>Invisible until a round, as a spell's 150 row makes him (direct damage takes it off).</summary>
        private static async Task PassiveInvisibleAsync(FightInstance fight, Fighter who, int untilRound)
        {
            var row = who.Buffs.Poner(new Buff
            {
                EffectId = Jondo.Unity.World.Combat.EffectSupport.Visibility, Cuanto = 1, Quien = who.Id,
                Disparador = Managers.EffectEngine.AlLanzar, CaducaEnRonda = untilRound, EmpiezaEnRonda = fight.RoundNumber,
            }, fight.SiguienteEmbrujo);
            var (categoria, boost) = DatabaseManager.EffectFamily(row.EffectId);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm, Network.FightProtocol.BuildBuff(
                who.Id, who.Id, row.Numero, row.EffectId, row.EffectUid, 1, 0, 0, 0, row.Disparador, untilRound, 0,
                Network.FightProtocol.FamiliaDelEmbrujo(row.EffectId, categoria, boost), 1, false)));
            who.LastSeenCell = who.CellId;
            who.HeldByFoesAt = who.CellId;
            who.ToldInvisible = true;
            await ASuBandoAsync(fight, who.TeamId, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildVisibility(who.Id, who.Id, Network.FightProtocol.Hidden)));
            await SalvoASuBandoAsync(fight, who.TeamId, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildVisibility(who.Id, who.Id, Network.FightProtocol.Unseen)));
        }
    }
}
