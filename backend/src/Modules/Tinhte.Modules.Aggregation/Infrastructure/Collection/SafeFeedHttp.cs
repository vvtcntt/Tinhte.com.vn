using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;

namespace Tinhte.Modules.Aggregation.Infrastructure.Collection;

/// <summary>
/// HTTP client của bộ thu thập (ADR-0008 §5, chờ Security duyệt — A-4). Lớp phòng thủ cuối chống SSRF nằm ở
/// <see cref="SocketsHttpHandler.ConnectCallback"/>: tự phân giải DNS rồi kiểm MỌI địa chỉ bằng <see cref="FeedUrlPolicy.IsBlocked"/>
/// ngay trước khi mở socket — nên đổi DNS sau lúc khai báo (DNS rebinding) cũng không tới được mạng nội bộ. Không proxy (proxy sẽ làm
/// ConnectCallback kết nối tới proxy thay vì đích), không cookie, không tự theo redirect (<see cref="FeedFetcher"/> tự theo và kiểm lại).
/// </summary>
public static class SafeFeedHttp
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static SocketsHttpHandler CreateHandler(IFeedHostResolver resolver) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 64,   // KB
        ConnectCallback = (context, cancellationToken) => ConnectAsync(resolver, context.DnsEndPoint, cancellationToken),
    };

    /// <summary>Phân giải + kiểm địa chỉ + kết nối. Có bất kỳ địa chỉ không công khai nào trong câu trả lời DNS → từ chối cả tên miền.</summary>
    public static async ValueTask<Stream> ConnectAsync(IFeedHostResolver resolver, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var addresses = await ResolveCheckedAsync(resolver, endpoint.Host, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new FeedDnsException(endpoint.Host);
    }

    /// <summary>Địa chỉ được phép kết nối của <paramref name="host"/>; ném <see cref="UnsafeAddressException"/> / <see cref="FeedDnsException"/>.</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveCheckedAsync(IFeedHostResolver resolver, string host, CancellationToken cancellationToken)
    {
        var bare = host.Trim('[', ']');
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(bare, out var literal) ? [literal] : await resolver.ResolveAsync(host, cancellationToken);
        if (addresses.Count == 0) throw new FeedDnsException(host);
        if (addresses.FirstOrDefault(FeedUrlPolicy.IsBlocked) is { } blocked)
        {
            throw new UnsafeAddressException($"The host '{host}' resolves to a non-public address ({blocked}); the connection was not opened.");
        }

        return addresses;
    }
}
