using SharpMind.Inference.Chat.PromptFormatters;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SftFormatterTests
{
    [Fact]
    public void Auto_UsesTheBaseTemplateAndFallsBackToSimple()
    {
        Assert.IsType<SimpleFormatter>(TrainingRunner.ResolveSftFormatter(null, null));
        Assert.IsType<SimpleFormatter>(TrainingRunner.ResolveSftFormatter("auto", null));
        Assert.IsType<JinjaTemplateFormatter>(
            TrainingRunner.ResolveSftFormatter("auto", "{{ messages[0]['content'] }}"));
    }

    [Fact]
    public void NamedFormatters_OverrideTheBaseTemplate()
    {
        Assert.IsType<ChatMLFormatter>(
            TrainingRunner.ResolveSftFormatter("chatml", "<|im_start|><|im_end|>"));
        Assert.IsType<SimpleFormatter>(
            TrainingRunner.ResolveSftFormatter("simple", "<|im_start|>"));
        Assert.IsType<Llama3Formatter>(
            TrainingRunner.ResolveSftFormatter("llama3", "<|im_start|>"));
    }
}
