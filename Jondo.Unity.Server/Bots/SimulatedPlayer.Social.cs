using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// A bot answers a real player who whispers to it (kth) or names it on its map (kti), with
    /// content/bots/chatter.json's whisper and mention lines, and moves as players do: it waves
    /// when it greets, dances or laughs after a fight won, sits down now and then (khl).
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>The attitudes it plays: sit, laugh, dance, hello, wave.</summary>
        internal const int Sit = 1, Laugh = 24, Dance = 27, Hello = 49, Wave = 78;

        private (string To, string Text, DateTime At) _lastWhisper;
        private DateTime _lastAnswer = DateTime.MinValue;

        /// <summary>
        /// An attitude played, as the client plays it (khl { f1 }). The few a bot uses are taken as
        /// known: a character starts with four and these are a player's commonest.
        /// </summary>
        private async Task EmoteAsync(int emote)
        {
            if (Session is not { } session || session.State.IsInFight) return;
            if (!session.State.Emotes.Contains(emote)) session.State.Emotes.Add(emote);
            await SendAsync(Op.Khl, Pb.New().Var(1, emote).Build());
        }

        /// <summary>
        /// A whisper (kth { f5 the other, f6 his name, f7 the text }): from a real player, an answer
        /// after a moment, the way somebody reading it would. Its own copy of what it whispered is
        /// the same message, and is not answered.
        /// </summary>
        private async Task WhisperedAsync(byte[] payload)
        {
            long from = Var(payload, 5);
            string name = Str(payload, 6), text = Str(payload, 7);
            if (from == 0 || name.Length == 0 || text.Length == 0) return;
            if (_lastWhisper.To == name && _lastWhisper.Text == text && DateTime.UtcNow - _lastWhisper.At < TimeSpan.FromSeconds(5)) return;
            var other = SessionRegistry.FindByCharacter(from);
            if (other == null || SimulatedPlayers.IsSimulated(other.AccountId)) return;
            if (DateTime.UtcNow - _lastAnswer < TimeSpan.FromSeconds(3)) return;
            _lastAnswer = DateTime.UtcNow;

            await Task.Delay(_dice.Next(2000, 6000));
            if (_closed || Session is not { } session) return;
            string? line = LineOf(KindOfWhisper(text), session, name, name);
            if (string.IsNullOrWhiteSpace(line)) return;
            _lastWhisper = (name, line, DateTime.UtcNow);
            // ktb { f1 the text, f5 whom to }, as the client whispers.
            await SendAsync(Op.Ktb, Pb.New().Str(1, line).Str(5, name).Build());
        }

        /// <summary>
        /// A line on its map (kti { f4 who, f5 his id, f7 the text, f9 the channel }) that names it,
        /// from a real player: an answer on the same map after a moment.
        /// </summary>
        private async Task HeardAsync(byte[] payload)
        {
            long from = Var(payload, 5);
            if (from == 0 || from == _characterId || Var(payload, 9) != ChatChannels.General) return;
            string text = Str(payload, 7);
            string shortName = _name.StartsWith(SimulatedPlayers.NamePrefix) ? _name[SimulatedPlayers.NamePrefix.Length..] : _name;
            if (text.IndexOf(_name, StringComparison.OrdinalIgnoreCase) < 0
                && !System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(shortName)}\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return;
            var other = SessionRegistry.FindByCharacter(from);
            if (other == null || SimulatedPlayers.IsSimulated(other.AccountId)) return;
            if (DateTime.UtcNow - _lastAnswer < TimeSpan.FromSeconds(3)) return;
            _lastAnswer = DateTime.UtcNow;

            await Task.Delay(_dice.Next(1500, 4500));
            if (_closed || Session is not { } session) return;
            string kind = KindOfWhisper(text);
            await SayAsync(ChatChannels.General, LineOf(kind == "whisper" ? "mention" : kind, session, Str(payload, 4)));
            if (kind == "whisper_hello") await EmoteAsync(_dice.Next(2) == 0 ? Wave : Hello);
        }

        /// <summary>Which of the file's whisper lists answers a text: a greeting, a party, a trade, or anything.</summary>
        internal static string KindOfWhisper(string text)
        {
            string t = text.ToLowerInvariant();
            if (new[] { "groupe", "grp", "party", "invit", "donjon", "dj", "farm", "aide", "help" }.Any(t.Contains)) return "whisper_party";
            if (new[] { "vend", "achat", "achète", "achete", "prix", "kamas", "hdv", "échange", "echange" }.Any(t.Contains)) return "whisper_trade";
            if (new[] { "salut", "bonjour", "slt", "yo", "cc", "coucou", "hello", "hey", "bjr", "wesh" }.Any(w => System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{w}\b"))) return "whisper_hello";
            return "whisper";
        }

        /// <summary>A text field of a payload, empty when absent.</summary>
        private static string Str(byte[] payload, int field)
        {
            try
            {
                var f = ProtoMessage.Parse(payload).Fields.FirstOrDefault(x => x.FieldNumber == field && x.WireType == 2);
                return f == null ? "" : System.Text.Encoding.UTF8.GetString(f.BytesValue);
            }
            catch { return ""; }
        }
    }
}
