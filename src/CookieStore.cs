using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftSessionTool;

public class SavedAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string RawCookies { get; set; } = "";
    public string? AccessToken { get; set; }
    public DateTime? LastRefreshed { get; set; }
    public string Status { get; set; } = "Hoạt động";
    public string SourceName { get; set; } = "";
    public string? LastProxyUsed { get; set; }

    public string FormattedUuid => !string.IsNullOrEmpty(Uuid) && Uuid.Length == 32
        ? Regex.Replace(Uuid, @"(.{8})(.{4})(.{4})(.{4})(.{12})", "$1-$2-$3-$4-$5")
        : Uuid;

    public string ShortUuid => !string.IsNullOrEmpty(Uuid) && Uuid.Length >= 8
        ? Uuid.Substring(0, 8) + "…"
        : Uuid;

    public string InitialLetter => !string.IsNullOrEmpty(Username)
        ? Username.Substring(0, 1).ToUpperInvariant()
        : "✦";

    public string LastRefreshedText => LastRefreshed.HasValue
        ? LastRefreshed.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : "Chưa làm mới";

    public string StatusColor => Status switch
    {
        "Hoạt động" => "#6E8A61",
        "Hết hạn" => "#D35400",
        _ => "#81768F"
    };

    public string StatusBgColor => Status switch
    {
        "Hoạt động" => "#EDF4E8",
        "Hết hạn" => "#FFF1E5",
        _ => "#F1EBF8"
    };
}

public static class CookieStore
{
    public static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "minecraft_cookie_manager"
    );

    private static readonly string AccountsFile = Path.Combine(StorageDir, "accounts.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<SavedAccount> LoadAll()
    {
        try
        {
            if (!File.Exists(AccountsFile)) return new List<SavedAccount>();
            string json = File.ReadAllText(AccountsFile);
            if (string.IsNullOrWhiteSpace(json)) return new List<SavedAccount>();
            return JsonSerializer.Deserialize<List<SavedAccount>>(json, JsonOptions) ?? new List<SavedAccount>();
        }
        catch
        {
            return new List<SavedAccount>();
        }
    }

    public static void SaveAll(List<SavedAccount> accounts)
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            string json = JsonSerializer.Serialize(accounts, JsonOptions);
            File.WriteAllText(AccountsFile, json);
        }
        catch { }
    }

    public static SavedAccount Upsert(string username, string uuid, string rawCookies, string accessToken, string sourceName, string? proxyUsed = null)
    {
        var list = LoadAll();
        var existing = list.FirstOrDefault(a =>
            (!string.IsNullOrEmpty(uuid) && a.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(username) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
        );

        if (existing != null)
        {
            existing.Username = username;
            existing.Uuid = uuid;
            existing.RawCookies = rawCookies;
            existing.AccessToken = accessToken;
            existing.LastRefreshed = DateTime.UtcNow;
            existing.Status = "Hoạt động";
            if (!string.IsNullOrEmpty(sourceName)) existing.SourceName = sourceName;
            if (!string.IsNullOrEmpty(proxyUsed)) existing.LastProxyUsed = proxyUsed;
            SaveAll(list);
            return existing;
        }

        var newAcc = new SavedAccount
        {
            Username = username,
            Uuid = uuid,
            RawCookies = rawCookies,
            AccessToken = accessToken,
            LastRefreshed = DateTime.UtcNow,
            Status = "Hoạt động",
            SourceName = sourceName,
            LastProxyUsed = proxyUsed
        };

        list.Insert(0, newAcc);
        SaveAll(list);
        return newAcc;
    }

    public static void Delete(string id)
    {
        var list = LoadAll();
        list.RemoveAll(a => a.Id == id);
        SaveAll(list);
    }

    public static void UpdateStatus(string id, string status, string? newAccessToken = null, string? proxyUsed = null)
    {
        var list = LoadAll();
        var item = list.FirstOrDefault(a => a.Id == id);
        if (item != null)
        {
            item.Status = status;
            if (newAccessToken != null)
            {
                item.AccessToken = newAccessToken;
                item.LastRefreshed = DateTime.UtcNow;
            }
            if (!string.IsNullOrEmpty(proxyUsed)) item.LastProxyUsed = proxyUsed;
            SaveAll(list);
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            Process.Start(new ProcessStartInfo(StorageDir) { UseShellExecute = true });
        }
        catch { }
    }
}
