using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Services;

public sealed record FirewallAttempt(bool Ok, bool Cancelled, string Detail);

/// <summary>Result of checking the inbound rule for the LAN listener.</summary>
public sealed record FirewallRuleStatus(bool Exists, bool Matches, string Detail);

/// <summary>The active internet network profile, for the Private-only firewall rule.</summary>
public sealed record NetworkProfileStatus(string Detail, bool IsPrivate, bool HasProfile);

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
        => (await RuleStatusAsync(port, cancellationToken).ConfigureAwait(false)).Matches;

    /// <summary>
    /// Checks the named rule, not just its presence: a stale rule with another
    /// port, a Public-only profile or a disabled state must not report "open".
    /// Uses netsh because Get-NetFirewallRule can return an empty set on some
    /// machines even when rules exist.
    /// </summary>
    public static async Task<FirewallRuleStatus> RuleStatusAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindows)
            return new FirewallRuleStatus(false, false, "Firewall rules only apply on Windows.");

        string output = await RunNetshAsync(
            $"advfirewall firewall show rule name=\"{RuleName}\" verbose",
            cancellationToken).ConfigureAwait(false);
        string text = output.Trim();

        if (text.Length == 0
            || text.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("Rule Name:", StringComparison.OrdinalIgnoreCase))
        {
            return new FirewallRuleStatus(false, false,
                $"No rule named '{RuleName}'. Use Allow through Windows Firewall.");
        }

        bool enabled = Regex.IsMatch(text, @"Enabled:\s+Yes", RegexOptions.IgnoreCase);
        bool inbound = Regex.IsMatch(text, @"Direction:\s+In\b", RegexOptions.IgnoreCase);
        bool privateProfile = Regex.IsMatch(
            text, @"Profiles:\s+(?:.*\bPrivate\b|Any)", RegexOptions.IgnoreCase);
        bool tcp = Regex.IsMatch(text, @"Protocol:\s+TCP\b", RegexOptions.IgnoreCase);
        bool portMatch = Regex.IsMatch(
            text, $@"LocalPort:\s+(?:Any|{Regex.Escape(port.ToString())})\b", RegexOptions.IgnoreCase);
        bool allow = Regex.IsMatch(text, @"Action:\s+Allow\b", RegexOptions.IgnoreCase);

        if (enabled && inbound && privateProfile && tcp && portMatch && allow)
        {
            return new FirewallRuleStatus(true, true,
                $"Enabled for TCP {port} on the Private profile.");
        }

        string detail = $"Rule exists but does not match the expected TCP/{port}/Private/Inbound/Allow shape "
            + $"(enabled={YesNo(enabled)}, inbound={YesNo(inbound)}, private={YesNo(privateProfile)}, "
            + $"tcp={YesNo(tcp)}, port={YesNo(portMatch)}, allow={YesNo(allow)}).";
        return new FirewallRuleStatus(true, false, detail);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>The active internet profile, so a Public Wi-Fi can be called out.</summary>
    public static async Task<NetworkProfileStatus> NetworkProfileAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsWindows)
            return new NetworkProfileStatus("Network profiles only apply on Windows.", false, false);

        const string script = """
            Get-NetConnectionProfile -ErrorAction SilentlyContinue |
                Where-Object { $_.IPv4Connectivity -eq 'Internet' } |
                ForEach-Object { "$($_.Name)|$($_.NetworkCategory)" }
            """;

        string output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        string[] lines = output.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return new NetworkProfileStatus("No active internet network profile found.", false, false);

        string detail = string.Join("; ", lines.Select(line => line.Replace('|', ':')));
        bool allPrivate = lines.All(line => line.EndsWith("|Private", StringComparison.OrdinalIgnoreCase));
        return new NetworkProfileStatus(detail, allPrivate, true);
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
                netsh advfirewall firewall delete rule name="{{RuleName}}" 2>$null | Out-Null
                netsh advfirewall firewall add rule name="{{RuleName}}" dir=in action=allow protocol=TCP localport={{port}} profile=private | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "netsh add rule failed with exit code $LASTEXITCODE" }
                'ok: TCP {{port}} allowed on the Private profile.' | Set-Content -LiteralPath '{{marker}}'
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

    private static async Task<string> RunNetshAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("netsh.exe")
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return "";
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output + error;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
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
