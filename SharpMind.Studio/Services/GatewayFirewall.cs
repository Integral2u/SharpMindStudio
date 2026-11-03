using System.Diagnostics;
using System.Security.Principal;
using System.Text;

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
    /// </summary>
    public static async Task<FirewallRuleStatus> RuleStatusAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindows)
            return new FirewallRuleStatus(false, false, "Firewall rules only apply on Windows.");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $rule = Get-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $rule) {
                    'missing'
                } else {
                    $pf = $rule | Get-NetFirewallPortFilter
                    $ports = @($pf.LocalPort)
                    $profile = [string]$rule.Profile
                    $portOk = (@($ports) -contains 'Any') -or (@($ports) -contains '{{port}}')
                    $protoOk = @($pf.Protocol) -contains 'TCP'
                    $ok = ("$($rule.Enabled)" -eq 'True') -and
                          ("$($rule.Direction)" -eq 'Inbound') -and
                          ("$($rule.Action)" -eq 'Allow') -and
                          $protoOk -and
                          ($profile -match 'Private|Any') -and
                          $portOk
                    if ($ok) { 'ok' } else {
                        'stale|' + $profile + '|' + ($ports -join ',') + '|' + $pf.Protocol + '|' +
                        "$($rule.Enabled)" + '|' + "$($rule.Direction)" + '|' + "$($rule.Action)"
                    }
                }
            } catch {
                'error|' + $_.Exception.Message
            }
            """;

        string output = (await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false)).Trim();
        if (output.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            return new FirewallRuleStatus(true, true, $"Enabled for TCP {port} on the Private profile.");

        if (output.StartsWith("missing", StringComparison.OrdinalIgnoreCase))
            return new FirewallRuleStatus(false, false,
                $"No rule named '{RuleName}'. Use Allow through Windows Firewall.");

        if (output.StartsWith("stale|", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = output.Split('|');
            string detail = parts.Length >= 7
                ? $"Rule exists but does not match: profile={parts[1]}, port={parts[2]}, "
                  + $"protocol={parts[3]}, enabled={parts[4]}, direction={parts[5]}, action={parts[6]}."
                : "Rule exists but does not match the expected TCP/Private/Inbound/Allow shape.";
            return new FirewallRuleStatus(true, false, detail);
        }

        if (output.StartsWith("error|", StringComparison.OrdinalIgnoreCase))
            return new FirewallRuleStatus(false, false, "Could not read the firewall rule: " + output[6..]);

        return new FirewallRuleStatus(false, false, "Windows did not return a firewall status.");
    }

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
                $existing = Get-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue
                if ($existing) {
                    Remove-NetFirewallRule -DisplayName '{{RuleName}}'
                }
                New-NetFirewallRule -DisplayName '{{RuleName}}' -Direction Inbound -Protocol TCP -LocalPort {{port}} -Profile Private -Action Allow | Out-Null
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
