using System.Diagnostics;
using System.Reflection;
using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Projects;
using SharpMind.Studio.Core.Recipes;
using SharpMind.Studio.Core.Runs;

return await StudioCli.RunAsync(args);

internal static class StudioCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "doctor" => Doctor(),
                "init" => Init(args),
                "validate" => Validate(args),
                "replay" => await ReplayAsync(args),
                "bench" => await BenchAsync(args),
                "canary" => await CanaryAsync(args),
                "tooltest" => await ToolTestAsync(args),
                "version" => Version(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"studio: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Doctor()
    {
        string? projectRoot = FindProjectRoot(Environment.CurrentDirectory);
        Console.WriteLine($"studio       {VersionText()}");
        Console.WriteLine($"library      {LibraryVersionText()}");
        Console.WriteLine($"host         {ModelBenchJob.DeviceDescription()}");
        Console.WriteLine($"working set  {Environment.WorkingSet / (1024.0 * 1024.0):F1} MiB");
        Console.WriteLine($"managed heap {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F1} MiB");
        Console.WriteLine($"project      {(projectRoot is null ? "(none)" : projectRoot)}");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            Console.WriteLine($"drive {drive.Name,-5} {drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):F1} GiB free");
        }

        return 0;
    }

    private static int Init(string[] args)
    {
        if (args.Length < 2) return UsageError("init needs a project directory.");
        string root = args[1];
        string name = Option(args, "--name") ?? Path.GetFileName(Path.GetFullPath(root));
        string? device = Option(args, "--device");

        var project = StudioProject.Create(root, name, device);
        Console.WriteLine($"Created Studio project '{project.Document.Name}'.");
        Console.WriteLine($"  root  {project.Root}");
        Console.WriteLine($"  id    {project.Document.Id}");
        Console.WriteLine($"  runs  {project.RunsDirectory}");
        return 0;
    }

    private static int Validate(string[] args)
    {
        if (args.Length < 2) return UsageError("validate needs a Recipe path.");
        Recipe recipe = RecipeStore.Load(args[1]);
        var result = RecipeValidator.Validate(recipe);

        Console.WriteLine($"Recipe       {recipe.Name}");
        Console.WriteLine($"Schema       {recipe.Schema}");
        Console.WriteLine($"Steps        {recipe.Steps.Count}");
        Console.WriteLine($"Hash         {RecipeHasher.Hash(recipe)}");
        Console.WriteLine($"Valid        {result.IsValid}");

        foreach (var issue in result.Issues)
            Console.WriteLine($"  {issue.Code,-18} {issue.Message}");
        return result.IsValid ? 0 : 1;
    }

    private static async Task<int> ReplayAsync(string[] args)
    {
        string? projectRoot = FindProjectRoot(Environment.CurrentDirectory);
        string runsRoot = projectRoot is null
            ? Path.Combine(Environment.CurrentDirectory, "runs")
            : StudioProject.Open(projectRoot).RunsDirectory;

        var store = new RunStore(runsRoot);
        RunHandle handle;
        if (args.Length > 1)
        {
            handle = store.Open(args[1]);
        }
        else
        {
            handle = store.List().FirstOrDefault()
                ?? throw new InvalidOperationException($"No runs found under '{runsRoot}'.");
        }

        Console.WriteLine($"Run {handle.RunId}");
        Console.WriteLine($"Events {handle.EventsPath}");
        await foreach (var runEvent in handle.ReadEventsAsync())
            Console.WriteLine(Describe(runEvent));
        return 0;
    }

    private static async Task<int> BenchAsync(string[] args)
    {
        if (args.Length < 2) return UsageError("bench needs a model path.");

        string modelPath = args[1];
        string prompt = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : "The capital of France is";
        int warmup = args.Length > 3 && int.TryParse(args[3], out int w) ? w : 16;
        int measure = args.Length > 4 && int.TryParse(args[4], out int m) ? m : 64;
        bool streaming = args.Any(a => a.Equals("--streaming", StringComparison.OrdinalIgnoreCase));

        string runRoot = RunsRoot();
        var store = new RunStore(runRoot);
        RunHandle run = store.Create(ModelBenchJob.Kind, Path.GetFileNameWithoutExtension(modelPath));
        await using var writer = run.OpenWriter();

        Console.WriteLine($"Run     {run.RunId}");
        Console.WriteLine($"Model   {Path.GetFileName(modelPath)}");
        Console.WriteLine($"Mode    {(streaming ? "streaming" : "full")}  warmup={warmup}  measure={measure}");
        Console.WriteLine();

        var request = new ModelBenchRequest
        {
            ModelPath = modelPath,
            Prompt = prompt,
            WarmupTokens = warmup,
            MeasureTokens = measure,
            FullLoad = !streaming,
        };
        ModelBenchResult result = await ModelBenchJob.RunAsync(request, run, writer);
        Console.WriteLine($"OUT: {result.Text}");
        Console.WriteLine();
        Console.WriteLine($"Decode  {result.TokensPerSecond:F2} tok/s  ({result.MeasureMs / result.Steps:F2} ms/token)");
        Console.WriteLine($"Alloc   {result.AllocatedBytesPerToken:N0} B/token");
        Console.WriteLine($"Events  {run.EventsPath}");
        return 0;
    }

    private static async Task<int> CanaryAsync(string[] args)
    {
        if (args.Length < 2) return UsageError("canary needs a model list file.");

        string listPath = Path.GetFullPath(args[1]);
        if (!File.Exists(listPath)) return UsageError($"model list '{listPath}' was not found.");

        string baseDirectory = Path.GetDirectoryName(listPath)!;
        string[] models = [.. File.ReadAllLines(listPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDirectory, line)))];

        if (models.Length == 0) return UsageError("the model list is empty.");

        string prompt = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : "The capital of France is";
        int warmup = args.Length > 3 && int.TryParse(args[3], out int w) ? w : 16;
        int measure = args.Length > 4 && int.TryParse(args[4], out int m) ? m : 32;
        bool streaming = args.Any(a => a.Equals("--streaming", StringComparison.OrdinalIgnoreCase));

        var store = new RunStore(RunsRoot());
        Console.WriteLine($"Canary  {models.Length} model(s)  warmup={warmup}  measure={measure}  " +
                          $"mode={(streaming ? "streaming" : "full")}");
        Console.WriteLine();
        Console.WriteLine($"{"model",-34} {"tok/s",8} {"ms/tok",8} {"B/tok",10} {"status",-9} run");
        Console.WriteLine(new string('-', 100));

        bool failed = false;
        foreach (string model in models)
        {
            string name = Path.GetFileNameWithoutExtension(model);
            if (name.Length > 34) name = name[..31] + "...";
            var run = store.Create(ModelBenchJob.Kind, Path.GetFileNameWithoutExtension(model));
            await using var writer = run.OpenWriter();

            try
            {
                var result = await ModelBenchJob.RunAsync(new ModelBenchRequest
                {
                    ModelPath = model,
                    Prompt = prompt,
                    WarmupTokens = warmup,
                    MeasureTokens = measure,
                    FullLoad = !streaming,
                }, run, writer);

                Console.WriteLine($"{name,-34} {result.TokensPerSecond,8:F2} {result.MeasureMs / result.Steps,8:F2} " +
                                  $"{result.AllocatedBytesPerToken,10:N0} {"ok",-9} {run.RunId}");
            }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine($"{name,-34} {"-",8} {"-",8} {"-",10} {"failed",-9} {run.RunId}");
                Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            }
        }

        return failed ? 1 : 0;
    }

    private static async Task<int> ToolTestAsync(string[] args)
    {
        if (args.Length < 2) return UsageError("tooltest needs a model path.");

        string modelPath = args[1];
        string prompt = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : "can you show me three options to choose from. Red, Green or Blue";
        int maxTokens = args.Length > 3 && int.TryParse(args[3], out int m) ? m : 96;
        bool streaming = args.Any(a => a.Equals("--streaming", StringComparison.OrdinalIgnoreCase));

        var store = new RunStore(RunsRoot());
        RunHandle run = store.Create(ToolProbeJob.Kind, Path.GetFileNameWithoutExtension(modelPath));
        await using var writer = run.OpenWriter();

        Console.WriteLine($"Run     {run.RunId}");
        Console.WriteLine($"Model   {Path.GetFileName(modelPath)}");
        Console.WriteLine($"Prompt  {prompt}");
        Console.WriteLine();

        var result = await ToolProbeJob.RunAsync(new ToolProbeRequest
        {
            ModelPath = modelPath,
            Prompt = prompt,
            MaxNewTokens = maxTokens,
            FullLoad = !streaming,
        }, run, writer);

        Console.WriteLine($"Tool    {(result.ToolCalled ? "called" : "not called")}");
        Console.WriteLine($"Options {string.Join(", ", result.Options)}");
        Console.WriteLine($"Result  {(result.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Reply   {result.Response}");
        Console.WriteLine($"Events  {run.EventsPath}");
        return result.Passed ? 0 : 1;
    }

    private static int Version()
    {
        Console.WriteLine($"studio  {VersionText()}");
        Console.WriteLine($"library {LibraryVersionText()}");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"studio: unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"studio: {message}");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SharpMind Studio (headless spine)");
        Console.WriteLine();
        Console.WriteLine("  studio doctor                    show host and library status");
        Console.WriteLine("  studio init <dir> [--name N]     create a project");
        Console.WriteLine("  studio validate <recipe.smmtx>   check the step graph and print its hash");
        Console.WriteLine("  studio replay [run]              print a run's event stream (latest by default)");
        Console.WriteLine("  studio bench <model> [prompt] [warmup] [measure] [--streaming]");
        Console.WriteLine("  studio canary <models.txt> [prompt] [warmup] [measure] [--streaming]");
        Console.WriteLine("  studio tooltest <model> [prompt] [maxTokens] [--streaming]");
        Console.WriteLine("  studio version                   print versions");
    }

    private static string Describe(RunEvent runEvent) => runEvent switch
    {
        RunStarted e => $"{Stamp(e)} runStarted {e.Kind} {e.Subject} device={e.Device}",
        StepLogged e => $"{Stamp(e)} step {e.Step,4} {e.TokensPerSecond,8:F2} tok/s  tokens={e.TokensSeen}",
        EvalCompleted e => $"{Stamp(e)} eval   step={e.Step} loss={e.ValLoss:F4} ppl={e.Perplexity:F2}",
        ExpertStats e => $"{Stamp(e)} expert step={e.Step} layer={e.Layer} entropy={e.RoutingEntropy:F3}",
        CheckpointSaved e => $"{Stamp(e)} checkpoint step={e.Step} {e.Path}",
        ProbeAnswered e => $"{Stamp(e)} probe  {e.ProbeId}: {Truncate(e.Text, 120)}",
        RunFinished e => $"{Stamp(e)} runFinished {e.Status} {e.DurationMs:F0} ms{(e.Error is null ? "" : $" error={e.Error}")}",
        _ => $"{Stamp(runEvent)} {runEvent.GetType().Name}",
    };

    private static string Stamp(RunEvent runEvent) => runEvent.AtUtc.ToString("HH:mm:ss.fff");
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string RunsRoot()
    {
        string? projectRoot = FindProjectRoot(Environment.CurrentDirectory);
        return projectRoot is null
            ? Path.Combine(Environment.CurrentDirectory, "runs")
            : StudioProject.Open(projectRoot).RunsDirectory;
    }

    private static string? FindProjectRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (StudioProject.IsProject(directory.FullName)) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help" or "/?";

    private static string VersionText() =>
        typeof(StudioCli).Assembly.GetName().Version?.ToString() ?? "1.1.0.0";

    private static string LibraryVersionText()
    {
        Assembly library = typeof(SharpMind.Model.Transformer).Assembly;
        return $"{library.GetName().Name} {library.GetName().Version}";
    }
}
