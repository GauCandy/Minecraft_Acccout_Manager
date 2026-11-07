using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftSessionTool;

internal enum ActivityEvent
{
    AppStarted, AppClosed, UnexpectedFailure, CookiesReading, MicrosoftConnecting,
    XboxVerifying, SessionFetching, ProfileFetching, ProxySelected, CookieSaved,
    SessionRefreshed, ProcessingCanceled, ProcessingFailed, RefreshFailed,
    AccountRemoved, CookieExported, TokenCopied, UsernameCopied, UuidCopied,
    ClipboardFailed, FileReadFailed, InputRejected, ProxyCheckStarted,
    ProxyCheckPassed, ProxyCheckFailed, ProxyCheckCanceled, ProxyAdded,
    ProxyRemoved, ProxiesCleared, UpdateAvailable, UpdateDownloadStarted,
    UpdateDownloadFinished, UpdateFailed, UpdateCanceled, UpdatePreviewEnabled,
    UpdatePreviewDisabled, LogsCleared, LogExported, ProxyResultNotSaved
}

internal enum ActivityLevel { Info, Success, Warning, Error }

internal enum ActivityFailure
{
    None, InvalidInput, Timeout, Canceled, Network, Dns, Connection, Tls,
    ProxyAuthentication, ProxyBlocked, RateLimited, RemoteService, ProxyTunnel,
    FileAccess, AuthenticationRequired, NoGameLicense, InvalidServiceResponse, Unexpected
}

// Every stored field has a closed vocabulary or a validated numeric type.
// In particular, this model cannot retain exception messages, cookies, names, URLs or paths.
internal sealed record ActivityEntry(
    DateTimeOffset Time,
    ActivityEvent Event,
    ActivityLevel Level,
    ActivityFailure Failure,
    int? HttpStatus,
    long? DurationMs,
    int? ItemCount)
{
    public string Message => SessionActivityLog.EventMessage(Event);
    public string Detail
    {
        get
        {
            var parts = new List<string> { $"Mã: {Event}" };
            if (Failure != ActivityFailure.None) parts.Add($"Lỗi: {Failure}");
            if (HttpStatus is int status) parts.Add($"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
            if (DurationMs is long duration) parts.Add($"{duration.ToString(CultureInfo.InvariantCulture)} ms");
            if (ItemCount is int count) parts.Add($"Số lượng: {count.ToString(CultureInfo.InvariantCulture)}");
            return string.Join(" · ", parts);
        }
    }
}

internal sealed class SessionActivityLog
{
    internal const int MaxEntries = 2000;
    private const int RetainedSessionFiles = 10;
    private static readonly Regex SessionFilePattern = new(
        @"^session-[0-9]{8}-[0-9]{6}-[a-f0-9]{12}\.log$", RegexOptions.CultureInvariant);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly object gate = new();
    private readonly List<ActivityEntry> entries = new();
    private readonly string appVersion;
    private bool canPersist;
    private bool truncated;

    internal string DirectoryPath { get; }
    internal string FilePath { get; }
    internal string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    internal bool CanPersist { get { lock (gate) return canPersist; } }
    internal IReadOnlyList<ActivityEntry> Entries { get { lock (gate) return entries.ToArray(); } }

    internal SessionActivityLog(string? directory = null)
    {
        appVersion = typeof(SessionActivityLog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(CookieStore.StorageDir, "logs"));
        FilePath = Path.Combine(DirectoryPath,
            $"session-{DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{SessionId}.log");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, Header(), Utf8);
            canPersist = true;
            TrimOwnedSessions();
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { canPersist = false; }
    }

    internal ActivityEntry Record(ActivityEvent evt, ActivityLevel level = ActivityLevel.Info,
        ActivityFailure failure = ActivityFailure.None, int? httpStatus = null,
        long? durationMs = null, int? itemCount = null)
    {
        // Invalid enum casts must never introduce caller-controlled diagnostic strings.
        if (!Enum.IsDefined(evt)) evt = ActivityEvent.UnexpectedFailure;
        if (!Enum.IsDefined(level)) level = ActivityLevel.Error;
        if (!Enum.IsDefined(failure)) failure = ActivityFailure.Unexpected;
        var entry = new ActivityEntry(DateTimeOffset.Now, evt, level, failure,
            httpStatus is >= 100 and <= 599 ? httpStatus : null,
            durationMs is >= 0 and <= 86400000 ? durationMs : null,
            itemCount is >= 0 and <= 1000000 ? itemCount : null);
        lock (gate)
        {
            entries.Add(entry);
            bool rewrite = entries.Count > MaxEntries;
            if (rewrite)
            {
                entries.RemoveRange(0, entries.Count - MaxEntries);
                truncated = true;
            }
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (rewrite || !canPersist || !File.Exists(FilePath))
                    File.WriteAllText(FilePath, ReportUnsafe(), Utf8);
                else
                    File.AppendAllText(FilePath, Format(entry) + Environment.NewLine, Utf8);
                canPersist = true;
            }
            catch (Exception ex) when (IsStorageFailure(ex)) { canPersist = false; }
        }
        return entry;
    }

    internal string ToReport()
    {
        lock (gate) return ReportUnsafe();
    }

    internal void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            truncated = false;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath, Header(), Utf8);
                canPersist = true;
            }
            catch (Exception ex) when (IsStorageFailure(ex)) { canPersist = false; }
        }
    }

    private string ReportUnsafe()
    {
        var report = new StringBuilder(Header());
        if (truncated) report.AppendLine($"Chỉ giữ {MaxEntries} sự kiện gần nhất trong phiên này.");
        foreach (var entry in entries) report.AppendLine(Format(entry));
        return report.ToString();
    }

    private string Header() =>
        "Minecraft Account Manager — Báo cáo hoạt động\n" +
        $"Phiên: {SessionId}\n" +
        $"Phiên bản app: {appVersion}\n" +
        $".NET: {Environment.Version}\n" +
        $"Nền tảng: Windows / {RuntimeInformation.ProcessArchitecture}\n" +
        "Chỉ ghi sự kiện, thời gian và mã lỗi. Không ghi cookie, token, dữ liệu tài khoản, địa chỉ proxy, mật khẩu hoặc đường dẫn cá nhân.\n\n";

    private static string Format(ActivityEntry entry) =>
        $"{entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{entry.Level}] {entry.Message} | {entry.Detail}";

    private void TrimOwnedSessions()
    {
        try
        {
            var owned = Directory.EnumerateFiles(DirectoryPath, "session-*.log", SearchOption.TopDirectoryOnly)
                .Where(path => SessionFilePattern.IsMatch(Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => string.Equals(info.FullName, FilePath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var old in owned.Skip(RetainedSessionFiles))
            {
                try { old.Delete(); }
                catch (Exception ex) when (IsStorageFailure(ex)) { }
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    internal static int? GetHttpStatus(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } status } && (int)status is >= 100 and <= 599)
            return (int)status;
        return null;
    }

    internal static ActivityFailure ClassifyFailure(Exception exception)
    {
        // Never pass exception.Message, Data, inner exception text or StackTrace to a log entry.
        if (exception is OperationCanceledException)
            return exception is TaskCanceledException ? ActivityFailure.Timeout : ActivityFailure.Canceled;
        if (exception is TimeoutException) return ActivityFailure.Timeout;
        if (exception is MicrosoftRefreshRevokedException) return ActivityFailure.AuthenticationRequired;
        if (exception is HttpRequestException request)
        {
            if (request.StatusCode is { } status)
                return (int)status switch
                {
                    407 => ActivityFailure.ProxyAuthentication,
                    403 => ActivityFailure.ProxyBlocked,
                    429 => ActivityFailure.RateLimited,
                    >= 500 => ActivityFailure.RemoteService,
                    401 => ActivityFailure.AuthenticationRequired,
                    _ => ActivityFailure.Network
                };
            return request.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => ActivityFailure.Dns,
                HttpRequestError.ConnectionError => ActivityFailure.Connection,
                HttpRequestError.SecureConnectionError => ActivityFailure.Tls,
                HttpRequestError.ProxyTunnelError => ActivityFailure.ProxyTunnel,
                _ => ActivityFailure.Network
            };
        }
        if (exception is SocketException socket)
            return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? ActivityFailure.Dns : ActivityFailure.Connection;
        if (exception is AuthenticationException) return ActivityFailure.Tls;
        if (exception is InvalidDataException) return ActivityFailure.InvalidInput;
        if (exception is UnauthorizedAccessException or IOException) return ActivityFailure.FileAccess;
        if (exception is FormatException or System.Text.Json.JsonException) return ActivityFailure.InvalidServiceResponse;
        if (exception is ArgumentException) return ActivityFailure.InvalidInput;
        // Equality against known application messages, never a copy of arbitrary exception text.
        return exception.Message switch
        {
            "Cookie đã hết hạn hoặc Microsoft yêu cầu xác minh tài khoản (2FA/OTP)." => ActivityFailure.AuthenticationRequired,
            "Tài khoản Microsoft này chưa sở hữu bản quyền Minecraft Java." => ActivityFailure.NoGameLicense,
            "Không tìm thấy dữ liệu phân đoạn (fragment) trong phản hồi." or
            "Không tìm thấy accessToken trong URL callback." or
            "Gói đăng nhập không chứa dịch vụ Minecraft." => ActivityFailure.InvalidServiceResponse,
            _ => ActivityFailure.Unexpected
        };
    }

    internal static ActivityEvent? ProgressEvent(string message) => message switch
    {
        "Đọc file cookie" or "Đọc dữ liệu cookie" => ActivityEvent.CookiesReading,
        "Kết nối Microsoft OAuth" => ActivityEvent.MicrosoftConnecting,
        "Xác nhận phiên Xbox" => ActivityEvent.XboxVerifying,
        "Lấy phiên Minecraft Java" => ActivityEvent.SessionFetching,
        "Đọc hồ sơ người chơi" => ActivityEvent.ProfileFetching,
        "Định tuyến qua proxy" or "Định tuyến qua Proxy" => ActivityEvent.ProxySelected,
        _ when message.StartsWith("Định tuyến qua Proxy: ", StringComparison.Ordinal) => ActivityEvent.ProxySelected,
        _ => null
    };

    internal static string EventMessage(ActivityEvent evt) => evt switch
    {
        ActivityEvent.AppStarted => "Đã mở ứng dụng",
        ActivityEvent.AppClosed => "Đã đóng ứng dụng",
        ActivityEvent.UnexpectedFailure => "Ứng dụng gặp lỗi ngoài dự kiến",
        ActivityEvent.CookiesReading => "Đang đọc dữ liệu cookie",
        ActivityEvent.MicrosoftConnecting => "Đang kết nối Microsoft",
        ActivityEvent.XboxVerifying => "Đang xác nhận phiên Xbox",
        ActivityEvent.SessionFetching => "Đang lấy phiên Minecraft",
        ActivityEvent.ProfileFetching => "Đang đọc hồ sơ Minecraft",
        ActivityEvent.ProxySelected => "Đã chọn định tuyến qua proxy",
        ActivityEvent.CookieSaved => "Đã lưu cookie thành công",
        ActivityEvent.SessionRefreshed => "Đã làm mới phiên thành công",
        ActivityEvent.ProcessingCanceled => "Đã dừng xử lý theo yêu cầu",
        ActivityEvent.ProcessingFailed => "Xử lý cookie thất bại",
        ActivityEvent.RefreshFailed => "Làm mới phiên thất bại",
        ActivityEvent.AccountRemoved => "Đã xóa tài khoản khỏi danh sách",
        ActivityEvent.CookieExported => "Đã xuất file cookie theo yêu cầu",
        ActivityEvent.TokenCopied => "Đã sao chép token theo yêu cầu",
        ActivityEvent.UsernameCopied => "Đã sao chép tên người chơi",
        ActivityEvent.UuidCopied => "Đã sao chép UUID",
        ActivityEvent.ClipboardFailed => "Không truy cập được bộ nhớ tạm",
        ActivityEvent.FileReadFailed => "Không đọc được file đã chọn",
        ActivityEvent.InputRejected => "Dữ liệu nhập chưa hợp lệ",
        ActivityEvent.ProxyCheckStarted => "Đang kiểm tra kết nối proxy",
        ActivityEvent.ProxyCheckPassed => "Proxy đã kết nối được đến dịch vụ",
        ActivityEvent.ProxyCheckFailed => "Kiểm tra proxy chưa thành công",
        ActivityEvent.ProxyCheckCanceled => "Đã dừng kiểm tra proxy",
        ActivityEvent.ProxyAdded => "Đã thêm proxy vào danh sách",
        ActivityEvent.ProxyRemoved => "Đã xóa proxy khỏi danh sách",
        ActivityEvent.ProxiesCleared => "Đã xóa toàn bộ danh sách proxy",
        ActivityEvent.UpdateAvailable => "Đã phát hiện phiên bản mới",
        ActivityEvent.UpdateDownloadStarted => "Đang tải bản cập nhật",
        ActivityEvent.UpdateDownloadFinished => "Đã tải và kiểm tra bản cập nhật",
        ActivityEvent.UpdateFailed => "Cập nhật chưa thành công",
        ActivityEvent.UpdateCanceled => "Đã dừng tải bản cập nhật",
        ActivityEvent.UpdatePreviewEnabled => "Đã bật xem thử cập nhật",
        ActivityEvent.UpdatePreviewDisabled => "Đã tắt xem thử cập nhật",
        ActivityEvent.LogsCleared => "Đã làm sạch nhật ký của phiên hiện tại",
        ActivityEvent.LogExported => "Đã xuất báo cáo hoạt động",
        ActivityEvent.ProxyResultNotSaved => "Chưa lưu được kết quả kiểm tra proxy",
        _ => "Sự kiện không xác định"
    };
}


