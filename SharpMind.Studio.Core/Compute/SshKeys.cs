using System.Diagnostics;

namespace SharpMind.Studio.Core.Compute;

public sealed record SshKeyPair(string PrivateKeyPath, string PublicKey);

/// <summary>
/// Creates the key pair the SSH helper uses when the PC has none. It shells out
/// to OpenSSH's <c>ssh-keygen</c>, which ships with Windows 10+ and every Linux
/// desktop, so no extra installation is needed.
/// </summary>
public static class SshKeyGenerator
{
    public static string DefaultPrivateKeyPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "sharpmind_ed25519");

    /// <summary>
    /// Returns the existing key pair, or creates an unencrypted ed25519 key at
    /// <paramref name="privateKeyPath"/>. An existing private key is never
    /// overwritten: a key already trusted by a host must keep working.
    /// </summary>
    public static SshKeyPair Ensure(string? privateKeyPath, string comment = "sharpmind-studio")
    {
        string path = string.IsNullOrWhiteSpace(privateKeyPath)
            ? DefaultPrivateKeyPath
            : Expand(privateKeyPath);
        path = Path.GetFullPath(path);
        string publicPath = path + ".pub";

        if (File.Exists(path) && File.Exists(publicPath))
            return new SshKeyPair(path, File.ReadAllText(publicPath).Trim());

        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The private key {path} exists but {Path.GetFileName(publicPath)} is missing. "
                + "Delete the key or choose another path.");
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        var info = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-q");
        info.ArgumentList.Add("-t");
        info.ArgumentList.Add("ed25519");
        info.ArgumentList.Add("-N");
        info.ArgumentList.Add("");
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(comment);
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(path);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "OpenSSH's ssh-keygen was not found. Install the OpenSSH client, or choose an existing key.",
                ex);
        }

        if (process is null)
            throw new InvalidOperationException("ssh-keygen could not be started.");

        using (process)
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ssh-keygen failed (exit {process.ExitCode}): {FirstLine(error)}");
            }
        }

        if (!File.Exists(publicPath))
            throw new InvalidOperationException(
                $"ssh-keygen finished without writing {Path.GetFileName(publicPath)}.");

        return new SshKeyPair(path, File.ReadAllText(publicPath).Trim());
    }

    private static string Expand(string path)
    {
        string value = path.Trim();
        if (value == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[2..]);
        }

        return value;
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim()
        ?? "no output";
}
