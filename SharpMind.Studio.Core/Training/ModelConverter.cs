using System.Diagnostics;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Model.Format.Conversion;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Converts between Studio's packaged model (<c>.smm</c>) and GGUF.
///
/// Both converters stream tensors verbatim, so a conversion never re-quantises
/// or buffers the whole model. This wrapper only adds the Studio concerns:
/// direction detection, overwrite protection, a progress relay, cancellation
/// and a read-back validation of the file it just wrote.
/// </summary>
public static class ModelConverter
{
    public static readonly string[] SourceExtensions = [".smm", ".gguf"];

    /// <summary>Returns the direction implied by the source extension, or null.</summary>
    public static ModelConversionDirection? Detect(string? sourcePath)
    {
        string extension = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
        return extension switch
        {
            ".smm" => ModelConversionDirection.SmmToGguf,
            ".gguf" => ModelConversionDirection.GgufToSmm,
            _ => null,
        };
    }

    /// <summary>Swaps the extension, keeping the file next to its source.</summary>
    public static string SuggestDestination(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ModelConversionDirection direction = Detect(sourcePath)
            ?? throw new ArgumentException("Expected a .smm or .gguf source file.", nameof(sourcePath));

        string targetExtension = direction == ModelConversionDirection.SmmToGguf ? ".gguf" : ".smm";
        string folder = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? StudioPaths.Models;
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(folder, name + targetExtension);
    }

    /// <summary>
    /// Converts on a worker thread. <paramref name="progress"/> reports 0..1;
    /// cancelling deletes the partial temp file and leaves the destination untouched.
    /// </summary>
    public static Task<ModelConversionResult> ConvertAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Convert(sourcePath, destinationPath, overwrite, progress, cancellationToken),
            cancellationToken);

    public static ModelConversionResult Convert(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        ModelConversionDirection? direction = null;

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);

            direction = Detect(sourcePath)
                ?? throw new InvalidOperationException("Pick a .smm or .gguf file to convert.");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source model not found: {sourcePath}", sourcePath);

            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The destination must be a different file from the source.");

            EnsureTargetExtension(direction.Value, destinationPath);

            if (File.Exists(destinationPath) && !overwrite)
                throw new IOException($"Destination already exists: {destinationPath}");

            string? folder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0f);

            switch (direction.Value)
            {
                case ModelConversionDirection.SmmToGguf:
                    SmmToGufConverter.Convert(sourcePath, destinationPath, progress, cancellationToken);
                    break;
                case ModelConversionDirection.GgufToSmm:
                    GgufToSmmConverter.Convert(
                        sourcePath,
                        destinationPath,
                        new SmmWriteOptions { Source = "gguf" },
                        progress,
                        cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported conversion: {direction}.");
            }

            Validate(destinationPath, direction.Value);

            clock.Stop();
            return new ModelConversionResult
            {
                Success = true,
                Direction = direction.Value,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                SourceBytes = new FileInfo(sourcePath).Length,
                DestinationBytes = new FileInfo(destinationPath).Length,
                Elapsed = clock.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            return Failure("Conversion cancelled. The destination was not changed.", direction, sourcePath, destinationPath, clock.Elapsed);
        }
        catch (Exception ex)
        {
            clock.Stop();
            return Failure($"{ex.GetType().Name}: {ex.Message}", direction, sourcePath, destinationPath, clock.Elapsed);
        }
    }

    private static void EnsureTargetExtension(ModelConversionDirection direction, string destinationPath)
    {
        string extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        string expected = direction == ModelConversionDirection.SmmToGguf ? ".gguf" : ".smm";
        if (extension.Length != 0 && extension != expected)
            throw new InvalidOperationException(
                $"A {direction} conversion must write a {expected} file, not '{extension}'.");
    }

    /// <summary>
    /// Reads the new file back with the loader that will consume it, so a
    /// truncated or malformed output fails now rather than when it is loaded.
    /// </summary>
    private static void Validate(string path, ModelConversionDirection direction)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
            throw new InvalidDataException("The converter wrote an empty file.");

        ModelMetaData meta = direction == ModelConversionDirection.SmmToGguf
            ? GgufLoader.LoadMeta(path)
            : SmmLoader.LoadMeta(path);

        if (meta.Tensors.Count == 0)
            throw new InvalidDataException("The converted file has no tensors.");

        ModelConfig? config = direction == ModelConversionDirection.SmmToGguf
            ? GgufLoader.LoadConfig(meta)
            : SmmLoader.LoadConfig(meta);
        if (config is null)
            throw new InvalidDataException("The converted file is missing its model config.");
    }

    private static ModelConversionResult Failure(
        string error,
        ModelConversionDirection? direction,
        string? sourcePath,
        string? destinationPath,
        TimeSpan elapsed)
        => new()
        {
            Success = false,
            Error = error,
            Direction = direction,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Elapsed = elapsed,
        };
}

public enum ModelConversionDirection
{
    SmmToGguf,
    GgufToSmm,
}

public sealed record ModelConversionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public ModelConversionDirection? Direction { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public long SourceBytes { get; init; }
    public long DestinationBytes { get; init; }
    public TimeSpan Elapsed { get; init; }

    public string Label => Direction switch
    {
        ModelConversionDirection.SmmToGguf => "SMM to GGUF",
        ModelConversionDirection.GgufToSmm => "GGUF to SMM",
        _ => "Conversion",
    };
}
