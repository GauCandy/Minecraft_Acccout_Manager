using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftSessionTool;

public enum ProxyCheckFailure
{
    None, InvalidAddress, Timeout, AuthRequired, Blocked, RateLimited,
    RemoteError, Dns, Connection, Tls, Tunnel, Canceled, Unknown
}

public class SavedProxy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Address { get; set; } = "";
    public string Status { get; set; } = "Chưa kiểm tra";
    public int LatencyMs { get; set; } = -1;
    public DateTime? LastChecked { get; set; }
    public int? LastHttpStatus { get; set; }
    public ProxyCheckFailure FailureKind { get; set; }

    [JsonIgnore] public string ErrorDescription => ProxyStore.DescribeFailure(FailureKind);
    [JsonIgnore] public string LatencyText => LatencyMs >= 0 ? $"{LatencyMs} ms" : "—";
    [JsonIgnore] public string StatusColor => Status switch
    {
        "Hoạt động" => "#4E8C5C",
        "Chưa kiểm tra" or "Đã hủy" => "#8C7D9E",
        _ => FailureKind is ProxyCheckFailure.Blocked or ProxyCheckFailure.RateLimited or ProxyCheckFailure.RemoteError ? "#D35400" : "#C0392B"
    };
    [JsonIgnore] public string StatusBgColor => Status switch
    {
        "Hoạt động" => "#EDF7EE",
        "Chưa kiểm tra" or "Đã hủy" => "#F1EAF7",
        _ => FailureKind is ProxyCheckFailure.Blocked or ProxyCheckFailure.RateLimited or ProxyCheckFailure.RemoteError ? "#FFF1E5" : "#FDF0F0"
    };
}

public static class ProxyStore
{
    private static readonly string ProxiesFile = Path.Combine(CookieStore.StorageDir, "proxies.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object StorageGate = new();
    private static int rotationIndex;

    public static SavedProxy? GetNextRotatingProxy()
    {
        var list = LoadAll();
        if (list.Count == 0) return null;
        var candidates = list.Where(p => !p.Status.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase)
            && p.FailureKind is ProxyCheckFailure.None or ProxyCheckFailure.Canceled).ToList();
        if (candidates.Count == 0) candidates = list;
        var index = (uint)Interlocked.Increment(ref rotationIndex) % (uint)candidates.Count;
        return candidates[(int)index];
    }

    public static List<SavedProxy> LoadAll()
    {
        lock (StorageGate)
        {
            try
            {
                if (!File.Exists(ProxiesFile)) return new();
                return JsonSerializer.Deserialize<List<SavedProxy>>(File.ReadAllText(ProxiesFile), JsonOptions) ?? new();
            }
            catch { return new(); }
        }
    }

    public static bool SaveAll(List<SavedProxy> list)
    {
        lock (StorageGate)
        {
            try
            {
                Directory.CreateDirectory(CookieStore.StorageDir);
                File.WriteAllText(ProxiesFile, JsonSerializer.Serialize(list, JsonOptions));
                return true;
            }
            catch { return false; }
        }
    }

    public static int AddFromText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return 0;
        lock (StorageGate)
        {
            var list = LoadAll();
            int added = 0;
            foreach (var raw in rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = Regex.Replace(raw.Trim(), @"^\d+[.)]\s+", "");
                if (Regex.IsMatch(line, @"^\d+[.)]\s*$")) continue;
                if (line.Length == 0 || line.StartsWith('#')) continue;
                // Credentials are case-sensitive; do not discard distinct credentials by ignoring case.
                if (list.Any(p => p.Address.Equals(line, StringComparison.Ordinal))) continue;
                list.Add(new SavedProxy { Address = line });
                added++;
            }
            return added > 0 && SaveAll(list) ? added : 0;
        }
    }

    public static void Delete(string id)
    {
        lock (StorageGate)
        {
            var list = LoadAll();
            list.RemoveAll(p => p.Id == id);
            SaveAll(list);
        }
    }

    public static void ClearAll() => SaveAll(new());

    public static bool SaveTestResult(SavedProxy result)
    {
        lock (StorageGate)
        {
            var latest = LoadAll();
            return MergeCheckResult(latest, result) && SaveAll(latest);
        }
    }

    internal static bool MergeCheckResult(List<SavedProxy> latest, SavedProxy result)
    {
        var stored = latest.FirstOrDefault(p => p.Id == result.Id);
        if (stored == null) return false;
        // A result for an older address must not overwrite an edited proxy.
        if (!string.Equals(stored.Address, result.Address, StringComparison.Ordinal)) return false;
        // Concurrent checks must not overwrite a newer completed result.
        if (stored.LastChecked > result.LastChecked) return false;
        stored.Status = result.Status;
        stored.LatencyMs = result.LatencyMs;
        stored.LastChecked = result.LastChecked;
        stored.LastHttpStatus = result.LastHttpStatus;
        stored.FailureKind = result.FailureKind;
        return true;
    }

    public static IWebProxy? CreateWebProxy(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        address = Regex.Replace(address.Trim(), @"^\d+[.)]\s+", "");
        try
        {
            string scheme = "socks5";
            string host;
            int port;
            string? user = null;
            string? password = null;
            if (!address.Contains("://"))
            {
                // Parse host:port:user:password before looking for @, which may be part of a password.
                var match = Regex.Match(address, @"^(?:\[(?<host>[^\]]+)\]|(?<host>[^:\s@/]+)):(?<port>[0-9]+)(?::(?<user>[^:]*):(?<password>.*))?$", RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    host = match.Groups["host"].Value;
                    if (!int.TryParse(match.Groups["port"].Value, out port)) return null;
                    if (match.Groups["user"].Success)
                    {
                        user = match.Groups["user"].Value;
                        password = match.Groups["password"].Value;
                    }
                    return BuildProxy(scheme, host, port, user, password);
                }
                // Use the last @: shorthand passwords may themselves contain @ or colons.
                var separator = address.LastIndexOf('@');
                if (separator <= 0) return null;
                var credentials = address[..separator].Split(':', 2);
                var authority = address[(separator + 1)..];
                if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var endpoint)
                    || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/"
                    || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return null;
                user = Uri.UnescapeDataString(credentials[0]);
                password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "";
                return BuildProxy("http", endpoint.IdnHost, endpoint.Port, user, password);
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
            scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) return null;
            if (uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
            host = uri.IdnHost;
            port = uri.Port;
            if (port == -1 && scheme.StartsWith("socks", StringComparison.Ordinal)) port = 1080;
            if (uri.UserInfo.Length != 0)
            {
                var credentials = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(credentials[0]);
                password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "";
            }
            return BuildProxy(scheme, host, port, user, password);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static WebProxy? BuildProxy(string scheme, string host, int port, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || port is < 1 or > 65535) return null;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        var uri = new UriBuilder(scheme, host, port).Uri;
        var proxy = new WebProxy(uri);
        if (user != null) proxy.Credentials = new NetworkCredential(user, password ?? "");
        return proxy;
    }

    public static string GetDisplayAddress(string address)
    {
        if (CreateWebProxy(address) is not WebProxy proxy || proxy.Address == null) return "Proxy không hợp lệ";
        var uri = proxy.Address;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }

    public static string DescribeFailure(ProxyCheckFailure failure) => failure switch
    {
        ProxyCheckFailure.None => "Đã nhận phản hồi từ dịch vụ Minecraft qua proxy.",
        ProxyCheckFailure.InvalidAddress => "Định dạng proxy chưa đúng. Dùng host:port hoặc host:port:user:pass (SOCKS5). Với HTTP, thêm http://.",
        ProxyCheckFailure.Timeout => "Kết nối mất quá nhiều thời gian. Proxy có thể tắt, chặn kết nối hoặc phản hồi chậm.",
        ProxyCheckFailure.AuthRequired => "Proxy yêu cầu xác thực. Kiểm tra tài khoản và mật khẩu của proxy.",
        ProxyCheckFailure.Blocked => "Đã kết nối nhưng dịch vụ từ chối truy cập qua proxy này (HTTP 403).",
        ProxyCheckFailure.RateLimited => "Dịch vụ đang giới hạn số yêu cầu từ proxy này (HTTP 429). Thử lại sau.",
        ProxyCheckFailure.RemoteError => "Proxy hoặc dịch vụ đích trả về lỗi HTTP. Kết nối được nhưng chưa xác nhận truy cập thành công.",
        ProxyCheckFailure.Dns => "Không phân giải được địa chỉ proxy. Kiểm tra tên máy chủ và kết nối mạng.",
        ProxyCheckFailure.Connection => "Không kết nối được proxy. Kiểm tra máy chủ, cổng và tình trạng hoạt động.",
        ProxyCheckFailure.Tls => "Không thiết lập được kết nối HTTPS an toàn qua proxy.",
        ProxyCheckFailure.Tunnel => "Proxy từ chối đường hầm HTTPS. Kiểm tra loại proxy, thông tin xác thực và quyền truy cập.",
        ProxyCheckFailure.Canceled => "Đã dừng kiểm tra theo yêu cầu.",
        _ => "Không kiểm tra được kết nối proxy. Thử lại hoặc kiểm tra loại proxy và cấu hình mạng."
    };

    public static Task<SavedProxy> TestProxyAsync(SavedProxy proxy, CancellationToken token = default)
        => TestProxyAsync(proxy, token, null);

    internal static async Task<SavedProxy> TestProxyAsync(SavedProxy proxy, CancellationToken token, HttpClient? transport)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        proxy.LatencyMs = -1;
        proxy.LastHttpStatus = null;
        proxy.FailureKind = ProxyCheckFailure.None;
        try
        {
            var webProxy = CreateWebProxy(proxy.Address);
            if (webProxy == null)
            {
                SetFailure(proxy, ProxyCheckFailure.InvalidAddress, "Lỗi định dạng");
                return proxy;
            }
            using var owned = transport == null ? new HttpClient(new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(8)
            }) { Timeout = TimeSpan.FromSeconds(12) } : null;
            var client = transport ?? owned!;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.UserAgent.ParseAdd("MinecraftCookieManager");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            proxy.LatencyMs = (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);
            proxy.LastHttpStatus = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                proxy.Status = "Hoạt động";
            else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                SetFailure(proxy, ProxyCheckFailure.AuthRequired, "Lỗi xác thực");
            else if (response.StatusCode == HttpStatusCode.Forbidden)
                SetFailure(proxy, ProxyCheckFailure.Blocked, "Bị chặn (403)");
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                SetFailure(proxy, ProxyCheckFailure.RateLimited, "Giới hạn (429)");
            else SetFailure(proxy, ProxyCheckFailure.RemoteError, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetFailure(proxy, ProxyCheckFailure.Canceled, "Đã hủy");
        }
        catch (OperationCanceledException)
        {
            SetFailure(proxy, ProxyCheckFailure.Timeout, "Lỗi hết thời gian");
        }
        catch (HttpRequestException ex)
        {
            var failure = ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ? ProxyCheckFailure.AuthRequired
                : ex.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => ProxyCheckFailure.Dns,
                    HttpRequestError.ConnectionError => ProxyCheckFailure.Connection,
                    HttpRequestError.SecureConnectionError => ProxyCheckFailure.Tls,
                    HttpRequestError.ProxyTunnelError => ProxyCheckFailure.Tunnel,
                    _ => ProxyCheckFailure.Unknown
                };
            proxy.LastHttpStatus = ex.StatusCode is { } status ? (int)status : null;
            SetFailure(proxy, failure, failure == ProxyCheckFailure.AuthRequired ? "Lỗi xác thực" : "Lỗi kết nối");
        }
        catch (SocketException)
        {
            SetFailure(proxy, ProxyCheckFailure.Connection, "Lỗi kết nối");
        }
        catch (Exception)
        {
            SetFailure(proxy, ProxyCheckFailure.Unknown, "Lỗi kiểm tra");
        }
        finally
        {
            timer.Stop();
            proxy.LastChecked = DateTime.UtcNow;
        }
        return proxy;
    }

    private static void SetFailure(SavedProxy proxy, ProxyCheckFailure failure, string status)
    {
        proxy.FailureKind = failure;
        proxy.Status = status;
    }
}


