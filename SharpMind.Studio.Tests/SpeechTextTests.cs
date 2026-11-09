using SharpMind.Studio.Core.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SpeechTextTests
{
    [Fact]
    public void ToSpeech_DropsFencedCodeBlocks()
    {
        string speech = SpeechText.ToSpeech("Here is how.\n\n```csharp\nvar x = 1;\n```\n\nDone.");

        Assert.Equal("Here is how.\nDone.", speech);
    }

    [Fact]
    public void ToSpeech_KeepsLinkLabelsAndDropsUrls()
    {
        string speech = SpeechText.ToSpeech("Read [the guide](https://example.com/a?b=c) first.");

        Assert.Equal("Read the guide first.", speech);
    }

    [Fact]
    public void ToSpeech_RemovesHeadingsAndListMarkers()
    {
        string speech = SpeechText.ToSpeech("## Steps\n- First thing\n- Second thing");

        Assert.Equal("Steps\nFirst thing\nSecond thing", speech);
    }

    [Fact]
    public void ToSpeech_RemovesEmphasisButKeepsWords()
    {
        string speech = SpeechText.ToSpeech("This is **very** important and *fast*.");

        Assert.Equal("This is very important and fast.", speech);
    }

    [Fact]
    public void ToSpeech_EmptyInput_IsEmpty()
    {
        Assert.Equal("", SpeechText.ToSpeech(""));
        Assert.Equal("", SpeechText.ToSpeech("   \n\n  "));
    }

    [Fact]
    public void ToSpeech_PunctuationOnly_IsEmpty()
    {
        Assert.Equal("", SpeechText.ToSpeech(":"));
        Assert.Equal("", SpeechText.ToSpeech("..."));
        Assert.Equal("", SpeechText.ToSpeech("—"));
    }
}
