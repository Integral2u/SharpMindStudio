using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace SharpMind.Studio.Services;

public sealed record FirewallAttempt(bool Ok, bool Cancelled, string Detail);

/// <summary>
/// Reads and creates the inbound rule that lets a phone reach the LAN listener.
/// Creating it needs administrator rights, so Studio asks Windows for elevation
/// instead of making the user type the command.
/// </summary>
public static class GatewayFirewall
{
    public const string RuleName = "SharpMind Studio phone";

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsElevated
    {
        get
        {
            if (!IsWindows) return false;
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static async Task<bool> RuleExistsAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindows) return false;

        string script =
            $"$rule = Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue; " +
            "if ($rule) { 'yes' } else { 'no' }";
        string output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return output.Trim().Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the rule, raising a UAC prompt when Studio is not already elevated.</summary>
    public static async Task<FirewallAttempt> AllowAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindows)
            return new FirewallAttempt(false, false, "Firewall rules only apply on Windows.");

        string marker = Path.Combine(
            Path.GetTempPath(), $"sharpmind-firewall-{Guid.NewGuid():n}.txt");
        string scriptPath = Path.Combine(
            Path.GetTempPath(), $"sharpmind-firewall-{Guid.NewGuid():n}.ps1");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                if (-not (Get-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue)) {
                    New-NetFirewallRule -DisplayName '{{RuleName}}' -Direction Inbound -Protocol TCP -LocalPort {{port}} -Profile Private -Action Allow | Out-Null
                }
                'ok' | Set-Content -LiteralPath '{{marker}}'
            } catch {
                "error: $_" | Set-Content -LiteralPath '{{marker}}'
            }
            """;

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            };

            try
            {
                using Process? process = Process.Start(startInfo);
                if (process is null)
                    return new FirewallAttempt(false, false, "Could not start the elevated PowerShell.");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new FirewallAttempt(false, true, "The administrator prompt was cancelled.");
            }

            // The elevated process writes a marker when it finishes; wait for it.
            for (int attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(marker))
                {
                    string detail = (await File.ReadAllTextAsync(marker, cancellationToken)
                        .ConfigureAwait(false)).Trim();
                    return new FirewallAttempt(
                        detail.StartsWith("ok", StringComparison.OrdinalIgnoreCase), false, detail);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            return new FirewallAttempt(false, false, "Windows did not report back. Check Windows Firewall rules.");
        }
        finally
        {
            TryDelete(marker);
            TryDelete(scriptPath);
        }
    }

    private static async Task<string> RunPowerShellAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return "";
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
