using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Tests;

public sealed class InferenceProfileTests
{
    [Fact]
    public void Resolver_WalksSessionModelFamilyThenSystem()
    {
        var system = InferenceOverrides.SystemDefaults();
        var family = new InferenceOverrides { Temperature = 0.4f, TopK = 20 };
        var model = new InferenceOverrides { TopK = 10 };
        var session = new InferenceOverrides { Temperature = 0.2f };

        ResolvedInference value = InferenceResolver.Resolve(system, family, model, session);

        Assert.Equal(0.2f, value.Temperature);
        Assert.Equal("Session", value.Sources["Temperature"]);
        Assert.Equal(10, value.TopK);
        Assert.Equal("Model", value.Sources["TopK"]);
        Assert.Equal(0.9f, value.TopP);
        Assert.Equal("System", value.Sources["TopP"]);
    }

    [Fact]
    public void Resolver_UsesFamilyBeforeSystem()
    {
        var system = InferenceOverrides.SystemDefaults();
        var family = new InferenceOverrides { MaxNewTokens = 128 };

        ResolvedInference value = InferenceResolver.Resolve(system, family, null, null);

        Assert.Equal(128, value.MaxNewTokens);
        Assert.Equal("Family", value.Sources["MaxNewTokens"]);
    }

    [Fact]
    public void Resolver_ParsesToolCallRecoveryThroughTheLayers()
    {
        var system = InferenceOverrides.SystemDefaults();
        var family = new InferenceOverrides { ToolCallRecovery = "Loose" };
        var model = new InferenceOverrides { ToolCallRecovery = "Strict" };

        ResolvedInference value = InferenceResolver.Resolve(system, family, model, null);
        Assert.Equal(ToolCallRecoveryMode.Strict, value.ToolCallRecovery);
        Assert.Equal("Model", value.Sources["ToolCallRecovery"]);

        ResolvedInference session = InferenceResolver.Resolve(
            system, family, model, new InferenceOverrides { ToolCallRecovery = "Lenient" });
        Assert.Equal(ToolCallRecoveryMode.Lenient, session.ToolCallRecovery);
        Assert.Equal("Session", session.Sources["ToolCallRecovery"]);

        ResolvedInference unknown = InferenceResolver.Resolve(
            system, null, new InferenceOverrides { ToolCallRecovery = "nonsense" }, null);
        Assert.Equal(ToolCallRecoveryMode.Lenient, unknown.ToolCallRecovery);
    }

    [Fact]
    public void FamilyDetector_UsesArchitectureAndFallsBackToHints()
    {
        Assert.Equal("qwen2", FamilyDetector.Detect("qwen2", "Qwen2 0.5B").Id);
        Assert.Equal("llama", FamilyDetector.Detect("llama", "TinyLlama").Id);
        Assert.Equal("gemma3", FamilyDetector.Detect(null, "gemma-3-270m").Id);
        Assert.Equal("unknown", FamilyDetector.Detect(null, "mystery-model").Id);
    }
}

public sealed class InferenceSettingsStoreTests
{
    [Fact]
    public void Store_PersistsAndDeletesSparseOverrides()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("inference.json");
        var store = new InferenceSettingsStore(path);

        store.SetFamily("qwen2", new InferenceOverrides { Temperature = 0.3f });
        store.SetModel(temp.Combine("models", "tiny.gguf"), new InferenceOverrides { TopK = 12 });
        store.SetWorkModel(temp.Combine("models", "tiny.gguf"), new InferenceOverrides { TopK = 7 });
        store.SetTuneSet(temp.Combine("models", "tiny.gguf"), new TuneProfileSet
        {
            ModelFingerprint = "1:2",
            Profiles = [new TuneProfile { Target = TuneTarget.Chat, Preset = TunePreset.Balanced }],
        });

        var reopened = new InferenceSettingsStore(path);
        Assert.Equal(0.3f, reopened.FamilyOverrides("qwen2")!.Temperature);
        Assert.Equal(12, reopened.ModelOverrides(temp.Combine("models", "tiny.gguf"))!.TopK);
        Assert.Equal(7, reopened.WorkModelOverrides(temp.Combine("models", "tiny.gguf"))!.TopK);
        Assert.Single(reopened.TuneSet(temp.Combine("models", "tiny.gguf"))!.Profiles);

        reopened.DeleteFamily("qwen2");
        reopened.DeleteModel(temp.Combine("models", "tiny.gguf"));
        reopened.DeleteWorkModel(temp.Combine("models", "tiny.gguf"));
        Assert.Null(reopened.FamilyOverrides("qwen2"));
        Assert.Null(reopened.ModelOverrides(temp.Combine("models", "tiny.gguf")));
        Assert.Null(reopened.WorkModelOverrides(temp.Combine("models", "tiny.gguf")));
    }
}

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void Store_RoundTripsMessagesAndDeletesSessions()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Combine("chats"));
        ChatSessionDocument document = store.Create(
            temp.Combine("models", "tiny.gguf"), "qwen2", "Qwen2", "1.0", "Be brief.");
        document.Name = "Tiny chat";
        document.Messages.Add(ChatMessage.User("hello", "You"));
        document.Messages.Add(ChatMessage.Agent("hi"));
        store.Save(document);

        ChatSessionSummary summary = Assert.Single(store.List());
        Assert.Equal("Tiny chat", summary.Name);
        Assert.Equal(2, summary.MessageCount);

        ChatSessionDocument reopened = store.Open(document.Id);
        Assert.Equal("Be brief.", reopened.SystemPrompt);
        Assert.Equal(2, reopened.Messages.Count);

        store.Delete(document.Id);
        Assert.Empty(store.List());
    }
}

public sealed class StudioModelHostTests
{
    [Fact]
    public void NewHost_DoesNotLoadOrCacheAnyModels()
    {
        using var host = new StudioModelHost();

        Assert.Equal(0, host.LoadedModelCount);
    }
}



