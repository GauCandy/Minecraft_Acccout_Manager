using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MinecraftSessionTool;

internal sealed record UpdateAsset(string Url, long Size, string Sha256);
internal sealed record AppRelease(string Tag, string PageUrl, string Notes, UpdateAsset? Asset);
internal sealed record UpdateDownloadProgress(long Received, long Total)
{
    public double Percent => Total > 0 ? Math.Min(100, 100d * Received / Total) : 0;
}

internal static class AppUpdater
{
    internal const string ReleasesUrl = "https://github.com/GauCandy/Minecraft_Acccout_Manager/releases";
    private const string UpdateAssetLabel = "auto-update-win-x64";
    private const long MaxPackageSize = 1024L * 1024 * 1024;
    private static string TempRoot => Path.Combine(Path.GetTempPath(), "MinecraftCookieManager-updates");

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftCookieManager-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static async Task<AppRelease?> CheckAsync(string currentVersion, CancellationToken token)
    {
        using var client = CreateClient();
        var json = await client.GetStringAsync("https://api.github.com/repos/GauCandy/Minecraft_Acccout_Manager/releases/latest", token);
        return ParseRelease(json, Version.Parse(currentVersion));
    }

    internal static AppRelease? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString()?.Trim().TrimStart('v', 'V') ?? "";
        if (!Version.TryParse(tag, out var version) || Normalize(version) <= Normalize(current)) return null;
        var page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
        if (!IsGitHubPage(page)) page = ReleasesUrl;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 8000) notes = notes[..8000] + "…";
        var asset = root.TryGetProperty("assets", out var assets) ? SelectUpdateAsset(assets) : null;
        return new AppRelease(tag, page!, notes, asset);
    }

    private static UpdateAsset? SelectUpdateAsset(JsonElement assets)
    {
        if (assets.ValueKind != JsonValueKind.Array) return null;
        var executables = assets.EnumerateArray()
            .Where(item => item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString()!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var labeled = executables.Where(item => item.TryGetProperty("label", out var label)
            && label.ValueKind == JsonValueKind.String
            && string.Equals(label.GetString(), UpdateAssetLabel, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A label selects the Windows x64 package when several executables are published.
        var candidates = labeled.Length > 0 ? labeled : executables;
        if (candidates.Length != 1) return null;

        var item = candidates[0];
        if (!item.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaxPackageSize) return null;
        var url = item.TryGetProperty("browser_download_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString() : null;
        var digest = item.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString() : null;
        if (!IsGitHubAsset(url) || digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hash = digest[7..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? new UpdateAsset(url!, size, hash.ToLowerInvariant()) : null;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static bool IsGitHubPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsGitHubAsset(string? value) => IsGitHubPage(value) && new Uri(value!).AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal);

    internal static string CreateWorkDirectory()
    {
        var directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static async Task<string> DownloadAsync(AppRelease release, string directory,
        IProgress<UpdateDownloadProgress> progress, CancellationToken token, HttpClient? transport = null)
    {
        var asset = release.Asset ?? throw new InvalidOperationException("Bản phát hành này chưa có file cập nhật tự động hợp lệ.");
        var path = Path.Combine(directory, "download.exe");
        using var ownedClient = transport == null ? CreateClient() : null;
        var client = transport ?? ownedClient!;
        try
        {
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
                throw new InvalidDataException("Dung lượng file cập nhật không khớp. Bạn thử tải lại nhé.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long received = 0;
                var lastReport = Stopwatch.StartNew();
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("File tải về lớn hơn dung lượng đã công bố.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    if (lastReport.ElapsedMilliseconds >= 100 || received == asset.Size)
                    {
                        progress.Report(new UpdateDownloadProgress(received, asset.Size));
                        lastReport.Restart();
                    }
                }
                if (received != asset.Size) throw new InvalidDataException("File cập nhật chưa tải xong. Bạn thử lại nhé.");
            }
            token.ThrowIfCancellationRequested();
            await Task.Run(() => VerifyPackage(path, asset.Sha256, release.Tag), token);
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    internal static void VerifyPackage(string path, string expectedHash, string expectedVersion)
    {
        using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File cập nhật không khớp mã kiểm tra của GitHub. Chưa có thay đổi nào được cài đặt.");
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        file.Position = 0;
        if (file.Length < 256 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("File cập nhật không phải ứng dụng Windows hợp lệ.");
        file.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > file.Length - 24) throw new InvalidDataException("File cập nhật bị lỗi.");
        file.Position = peOffset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("Bản cập nhật không dành cho Windows 64-bit.");
        var info = FileVersionInfo.GetVersionInfo(path);
        var fileVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (Normalize(fileVersion) != Normalize(Version.Parse(expectedVersion)))
            throw new InvalidDataException("Phiên bản bên trong file chưa khớp với bản phát hành. Bạn có thể tải thủ công từ GitHub.");
    }

    internal static async Task StartInstallerAsync(string directory, string downloaded, AppRelease release, CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Chưa tìm được file ứng dụng đang chạy.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hãy mở bản .exe đã đóng gói để cập nhật tự động.");
        // Check write access before the current app exits.
        var probe = Path.Combine(Path.GetDirectoryName(executable)!, ".update-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var helper = Path.Combine(directory, "installer.exe");
        await Task.Run(() => File.Copy(executable, helper, overwrite: false), token);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var value in new[] { "--apply-update", executable, downloaded, Environment.ProcessId.ToString(), Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(), release.Asset!.Sha256, release.Tag })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Chưa mở được trình cập nhật.");
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("Trình cập nhật chưa khởi động được. App vẫn giữ nguyên bản hiện tại.");
                if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new IOException("Trình cập nhật mất quá nhiều thời gian để khởi động. Bạn thử lại nhé.");
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            throw;
        }
    }

    // Runs before WPF startup in a temporary copy of the current executable.
    internal static int RunInstaller(string[] args, Action<string, string, bool>? restart = null)
    {
        if (args.Length != 7) return 1;
        var launch = restart ?? StartApplication;
        var target = Path.GetFullPath(args[1]);
        var downloaded = Path.GetFullPath(args[2]);
        var directory = Path.GetDirectoryName(downloaded)!;
        bool installed = false;
        bool parentExited = false;
        try
        {
            if (!IsWorkDirectory(directory) || !Path.GetFileName(downloaded).Equals("download.exe", StringComparison.OrdinalIgnoreCase) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update paths.");
            VerifyPackage(downloaded, args[5], args[6]);
            Process? parent = null;
            try { parent = Process.GetProcessById(int.Parse(args[3])); }
            catch (ArgumentException) { }
            if (parent != null)
            {
                using (parent)
                {
                    if (!parent.HasExited)
                    {
                        if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(args[4]) || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The updating process does not match.");
                        File.WriteAllText(Path.Combine(directory, "ready"), "ready");
                        if (!parent.WaitForExit(60000)) return 1;
                    }
                }
            }
            parentExited = true;
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                try { ReplaceExecutable(downloaded, target); installed = true; break; }
                catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(15)) { Thread.Sleep(250); }
            }
            launch(target, directory, true);
            return 0;
        }
        catch
        {
            try
            {
                if (installed && File.Exists(target + ".previous"))
                    RestoreExecutable(target);
                if (parentExited) launch(target, directory, false);
            }
            catch { }
            return 1;
        }
    }

    internal static void ReplaceExecutable(string downloaded, string target)
    {
        var adjacent = target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(downloaded, adjacent, false);
            File.Copy(target, target + ".previous", true);
            File.Replace(adjacent, target, null);
        }
        finally { if (File.Exists(adjacent)) File.Delete(adjacent); }
    }

    internal static void RestoreExecutable(string target)
    {
        var adjacent = target + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(target + ".previous", adjacent, false);
            File.Replace(adjacent, target, null);
        }
        finally { if (File.Exists(adjacent)) File.Delete(adjacent); }
    }

    private static void StartApplication(string target, string directory, bool success)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--cleanup-update");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(success ? "--updated" : "--update-failed");
        using var process = Process.Start(start) ?? throw new IOException("Could not restart the application.");
        if (success && process.WaitForExit(1500)) throw new IOException("The new application exited during startup.");
    }

    private static bool IsWorkDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(TempRoot), StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(Path.GetFileName(full), "N", out _);
    }

    internal static async Task CleanupAsync(string directory)
    {
        if (!IsWorkDirectory(directory)) return;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                foreach (var file in new[] { "installer.exe", "download.exe", "ready" })
                {
                    var path = Path.Combine(directory, file);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
                return;
            }
            catch (IOException) { await Task.Delay(500); }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}


