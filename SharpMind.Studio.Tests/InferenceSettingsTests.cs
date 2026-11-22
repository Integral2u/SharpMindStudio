using SharpMind.Studio.Core.Inference;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class InferenceSettingsTests
{
    [Fact]
    public void LegacySpeculativeInt8SystemDefaults_AreMigratedOnce()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"studio-inference-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """
                {
                  "schema": "studio-inference/1",
                  "system": {
                    "generator": "SpeculativeGeneratorBuilder",
                    "cache": "Int8KVCacherBuilder"
                  }
                }
                """);

            var store = new InferenceSettingsStore(path);

            Assert.Equal("StandardGeneratorBuilder", store.Document.System.Generator);
            Assert.Equal("KVCacherBuilder", store.Document.System.Cache);
            Assert.True(store.Document.InferenceOptimizationMigrated);

            // After the marker is set, an explicit choice of the old pair is
            // preserved on the next load.
            store.SetSystem(store.Document.System with
            {
                Generator = "SpeculativeGeneratorBuilder",
                Cache = "Int8KVCacherBuilder",
            });

            var reloaded = new InferenceSettingsStore(path);
            Assert.Equal("SpeculativeGeneratorBuilder", reloaded.Document.System.Generator);
            Assert.Equal("Int8KVCacherBuilder", reloaded.Document.System.Cache);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CustomSystemDefaults_AreLeftAlone()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"studio-inference-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """
                {
                  "schema": "studio-inference/1",
                  "system": {
                    "generator": "StandardGeneratorBuilder",
                    "cache": "PagedKVCacherBuilder"
                  }
                }
                """);

            var store = new InferenceSettingsStore(path);

            Assert.Equal("StandardGeneratorBuilder", store.Document.System.Generator);
            Assert.Equal("PagedKVCacherBuilder", store.Document.System.Cache);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MedusaHeadsPath_IsStablePerModelAndHeadCount()
    {
        string first = StudioMedusaPaths.DefaultFor(@"C:\models\Qwen3.5-2B.Q4_K_M.gguf", 3);
        string again = StudioMedusaPaths.DefaultFor(@"C:\models\Qwen3.5-2B.Q4_K_M.gguf", 3);
        string four = StudioMedusaPaths.DefaultFor(@"C:\models\Qwen3.5-2B.Q4_K_M.gguf", 4);

        Assert.Equal(first, again);
        Assert.NotEqual(first, four);
        Assert.EndsWith("Qwen3.5-2B.Q4_K_M-h3.mdh", first);
    }
}
