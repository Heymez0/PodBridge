using System.ComponentModel;
using System.Diagnostics;

namespace PodBridge;

/// <summary>The HomePod sends clock sync and retransmit requests to the computer over UDP.
/// That needs an inbound rule for this program only, from the local network only.
/// The rule is added through the Windows Firewall API by an elevated copy of ourselves, after UAC consent.</summary>
internal static class FirewallRule
{
    public const string ApplyArgument = "--firewall-apply";
    public const string RemoveArgument = "--firewall-remove";

    private const string RuleName = "PodBridge HomePod";
    private const int ProtocolUdp = 17;
    private const int DirectionIn = 1;
    private const int ActionAllow = 1;
    private const int AllProfiles = 0x7FFFFFFF;

    /// <summary>Returns null on success, otherwise the reason to show the user.</summary>
    public static string? Request() => RunElevated(ApplyArgument);

    public static string? RequestRemoval() => RunElevated(RemoveArgument);

    private static string? RunElevated(string argument)
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"podbridge-fw-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{argument} \"{resultFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            process!.WaitForExit();
            if (process.ExitCode == 0) return null;
            return File.Exists(resultFile) ? File.ReadAllText(resultFile) : $"Exit code {process.ExitCode}";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The consent prompt was declined.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(resultFile); } catch (IOException) { }
        }
    }

    /// <summary>Runs in the elevated copy.</summary>
    public static int ApplyElevated(bool add, string? resultFile)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            RemoveExisting(policy);
            if (add)
            {
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                rule.Name = RuleName;
                rule.Description = "Clock sync and retransmit packets from HomePod";
                rule.ApplicationName = Environment.ProcessPath;
                rule.Protocol = ProtocolUdp;
                rule.Direction = DirectionIn;
                rule.Action = ActionAllow;
                rule.RemoteAddresses = "LocalSubnet";
                rule.Profiles = AllProfiles;
                rule.Enabled = true;
                policy.Rules.Add(rule);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (resultFile != null) File.WriteAllText(resultFile, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void RemoveExisting(dynamic policy)
    {
        try { policy.Rules.Remove(RuleName); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException) { }
    }

    public static string Diagnose()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int count = policy.Rules.Count;
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = RuleName;
            rule.ApplicationName = Environment.ProcessPath;
            rule.Protocol = ProtocolUdp;
            rule.Direction = DirectionIn;
            rule.Action = ActionAllow;
            rule.RemoteAddresses = "LocalSubnet";
            rule.Profiles = AllProfiles;
            rule.Enabled = true;
            return $"rule count={count}, rule object built (not added), exists={Exists()}";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
        }
    }

    public static bool Exists()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            dynamic rule = policy.Rules.Item(RuleName);
            return string.Equals((string)rule.ApplicationName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
