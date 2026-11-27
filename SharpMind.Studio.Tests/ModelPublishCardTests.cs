using SharpMind.Studio.Core.HuggingFace;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ModelPublishCardTests
{
    private static ModelPublishMetadata Metadata(bool extras = false) => new(
        @"C:\models\my-model.smm",
        "gguf",
        "my-model_Q8_0.gguf",
        2_000_000_000,
        "qwen2",
        32000,
        2048,
        24,
        16,
        4,
        5632,
        32768,
        1_500_000_000,
        ["Q8_0", "F16"],
        "sha256:data",
        new Dictionary<string, string>
        {
            ["recipe"] = "demo",
            ["base"] = "acme/base",
        },
        extras ? ["skill-a.md"] : [],
        extras,
        extras ? ["plugin.dll"] : [],
        true);

    private static ModelPublishCard Card(bool includesSmm = false) => new(
        "alice/my-model",
        "My Model",
        "A compact test model.",
        "apache-2.0",
        ["demo"],
        "acme/base",
        "my-model_Q8_0.gguf",
        "Q8_0",
        includesSmm,
        includesSmm ? "my-model.smm" : null,
        new ModelPublishRunInfo(100, 100, 1.25, 1.1, 90, 1.2, 3.3, 0.8, 120));

    [Fact]
    public void Readme_HasCardFrontMatterAndTrainingDetails()
    {
        string readme = ModelPublishCardBuilder.BuildReadme(Metadata(), Card());

        Assert.StartsWith("---", readme);
        Assert.Contains("license: \"apache-2.0\"", readme);
        Assert.Contains("base_model: \"acme/base\"", readme);
        Assert.Contains("pipeline_tag: text-generation", readme);
        Assert.Contains("## Training", readme);
        Assert.Contains("best validation", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("my-model_Q8_0.gguf", readme);
        Assert.DoesNotContain("Plugin assemblies", readme);
    }

    [Fact]
    public void Readme_WarnsWhenEmbeddedContentIsIncluded()
    {
        string readme = ModelPublishCardBuilder.BuildReadme(
            Metadata(extras: true), Card(includesSmm: true));

        Assert.Contains("Plugin assemblies: plugin.dll", readme);
        Assert.Contains("Skills: skill-a.md", readme);
        Assert.Contains("embedded system prompt", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smm", readme);
    }

    [Fact]
    public void Readme_ListsOtherQuantizations()
    {
        ModelPublishCard card = Card() with
        {
            OtherArtifacts = ["my-model_Q4_K.gguf"],
        };

        string readme = ModelPublishCardBuilder.BuildReadme(Metadata(), card);

        Assert.Contains("Other model files in this repository", readme);
        Assert.Contains("my-model_Q4_K.gguf", readme);
    }

    [Fact]
    public void Readme_DoesNotExposeLocalLineagePaths()
    {
        ModelPublishMetadata metadata = Metadata() with
        {
            Lineage = new Dictionary<string, string>
            {
                ["base"] = @"C:\Users\someone\models\base.smm",
            },
        };

        string readme = ModelPublishCardBuilder.BuildReadme(metadata, Card());
        string json = ModelPublishCardBuilder.BuildMetadataJson(metadata, Card());

        Assert.DoesNotContain(@"C:\Users\someone", readme);
        Assert.DoesNotContain(@"C:\Users\someone", json);
        Assert.Contains("base.smm", readme);
        Assert.Contains("base.smm", json);
    }

    [Fact]
    public void MetadataJson_ContainsSourceAndEmbeddedSections()
    {
        string json = ModelPublishCardBuilder.BuildMetadataJson(
            Metadata(extras: true), Card(includesSmm: true));

        Assert.Contains("\"schema\": \"sharp-mind-publish/1\"", json);
        Assert.Contains("\"artifact\": \"my-model_Q8_0.gguf\"", json);
        Assert.Contains("\"architecture\": \"qwen2\"", json);
        Assert.Contains("\"plugins\":", json);
        Assert.Contains("plugin.dll", json);
    }

    [Fact]
    public void ArtifactName_UsesQuantizationSuffix()
    {
        Assert.Equal(
            "my-model_Q8_0.gguf",
            HuggingFacePublishService.DefaultArtifactName(@"C:\models\my-model.smm", "Q8_0"));
        Assert.Equal(
            "my-model_Q8_0.gguf",
            HuggingFacePublishService.DefaultArtifactName(@"C:\models\my-model_Q4_K.gguf", "Q8_0"));
    }

    [Fact]
    public void GitAttributes_TracksBothPublishedFormats()
    {
        string attributes = ModelPublishCardBuilder.BuildGitAttributes();

        Assert.Contains("*.gguf", attributes);
        Assert.Contains("*.smm", attributes);
        Assert.Contains("filter=lfs", attributes);
    }
}
