using Jondo.Unity.Server.Bots;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class BotWhisperTests
{
    [Theory]
    [InlineData("Salut ça va ?", "whisper_hello")]
    [InlineData("tu viens en groupe pour le donjon ?", "whisper_party")]
    [InlineData("tu vends ta cape combien de kamas", "whisper_trade")]
    [InlineData("t'as vu le boss ce matin", "whisper")]
    [InlineData("yoyo", "whisper")]
    public void A_whisper_is_answered_from_the_list_its_words_call_for(string text, string kind)
        => Assert.Equal(kind, SimulatedPlayer.KindOfWhisper(text));
}
