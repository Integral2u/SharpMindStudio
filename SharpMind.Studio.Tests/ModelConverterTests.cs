using SharpMind.Core.Quantization;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Exercises the Studio conversion wrapper in both directions with a tiny
/// hand-built model: SMM to GGUF, then GGUF back to SMM, plus the guards that
/// matter to the UI (unknown source, overwrite protection, identical paths).
/// </summary>
public sealed class ModelConverterTests
{
    [Fact]
    public void ConvertRoundTrip_PreservesTensors()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string smm = Path.Combine(dir, "tiny.smm");
            CreateTinySmm(smm);

            string gguf = Path.Combine(dir, "tiny.gguf");
            ModelConversionResult toGguf = ModelConverter.Convert(smm, gguf);

            Assert.True(toGguf.Success, toGguf.Error);
            Assert.Equal(ModelConversionDirection.SmmToGguf, toGguf.Direction);
            Assert.True(new FileInfo(gguf).Length > 0);
            Assert.Equal("GGUF", ReadMagic(gguf));

            string backToSmm = Path.Combine(dir, "roundtrip.smm");
            ModelConversionResult toSmm = ModelConverter.Convert(gguf, backToSmm);

            Assert.True(toSmm.Success, toSmm.Error);
            Assert.Equal(ModelConversionDirection.GgufToSmm, toSmm.Direction);
            Assert.True(new FileInfo(backToSmm).Length > 0);

            var meta = SmmLoader.LoadMeta(backToSmm);
            Assert.Single(meta.Tensors);
            Assert.Equal("token_embd.weight", meta.Tensors[0].Name);
            Assert.Equal([8, 8], meta.Tensors[0].Shape);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Convert_RefusesToOverwriteUnlessAsked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string smm = Path.Combine(dir, "tiny.smm");
            CreateTinySmm(smm);

            string gguf = Path.Combine(dir, "tiny.gguf");
            Assert.True(ModelConverter.Convert(smm, gguf).Success);

            ModelConversionResult blocked = ModelConverter.Convert(smm, gguf);
            Assert.False(blocked.Success);
            Assert.Contains("already exists", blocked.Error);

            ModelConversionResult replaced = ModelConverter.Convert(smm, gguf, overwrite: true);
            Assert.True(replaced.Success, replaced.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Convert_RejectsUnknownAndIdenticalFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string text = Path.Combine(dir, "notes.txt");
            File.WriteAllText(text, "not a model");

            Assert.Null(ModelConverter.Detect(text));
            Assert.False(ModelConverter.Convert(text, Path.Combine(dir, "out.smm")).Success);

            string smm = Path.Combine(dir, "tiny.smm");
            CreateTinySmm(smm);

            ModelConversionResult same = ModelConverter.Convert(smm, smm, overwrite: true);
            Assert.False(same.Success);
            Assert.Contains("different file", same.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SuggestDestination_SwapsExtension()
    {
        Assert.Equal(
            ModelConversionDirection.SmmToGguf,
            ModelConverter.Detect(@"C:\models\chat.smm"));
        Assert.Equal(
            ModelConversionDirection.GgufToSmm,
            ModelConverter.Detect(@"C:\models\chat.GGUF"));

        string source = Path.Combine(Path.GetTempPath(), "suggest", "chat.smm");
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "suggest", "chat.gguf"),
            ModelConverter.SuggestDestination(source));
    }

    private static void CreateTinySmm(string path)
    {
        string[] tokens = ["<unk>", "<s>", "</s>", "a", "b", "c", " ", "\n"];
        var tokenizer = Tokenizer.FromGguf(tokens, merges: null, tokenTypes: null, bosId: 1, eosId: 2);

        var config = new ModelConfig
        {
            Architecture = "gpt2",
            VocabSize = tokens.Length,
            HiddenDim = 8,
            NumLayers = 1,
            NumHeads = 2,
            NumKvHeads = 2,
            FfnDim = 16,
            MaxSeqLen = 16,
            NormEps = 1e-5f,
        };

        var weights = new byte[8 * 8 * sizeof(float)];
        Random.Shared.NextBytes(weights);

        SmmWriter.Write(
            path,
            config,
            tokenizer,
            "<|user|>{{prompt}}",
            [
                new SmmTensorData
                {
                    Name = "token_embd.weight",
                    Shape = [8, 8],
                    Dtype = QuantDType.F32,
                    GetBytes = () => weights,
                },
            ],
            new SmmWriteOptions { Source = "test" });
    }

    private static string ReadMagic(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        return System.Text.Encoding.ASCII.GetString(magic);
    }
}
