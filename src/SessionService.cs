using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftSessionTool;

internal sealed record SessionResult(string Username, string Uuid, string AccessToken)
{
    public string FormattedUuid => Regex.Replace(Uuid, @"(.{8})(.{4})(.{4})(.{4})(.{12})", "$1-$2-$3-$4-$5");
    public string LaunchArguments => $"--username {Username} --uuid {Uuid} --accessToken {AccessToken} --userType mojang";
}

internal static class SessionService
{
    internal static async Task<SessionResult> CreateAsync(string cookieFile,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Đọc file cookie");
        var jar = ParseNetscapeCookies(cookieFile);
        if (jar.Count == 0)
            throw new InvalidDataException("File chưa có cookie đăng nhập Microsoft hợp lệ. Hãy chọn file Netscape khác nhé.");

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report("Kết nối Microsoft OAuth");
        var callback = await ResolveSisuOAuthAsync(jar, cancellationToken);
        progress.Report("Xác nhận phiên Xbox");
        var (userHash, xstsToken) = ExtractXstsBundle(callback);
        progress.Report("Lấy phiên Minecraft Java");
        var accessToken = await ExchangeMinecraftTokenAsync(userHash, xstsToken, cancellationToken);
        progress.Report("Đọc hồ sơ người chơi");
        var (username, uuid) = await GetMinecraftProfileAsync(accessToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new SessionResult(username, uuid, accessToken);
    }

    private static readonly HashSet<string> AuthDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "live.com", "login.live.com", "login.microsoft.com", "login.microsoftonline.com",
        "microsoftonline.com", "minecraft.net", "www.minecraft.net", "xbox.com",
        "account.xbox.com", "sisu.xboxlive.com", "xboxlive.com"
    };

    private static CookieContainer ParseNetscapeCookies(string path)
    {
        var container = new CookieContainer();
        foreach (var line in File.ReadLines(path))
        {
            string clean = line.Trim();
            if (string.IsNullOrEmpty(clean)) continue;
            if (clean.StartsWith("#HttpOnly_")) clean = clean.Substring(10);
            else if (clean.StartsWith("#")) continue;

            var parts = clean.Split('\t');
            if (parts.Length < 7) continue;

            string domain = parts[0].TrimStart('.');
            string name = parts[5].Trim();
            string val = parts[6].Trim();

            if (AuthDomains.Contains(domain) && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
            {
                try
                {
                    container.Add(new Cookie(name, val, "/", "." + domain));
                }
                catch { }
            }
        }
        return container;
    }

    private static async Task<string> ResolveSisuOAuthAsync(CookieContainer jar, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = jar,
            AllowAutoRedirect = false,
            MaxResponseHeadersLength = 128 * 1024
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Referer", "https://www.minecraft.net/");

        string currentUrl = "https://sisu.xboxlive.com/connect/XboxLive/?state=login&cobrandId=8058f65d-ce06-4c30-9559-473c9275a65d&tid=896928775&ru=https%3A%2F%2Fwww.minecraft.net%2Fen-us%2Flogin&aid=1142970254&as=1";

        for (int i = 0; i < 8; i++)
        {
            using var response = await client.GetAsync(currentUrl, cancellationToken);
            var loc = response.Headers.Location;
            if (loc == null) break;

            string target = loc.IsAbsoluteUri ? loc.AbsoluteUri : new Uri(new Uri(currentUrl), loc).AbsoluteUri;
            if (target.Contains("minecraft.net/en-us/login#"))
            {
                return target;
            }
            currentUrl = target;
        }

        throw new Exception("Cookie đã hết hạn hoặc Microsoft yêu cầu xác minh tài khoản (2FA/OTP).");
    }

    private static (string userHash, string token) ExtractXstsBundle(string callbackUrl)
    {
        int hashIdx = callbackUrl.IndexOf('#');
        if (hashIdx == -1) throw new Exception("Không tìm thấy dữ liệu phân đoạn (fragment) trong phản hồi.");

        string fragment = callbackUrl.Substring(hashIdx + 1);
        string? encoded = null;

        foreach (var pair in fragment.Split('&'))
        {
            var kv = pair.Split('=');
            if (kv.Length >= 2 && kv[0] == "accessToken")
            {
                // Thay dấu + trước khi unescape tương tự node: replace(/\+/g, '%2B')
                string safeValue = kv[1].Replace("+", "%2B");
                encoded = Uri.UnescapeDataString(safeValue);
                break;
            }
        }

        if (string.IsNullOrEmpty(encoded)) throw new Exception("Không tìm thấy accessToken trong URL callback.");

        // Fix padding base64 nếu thiếu
        int pad = (4 - encoded.Length % 4) % 4;
        if (pad > 0 && pad < 4) encoded += new string('=', pad);

        byte[] bytes = Convert.FromBase64String(encoded);
        using var doc = JsonDocument.Parse(bytes);

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("Item1", out var item1) && item1.GetString() == "rp://api.minecraftservices.com/")
            {
                var item2 = element.GetProperty("Item2");
                string token = item2.GetProperty("Token").GetString()!;
                string uhs = item2.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
                return (uhs, token);
            }
        }

        throw new Exception("Gói đăng nhập không chứa dịch vụ Minecraft.");
    }

    private static async Task<string> ExchangeMinecraftTokenAsync(string userHash, string xstsToken, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var payload = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var res = await client.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", content, cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            throw new Exception($"Đổi token Minecraft thất bại: HTTP {res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<(string ign, string uuid)> GetMinecraftProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var res = await client.GetAsync("https://api.minecraftservices.com/minecraft/profile", cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            throw new Exception("Tài khoản Microsoft này chưa sở hữu bản quyền Minecraft Java.");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string ign = doc.RootElement.GetProperty("name").GetString()!;
        string uuid = doc.RootElement.GetProperty("id").GetString()!;
        return (ign, uuid);
    }


}

