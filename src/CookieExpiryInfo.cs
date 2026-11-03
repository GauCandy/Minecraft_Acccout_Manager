using System.Globalization;

namespace MinecraftSessionTool;

internal sealed record CookieExpiryInfo(int Active, int Expired, int Session, int Unknown, DateTimeOffset? NextExpiry)
{
    private static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "live.com", "login.live.com", "login.microsoft.com", "login.microsoftonline.com",
        "microsoftonline.com", "minecraft.net", "www.minecraft.net", "xbox.com",
        "account.xbox.com", "sisu.xboxlive.com", "xboxlive.com"
    };

    internal static CookieExpiryInfo Read(string? text, DateTimeOffset now)
    {
        int active = 0, expired = 0, session = 0, unknown = 0;
        DateTimeOffset? next = null;
        using var reader = new System.IO.StringReader(text ?? "");
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line[10..];
            else if (line.StartsWith('#') || line.Length == 0) continue;
            var parts = line.Split('\t', 7);
            if (parts.Length != 7 || !Domains.Contains(parts[0].TrimStart('.')) || string.IsNullOrWhiteSpace(parts[5]) || string.IsNullOrWhiteSpace(parts[6])) continue;
            if (!long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0) { unknown++; continue; }
            if (seconds == 0) { session++; continue; }
            DateTimeOffset expiry;
            try { expiry = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { unknown++; continue; }
            if (expiry <= now) expired++;
            else { active++; if (next == null || expiry < next) next = expiry; }
        }
        return new(active, expired, session, unknown, next);
    }

    internal string Summary => $"{Active} còn hạn  ·  {Expired} hết hạn  ·  {Session} cookie phiên" + (Unknown > 0 ? $"  ·  {Unknown} chưa rõ hạn" : "");
    internal string ExpiryText(DateTimeOffset now)
    {
        if (NextExpiry is not { } expiry)
            return Active + Expired + Session + Unknown == 0 ? "Chưa có thông tin cookie đăng nhập."
                : Expired > 0 ? "Không còn mốc hết hạn nào ở tương lai." : "Không có ngày hết hạn cố định trong dữ liệu cookie.";
        var left = expiry - now;
        string remaining = left.TotalDays >= 1 ? $"còn {(int)left.TotalDays} ngày {left.Hours} giờ"
            : left.TotalHours >= 1 ? $"còn {(int)left.TotalHours} giờ {left.Minutes} phút" : $"còn {Math.Max(1, (int)left.TotalMinutes)} phút";
        return $"Hết hạn gần nhất: {expiry.ToLocalTime():dd/MM/yyyy HH:mm zzz}  ·  {remaining}";
    }
}
