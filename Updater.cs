using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PodBridge;

public sealed record UpdateInfo(Version Version, string PageUrl, string AssetUrl, string Sha256, long Size);

/// <summary>Finds new releases on GitHub, downloads PodBridge.exe, checks it against the SHA-256 digest GitHub
/// publishes for the asset, and swaps it in. The running exe cannot be overwritten but can be renamed, so the
/// old file is moved aside, the new one copied in its place and started; it deletes the old one on launch.</summary>
public static class Updater
{
    public const string AfterUpdateArgument = "--after-update";
    private const string LatestReleaseApi = "https://api.github.com/repos/Heymez0/PodBridge/releases/latest";
    private const string AssetName = "PodBridge.exe";
    private const string OldSuffix = ".old.exe";

    private static readonly string Folder = Path.Combine(Settings.Folder, "update");
    private static readonly string StagedExe = Path.Combine(Folder, AssetName);
    private static readonly string StagedInfo = Path.Combine(Folder, "update.json");

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ---------------------------------------------------------------- check

    /// <summary>Returns the latest release if it is newer than this build and has a verifiable PodBridge.exe.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"Update check: GitHub answered {(int)response.StatusCode}");
            return null;
        }
        return ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Current);
    }

    /// <summary>Reads the GitHub "latest release" JSON. Kept separate so the self-test can check it offline.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var version = Normalize(parsed);
        if (version <= current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            // Without GitHub's digest the download cannot be verified, so it is not installed automatically.
            string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            return new UpdateInfo(version,
                                  root.GetProperty("html_url").GetString() ?? "",
                                  asset.GetProperty("browser_download_url").GetString() ?? "",
                                  digest["sha256:".Length..].ToUpperInvariant(),
                                  asset.GetProperty("size").GetInt64());
        }
        return null;   // Release published before its files were attached; the next check will see them.
    }

    // ---------------------------------------------------------------- download

    /// <summary>Downloads and verifies the update into the staging folder. Returns false if it does not match.</summary>
    public static async Task<bool> DownloadAsync(UpdateInfo update, CancellationToken ct)
    {
        if (Staged() is { } staged && staged.Version == update.Version) return true;

        Directory.CreateDirectory(Folder);
        string partial = StagedExe + ".download";
        using (var http = NewClient())
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            using var response = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        string hash = HashOf(partial);
        if (new FileInfo(partial).Length != update.Size || hash != update.Sha256)
        {
            File.Delete(partial);
            Log.Write($"Update {update.Version}: download did not match GitHub's SHA-256, discarded");
            return false;
        }
        File.Move(partial, StagedExe, overwrite: true);
        File.WriteAllText(StagedInfo, JsonSerializer.Serialize(new StagedFile(update.Version.ToString(), hash)));
        Log.Write($"Update {update.Version} downloaded and verified");
        return true;
    }

    private sealed record StagedFile(string Version, string Sha256);

    /// <summary>A verified update waiting in the staging folder that is newer than this build.</summary>
    public static UpdateInfo? Staged()
    {
        try
        {
            if (!File.Exists(StagedExe) || !File.Exists(StagedInfo)) return null;
            var info = JsonSerializer.Deserialize<StagedFile>(File.ReadAllText(StagedInfo));
            if (info == null || !Version.TryParse(info.Version, out var version) || Normalize(version) <= Current) return null;
            return new UpdateInfo(Normalize(version), "", "", info.Sha256, new FileInfo(StagedExe).Length);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- install

    /// <summary>Puts the staged exe in place of the running one and starts it. The caller must exit right after a
    /// true result; the new process waits for this one to release the single-instance lock.</summary>
    public static bool TryApply(out string error)
    {
        error = "";
        var staged = Staged();
        string? exe = Environment.ProcessPath;
        if (staged == null || exe == null) { error = "no update ready"; return false; }

        // Check again right before running it: the staging folder is writable by this user.
        if (HashOf(StagedExe) != staged.Sha256)
        {
            Discard();
            error = "staged file changed after download";
            return false;
        }

        string old = Path.ChangeExtension(exe, null) + OldSuffix;
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically a folder that needs administrator rights, such as Program Files.
            error = ex.Message;
            return false;
        }
        try
        {
            File.Copy(StagedExe, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, exe);   // Put the working version back.
            error = ex.Message;
            return false;
        }

        Discard();
        Log.Write($"Updated {Current} -> {staged.Version}, restarting");
        Process.Start(new ProcessStartInfo(exe, AfterUpdateArgument) { UseShellExecute = false });
        return true;
    }

    /// <summary>Removes the previous version's exe once the old process is gone.</summary>
    public static void CleanUpOldVersion()
    {
        if (Environment.ProcessPath is not { } exe) return;
        string old = Path.ChangeExtension(exe, null) + OldSuffix;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }

    private static void Discard()
    {
        try
        {
            File.Delete(StagedExe);
            File.Delete(StagedInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PodBridge", Current.ToString()));
        return http;
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
