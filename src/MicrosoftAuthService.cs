using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftSessionTool;

internal sealed record DeviceCodeInfo(
    string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete,
    int IntervalSeconds, int ExpiresInSeconds);

internal sealed record MicrosoftLoginResult(SessionResult Session, string RefreshToken);

/// <summary>Refresh token đã bị Microsoft thu hồi (invalid_grant) — cần đăng nhập lại.</summary>
internal sealed class MicrosoftRefreshRevokedException : Exception
{
    public const string Text = "Phiên Microsoft đã bị thu hồi hoặc hết hạn. Hãy đăng nhập lại tài khoản.";
    public MicrosoftRefreshRevokedException() : base(Text) { }
}

/// <summary>
/// Đăng nhập tài khoản Microsoft bằng device code (flow login.live.com của Minecraft Launcher),
/// rồi đổi qua Xbox Live (device/title/user → XSTS) → Minecraft Services để lấy access token.
/// Port từ Bot Dashboard: src/features/accounts/msauth/*.
/// </summary>
internal static class MicrosoftAuthService
{
    // Title ID của Minecraft — public client, không cần client secret.
    private const string ClientId = "00000000441cc96b";
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string FallbackVerificationUri = "https://login.live.com/oauth20_remoteconnect.srf";

    private const string DeviceAuthUrl = "https://device.auth.xboxlive.com/device/authenticate";
    private const string TitleAuthUrl = "https://title.auth.xboxlive.com/title/authenticate";
    private const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McRelyingParty = "rp://api.minecraftservices.com/";
    private const string LauncherUserAgent = "MinecraftLauncher/2.2.10675";

    private static HttpClient CreateClient(string? proxyAddress)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(proxyAddress) && ProxyStore.CreateWebProxy(proxyAddress) is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ---------------------------------------------------------------- Device code

    internal static async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string? proxyAddress, CancellationToken ct)
    {
        using var client = CreateClient(proxyAddress);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code"
        });
        using var res = await client.PostAsync(DeviceCodeUrl, form, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException("Không lấy được mã đăng nhập từ Microsoft.", null, res.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string deviceCode = root.GetProperty("device_code").GetString()!;
        string userCode = root.GetProperty("user_code").GetString()!;
        string verification = root.TryGetProperty("verification_uri", out var v) ? v.GetString() ?? "" : "";
        if (!IsMicrosoftUri(verification)) verification = FallbackVerificationUri;

        string complete = root.TryGetProperty("verification_uri_complete", out var c) ? c.GetString()?.Trim() ?? "" : "";
        if (!IsMicrosoftUri(complete))
            complete = $"{verification}?otc={Uri.EscapeDataString(userCode)}";

        int interval = root.TryGetProperty("interval", out var i) && i.TryGetInt32(out var iv) ? Math.Max(iv, 1) : 5;
        int expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ev) ? ev : 900;
        return new DeviceCodeInfo(deviceCode, userCode, verification, complete, interval, expires);
    }

    internal static bool IsMicrosoftUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps) return false;
        string host = parsed.Host.ToLowerInvariant();
        return host == "login.live.com" || host.EndsWith(".login.live.com")
            || host == "microsoft.com" || host.EndsWith(".microsoft.com");
    }

    /// <summary>Chờ người dùng xác nhận trên trình duyệt; trả về (access, refresh) của Microsoft.</summary>
    internal static async Task<(string AccessToken, string RefreshToken)> WaitForLoginAsync(
        DeviceCodeInfo info, string? proxyAddress, CancellationToken ct)
    {
        using var client = CreateClient(proxyAddress);
        var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresInSeconds);
        int interval = info.IntervalSeconds;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(DeviceCodeExpiredText);

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = info.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var res = await client.PostAsync($"{TokenUrl}?client_id={ClientId}", form, ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                switch (err.GetString())
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += 5; continue;
                    case "expired_token": throw new TimeoutException(DeviceCodeExpiredText);
                    case "authorization_declined": throw new InvalidDataException(LoginDeclinedText);
                    default: throw new HttpRequestException("Đăng nhập Microsoft thất bại.", null, res.StatusCode);
                }
            }

            if (root.TryGetProperty("access_token", out var at) && root.TryGetProperty("refresh_token", out var rt)
                && at.GetString() is { Length: > 0 } access && rt.GetString() is { Length: > 0 } refresh)
                return (access, refresh);

            throw new JsonException("Phản hồi Microsoft thiếu token.");
        }
    }

    internal const string DeviceCodeExpiredText = "Mã đăng nhập đã hết hạn. Hãy bấm đăng nhập lại để lấy mã mới.";
    internal const string LoginDeclinedText = "Bạn đã từ chối đăng nhập trên trang Microsoft.";

    // ---------------------------------------------------------------- Refresh

    internal static async Task<(string AccessToken, string RefreshToken)> RefreshMicrosoftTokenAsync(
        string refreshToken, string? proxyAddress, CancellationToken ct)
    {
        using var client = CreateClient(proxyAddress);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var res = await client.PostAsync(TokenUrl, form, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? code = null;
            try { code = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); } catch { }
            if ((int)res.StatusCode == 400 && code == "invalid_grant") throw new MicrosoftRefreshRevokedException();
            throw new HttpRequestException("Làm mới phiên Microsoft thất bại.", null, res.StatusCode);
        }

        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("access_token").GetString()!,
                doc.RootElement.GetProperty("refresh_token").GetString()!);
    }

    internal static async Task<MicrosoftLoginResult> RefreshSessionAsync(
        string refreshToken, IProgress<string> progress, CancellationToken ct, string? proxyAddress = null)
    {
        progress.Report("Kết nối Microsoft OAuth");
        var (access, newRefresh) = await RefreshMicrosoftTokenAsync(refreshToken, proxyAddress, ct);
        return await ExchangeAsync(access, newRefresh, progress, ct, proxyAddress);
    }

    // ---------------------------------------------------------------- Xbox + Minecraft

    internal static async Task<MicrosoftLoginResult> ExchangeAsync(
        string msAccessToken, string refreshToken, IProgress<string> progress, CancellationToken ct, string? proxyAddress = null)
    {
        using var client = CreateClient(proxyAddress);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        progress.Report("Xác nhận phiên Xbox");
        string device = await DeviceTokenAsync(client, key, ct);
        string title = await TitleTokenAsync(client, key, msAccessToken, device, ct);
        string user = await UserTokenAsync(client, key, msAccessToken, ct);
        var (userHash, xsts) = await XstsAsync(client, key, user, device, title, ct);

        progress.Report("Lấy phiên Minecraft Java");
        string mcToken = await LoginWithXboxAsync(client, userHash, xsts, ct);

        progress.Report("Đọc hồ sơ người chơi");
        var (name, uuid) = await GetProfileAsync(client, mcToken, ct);

        return new MicrosoftLoginResult(new SessionResult(name, uuid, mcToken), refreshToken);
    }

    private static async Task<string> DeviceTokenAsync(HttpClient client, ECDsa key, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "ProofOfPossession",
                Id = $"{{{Guid.NewGuid()}}}",
                DeviceType = "Nintendo",
                SerialNumber = $"{{{Guid.NewGuid()}}}",
                Version = "0.0.0",
                ProofKey = ProofKey(key)
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        return (await SignedPostAsync(client, key, DeviceAuthUrl, payload, "1", ct)).Token;
    }

    private static async Task<string> TitleTokenAsync(HttpClient client, ECDsa key, string msAccessToken, string deviceToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                DeviceToken = deviceToken,
                RpsTicket = $"t={msAccessToken}", // "t=" cho flow login.live.com (không phải "d=" của Azure AD)
                SiteName = "user.auth.xboxlive.com",
                ProofKey = ProofKey(key)
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        return (await SignedPostAsync(client, key, TitleAuthUrl, payload, "1", ct)).Token;
    }

    private static async Task<string> UserTokenAsync(HttpClient client, ECDsa key, string msAccessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"t={msAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        return (await SignedPostAsync(client, key, UserAuthUrl, payload, "2", ct)).Token;
    }

    private static async Task<(string UserHash, string Token)> XstsAsync(
        HttpClient client, ECDsa key, string userToken, string deviceToken, string titleToken, CancellationToken ct)
    {
        var payload = new
        {
            RelyingParty = McRelyingParty,
            TokenType = "JWT",
            Properties = new
            {
                UserTokens = new[] { userToken },
                DeviceToken = deviceToken,
                TitleToken = titleToken,
                ProofKey = ProofKey(key),
                SandboxId = "RETAIL"
            }
        };
        var res = await SignedPostAsync(client, key, XstsUrl, payload, "1", ct);
        if (string.IsNullOrEmpty(res.UserHash))
            throw new JsonException("Phản hồi Xbox thiếu userhash.");
        return (res.UserHash, res.Token);
    }

    private sealed record XboxToken(string Token, string? UserHash);

    private static async Task<XboxToken> SignedPostAsync(
        HttpClient client, ECDsa key, string url, object payload, string contractVersion, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-store, must-revalidate, no-cache");
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion);
        request.Headers.TryAddWithoutValidation("Signature", Convert.ToBase64String(Sign(key, url, body)));

        using var res = await client.SendAsync(request, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            ThrowXboxError(text, res.StatusCode);
            throw new HttpRequestException("Xbox Live từ chối yêu cầu.", null, res.StatusCode);
        }

        using var doc = JsonDocument.Parse(text);
        string token = doc.RootElement.GetProperty("Token").GetString()!;
        string? uhs = null;
        if (doc.RootElement.TryGetProperty("DisplayClaims", out var claims)
            && claims.TryGetProperty("xui", out var xui) && xui.GetArrayLength() > 0
            && xui[0].TryGetProperty("uhs", out var u))
            uhs = u.GetString();
        return new XboxToken(token, uhs);
    }

    private static void ThrowXboxError(string body, System.Net.HttpStatusCode status)
    {
        long xerr = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var x) && x.TryGetInt64(out var v)) xerr = v;
        }
        catch { }
        string? message = xerr switch
        {
            2148916233 => "Tài khoản Microsoft này chưa có hồ sơ Xbox. Hãy tạo hồ sơ Xbox trước khi đăng nhập.",
            2148916235 => "Xbox Live không khả dụng tại quốc gia của tài khoản này.",
            2148916236 or 2148916237 => "Tài khoản cần xác minh người lớn trên Xbox.",
            2148916238 => "Tài khoản thuộc trẻ em, cần được thêm vào gia đình Microsoft để dùng Xbox.",
            _ => null
        };
        if (message != null) throw new InvalidDataException(message);
    }

    private static async Task<string> LoginWithXboxAsync(HttpClient client, string userHash, string xstsToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", LauncherUserAgent);
        using var res = await client.SendAsync(request, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Đổi token Minecraft thất bại: HTTP {(int)res.StatusCode}", null, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<(string Name, string Uuid)> GetProfileAsync(HttpClient client, string mcToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {mcToken}");
        request.Headers.TryAddWithoutValidation("User-Agent", LauncherUserAgent);
        using var res = await client.SendAsync(request, ct);
        if (!res.IsSuccessStatusCode)
            throw new Exception("Tài khoản Microsoft này chưa sở hữu bản quyền Minecraft Java.");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return (doc.RootElement.GetProperty("name").GetString()!, doc.RootElement.GetProperty("id").GetString()!);
    }

    // ---------------------------------------------------------------- Request signing

    private static object ProofKey(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return new
        {
            kty = "EC",
            crv = "P-256",
            x = Base64Url.EncodeToString(q.X!),
            y = Base64Url.EncodeToString(q.Y!),
            alg = "ES256",
            use = "sig"
        };
    }

    /// <summary>
    /// Chữ ký Xbox Live "title auth": ECDSA P-256/SHA-256 (r||s thô) trên buffer
    /// [policy=1][0][FILETIME][0]["POST\0"][path\0][\0][payload\0]. Phải khớp từng byte với bản gốc.
    /// </summary>
    private static byte[] Sign(ECDsa key, string url, string payload)
    {
        long timestamp = DateTime.UtcNow.ToFileTimeUtc();
        string path = new Uri(url).AbsolutePath;

        using var message = new MemoryStream();
        WriteInt32(message, 1);
        message.WriteByte(0);
        WriteInt64(message, timestamp);
        message.WriteByte(0);
        WriteCString(message, "POST");
        WriteCString(message, path);
        WriteCString(message, ""); // authorization token luôn rỗng
        WriteCString(message, payload);

        byte[] signature = key.SignData(message.ToArray(), HashAlgorithmName.SHA256); // IEEE P1363

        using var result = new MemoryStream();
        WriteInt32(result, 1);
        WriteInt64(result, timestamp);
        result.Write(signature);
        return result.ToArray();
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteInt64(Stream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteCString(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }
}
