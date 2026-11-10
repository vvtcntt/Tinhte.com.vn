using System.Net;
using System.Net.Sockets;
using Tinhte.Modules.Aggregation.Domain;

namespace Tinhte.Modules.Aggregation.Infrastructure;

/// <summary>DNS hệ thống, giới hạn 3 giây. Không phân giải được hoặc hết giờ → rỗng (worker sẽ báo FeedFailure.DNS khi thu thập).</summary>
public sealed class DnsFeedHostResolver : IFeedHostResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}
