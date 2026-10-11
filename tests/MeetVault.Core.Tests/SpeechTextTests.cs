using MeetVault.Core;
using Xunit;

namespace MeetVault.Core.Tests;

// The streaming exports are case-less (zipformer: ALL-CAPS vocabulary, vosk: all lower)
// and write no punctuation. SpeechText.SentenceCase is the single normalizer both
// streaming engines apply so the live list, the overlay and the saved DB all read as
// written English instead of a shouting caption.
public class SpeechTextTests
{
    [Theory]
    [InlineData("THE QUICK BROWN FOX JUMPED OVER THE LAZY DOG", "The quick brown fox jumped over the lazy dog")]
    [InlineData("the quick brown fox", "The quick brown fox")]
    [InlineData("hello", "Hello")]
    [InlineData("20 minutes later the room was quiet", "20 minutes later the room was quiet")] // leading number: no false capital
    [InlineData("i think i'm done", "I think I'm done")] // English pronoun I, standalone and contracted
    [InlineData("we should order more coffee", "We should order more coffee")]
    public void SentenceCase_Normalizes_Caseless_Model_Output(string input, string expected)
        => Assert.Equal(expected, SpeechText.SentenceCase(input));

    [Fact]
    public void SentenceCase_Leaves_Empty_And_NonLatin_Untouched()
    {
        Assert.Equal("", SpeechText.SentenceCase(""));
        Assert.Equal("こんにちは 世界", SpeechText.SentenceCase("こんにちは 世界"));
    }
}
