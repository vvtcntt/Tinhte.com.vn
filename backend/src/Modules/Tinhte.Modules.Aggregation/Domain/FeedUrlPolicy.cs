using System.Net;
using System.Net.Sockets;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Aggregation.Domain;

/// <summary>Phân giải tên miền của feed (DNS). Không phân giải được → danh sách rỗng (không phải lỗi khi khai báo).</summary>
public interface IFeedHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// Chống SSRF khi khai báo feed (ADR-0008 §5, chờ Security duyệt — A-4). Kiểm lúc khai báo/sửa URL:
/// <list type="bullet">
///   <item>URL tuyệt đối, ≤ 2048 ký tự; sai cú pháp → 422 VALIDATION_FAILED;</item>
///   <item>chỉ <c>https</c>, cổng mặc định 443, không có user:password → 422 UNSAFE_URL;</item>
///   <item>host là IP (hoặc tên phân giải ra IP) loopback, private RFC 1918, link-local/metadata, CGNAT, multicast, ULA IPv6, dải dành riêng
///         → 422 UNSAFE_URL; tên nội bộ (localhost, *.local, *.internal, tên không có dấu chấm) → 422 UNSAFE_URL.</item>
/// </list>
/// DNS có thể đổi sau khi khai báo (DNS rebinding) nên worker phải kiểm lại IP trong <c>ConnectCallback</c> trước khi kết nối (lát sau);
/// tên chưa phân giải được lúc khai báo vẫn được nhận — lỗi DNS khi thu thập là dữ liệu sức khỏe feed (FeedFailure.DNS), không phải Problem.
/// </summary>
public sealed class FeedUrlPolicy(IFeedHostResolver resolver)
{
    public const int MaxLength = 2048;

    private static readonly string[] InternalSuffixes = [".localhost", ".local", ".internal", ".intranet", ".lan", ".home.arpa", ".corp"];

    /// <summary>Trả URL chuẩn hóa (<see cref="Uri.AbsoluteUri"/>) nếu an toàn; ngược lại ném Problem.</summary>
    public async Task<string> CheckAsync(string field, string? value, CancellationToken cancellationToken)
    {
        var uri = Parse(field, value);
        var host = uri.IdnHost.TrimEnd('.');
        if (uri.HostNameType is UriHostNameType.Dns or UriHostNameType.Basic)
        {
            foreach (var address in await resolver.ResolveAsync(host, cancellationToken))
            {
                if (IsBlocked(address)) throw Unsafe(field, $"The host resolves to a non-public address ({address}).");
            }
        }

        return uri.AbsoluteUri;
    }

    /// <summary>Kiểm cú pháp + những gì biết được mà không cần DNS (dùng cả trong test đơn vị).</summary>
    public static Uri Parse(string field, string? value)
    {
        // "://" bắt buộc: trên Linux "/duong-dan" cũng là URI tuyệt đối (file://), trên Windows thì không.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || !value.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The feed URL is not a valid absolute URL.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [$"An absolute https URL of at most {MaxLength} characters."] });
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) throw Unsafe(field, "Feeds must use https.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw Unsafe(field, "Credentials in the URL are not allowed.");
        if (!uri.IsDefaultPort) throw Unsafe(field, "Only the default https port (443) is allowed.");

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4 or UriHostNameType.IPv6:
                if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsBlocked(ip)) throw Unsafe(field, $"The address {ip} is not public.");
                break;
            default:
                if (!host.Contains('.', StringComparison.Ordinal) || host == "localhost"
                    || InternalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
                {
                    throw Unsafe(field, $"The host '{host}' is an internal name.");
                }

                break;
        }

        return uri;
    }

    /// <summary>Địa chỉ không được phép kết nối tới (dùng lại ở ConnectCallback của worker).</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return Ipv4Blocked.Any(r => InRange(b, r.Network, r.Prefix));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return true;
        if (InRange(b, Prefix6To4, 16)) return Ipv4Blocked.Any(r => InRange(b[2..6], r.Network, r.Prefix));   // 6to4 nhúng IPv4
        return Ipv6Blocked.Any(r => InRange(b, r.Network, r.Prefix));
    }

    private static DomainProblemException Unsafe(string field, string detail) =>
        new(ErrorCodes.UnsafeUrl, "Unsafe URL", detail, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [detail] });

    private static bool InRange(byte[] address, byte[] network, int prefix)
    {
        if (address.Length != network.Length) return false;
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
        {
            if (address[i] != network[i]) return false;
        }

        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (address[full] & mask) == (network[full] & mask);
    }

    private static (byte[] Network, int Prefix) R(string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        return (IPAddress.Parse(cidr[..slash]).GetAddressBytes(), int.Parse(cidr[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly byte[] Prefix6To4 = IPAddress.Parse("2002::").GetAddressBytes();

    // IANA special-purpose registry: "this network", private, CGNAT (cả metadata 100.100.100.200), loopback, link-local
    // (metadata 169.254.169.254), giao thức IETF, tài liệu, benchmark, multicast, dành riêng + broadcast.
    private static readonly (byte[] Network, int Prefix)[] Ipv4Blocked =
    [
        R("0.0.0.0/8"), R("10.0.0.0/8"), R("100.64.0.0/10"), R("127.0.0.0/8"), R("169.254.0.0/16"), R("172.16.0.0/12"),
        R("192.0.0.0/24"), R("192.0.2.0/24"), R("192.88.99.0/24"), R("192.168.0.0/16"), R("198.18.0.0/15"), R("198.51.100.0/24"),
        R("203.0.113.0/24"), R("224.0.0.0/4"), R("240.0.0.0/4"),
    ];

    // ::/96 (IPv4-compatible cũ), NAT64, discard, Teredo, tài liệu, ULA (cả metadata fd00:ec2::254), link-local, site-local cũ, multicast.
    private static readonly (byte[] Network, int Prefix)[] Ipv6Blocked =
    [
        R("::/96"), R("64:ff9b::/96"), R("64:ff9b:1::/48"), R("100::/64"), R("2001::/32"), R("2001:db8::/32"), R("fc00::/7"), R("fe80::/10"),
        R("fec0::/10"), R("ff00::/8"),
    ];
}
