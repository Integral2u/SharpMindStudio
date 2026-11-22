using System.Text;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

string mode = args.Length > 0 ? args[0] : "echo";
string[] rest = args.Length > 1 ? args[1..] : [];

switch (mode)
{
    case "echo":
        Console.Out.WriteLine(string.Join(' ', rest));
        break;

    case "cat":
        Console.Out.Write(Console.In.ReadToEnd());
        break;

    case "fail":
        Console.Error.WriteLine("failed");
        Environment.Exit(3);
        break;

    case "sleep":
        if (rest.Length > 0 && int.TryParse(rest[0], out int milliseconds))
            Thread.Sleep(Math.Clamp(milliseconds, 0, 30_000));
        break;

    case "env":
        Console.Out.WriteLine(
            rest.Length > 0 ? Environment.GetEnvironmentVariable(rest[0]) ?? "" : "");
        break;

    case "cwd":
        Console.Out.WriteLine(Environment.CurrentDirectory);
        break;

    case "leak":
        string value = rest.Length > 0 ? rest[0] : "";
        Console.Out.WriteLine(value);
        Console.Error.WriteLine("stderr:" + value);
        break;

    case "exit":
        if (rest.Length > 0 && int.TryParse(rest[0], out int code)) Environment.Exit(code);
        break;

    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'.");
        Environment.Exit(2);
        break;
}
