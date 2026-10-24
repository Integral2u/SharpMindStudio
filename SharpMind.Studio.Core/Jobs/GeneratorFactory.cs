using System.Collections;
using System.Reflection;
using SharpMind.Inference;
using SharpMind.Model;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Jobs;

/// <summary>
/// Creates a generator from the open/closed builder types discovered by
/// <c>BuilderCatalog</c>. The non-generic surface is enough for the harness:
/// GenerateAsync, CurrentGeneratedIds and Dispose.
/// </summary>
internal static class GeneratorFactory
{
    public static IDisposable Create(
        Type generatorType,
        Type cacheType,
        Transformer model,
        Tokenizer tokenizer)
    {
        Type closedBuilder = generatorType.IsGenericTypeDefinition
            ? generatorType.MakeGenericType(cacheType)
            : generatorType;

        object builder = Activator.CreateInstance(closedBuilder)
            ?? throw new InvalidOperationException($"Could not create {closedBuilder.Name}.");

        MethodInfo create = closedBuilder.GetMethod(
            "CreateGenerator",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{closedBuilder.Name} has no CreateGenerator method.");

        object generator = create.Invoke(builder, [model, tokenizer, true, false, null, null, null])
            ?? throw new InvalidOperationException($"{closedBuilder.Name}.CreateGenerator returned null.");
        return (IDisposable)generator;
    }

    public static IAsyncEnumerable<string> GenerateAsync(
        object generator,
        string prompt,
        SamplingConfig sampling,
        GenerationConfig generation,
        CancellationToken cancellationToken)
    {
        MethodInfo method = generator.GetType().GetMethod(
            "GenerateAsync",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{generator.GetType().Name} has no GenerateAsync method.");

        return (IAsyncEnumerable<string>)method.Invoke(
            generator, [prompt, sampling, generation, cancellationToken])!;
    }

    public static IReadOnlyList<int>? CurrentGeneratedIds(object generator) =>
        generator.GetType().GetProperty("CurrentGeneratedIds")?.GetValue(generator) as IReadOnlyList<int>;

    public static string DisplayName(Type type) => type.Name.Split('`')[0];
}

