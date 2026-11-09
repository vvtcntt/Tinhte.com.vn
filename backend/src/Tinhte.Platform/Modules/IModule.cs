using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tinhte.Platform.Modules;

/// <summary>
/// Ranh giới module (WBS-012). Module chỉ được tham chiếu Tinhte.Platform, không tham chiếu module khác
/// (test kiến trúc kiểm). Giao tiếp giữa module qua sự kiện outbox (WBS-020), không gọi trực tiếp.
/// </summary>
public interface IModule
{
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder api);
}
